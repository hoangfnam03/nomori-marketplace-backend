using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Settlement;

namespace Nomori.Marketplace.Data.Settlement;

public sealed class SqlSettlementStore(IOptions<DatabaseOptions> options) : ISettlementStore
{
    private const string OrderColumns =
        "s.Id, s.Number, s.VendorId, s.Subtotal, s.ShippingFee, s.DiscountAmount, s.DiscountFunding, s.TaxAmount, s.CommissionRate, o.CurrencyCode";

    private const string EntryColumns =
        "e.Id, e.VendorId, e.Kind, e.ShopOrderId, e.ReturnRequestId, e.ShopOrderNumber, e.Gross, e.Commission, e.Amount, e.CurrencyCode, e.Note, e.PayoutId, " +
        "e.CreatedByCustomerId, e.CreatedOnUtc";

    private const string PayoutColumns =
        "p.Id, p.Number, p.VendorId, v.Name, p.Status, p.Amount, p.CurrencyCode, p.EntryCount, p.CutoffUtc, p.BankName, p.AccountNumberProtected, p.AccountLast4, " +
        "p.HolderName, p.Branch, p.Reference, p.FailureReason, p.CreatedByCustomerId, p.ResolvedByCustomerId, p.CreatedOnUtc, p.UpdatedOnUtc";

    // ---- Commission ----

