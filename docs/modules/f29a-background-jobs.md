# F29-A Background jobs and cleanup

| | |
|---|---|
| **Module ID** | F29-A (slice of F29 "Background work, events, cache, maintenance and observability"): a job runner and the first cleanup jobs |
| **Status** | Backend and Angular implemented; 782 service tests (45 new) and 38 data tests pass, the Angular build, lint and 30 unit tests pass. SQL stores, the migration, the hosted service and the screen have not been run against a real database (see 12). |
| **Branch** | `feat/background-jobs/foundation` (backend and frontend) |
| **Depends on** | F00 (runtime), F03 (permissions), F12-A (reservations), F16-A (cart), F17-A, F18-A (orders), F19-A/B (payments), F15-A (discount use) |
| **Unblocks** | F22 (email queue and reminders are jobs), F21 (refund follow-ups), F26 (job monitoring), F27 (import/export jobs) |
| **Feature map** | "jobs must be idempotent, lock-safe, monitored and permission-aware." |

## 1. Purpose

Several finished slices left work that nobody does: orders whose payment never came stay "awaiting payment" forever, expired stock holds and old payments pile up, carts are never cleaned. This slice adds the **runner** every later background task needs (schedule, lock, history, admin control) and the **first five jobs** that pay down those debts.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Jobs are code, schedules are data.** A job is a class with a stable name and a default interval. A row in `ScheduledTask` (created on start when missing) holds what an administrator may change: on or off, interval. The code is never replaced by data. |
| D2 | **Lock-safe by a lease in the database**, not by a process lock. Starting a job is one atomic `UPDATE ... WHERE not locked and due`; the winner holds a lease for a few minutes. Two API instances, or a manual run during a scheduled run, cannot run the same job twice. A crashed run frees the job when its lease ends. |
| D3 | **Every job is idempotent and works in small batches** (at most 200 items per run). Running it twice, or being cut off half-way, does no harm: the next run continues. One failing item never stops the others. |
| D4 | **Jobs reuse the services that already own the rule** (cancel an order, give a discount use back, void or refund a payment). A job contains no business rule of its own and never edits another module's tables, except the housekeeping deletes in D7. |
| D5 | **Money is checked after cancelling.** An unpaid order is cancelled first (stock goes back at once). Afterwards the job looks at the order's payments: one that turns out to be paid (a callback won the race) is refunded in full. A cancelled order never keeps customer money. |
| D6 | **Pending payments of cancelled orders are voided after a grace period (24 hours by default), not at once.** A gateway can still report a payment for a session the customer left open. Until then a late "captured" is refunded by F19-B (D4 there); after the grace period the session is voided. Payments of orders that are not fully cancelled (cash on delivery in progress) are never touched. |
| D7 | **Housekeeping may delete only rows that nothing reads any more**: closed stock holds past a retention period, cart lines nobody touched for a long time, job history past a retention period. Orders, payments, ledger rows and audit rows are never deleted by a job. |
| D8 | **Failures are visible.** Every run writes a history row (trigger, start, end, status, counts, short message). Status is `succeeded`, `partial` (some items failed) or `failed` (the job threw). The message never contains a stack trace; the full error goes to the log. |
| D9 | **Permission-aware.** Seeing and controlling jobs needs the new permission `jobs.manage` (Administrator by default). Manual runs and schedule changes are audited. A job acts as the system: its audit rows have no customer. |
| D10 | **The runner can be switched off by configuration** (`Jobs:Enabled`, default `true`), so a second API node, the migrator or a test run can serve requests without running jobs. Manual runs from the admin screen work either way. |
| D11 | **No new library.** The runner is a `BackgroundService` that wakes every few seconds and asks the database what is due. Hangfire, Quartz or a message broker would come with F22, if the queue needs them. |

## 3. Actors and authorization matrix

| Action | Customer | Shop member | Administrator (`jobs.manage`) | System |
|---|---|---|---|---|
| See jobs, their schedule and history | no | no | Yes | |
| Switch a job on or off, change its interval | no | no | Yes | |
| Run a job now | no | no | Yes | |
| Run due jobs | | | | Yes (hosted service) |

Anyone else gets `403` (`401` when not signed in).

## 4. Included behavior

