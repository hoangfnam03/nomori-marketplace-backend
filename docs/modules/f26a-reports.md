# F26-A Reports and dashboards

| | |
|---|---|
| **Module ID** | F26-A (slice of F26 "Reporting, audit, logs and admin insight"): read-only dashboards for shops and for the platform, and the settlement reconciliation report |
| **Status** | Backend and Angular implemented; 968 service tests (11 new) and 44 data tests pass, the Angular build, lint and 37 unit tests pass. Every query and the HTTP flow (shop and platform dashboards, operations, reconciliation, authorization) were run against SQL Server LocalDB. The screens were not opened in a browser. |
| **Branch** | `feat/reports/foundation` (backend and frontend) |
| **Depends on** | F18-A (orders), F21-A (returns), F05-S (ledger, payouts), F29-A (jobs), F22-A (email queue), F12-A (stock thresholds), F03 (permissions) |
| **Unblocks** | F26-B (exports, audit browser, search terms), F25 (affiliate reports) |

## 1. Purpose

Money and orders now exist but nobody can see them in one place. This slice adds **read-only** screens: a shop sees its own sales, returns, best sellers and low stock; the platform sees its sales, commission, what needs attention, and a **reconciliation report** that proves the money adds up (customer paid = shop revenue + commission + tax − platform-funded discounts).

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **No new business tables.** Every figure is a query over data that already exists. The only migration adds the permission. |
| D2 | **A shop sees only itself.** The shop id comes from the route and is checked against the membership on every call; another shop is `404`. Platform reports need the new permission `reports.view`. |
| D3 | **Days are UTC days** (F07-B, time zones, is deferred). A range is 1 to 92 days; the default is the last 30 days including today. The range is `[from, to]` inclusive of both dates; the queries use `[from, to + 1 day)`. |
| D4 | **"Sales" are shop orders that were not cancelled and whose payment is confirmed**, dated by the day the order was created. Sales = items less a discount the shop pays for (the same base as commission, F05-S D3). Cancelled orders are counted separately. |
| D5 | **Refunds are dated by the day the return was refunded.** The ledger figures of the reconciliation are dated by the day the line was written to the ledger (the job may write it a few minutes after the order was completed). |
| D6 | **Reconciliation identity:** for sale lines in the range, `customer paid = shop revenue + commission + tax - platform-funded discount`. The report shows every term and the difference, which must be 0; a non-zero difference is a defect to investigate, never something to fix in the report. It also shows completed orders and refunded returns that have no ledger line yet, and payouts whose amount differs from the sum of their lines. |
| D7 | **Missing days are filled with zeros** by the service so the chart has no gaps. |
| D8 | **Charts without a library.** The Angular screens draw bars with plain CSS; no chart package is added. |
| D9 | Reports are computed on request (no cache, no snapshot table). They are bounded by the 92-day range and by indexes that already exist; heavy or long-range reporting goes to F26-B with exports and background jobs. |

## 3. Authorization matrix

| Report | Customer | Shop member | Administrator with `reports.view` |
|---|---|---|---|
| Shop dashboard | no | own shop | any shop (admin route) |
| Platform dashboard and operations | no | no | Yes |
| Reconciliation | no | no | Yes |

New permission `reports.view` (category "Reports"), given to `Administrator`.

## 4. Included behavior

**Shop dashboard** (range): orders, units, sales, shipping, cancelled orders, returns opened, refunded amount; sales by day; top 10 products by revenue; orders by status (all time); products at or under their low-stock threshold (up to 20).

**Platform dashboard** (range): orders, merchandise value paid by customers, new customers, active shops, commission kept (net of refunds), refunded amount, cancelled orders; sales by day; top 10 shops.

**Operations** (now): payouts pending and their amount; open returns; shop orders waiting for the shop to confirm for more than 24 hours; jobs that failed or partly failed in the last 24 hours; failed emails; completed orders and refunded returns not yet in the ledger; products at or under their low-stock threshold.

**Reconciliation** (range): the identity of D6, the refund lines, the commission given back, and the three integrity counts.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| CSV or Excel export, scheduled reports, e-mailed reports | F26-B |
| Audit log browser, activity log, application log viewer, online customers | F26-B |
| Search term statistics, customer reports, bestseller by category | F26-B |
| Time zones, comparing two periods, forecasts | F07-B / F26-B |
| Alerts when a figure crosses a limit | F26-B |
| Per-product margin (cost price does not exist) | not planned |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `OrderReportService` (`GetOrderAverageReportLine`, `BestSellersReport`, `ProductsNeverSold`), Admin `HomeController` dashboard | Totals, best sellers | Per shop with membership scope; days in UTC; no plugin widgets |
| `ReportController` (low stock, bestsellers, never purchased) | Low stock | Seller sees only its own |
| `CustomerReportService` (new customers) | New customers | One figure on the platform dashboard |
| Reconciliation does not exist | | Nomori addition, because the platform holds the money |

## 7. Data model

Migration `202611100001 ReportsMigration`: adds the permission `reports.view` and gives it to `Administrator`. No table changes.

## 8. Contracts

```text
ReportRules       ResolveRange(from, to, today) -> (From, ToExclusive, Days) or an error; FillDays(from, days, rows)
IReportStore      shop summary, daily sales (shop or all), top products, low stock, status counts,
                  platform summary, top shops, operations, reconciliation
IReportService    GetShopDashboardAsync(vendorId, from, to), GetPlatformDashboardAsync(from, to),
                  GetOperationsAsync(), GetReconciliationAsync(from, to)
```

Field errors: `from`, `to` (not a date range, more than 92 days, or in the wrong order).

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/vendors/{vendorId}/reports/dashboard?from&to` | shop member |
| 2 | `GET` | `/api/v1/admin/reports/dashboard?from&to` | `reports.view` |
| 3 | `GET` | `/api/v1/admin/reports/operations` | `reports.view` |
| 4 | `GET` | `/api/v1/admin/reports/reconciliation?from&to` | `reports.view` |
| 5 | `GET` | `/api/v1/admin/reports/vendors/{vendorId}/dashboard?from&to` | `reports.view` |

## 10. Angular

`/vendor/reports` and `/admin/reports` (tabs: Overview, Operations, Reconciliation). Range picker with two dates and quick buttons (7, 30, 90 days). States: loading, empty (no sales), validation, forbidden, network error.

## 11. Events, jobs, cache

None.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | default range, inclusive end, limits, wrong order, days filled with zeros |
| Service | average order value, margin of the identity, difference computed, a shop's dashboard uses only its own id |
| Authorization (over HTTP) | a member sees only the own shop, another shop and a customer get `404`, an administrator without the permission gets `403` |
| SQL (by hand, done) | every query ran on SQL Server LocalDB; the identity gave 0 on a real completed order, a refunded return and a payout; not covered: orders with tax or a discount code, so the tax and discount terms were only checked by the unit tests of the formula |

### Manual test guide

1. Run the migrator. As admin open `/admin/reports`: the overview, operations and reconciliation tabs load.
2. Place, confirm, ship, deliver and complete an order; run the job `settlement.record_entries`; reconcile: difference 0.
3. Refund a return and run the job again: the refund lines and the commission given back appear.
4. As a shop member open `/vendor/reports`: only the own figures, best sellers, low stock.
5. As a member of another shop call `/api/v1/vendors/{id}/reports/dashboard` of the first shop: `404`.

## 13. Rollout and compatibility

Run the migrator before the API. Nothing is backfilled.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Exports, scheduled reports, audit browser | F26-B |
| Day boundaries in the shop's time zone | F07-B |
| Heavy ranges and a pre-aggregated table | F26-B |