    public async Task<decimal> GetDefaultRateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DefaultRate FROM CommissionSetting WHERE Id = 1";
        return await cmd.ExecuteScalarAsync(cancellationToken) is decimal rate ? rate : 0m;
    }

    public async Task<decimal?> GetVendorRateAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Rate FROM VendorCommission WHERE VendorId = @VendorId";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is decimal rate ? rate : null;
    }

    public async Task SetDefaultRateAsync(decimal rate, string? note, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        decimal? old;
        await using (var read = Command(connection, transaction, "SELECT DefaultRate FROM CommissionSetting WITH (UPDLOCK) WHERE Id = 1"))
            old = await read.ExecuteScalarAsync(cancellationToken) as decimal?;

        await using (var update = Command(connection, transaction, "UPDATE CommissionSetting SET DefaultRate = @Rate, UpdatedOnUtc = @Now WHERE Id = 1"))
        {
            update.Parameters.AddWithValue("@Rate", rate);
            Add(update, "@Now", nowUtc);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddHistoryAsync(connection, transaction, null, old, rate, note, actorCustomerId, nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetVendorRateAsync(int vendorId, decimal? rate, string? note, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        decimal? old;
        await using (var read = Command(connection, transaction, "SELECT Rate FROM VendorCommission WITH (UPDLOCK, HOLDLOCK) WHERE VendorId = @VendorId"))
        {
            read.Parameters.AddWithValue("@VendorId", vendorId);
            old = await read.ExecuteScalarAsync(cancellationToken) as decimal?;
        }

        if (rate is null)
        {
            await using var delete = Command(connection, transaction, "DELETE FROM VendorCommission WHERE VendorId = @VendorId");
            delete.Parameters.AddWithValue("@VendorId", vendorId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var upsert = Command(connection, transaction, """
                UPDATE VendorCommission SET Rate = @Rate, Note = @Note, UpdatedOnUtc = @Now WHERE VendorId = @VendorId;
                IF @@ROWCOUNT = 0 INSERT INTO VendorCommission (VendorId, Rate, Note, UpdatedOnUtc) VALUES (@VendorId, @Rate, @Note, @Now);
                """);
            upsert.Parameters.AddWithValue("@VendorId", vendorId);
            upsert.Parameters.AddWithValue("@Rate", rate.Value);
            upsert.Parameters.AddWithValue("@Note", (object?)note ?? DBNull.Value);
            Add(upsert, "@Now", nowUtc);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddHistoryAsync(connection, transaction, vendorId, old, rate, note, actorCustomerId, nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AddHistoryAsync(
        SqlConnection connection, SqlTransaction transaction, int? vendorId, decimal? oldRate, decimal? newRate, string? note, int actor, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var cmd = Command(connection, transaction, """
            INSERT INTO CommissionHistory (VendorId, OldRate, NewRate, Note, ChangedByCustomerId, ChangedOnUtc)
            VALUES (@VendorId, @Old, @New, @Note, @Actor, @Now)
            """);
        cmd.Parameters.AddWithValue("@VendorId", (object?)vendorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Old", (object?)oldRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@New", (object?)newRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Actor", actor);
        Add(cmd, "@Now", now);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CommissionOverview> GetCommissionAsync(int historyTake, CancellationToken cancellationToken)
    {
        var rate = await GetDefaultRateAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);

        var overrides = new List<VendorCommission>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.VendorId, v.Name, c.Rate, c.Note, c.UpdatedOnUtc
                FROM VendorCommission c INNER JOIN Vendor v ON v.Id = c.VendorId ORDER BY v.Name
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                overrides.Add(new VendorCommission(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2), NullableString(reader, 3), Utc(reader.GetDateTime(4))));
        }

        var history = new List<CommissionChange>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TOP (@Take) h.Id, h.VendorId, v.Name, h.OldRate, h.NewRate, h.Note, h.ChangedByCustomerId, h.ChangedOnUtc
                FROM CommissionHistory h LEFT JOIN Vendor v ON v.Id = h.VendorId ORDER BY h.Id DESC
                """;
            cmd.Parameters.AddWithValue("@Take", historyTake);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                history.Add(new CommissionChange(
                    reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), NullableString(reader, 2),
                    reader.IsDBNull(3) ? null : reader.GetDecimal(3), reader.IsDBNull(4) ? null : reader.GetDecimal(4), NullableString(reader, 5),
                    reader.GetInt32(6), Utc(reader.GetDateTime(7))));
        }
        return new CommissionOverview(rate, overrides, history);
    }

    // ---- Recording ----

    public async Task<IReadOnlyList<SettlementOrderRow>> FindUnrecordedSalesAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP (@Take) {OrderColumns}
            FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId
            WHERE s.Status = 4 AND NOT EXISTS (SELECT 1 FROM SettlementEntry e WHERE e.ShopOrderId = s.Id AND e.Kind = 0)
            ORDER BY s.Id
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SettlementOrderRow>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadOrder(reader, 0));
        return rows;
    }

    public async Task<IReadOnlyList<SettlementRefundRow>> FindUnrecordedRefundsAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP (@Take) r.Id, r.RefundAmount, {OrderColumns}
            FROM ReturnRequest r
            INNER JOIN ShopOrder s ON s.Id = r.ShopOrderId
            INNER JOIN CustomerOrder o ON o.Id = s.OrderId
            WHERE r.Status = 4 AND NOT EXISTS (SELECT 1 FROM SettlementEntry e WHERE e.ReturnRequestId = r.Id AND e.Kind = 1)
            ORDER BY r.Id
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SettlementRefundRow>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new SettlementRefundRow(reader.GetInt32(0), reader.GetDecimal(1), ReadOrder(reader, 2)));
        return rows;
    }

    public async Task<bool> TryInsertEntryAsync(SettlementEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await InsertEntryAsync(entry, cancellationToken);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // The unique index of the sale or refund: someone recorded it first.
            return false;
        }
    }

    public Task<long> InsertAdjustmentAsync(SettlementEntry entry, CancellationToken cancellationToken) => InsertEntryAsync(entry, cancellationToken);

    private async Task<long> InsertEntryAsync(SettlementEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SettlementEntry (VendorId, Kind, ShopOrderId, ReturnRequestId, ShopOrderNumber, Gross, Commission, Amount, CurrencyCode, Note, CreatedByCustomerId, CreatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@VendorId, @Kind, @ShopOrderId, @ReturnRequestId, @ShopOrderNumber, @Gross, @Commission, @Amount, @CurrencyCode, @Note, @CreatedBy, @Created)
            """;
        cmd.Parameters.AddWithValue("@VendorId", entry.VendorId);
        cmd.Parameters.AddWithValue("@Kind", (int)entry.Kind);
        cmd.Parameters.AddWithValue("@ShopOrderId", (object?)entry.ShopOrderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReturnRequestId", (object?)entry.ReturnRequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ShopOrderNumber", (object?)entry.ShopOrderNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Gross", entry.Gross);
        cmd.Parameters.AddWithValue("@Commission", entry.Commission);
        cmd.Parameters.AddWithValue("@Amount", entry.Amount);
        cmd.Parameters.AddWithValue("@CurrencyCode", entry.CurrencyCode);
        cmd.Parameters.AddWithValue("@Note", (object?)entry.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedByCustomerId ?? DBNull.Value);
        Add(cmd, "@Created", entry.CreatedOnUtc);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    // ---- Reads ----

    public async Task<VendorTotals> GetTotalsAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN e.PayoutId IS NULL THEN e.Amount END), 0),
                   COALESCE(SUM(CASE WHEN p.Status = 0 THEN e.Amount END), 0),
                   COALESCE(SUM(CASE WHEN p.Status = 1 THEN e.Amount END), 0)
            FROM SettlementEntry e LEFT JOIN SettlementPayout p ON p.Id = e.PayoutId
            WHERE e.VendorId = @VendorId
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new VendorTotals(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
    }

    public async Task<IReadOnlyList<SettlementOrderRow>> GetDeliveredAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {OrderColumns}
            FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId
            WHERE s.VendorId = @VendorId AND s.Status = 3
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SettlementOrderRow>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadOrder(reader, 0));
        return rows;
    }

    public async Task<PagedResult<SettlementEntry>> ListEntriesAsync(EntryQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string where = """
            WHERE e.VendorId = @VendorId
              AND (@Kind IS NULL OR e.Kind = @Kind)
              AND (@From IS NULL OR e.CreatedOnUtc >= @From)
              AND (@To IS NULL OR e.CreatedOnUtc < @To)
              AND (@Search IS NULL OR e.ShopOrderNumber LIKE @Search ESCAPE '\')
            """;

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("@VendorId", query.VendorId);
            cmd.Parameters.AddWithValue("@Kind", query.Kind is { } kind ? (object)(int)kind : DBNull.Value);
            cmd.Parameters.Add(new SqlParameter("@From", SqlDbType.DateTime2) { Value = (object?)query.FromUtc ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@To", SqlDbType.DateTime2) { Value = (object?)query.ToUtc ?? DBNull.Value });
            cmd.Parameters.AddWithValue("@Search", string.IsNullOrWhiteSpace(query.Search) ? DBNull.Value : (object)("%" + EscapeLike(query.Search.Trim()) + "%"));
        }

        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM SettlementEntry e {where}";
            Bind(count);
            total = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {EntryColumns} FROM SettlementEntry e {where} ORDER BY e.Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        Bind(cmd);
        cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@Take", query.PageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<SettlementEntry>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadEntry(reader));
        return new PagedResult<SettlementEntry>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<SettlementEntry>> ListPayoutEntriesAsync(int payoutId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {EntryColumns} FROM SettlementEntry e WHERE e.PayoutId = @PayoutId ORDER BY e.Id";
        cmd.Parameters.AddWithValue("@PayoutId", payoutId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<SettlementEntry>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadEntry(reader));
        return items;
    }

    public async Task<IReadOnlyList<MonthlyTotal>> GetMonthlyAsync(int? vendorId, int months, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The commission figure is what the platform kept: commission of sales minus the commission given back by refunds.
        cmd.CommandText = """
            SELECT CONVERT(char(7), e.CreatedOnUtc, 120) AS Month,
                   COALESCE(SUM(CASE WHEN e.Kind = 0 THEN e.Gross END), 0),
                   COALESCE(SUM(CASE WHEN e.Kind = 0 THEN e.Commission WHEN e.Kind = 1 THEN -e.Commission END), 0),
                   COALESCE(SUM(CASE WHEN e.Kind = 1 THEN -e.Amount END), 0)
            FROM SettlementEntry e
            WHERE (@VendorId IS NULL OR e.VendorId = @VendorId)
              AND e.CreatedOnUtc >= DATEADD(month, -(@Months - 1), DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1))
            GROUP BY CONVERT(char(7), e.CreatedOnUtc, 120)
            ORDER BY Month DESC
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId is { } id ? (object)id : DBNull.Value);
        cmd.Parameters.AddWithValue("@Months", months);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<MonthlyTotal>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new MonthlyTotal(reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3)));
        return rows;
    }

    public async Task<PagedResult<VendorBalance>> ListBalancesAsync(BalanceQuery query, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string from = """
            FROM Vendor v
            OUTER APPLY (
                SELECT COALESCE(SUM(CASE WHEN e.PayoutId IS NULL THEN e.Amount END), 0) AS Available,
                       COALESCE(SUM(CASE WHEN p.Status = 0 THEN e.Amount END), 0) AS InPayout,
                       COALESCE(SUM(CASE WHEN p.Status = 1 THEN e.Amount END), 0) AS Paid,
                       COUNT(e.Id) AS Cnt
                FROM SettlementEntry e LEFT JOIN SettlementPayout p ON p.Id = e.PayoutId
                WHERE e.VendorId = v.Id) t
            LEFT JOIN VendorBankAccount b ON b.VendorId = v.Id
            WHERE v.Deleted = 0 AND (t.Cnt > 0 OR b.VendorId IS NOT NULL)
              AND (@Search IS NULL OR v.Name LIKE @Search ESCAPE '\')
              AND (@Bank IS NULL OR @Bank = CASE WHEN b.VendorId IS NULL THEN 'missing' WHEN b.UsableFromUtc <= @Now THEN 'ready' ELSE 'hold' END)
            """;

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("@Search", string.IsNullOrWhiteSpace(query.Search) ? DBNull.Value : (object)("%" + EscapeLike(query.Search.Trim()) + "%"));
            cmd.Parameters.AddWithValue("@Bank", string.IsNullOrWhiteSpace(query.Bank) ? DBNull.Value : query.Bank);
            Add(cmd, "@Now", nowUtc);
        }

        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) {from}";
            Bind(count);
            total = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT v.Id, v.Name, t.Available, t.InPayout, t.Paid,
                   CASE WHEN b.VendorId IS NULL THEN 'missing' WHEN b.UsableFromUtc <= @Now THEN 'ready' ELSE 'hold' END, b.AccountLast4
            {from}
            ORDER BY t.Available DESC, v.Id
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            """;
        Bind(cmd);
        cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@Take", query.PageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<VendorBalance>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new VendorBalance(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4),
                reader.GetString(5), NullableString(reader, 6), string.Empty));
        return new PagedResult<VendorBalance>(items, total, query.Page, query.PageSize);
    }

    // ---- Bank account ----

    public async Task<BankAccount?> GetBankAccountAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT VendorId, BankName, AccountNumberProtected, AccountLast4, HolderName, Branch, UpdatedByCustomerId, UpdatedOnUtc, UsableFromUtc
            FROM VendorBankAccount WHERE VendorId = @VendorId
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new BankAccount
        {
            VendorId = reader.GetInt32(0), BankName = reader.GetString(1), AccountNumberProtected = reader.GetString(2), AccountLast4 = reader.GetString(3),
            HolderName = reader.GetString(4), Branch = NullableString(reader, 5), UpdatedByCustomerId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            UpdatedOnUtc = Utc(reader.GetDateTime(7)), UsableFromUtc = Utc(reader.GetDateTime(8))
        };
    }

    public async Task SaveBankAccountAsync(BankAccount account, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE VendorBankAccount SET BankName = @BankName, AccountNumberProtected = @Protected, AccountLast4 = @Last4, HolderName = @Holder,
                Branch = @Branch, UpdatedByCustomerId = @By, UpdatedOnUtc = @Updated, UsableFromUtc = @Usable
            WHERE VendorId = @VendorId;
            IF @@ROWCOUNT = 0
                INSERT INTO VendorBankAccount (VendorId, BankName, AccountNumberProtected, AccountLast4, HolderName, Branch, UpdatedByCustomerId, UpdatedOnUtc, UsableFromUtc)
                VALUES (@VendorId, @BankName, @Protected, @Last4, @Holder, @Branch, @By, @Updated, @Usable);
            """;
        cmd.Parameters.AddWithValue("@VendorId", account.VendorId);
        cmd.Parameters.AddWithValue("@BankName", account.BankName);
        cmd.Parameters.AddWithValue("@Protected", account.AccountNumberProtected);
        cmd.Parameters.AddWithValue("@Last4", account.AccountLast4);
        cmd.Parameters.AddWithValue("@Holder", account.HolderName);
        cmd.Parameters.AddWithValue("@Branch", (object?)account.Branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@By", (object?)account.UpdatedByCustomerId ?? DBNull.Value);
        Add(cmd, "@Updated", account.UpdatedOnUtc);
        Add(cmd, "@Usable", account.UsableFromUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- Payouts ----

    public async Task<IReadOnlyList<int>> VendorsWithEntriesAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT VendorId FROM SettlementEntry WHERE PayoutId IS NULL AND CreatedOnUtc <= @Cutoff ORDER BY VendorId";
        Add(cmd, "@Cutoff", cutoffUtc);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<Payout?> TryCreatePayoutAsync(
        int vendorId, DateTime cutoffUtc, decimal minimum, BankAccount bank, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // The range lock keeps every other writer away from this shop's unclaimed entries until the commit.
            int count;
            decimal sum;
            string currency;
            await using (var read = Command(connection, transaction, """
                SELECT COUNT(*), COALESCE(SUM(Amount), 0), COALESCE(MAX(CurrencyCode), '')
                FROM SettlementEntry WITH (UPDLOCK, HOLDLOCK)
                WHERE VendorId = @VendorId AND PayoutId IS NULL AND CreatedOnUtc <= @Cutoff
                """))
            {
                read.Parameters.AddWithValue("@VendorId", vendorId);
                Add(read, "@Cutoff", cutoffUtc);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                count = reader.GetInt32(0);
                sum = reader.GetDecimal(1);
                currency = reader.GetString(2);
            }

            if (count == 0 || sum <= 0m || sum < minimum)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            int id;
            await using (var insert = Command(connection, transaction, """
                INSERT INTO SettlementPayout (Number, VendorId, Status, Amount, CurrencyCode, EntryCount, CutoffUtc, BankName, AccountNumberProtected, AccountLast4,
                    HolderName, Branch, CreatedByCustomerId, CreatedOnUtc, UpdatedOnUtc)
                OUTPUT INSERTED.Id
                VALUES (REPLACE(CONVERT(nvarchar(36), NEWID()), '-', ''), @VendorId, 0, @Amount, @Currency, @Count, @Cutoff, @BankName, @Protected, @Last4,
                    @Holder, @Branch, @Actor, @Now, @Now)
                """))
            {
                insert.Parameters.AddWithValue("@VendorId", vendorId);
                insert.Parameters.AddWithValue("@Amount", sum);
                insert.Parameters.AddWithValue("@Currency", currency);
                insert.Parameters.AddWithValue("@Count", count);
                Add(insert, "@Cutoff", cutoffUtc);
                insert.Parameters.AddWithValue("@BankName", bank.BankName);
                insert.Parameters.AddWithValue("@Protected", bank.AccountNumberProtected);
                insert.Parameters.AddWithValue("@Last4", bank.AccountLast4);
                insert.Parameters.AddWithValue("@Holder", bank.HolderName);
                insert.Parameters.AddWithValue("@Branch", (object?)bank.Branch ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Actor", actorCustomerId);
                Add(insert, "@Now", nowUtc);
                id = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            await using (var number = Command(connection, transaction, "UPDATE SettlementPayout SET Number = @Number WHERE Id = @Id"))
            {
                number.Parameters.AddWithValue("@Number", SettlementRules.NumberFor(nowUtc, id));
                number.Parameters.AddWithValue("@Id", id);
                await number.ExecuteNonQueryAsync(cancellationToken);
            }

            int claimed;
            await using (var claim = Command(connection, transaction, """
                UPDATE SettlementEntry SET PayoutId = @Id
                WHERE VendorId = @VendorId AND PayoutId IS NULL AND CreatedOnUtc <= @Cutoff
                """))
            {
                claim.Parameters.AddWithValue("@Id", id);
                claim.Parameters.AddWithValue("@VendorId", vendorId);
                Add(claim, "@Cutoff", cutoffUtc);
                claimed = await claim.ExecuteNonQueryAsync(cancellationToken);
            }
            if (claimed != count) throw new InvalidOperationException("The entries of the payout changed while it was being made.");

            await transaction.CommitAsync(cancellationToken);
            return await GetPayoutAsync(id, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<Payout?> GetPayoutAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {PayoutColumns} FROM SettlementPayout p INNER JOIN Vendor v ON v.Id = p.VendorId WHERE p.Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPayout(reader) : null;
    }

    public async Task<PagedResult<Payout>> ListPayoutsAsync(PayoutQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string where = "WHERE (@VendorId IS NULL OR p.VendorId = @VendorId) AND (@Status IS NULL OR p.Status = @Status)";

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("@VendorId", query.VendorId is { } v ? (object)v : DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", query.Status is { } s ? (object)(int)s : DBNull.Value);
        }

        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM SettlementPayout p {where}";
            Bind(count);
            total = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PayoutColumns} FROM SettlementPayout p INNER JOIN Vendor v ON v.Id = p.VendorId {where}
            ORDER BY p.Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            """;
        Bind(cmd);
        cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@Take", query.PageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Payout>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadPayout(reader));
        return new PagedResult<Payout>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<Payout>> ListPendingPayoutsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {PayoutColumns} FROM SettlementPayout p INNER JOIN Vendor v ON v.Id = p.VendorId WHERE p.Status = 0 ORDER BY p.Id";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Payout>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadPayout(reader));
        return items;
    }

    public async Task<bool> TryMarkPaidAsync(int id, string reference, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE SettlementPayout SET Status = 1, Reference = @Reference, ResolvedByCustomerId = @Actor, UpdatedOnUtc = @Now
            WHERE Id = @Id AND Status = 0
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Reference", reference);
        cmd.Parameters.AddWithValue("@Actor", actorCustomerId);
        Add(cmd, "@Now", nowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryMarkFailedAsync(int id, string reason, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = Command(connection, transaction, """
            UPDATE SettlementPayout SET Status = 2, FailureReason = @Reason, ResolvedByCustomerId = @Actor, UpdatedOnUtc = @Now
            WHERE Id = @Id AND Status = 0
            """))
        {
            update.Parameters.AddWithValue("@Id", id);
            update.Parameters.AddWithValue("@Reason", reason);
            update.Parameters.AddWithValue("@Actor", actorCustomerId);
            Add(update, "@Now", nowUtc);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // The money is the shop's again: its entries go back to the balance.
        await using (var release = Command(connection, transaction, "UPDATE SettlementEntry SET PayoutId = NULL WHERE PayoutId = @Id"))
        {
            release.Parameters.AddWithValue("@Id", id);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // ---- Mapping and plumbing ----

    private static SettlementOrderRow ReadOrder(SqlDataReader r, int o) => new(
        r.GetInt32(o), r.GetString(o + 1), r.GetInt32(o + 2), r.GetDecimal(o + 3), r.GetDecimal(o + 4), r.GetDecimal(o + 5), NullableString(r, o + 6),
        r.GetDecimal(o + 7), r.GetDecimal(o + 8), r.GetString(o + 9));

    private static SettlementEntry ReadEntry(SqlDataReader r) => new()
    {
        Id = r.GetInt64(0), VendorId = r.GetInt32(1), Kind = (EntryKind)r.GetInt32(2), ShopOrderId = r.IsDBNull(3) ? null : r.GetInt32(3),
        ReturnRequestId = r.IsDBNull(4) ? null : r.GetInt32(4), ShopOrderNumber = NullableString(r, 5), Gross = r.GetDecimal(6), Commission = r.GetDecimal(7),
        Amount = r.GetDecimal(8), CurrencyCode = r.GetString(9), Note = NullableString(r, 10), PayoutId = r.IsDBNull(11) ? null : r.GetInt32(11),
        CreatedByCustomerId = r.IsDBNull(12) ? null : r.GetInt32(12), CreatedOnUtc = Utc(r.GetDateTime(13))
    };

    private static Payout ReadPayout(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0), Number = r.GetString(1), VendorId = r.GetInt32(2), ShopName = r.GetString(3), Status = (PayoutStatus)r.GetInt32(4),
        Amount = r.GetDecimal(5), CurrencyCode = r.GetString(6), EntryCount = r.GetInt32(7), CutoffUtc = Utc(r.GetDateTime(8)), BankName = r.GetString(9),
        AccountNumberProtected = r.GetString(10), AccountLast4 = r.GetString(11), HolderName = r.GetString(12), Branch = NullableString(r, 13),
        Reference = NullableString(r, 14), FailureReason = NullableString(r, 15), CreatedByCustomerId = r.GetInt32(16),
        ResolvedByCustomerId = r.IsDBNull(17) ? null : r.GetInt32(17), CreatedOnUtc = Utc(r.GetDateTime(18)), UpdatedOnUtc = Utc(r.GetDateTime(19))
    };

    private static string? NullableString(SqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Add(SqlCommand cmd, string name, DateTime value) =>
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2) { Value = value });

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