- **Runner:** every `Jobs:PollSeconds` (default 30) the hosted service runs every job that is enabled and due. A run is leased (D2), timed, recorded and its next run is set to now plus the interval.
- **Manual run:** runs now, even when the job is switched off or not due, still respecting the lease (`409 job.already_running`).
- **Interval:** 1 to 10080 minutes (one week). Out of range is a field error.
- **Job `orders.expire_unpaid`** (every 5 min): orders still awaiting payment after `Jobs:AwaitingPaymentMinutes` (default 60) with at least one pending shop order are cancelled by the system ("Payment not completed in time"), stock goes back, the discount use is given back (D4), then D5.
- **Job `payments.void_stale`** (every 60 min): payments still pending or authorized, whose order is fully cancelled, last changed more than `Jobs:StalePaymentGraceHours` (default 24) ago, are voided (D6).
- **Job `inventory.purge_reservations`** (every 60 min): active holds past their expiry are closed as released; closed holds older than `Jobs:ReservationRetentionDays` (default 30) are deleted. Availability already ignores expired holds, so this changes no stock figure.
- **Job `cart.purge_stale`** (daily): cart lines untouched for `Jobs:CartRetentionDays` (default 90).
- **Job `jobs.purge_history`** (daily): run history older than `Jobs:RunRetentionDays` (default 30).
- **Screen:** `/admin/jobs` lists the jobs with schedule, last run, status, next run; edit interval and switch; "Run now"; history of one job.
- **Audit:** `job.run_manual`, `job.updated`, plus the events the reused services already write (`order.shop_order_changed`, `discount.released`, `payment.voided`, `payment.refunded`).

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Email queue, abandoned cart and pending order reminders | F22 |
| Order automation by time: auto-complete delivered orders, auto-deliver, auto-cancel unconfirmed orders (needs a business decision) | F29-B |
| Reconciliation of a checkout that died between taking stock and creating the order (needs a stock-hold reference per checkout) | F29-B |
| Orphan media clean-up, product publish schedule, currency rate refresh | F29-B |
| Cache and cache invalidation, distributed cache | F29-B |
| Cron expressions, per-job retries with back-off, job parameters set from the screen, running on a chosen node | F29-B |
| Alerts when a job keeps failing, dashboards, metrics | F26 |
| An external scheduler (Hangfire, Quartz) or a message broker | When F22 needs it |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `ScheduleTask` (`Seconds`, `Enabled`, `LastStartUtc`, `LastEndUtc`, `LastSuccessUtc`), `ScheduleTaskRunner`, `TaskThread` | Schedule, last run, run now | Interval in minutes; a database lease replaces nopCommerce's in-process lock; history is one row per run |
| `ClearCacheTask`, `DeleteGuestsTask`, `ResetLicenseCheckTask` | Clean-up tasks | Tasks that match Nomori's data (holds, carts, unpaid orders) |
| `ScheduleTaskController` | Admin grid | `/admin/jobs` |
| Order auto-cancel does not exist in core nopCommerce | Unpaid orders | `orders.expire_unpaid` |

## 7. Data model

Migration `202610220001 JobsMigration`:

`ScheduledTask`
- `Id` int identity.
- `SystemName` nvarchar(100), unique.
- `Enabled` bit, `IntervalMinutes` int with check 1..10080.
- `NextRunUtc` datetime2 null (null means due now), `LockedUntilUtc` datetime2 null, `LockOwner` nvarchar(100) null.
- `LastStartedUtc`, `LastFinishedUtc` datetime2 null, `LastStatus` nvarchar(20) null, `LastMessage` nvarchar(500) null.
- `UpdatedOnUtc` datetime2.

`ScheduledTaskRun`
- `Id` bigint identity, `TaskName` nvarchar(100), `TriggerKind` nvarchar(20) (`schedule` or `manual`), `StartedUtc`, `FinishedUtc` datetime2, `Status` nvarchar(20), `Processed` int, `Failed` int, `Message` nvarchar(500) null.
- Index `(TaskName, StartedUtc DESC)`; index `(StartedUtc)` for the clean-up.

Permission `jobs.manage` (category "Jobs") is added and given to `Administrator`, the same way as `payments.manage`.

Rows of `ScheduledTask` are created by the runner at start from the job classes (insert when missing). No other table changes.

## 8. Use cases and service contracts

