# F05-S Settlement: commission, ledger and payouts

| | |
|---|---|
| **Module ID** | F05-S (slice of F05 "Seller/Shop/Vendor"): commission, the shop ledger, bank account and payouts. Follows `vendor-settlement-prd.md` |
| **Status** | Backend and Angular implemented. 931 service tests (35 new), 42 data tests, Angular build, lint and 37 unit tests pass. The migration, store SQL and the whole HTTP flow were run against SQL Server LocalDB (see 12). Not tried in a browser. |
| **Branch** | `feat/settlement/foundation` (backend and frontend) |
| **Depends on** | F05 (vendors, members), F18-A (orders), F21-A (returns), F29-A (jobs), F22-A (email queue), F15-A (discount funding) |
| **Unblocks** | F26 (finance reports), F21-B (refund deducted from settlement), F15-B |

## 1. Purpose

The platform collects the customer's money. For each completed shop order it keeps a **commission** and owes the rest to the shop. This slice records that debt as an immutable **ledger**, lets the shop see it, and lets an administrator **pay it out** (by hand, then confirm).

## 2. Decisions (the open questions Q1 to Q6 of the PRD)

| # | Decision |
|---|---|
| D1 | Q1: one default rate for the platform plus an optional rate per shop. The platform shop ("Nomori Official") has no commission unless an administrator sets a rate for it. No rate per category. |
| D2 | **The rate is frozen on the shop order when it is created** (`ShopOrder.CommissionRate`, filled by the order insert from the shop's rate or the default). Changing a rate never changes an existing order. |
| D3 | Q3: commission is charged on the goods only: `items - shop-funded discount`. Shipping is not charged commission and goes to the shop. A **platform-funded** discount does not reduce the shop's revenue (F15-A recorded who funds it); a **shop-funded** one does. **Tax is collected by the platform** and is not shop revenue (paying tax to the state is outside this slice). |
| D4 | `Revenue = (items - shop discount) - commission + shipping`, `commission = round(base x rate / 100)` to the currency's decimal places, half away from zero. |
| D5 | **The ledger is append-only.** No update or delete of an amount, ever. A mistake is fixed with an adjustment. The only change to a row is `PayoutId` (set when it is put in a payout, cleared when that payout fails). |
| D6 | **Entries are recorded by a job** (`settlement.record_entries`, every 5 minutes), not by a hook inside order or return code: it finds completed shop orders and refunded returns that have no entry yet and writes them. A unique index per shop order (sale) and per return (refund) makes it idempotent and safe on several nodes; a missed run is caught up by the next. An administrator can press "Run now". |
| D7 | A **refund** takes back the part of the shop's revenue that matches the part of the items the customer got back: `ratio = refund / (items - discount + tax)`; the shop gives back `ratio x (base - commission)`; the commission on that part is returned to the shop (it is not charged). Shipping is not refunded. A refund can be recorded before the sale (the order was not confirmed received yet): the net is right once the sale arrives. |
| D8 | Cancelled orders create no entry. |
| D9 | Q2/Q5: the administrator picks the cut-off date of a payout run (no fixed weekday). A shop is paid only when its available balance is at least `Settlement:MinimumPayout` (default 10 in the primary currency) and it has a bank account that passed the hold. Otherwise its balance stays. |
| D10 | Q4: a new or changed bank account is usable for payouts only after `Settlement:BankHoldHours` (default 48). Changing it needs the signed-in person's password, and every member of the shop gets an email with the last 4 digits. |
| D11 | The account number is stored **encrypted** (ASP.NET data protection); the shop sees only the last 4 digits. The administrator's payout CSV contains the full number, because the transfer needs it; exporting is audited. |
| D12 | Q6: any member of a shop may manage its bank account (the PRD keeps members equal). The password re-entry, the email and the hold are the protection. |
| D13 | A payout run is one transaction **per shop**: the entries up to the cut-off that belong to no payout are claimed with one `UPDATE`, the sum is taken from the claimed rows, and the payout is written; if the sum is under the minimum the transaction is rolled back. A row can never be in two payouts. |
| D14 | Payout statuses: `pending`, `paid` (with the bank reference), `failed` (with the reason; its entries return to the balance). Each change is a compare-and-set. |
| D15 | A shop that is deactivated still sees its finance and is still paid (FR-12). |

## 3. Actors and authorization matrix

| Action | Customer | Shop member | Administrator (`finance.manage`) |
|---|---|---|---|
| See balance, ledger, payouts, rate of own shop | no | Yes (own shop; another shop is `404`) | all shops |
| Manage the bank account of a shop | no | Yes (own shop, with password) | no (cannot see the full number, cannot change it) |
| Set default or shop commission rate | no | no | Yes |
| Create an adjustment | no | no | Yes |
| Create payouts, mark paid, mark failed, export | no | download own payout sheet | Yes |

New permission `finance.manage` (category "Finance"), given to `Administrator` by the migration.

## 4. Included behavior

- Commission: default rate (0 to 50, two decimals), per-shop rate (set or removed, with a note), history of changes; emails to the shop's members when its rate changes.
- Ledger entries: `sale`, `refund`, `adjustment`; balances: **pending** (revenue of delivered shop orders not yet completed, for information), **available** (entries not in a payout), **in payout** (pending payouts), **paid** (paid payouts).
- Bank account: set/replace with password, masked read, hold.
- Payouts: create run, list, detail with entries, mark paid, mark failed, CSV of a run (admin) and CSV of one payout (shop).
- Monthly totals (sales, commission) for a shop and for the platform.
- Emails (queued): rate changed, bank account changed, payout paid, payout failed.
- Audit: `finance.rate_changed`, `finance.bank_changed`, `finance.adjusted`, `finance.payout_created`, `finance.payout_paid`, `finance.payout_failed`, `finance.payout_exported`.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Commission per category, other fees (payment fee, ads) | F05-S-B |
| Automatic bank transfer, bank API | F19 / F05-S-B |
| Tax remittance, invoices | F07-E-B / F26 |
| Reconciliation report "customer money = shop revenue + commission + refunds" | F26 |
| A fixed weekly payout schedule and an automatic run | F05-S-B / F29-B |
| Separate permission for who in the shop may change the bank account | F05 roles |
| Hold of money for a complaint period; chargebacks | F05-S-B |
| Deducting a platform refund from the shop when the order was refunded without a return | F21-B |

## 6. Source map

nopCommerce has no commission or payout (a vendor only gets a name, a fee-less product list and order visibility; `Vendor` has no balance). The ledger, payout and bank account are Nomori additions, as the feature map says ("optional commission and payout account references", F05).

## 7. Data model (migration `202611010001 SettlementMigration`)

- `ShopOrder.CommissionRate decimal(5,2) not null default 0` (D2).
- `CommissionSetting(Id = 1, DefaultRate decimal(5,2), UpdatedOnUtc)` one row, check `0 <= rate <= 50`.
- `VendorCommission(VendorId PK, Rate decimal(5,2), Note, UpdatedOnUtc)`.
- `CommissionHistory(Id, VendorId null, OldRate null, NewRate null, Note, ChangedByCustomerId, ChangedOnUtc)`.
- `SettlementEntry(Id bigint, VendorId, Kind 0..2, ShopOrderId null, ReturnRequestId null, ShopOrderNumber null, Gross, Commission, Amount, CurrencyCode, Note, PayoutId null, CreatedByCustomerId null, CreatedOnUtc)`; unique filtered index `(ShopOrderId) WHERE Kind = 0`, unique filtered `(ReturnRequestId) WHERE Kind = 1`; indexes on `(VendorId, PayoutId)` and `(VendorId, CreatedOnUtc)`.
- `VendorBankAccount(VendorId PK, BankName, AccountNumberProtected, AccountLast4, HolderName, Branch, UpdatedByCustomerId, UpdatedOnUtc, UsableFromUtc)`.
- `SettlementPayout(Id, Number, VendorId, Status 0..2, Amount, CurrencyCode, EntryCount, CutoffUtc, BankName, AccountNumberProtected, AccountLast4, HolderName, Branch, Reference null, FailureReason null, CreatedByCustomerId, ResolvedByCustomerId null, CreatedOnUtc, UpdatedOnUtc)`; check `Amount > 0`.
- Permission `finance.manage`.

## 8. Service contracts

```text
SettlementRules   pure: Commission(base, rate, places), Revenue(shopOrder, rate, places), RefundEntry(shopOrder, rate, refund, places),
                  ValidRate, Last4, MaskedNumber, IsUsable(bank, now, hours), NumberFor(payout)
ISettlementStore  commission rows, unrecorded sales and refunds, TryInsertEntry, entries and totals, bank account, payout create / list /
                  mark (compare-and-set), balances, monthly totals
ISettlementService  admin: rates, adjustment, balances, payouts, CSV; shop: overview, ledger, payouts, bank account
IScheduledJob     settlement.record_entries
```

Business codes (`409`): `finance.no_bank_account`, `finance.payout_invalid_transition`. Field errors: `rate`, `amount`, `reason`, `password`, `bankName`, `accountNumber`, `holderName`, `reference`.

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/vendors/{id}/finance` | member: overview (balances, rate, bank, months) |
| 2 | `GET` | `/api/v1/vendors/{id}/finance/ledger?kind&from&to&search&page` | member |
| 3 | `GET` | `/api/v1/vendors/{id}/finance/payouts` , `/payouts/{payoutId}` , `/payouts/{payoutId}/csv` | member |
| 4 | `GET`/`PUT` | `/api/v1/vendors/{id}/finance/bank-account` | member (`PUT` needs CSRF and the password) |
| 5 | `GET` | `/api/v1/finance/banks` | signed in |
| 6 | `GET` | `/api/v1/admin/finance/commission` | admin: default, shop overrides, history |
| 7 | `PUT` | `/api/v1/admin/finance/commission/default` , `/commission/vendors/{id}` , `DELETE` the latter | admin |
| 8 | `GET` | `/api/v1/admin/finance/balances` , `/monthly` | admin |
| 9 | `POST` | `/api/v1/admin/finance/adjustments` | admin |
| 10 | `POST` | `/api/v1/admin/finance/payouts` (create run) | admin |
| 11 | `GET` | `/api/v1/admin/finance/payouts` , `/payouts/{id}` , `/payouts/export.csv` | admin |
| 12 | `POST` | `/api/v1/admin/finance/payouts/{id}/paid` , `/failed` | admin |

## 10. Angular

Shop: `/vendor/finance` (overview, ledger tab, payouts tab, bank-account tab). Admin: `/admin/finance` (commission, balances, payouts tabs). States: loading, empty, saving, forbidden, `409`.

## 11. Events, jobs, cache

Job `settlement.record_entries` (5 min, batch 200). Emails go through the F22-A queue (kinds `finance.*`). No cache.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | commission rounding, revenue with and without shop discount and shipping, refund ratio, rate range, masking, hold |
| Recording | a completed order gives one sale entry; running twice gives one; a refunded return gives one refund entry; cancelled orders give none |
| Rates | range, history, emails to members |
| Bank account | wrong password refused, number never returned, hold, emails |
| Payouts | below minimum or no usable account skipped, a payout takes each entry once, paid/failed compare-and-set, failed returns the money |
| Authorization | a member sees only the shop it belongs to |
| Migration | version ordering |

Automated: rules and services with fakes. **Run once by hand against SQL Server LocalDB** (empty database, then cart, checkout, order to completed, return and refund, bank account, payouts, failure, parallel payout runs and the authorization checks, all through the HTTP API): the results matched the rules above, including four parallel payout runs producing exactly one payout. The Angular screens were only built, linted and unit-tested, not opened in a browser.

### Manual test guide

1. Run the migrator; `ShopOrder.CommissionRate`, `SettlementEntry` and the other tables exist.
2. As admin set the default rate to 10. Place an order for a shop, confirm, ship, deliver; as the customer confirm receipt. Run `settlement.record_entries`: one `sale` entry; commission is 10 percent of the items; revenue adds the shipping fee.
3. Change the default to 20: the earlier order keeps 10.
4. Request, approve, receive and refund a return: one `refund` entry that is negative.
5. As a shop member set the bank account without the right password: refused. With it: saved, hold of 48 hours, members emailed (see `/admin/emails`).
6. As admin create a payout run: the shop is skipped while the bank account is on hold (set `Settlement:BankHoldHours` to 0 to test), then paid.
7. Mark the payout failed: the balance is back. Mark another paid: reference saved.
8. A member of another shop opens `/api/v1/vendors/{id}/finance`: `404`.

> Deployment note: the bank account number is encrypted with ASP.NET data protection. The key ring must be kept (shared and persisted) on every environment that is not a developer machine; with lost keys the stored numbers cannot be read. Configure the key storage before the first production bank account is saved.

## 13. Rollout and compatibility

Run the migrator before the API. Orders created before the migration have commission 0 and are not changed. Entries are recorded for **all** completed orders and refunded returns that exist, at the next job runs (200 per run); set the default rate first if old orders should carry it (they will not: their frozen rate is 0). The platform shop is paid like any other shop, so give it a bank account or leave its balance.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Commission per category, other fees | F05-S-B |
| Weekly automatic run, holds for complaint period | F05-S-B / F29-B |
| Reconciliation report | F26 |
| Permission to change bank account within the shop | F05 |
| Refund without a return (admin payment refund) does not touch the ledger | F21-B |
