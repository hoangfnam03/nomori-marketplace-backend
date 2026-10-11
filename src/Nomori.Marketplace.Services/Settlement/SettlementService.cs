using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Settlement;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Authentication;

namespace Nomori.Marketplace.Services.Settlement;

/// <summary>
/// Commission, the shop ledger, bank accounts and payouts (F05-S). The ledger is only ever added to; every change of state is a compare-and-set
/// in the store, so two administrators or two nodes never pay the same money twice.
/// </summary>
public sealed partial class SettlementService(
    ISettlementStore store,
    IVendorStore vendors,
    IVendorMemberStore members,
    ICustomerIdentityStore customers,
    IPasswordHasher passwordHasher,
    ISecretProtector protector,
    IEmailQueueService emailQueue,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<SettlementOptions> options,
    ILogger<SettlementService> logger) : ISettlementService
{
    // ---- Recording (the job, D6) ----

    public async Task<(int Recorded, int Failed)> RecordPendingAsync(CancellationToken cancellationToken)
    {
        var places = (await primaryCurrency.GetPrimaryAsync(cancellationToken)).DecimalPlaces;
        var recorded = 0;
        var failed = 0;

        foreach (var order in await store.FindUnrecordedSalesAsync(SettlementLimits.BatchSize, cancellationToken))
        {
            try
            {
                if (await store.TryInsertEntryAsync(SettlementRules.Sale(order, clock.UtcNow, places), cancellationToken)) recorded++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                LogRecordFailed(exception, "sale", order.ShopOrderId);
            }
        }

        foreach (var refund in await store.FindUnrecordedRefundsAsync(SettlementLimits.BatchSize, cancellationToken))
        {
            try
            {
                if (await store.TryInsertEntryAsync(SettlementRules.Refund(refund, clock.UtcNow, places), cancellationToken)) recorded++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                LogRecordFailed(exception, "refund", refund.ReturnRequestId);
            }
        }
        return (recorded, failed);
    }

    // ---- Commission ----

    public Task<CommissionOverview> GetCommissionAsync(CancellationToken cancellationToken) => store.GetCommissionAsync(50, cancellationToken);

    public async Task<CatalogResult<decimal>> SetDefaultRateAsync(decimal rate, string? note, int actorCustomerId, CancellationToken cancellationToken)
    {
        var text = note?.Trim();
        if (!SettlementRules.ValidRate(rate))
            return CatalogResult.Failure<decimal>("rate", $"The rate must be between 0 and {SettlementLimits.MaxRate} percent, with at most 2 decimals.");
        if (text?.Length > SettlementLimits.MaxNoteLength)
            return CatalogResult.Failure<decimal>("note", $"The note can have at most {SettlementLimits.MaxNoteLength} characters.");

        var old = await store.GetDefaultRateAsync(cancellationToken);
        await store.SetDefaultRateAsync(rate, Clean(text), actorCustomerId, clock.UtcNow, cancellationToken);
        await auditLog.WriteAsync("finance.rate_changed", actorCustomerId, entityType: "CommissionSetting", entityId: 1,
            details: new { scope = "default", oldRate = old, newRate = rate }, cancellationToken: cancellationToken);
        return CatalogResult.Success(rate);
    }

    public async Task<CatalogResult<decimal?>> SetVendorRateAsync(int vendorId, decimal? rate, string? note, int actorCustomerId, CancellationToken cancellationToken)
    {
        var text = note?.Trim();
        if (rate is not null && !SettlementRules.ValidRate(rate.Value))
            return CatalogResult.Failure<decimal?>("rate", $"The rate must be between 0 and {SettlementLimits.MaxRate} percent, with at most 2 decimals.");
        if (text?.Length > SettlementLimits.MaxNoteLength)
            return CatalogResult.Failure<decimal?>("note", $"The note can have at most {SettlementLimits.MaxNoteLength} characters.");

        var vendor = await vendors.GetAsync(vendorId, cancellationToken);
        if (vendor is null || vendor.Deleted) return CatalogResult.Error<decimal?>(CatalogErrors.NotFound);

        var before = await EffectiveRateAsync(vendor, cancellationToken);
        await store.SetVendorRateAsync(vendorId, rate, Clean(text), actorCustomerId, clock.UtcNow, cancellationToken);
        var after = await EffectiveRateAsync(vendor, cancellationToken);

        await auditLog.WriteAsync("finance.rate_changed", actorCustomerId, entityType: "Vendor", entityId: vendorId,
            details: new { scope = "vendor", vendorId, oldRate = before, newRate = after, own = rate }, cancellationToken: cancellationToken);
        await NotifyMembersAsync(vendorId, SettlementEmailKinds.RateChanged, "Your commission rate changed",
            $"<p>The commission rate of <strong>{Html(vendor.Name)}</strong> changed from {Percent(before)} to {Percent(after)}. " +
            "It applies to orders placed from now on; earlier orders keep their rate.</p>", cancellationToken);
        return CatalogResult.Success(rate);
    }

    // ---- Adjustments ----

    public async Task<CatalogResult<SettlementEntry>> AdjustAsync(NewAdjustment command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > SettlementLimits.MaxReasonLength) errors["reason"] = [$"Give a reason of 1 to {SettlementLimits.MaxReasonLength} characters."];
        if (command.Amount == 0m || Math.Abs(command.Amount) > SettlementLimits.MaxAmount || !CurrencyRules.HasValidScale(command.Amount, currency.DecimalPlaces))
            errors["amount"] = [$"Enter an amount that is not zero, with at most {currency.DecimalPlaces} decimal places."];
        if (errors.Count > 0) return CatalogResult.Failure<SettlementEntry>(errors);

        var vendor = await vendors.GetAsync(command.VendorId, cancellationToken);
        if (vendor is null || vendor.Deleted) return CatalogResult.Error<SettlementEntry>(CatalogErrors.NotFound);

        var entry = new SettlementEntry
        {
            VendorId = vendor.Id, Kind = EntryKind.Adjustment, Gross = 0m, Commission = 0m, Amount = command.Amount, CurrencyCode = currency.Code,
            Note = reason, CreatedByCustomerId = actorCustomerId, CreatedOnUtc = clock.UtcNow
        };
        entry.Id = await store.InsertAdjustmentAsync(entry, cancellationToken);
        await auditLog.WriteAsync("finance.adjusted", actorCustomerId, entityType: "SettlementEntry", entityId: (int)entry.Id,
            details: new { vendorId = vendor.Id, command.Amount, reason }, cancellationToken: cancellationToken);
        return CatalogResult.Success(entry);
    }

    // ---- Balances ----

    public async Task<PagedResult<VendorBalance>> GetBalancesAsync(BalanceQuery query, CancellationToken cancellationToken)
    {
        var code = (await primaryCurrency.GetPrimaryAsync(cancellationToken)).Code;
        var page = await store.ListBalancesAsync(
            query with { Page = Math.Max(query.Page, 1), PageSize = Math.Clamp(query.PageSize, 1, SettlementLimits.MaxPageSize) }, clock.UtcNow, cancellationToken);
        return page with { Items = page.Items.Select(b => b with { CurrencyCode = code }).ToList() };
    }

    public Task<IReadOnlyList<MonthlyTotal>> GetPlatformMonthlyAsync(int months, CancellationToken cancellationToken) =>
        store.GetMonthlyAsync(null, Math.Clamp(months, 1, 36), cancellationToken);

    // ---- Payouts ----

    public async Task<CatalogResult<PayoutRunResult>> CreatePayoutsAsync(DateTime cutoffUtc, int actorCustomerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (cutoffUtc > now) return CatalogResult.Failure<PayoutRunResult>("cutoff", "The cut-off cannot be in the future.");

        var created = new List<Payout>();
        var skipped = 0;
        foreach (var vendorId in await store.VendorsWithEntriesAsync(cutoffUtc, cancellationToken))
        {
            // No usable bank account (missing, or still on hold): the money waits.
            var bank = await store.GetBankAccountAsync(vendorId, cancellationToken);
            if (!SettlementRules.IsUsable(bank, now))
            {
                skipped++;
                continue;
            }

            var payout = await store.TryCreatePayoutAsync(vendorId, cutoffUtc, options.Value.MinimumPayout, bank!, actorCustomerId, now, cancellationToken);
            if (payout is null)
            {
                skipped++;
                continue;
            }

            created.Add(payout);
            await auditLog.WriteAsync("finance.payout_created", actorCustomerId, entityType: "SettlementPayout", entityId: payout.Id,
                details: new { payoutId = payout.Id, payout.Number, payout.VendorId, payout.Amount, payout.EntryCount, accountLast4 = payout.AccountLast4 },
                cancellationToken: cancellationToken);
        }
        return CatalogResult.Success(new PayoutRunResult(created, skipped));
    }

    public async Task<PagedResult<Payout>> GetPayoutsAsync(SettlementCaller caller, PayoutStatus? status, int page, int pageSize, CancellationToken cancellationToken) =>
        // A shop sees its own payouts, whatever else was asked.
        await store.ListPayoutsAsync(new PayoutQuery(
            caller.IsAdmin ? null : caller.VendorId ?? -1, status, Math.Max(page, 1), Math.Clamp(pageSize, 1, SettlementLimits.MaxPageSize)), cancellationToken);

    public async Task<CatalogResult<PayoutDetail>> GetPayoutAsync(SettlementCaller caller, int id, CancellationToken cancellationToken)
    {
        var payout = await store.GetPayoutAsync(id, cancellationToken);
        if (payout is null || (!caller.IsAdmin && payout.VendorId != caller.VendorId)) return CatalogResult.Error<PayoutDetail>(CatalogErrors.NotFound);
        return CatalogResult.Success(new PayoutDetail(payout, await store.ListPayoutEntriesAsync(id, cancellationToken)));
    }

    public async Task<CatalogResult<Payout>> MarkPaidAsync(int id, string? reference, int actorCustomerId, CancellationToken cancellationToken)
    {
        var text = reference?.Trim() ?? string.Empty;
        if (text.Length is 0 or > SettlementLimits.MaxReferenceLength)
            return CatalogResult.Failure<Payout>("reference", $"Enter the bank reference (1 to {SettlementLimits.MaxReferenceLength} characters).");

        var payout = await store.GetPayoutAsync(id, cancellationToken);
        if (payout is null) return CatalogResult.Error<Payout>(CatalogErrors.NotFound);
        if (!await store.TryMarkPaidAsync(id, text, actorCustomerId, clock.UtcNow, cancellationToken))
            return CatalogResult.Error<Payout>(SettlementErrors.PayoutInvalidTransition);

        await auditLog.WriteAsync("finance.payout_paid", actorCustomerId, entityType: "SettlementPayout", entityId: id,
            details: new { payoutId = id, payout.Number, payout.Amount, reference = text }, cancellationToken: cancellationToken);
        await NotifyMembersAsync(payout.VendorId, SettlementEmailKinds.PayoutPaid, $"Payout {payout.Number} was paid",
            $"<p>Payout <strong>{Html(payout.Number)}</strong> of {Money(payout.Amount, payout.CurrencyCode)} was sent to your account ending {Html(payout.AccountLast4)}.</p>" +
            $"<p>Bank reference: {Html(text)}</p>", cancellationToken);
        return CatalogResult.Success((await store.GetPayoutAsync(id, cancellationToken))!);
    }

    public async Task<CatalogResult<Payout>> MarkFailedAsync(int id, string? reason, int actorCustomerId, CancellationToken cancellationToken)
    {
        var text = reason?.Trim() ?? string.Empty;
        if (text.Length is 0 or > SettlementLimits.MaxReasonLength)
            return CatalogResult.Failure<Payout>("reason", $"Give a reason of 1 to {SettlementLimits.MaxReasonLength} characters.");

        var payout = await store.GetPayoutAsync(id, cancellationToken);
        if (payout is null) return CatalogResult.Error<Payout>(CatalogErrors.NotFound);
        if (!await store.TryMarkFailedAsync(id, text, actorCustomerId, clock.UtcNow, cancellationToken))
            return CatalogResult.Error<Payout>(SettlementErrors.PayoutInvalidTransition);

        await auditLog.WriteAsync("finance.payout_failed", actorCustomerId, entityType: "SettlementPayout", entityId: id,
            details: new { payoutId = id, payout.Number, payout.Amount, reason = text }, cancellationToken: cancellationToken);
        await NotifyMembersAsync(payout.VendorId, SettlementEmailKinds.PayoutFailed, $"Payout {payout.Number} failed",
            $"<p>Payout <strong>{Html(payout.Number)}</strong> of {Money(payout.Amount, payout.CurrencyCode)} could not be sent: {Html(text)}</p>" +
            "<p>Please check your bank account details. The money is back in your balance and goes in the next payout.</p>", cancellationToken);
        return CatalogResult.Success((await store.GetPayoutAsync(id, cancellationToken))!);
    }

    public async Task<string> ExportPendingPayoutsCsvAsync(int actorCustomerId, CancellationToken cancellationToken)
    {
        var pending = await store.ListPendingPayoutsAsync(cancellationToken);
        var csv = new StringBuilder("Payout,Shop,Bank,Account number,Account holder,Branch,Amount,Currency\r\n");
        foreach (var payout in pending)
        {
            csv.Append(string.Join(',',
                SettlementRules.Csv(payout.Number), SettlementRules.Csv(payout.ShopName), SettlementRules.Csv(payout.BankName),
                SettlementRules.Csv(protector.Unprotect(payout.AccountNumberProtected)), SettlementRules.Csv(payout.HolderName), SettlementRules.Csv(payout.Branch),
                payout.Amount.ToString(CultureInfo.InvariantCulture), SettlementRules.Csv(payout.CurrencyCode))).Append("\r\n");
        }
        await auditLog.WriteAsync("finance.payout_exported", actorCustomerId, details: new { payouts = pending.Count }, cancellationToken: cancellationToken);
        return csv.ToString();
    }

    // ---- Shop members ----

    public async Task<FinanceOverview> GetOverviewAsync(int vendorId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        var vendor = await vendors.GetAsync(vendorId, cancellationToken);
        var rate = vendor is null ? await store.GetDefaultRateAsync(cancellationToken) : await EffectiveRateAsync(vendor, cancellationToken);
        var totals = await store.GetTotalsAsync(vendorId, cancellationToken);

        // Delivered, not yet confirmed received: the shop may count on it, but it is not in the balance yet.
        var pending = (await store.GetDeliveredAsync(vendorId, cancellationToken)).Sum(o => SettlementRules.Sale(o, now, currency.DecimalPlaces).Amount);
        var bank = await store.GetBankAccountAsync(vendorId, cancellationToken);

        return new FinanceOverview(
            rate, pending, totals.Available, totals.InPayout, totals.Paid, currency.Code, options.Value.MinimumPayout,
            SettlementRules.BankStatus(bank, now), await store.GetMonthlyAsync(vendorId, 12, cancellationToken));
    }

    public Task<PagedResult<SettlementEntry>> GetEntriesAsync(EntryQuery query, CancellationToken cancellationToken) =>
        store.ListEntriesAsync(query with { Page = Math.Max(query.Page, 1), PageSize = Math.Clamp(query.PageSize, 1, SettlementLimits.MaxPageSize) }, cancellationToken);

    public async Task<CatalogResult<string>> ExportPayoutCsvAsync(int vendorId, int payoutId, CancellationToken cancellationToken)
    {
        var payout = await store.GetPayoutAsync(payoutId, cancellationToken);
        if (payout is null || payout.VendorId != vendorId) return CatalogResult.Error<string>(CatalogErrors.NotFound);

        var csv = new StringBuilder("Date,Type,Order,Goods,Commission,Amount,Note\r\n");
        foreach (var entry in await store.ListPayoutEntriesAsync(payoutId, cancellationToken))
        {
            csv.Append(string.Join(',',
                SettlementRules.Csv(entry.CreatedOnUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)), SettlementRules.Csv(SettlementRules.ToWire(entry.Kind)),
                SettlementRules.Csv(entry.ShopOrderNumber), entry.Gross.ToString(CultureInfo.InvariantCulture), entry.Commission.ToString(CultureInfo.InvariantCulture),
                entry.Amount.ToString(CultureInfo.InvariantCulture), SettlementRules.Csv(entry.Note))).Append("\r\n");
        }
        csv.Append(string.Join(',', SettlementRules.Csv("Total"), "", "", "", "", payout.Amount.ToString(CultureInfo.InvariantCulture), SettlementRules.Csv(payout.Number))).Append("\r\n");
        return CatalogResult.Success(csv.ToString());
    }

    public async Task<BankAccountView?> GetBankAccountAsync(int vendorId, CancellationToken cancellationToken) =>
        View(await store.GetBankAccountAsync(vendorId, cancellationToken), clock.UtcNow);

    public async Task<CatalogResult<BankAccountView>> SaveBankAccountAsync(
        int vendorId, SaveBankAccountCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var bank = SettlementBanks.All.FirstOrDefault(b => string.Equals(b, command.BankName?.Trim(), StringComparison.OrdinalIgnoreCase));
        var number = new string((command.AccountNumber ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        var holder = command.HolderName?.Trim() ?? string.Empty;
        var branch = command.Branch?.Trim();

        if (bank is null) errors["bankName"] = ["Choose a bank from the list."];
        if (number.Length is < SettlementLimits.MinAccountDigits or > SettlementLimits.MaxAccountDigits || !number.All(char.IsAsciiDigit))
            errors["accountNumber"] = [$"The account number has {SettlementLimits.MinAccountDigits} to {SettlementLimits.MaxAccountDigits} digits."];
        if (holder.Length is 0 or > 200) errors["holderName"] = ["Enter the account holder's name (at most 200 characters)."];
        if (branch?.Length > 200) errors["branch"] = ["The branch can have at most 200 characters."];
        if (errors.Count > 0) return CatalogResult.Failure<BankAccountView>(errors);

        // Changing where money goes needs the password of the person who does it (D10).
        var stored = await customers.GetLatestPasswordAsync(actorCustomerId, cancellationToken);
        if (string.IsNullOrEmpty(command.Password) || stored is null || !passwordHasher.Verify(command.Password, stored))
            return CatalogResult.Failure<BankAccountView>("password", "The password is not correct.");

        var vendor = await vendors.GetAsync(vendorId, cancellationToken);
        if (vendor is null || vendor.Deleted) return CatalogResult.Error<BankAccountView>(CatalogErrors.NotFound);

        var now = clock.UtcNow;
        var account = new BankAccount
        {
            VendorId = vendorId, BankName = bank!, AccountNumberProtected = protector.Protect(number), AccountLast4 = SettlementRules.Last4(number),
            HolderName = holder, Branch = string.IsNullOrEmpty(branch) ? null : branch, UpdatedByCustomerId = actorCustomerId, UpdatedOnUtc = now,
            UsableFromUtc = now.AddHours(Math.Max(0, options.Value.BankHoldHours))
        };
        await store.SaveBankAccountAsync(account, cancellationToken);

        // The full number is never written to the audit log or an email.
        await auditLog.WriteAsync("finance.bank_changed", actorCustomerId, entityType: "Vendor", entityId: vendorId,
            details: new { vendorId, bank, accountLast4 = account.AccountLast4, usableFromUtc = account.UsableFromUtc }, cancellationToken: cancellationToken);

        var changedBy = await customers.FindByIdAsync(actorCustomerId, cancellationToken);
        await NotifyMembersAsync(vendorId, SettlementEmailKinds.BankChanged, "The bank account of your shop changed",
            $"<p>The bank account for payouts of <strong>{Html(vendor.Name)}</strong> was changed by {Html(changedBy?.Email ?? "a member")}.</p>" +
            $"<p>New account: {Html(bank!)} ending {Html(account.AccountLast4)}. It can be used for payouts from {account.UsableFromUtc:yyyy-MM-dd HH:mm} UTC.</p>" +
            "<p>If this was not you, contact the platform at once.</p>", cancellationToken);
        return CatalogResult.Success(View(account, now)!);
    }

    // ---- Helpers ----

    /// <summary>The rate a new order of this shop would get: its own, else the default; the platform shop has none unless it has its own.</summary>
    private async Task<decimal> EffectiveRateAsync(Vendor vendor, CancellationToken cancellationToken)
    {
        if (await store.GetVendorRateAsync(vendor.Id, cancellationToken) is { } own) return own;
        return vendor.IsPlatformShop ? 0m : await store.GetDefaultRateAsync(cancellationToken);
    }

    private static BankAccountView? View(BankAccount? account, DateTime nowUtc) =>
        account is null
            ? null
            : new BankAccountView(account.BankName, SettlementRules.Masked(account.AccountLast4), account.HolderName, account.Branch, account.UpdatedOnUtc,
                account.UsableFromUtc, SettlementRules.IsUsable(account, nowUtc));

    /// <summary>Best effort: a failure to queue an email never undoes the change that caused it.</summary>
    private async Task NotifyMembersAsync(int vendorId, string kind, string subject, string html, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var member in await members.ListAsync(vendorId, cancellationToken))
                await emailQueue.EnqueueAsync(kind, new EmailMessage(member.Email, subject, html), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(exception, kind, vendorId);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Percent(decimal rate) => rate.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Money(decimal amount, string code) => amount.ToString("#,0.##", CultureInfo.InvariantCulture) + " " + Html(code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the {Kind} entry of source {SourceId}.")]
    private partial void LogRecordFailed(Exception exception, string kind, int sourceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not queue the {Kind} email of shop {VendorId}.")]
    private partial void LogNotifyFailed(Exception exception, string kind, int vendorId);
}