```text
IScheduledJob         Name, Description, DefaultIntervalMinutes, RunAsync(ct) -> JobResult(Processed, Failed, Message)
JobRules              pure: ClampBatch, IsValidInterval, Status(result), NextRun(now, interval), Trim(message)
IJobStore             EnsureAsync(definitions); ListAsync; GetAsync; UpdateAsync(name, enabled, interval)
                      TryClaimAsync(name, owner, now, leaseUntil, force) -> bool   (atomic, D2)
                      CompleteAsync(name, owner, finish)                          (releases the lease, sets the next run)
                      AddRunAsync(run); GetRunsAsync(name, take); DueNamesAsync(now)
IMaintenanceStore     ExpiredAwaitingOrderIdsAsync(createdBefore, take)
                      PaymentIdsForOrderAsync(orderId)
                      StalePaymentIdsAsync(changedBefore, take)
                      CloseExpiredReservationsAsync(now); DeleteClosedReservationsAsync(before, take)
                      DeleteStaleCartLinesAsync(before, take); DeleteRunsAsync(before, take)
IJobService           GetJobsAsync, UpdateAsync(name, enabled, interval, actor), RunNowAsync(name, actor), GetRunsAsync(name), RunDueAsync(ct)
```

Business codes (`409`): `job.already_running`. Not found: an unknown job name. Field errors: `intervalMinutes`.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/admin/jobs` | All jobs with schedule and last run |
| 2 | `PUT` | `/api/v1/admin/jobs/{name}` | `{ enabled, intervalMinutes }`, CSRF |
| 3 | `POST` | `/api/v1/admin/jobs/{name}/run` | Runs now, answers the run; CSRF |
| 4 | `GET` | `/api/v1/admin/jobs/{name}/runs` | Latest 50 runs |

## 10. Angular

- `/admin/jobs` (permission `jobs.manage`): table of jobs, edit of interval and switch, "Run now" with a result line, history of the selected job.
- States: loading, empty, saving, `409` already running, network error.

## 11. Events, jobs, cache

This slice **is** the jobs part. No domain events and no cache yet (F29-B).

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Interval limits, batch clamp, status from counts, next run, message trimming |
| Runner | Runs due and enabled jobs only; skips a locked job; manual run ignores off and due but not the lock; a throwing job is recorded as `failed` and the lease is released; failures of items give `partial`; history row for every run; schedule update validates and audits |
| `orders.expire_unpaid` | Cancels only listed orders, gives the discount use back, refunds a payment that turned out to be paid, one failing order does not stop the next, nothing to do is a clean run |
| `payments.void_stale` | Voids pending and authorized payments, ignores paid, failed and voided, a provider that refuses is counted as failed |
| Housekeeping jobs | They call the store with the cut-off dates from the options and report the counts |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (the lease `UPDATE`, the joins, the deletes), the hosted service, HTTP, Angular. Manual guide below.

### Manual test guide

1. Run the migrator, start the API. In `/admin/jobs` the five jobs appear within a minute, enabled.
2. Place an order with the test gateway and leave the gateway page. Set `Jobs:AwaitingPaymentMinutes` to 1 (or change `CreatedOnUtc` of the order in the database). Click "Run now" on `orders.expire_unpaid`: the order is cancelled, stock is back, a used discount code is available again, the run shows "1 processed".
3. On the gateway page of that cancelled order click "Pay": the payment is refunded in full (F19-B).
4. Click "Run now" twice quickly (two tabs): one run succeeds, the other answers `409 job.already_running`.
5. Switch a job off: it stops running on schedule, "Run now" still works.
6. Set the interval of a job to 0: field error.
7. Stop the API during a run and start it again: the job runs again after its lease ended; no duplicate effects.
8. Set `Jobs:Enabled` to `false`: no scheduled runs, manual runs still work.
9. Sign in as a customer or a shop member: `/admin/jobs` and its API answer `403`.

## 13. Rollout and compatibility

Run the migrator before the API. Existing data is not changed by the migration. The first scheduled runs may clean a backlog (old holds, old carts), in batches of at most 200 items per run, so a first run is small. Set `Jobs:Enabled=false` on every node except one if the nodes should not all poll (they stay safe with the lease either way).

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Time-based order automation, stale checkout reconciliation, orphan media, cache | F29-B |
| Retries with back-off, cron, job parameters on the screen | F29-B |
| Email queue and reminders | F22 |
| Alerting and dashboards over job history | F26 |
| The lease `UPDATE`, the cross-module joins of `SqlMaintenanceStore` and the deletes are checked by hand only | F26 |
