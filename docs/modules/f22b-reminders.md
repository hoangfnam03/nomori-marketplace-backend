# F22-B Reminders: unpaid orders and abandoned carts

| | |
|---|---|
| **Module ID** | F22-B (slice of F22): two reminder emails sent by jobs |
| **Status** | Backend implemented; 832 service tests (12 new) and 40 data tests pass. No Angular change: the jobs appear in `/admin/jobs` (F29-A) and the emails in `/admin/emails` (F22-A). SQL store and migration not run against a real database (see 12). |
| **Branch** | `feat/email-reminders/foundation` (backend only) |
| **Depends on** | F22-A (email queue), F29-A (jobs), F16-A (cart), F18-A/F19-B (orders awaiting payment) |
| **Feature map** | "abandoned cart/pending order reminders" |

## 1. Purpose

A customer who left the payment page or the cart gets one polite email, so a sale is not lost for nothing. Both emails go through the F22-A queue, so they are sent, retried and visible like every other email.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **At most one reminder per order**, and **one per cart state**: a cart is reminded again only if the customer changed it after the last reminder and left it again. A customer who does not come back is never nagged twice about the same cart. |
| D2 | **Recorded before it is queued**, in one statement (`ReminderLog`, unique on kind and reference). Two nodes never both send. When queuing throws, the record is taken back and the next run tries again; when the queue refuses the email (a bad address) the record stays, so the same bad address is not retried every run. |
| D3 | **The unpaid-order reminder comes before the cancel.** Default reminder after 30 minutes, `orders.expire_unpaid` cancels after 60 (`Jobs:AwaitingPaymentMinutes`). Keep `Reminders:UnpaidOrderMinutes` below it. Cancelled and paid orders are never listed. |
| D4 | **Carts left for 24 hours up to 7 days.** Fresher carts are still being used; older ones are given up on (and `cart.purge_stale` deletes lines after 90 days). |
| D5 | **No secret and no price promise.** The link goes to a plain storefront page (`/customer/orders`, `/cart`) built from `Email:FrontendBaseUrl`; without that setting the email has no link. The cart email does not quote prices (they change). Values are HTML-encoded. |
| D6 | **A customer who is gone or inactive gets nothing**, and is not an error. |
| D7 | **Switched on and off like any job** (`/admin/jobs`); no new permission, no new screen. |

## 3. Included behavior

- **Job `reminders.unpaid_orders`** (every 10 min): orders awaiting payment with a pending shop order, created more than `Reminders:UnpaidOrderMinutes` (30) ago, not reminded. Email kind `reminder.unpaid_order`: "Complete your payment for order NM-...", with the number and total.
- **Job `reminders.abandoned_carts`** (hourly): customers whose newest cart line was touched between `Reminders:AbandonedCartMaxDays` (7) days and `Reminders:AbandonedCartHours` (24) hours ago and who were not reminded since that touch. Email kind `reminder.abandoned_cart`: "You left something in your cart", with the number of lines.
- **Job `reminders.purge_log`** (daily): deletes records older than `Reminders:LogRetentionDays` (90).
- Up to 200 customers per run; one failing customer does not stop the others.

## 4. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Templates in the database, languages, editing the texts | F22-C |
| Reminders for guests (there is no guest cart yet) | F16-B |
| A second or third reminder, discount codes in reminders | later, with a business decision |
| Unsubscribe and marketing consent (these are service emails about the customer's own order and cart) | F22-C (newsletter) |
| Alerts when reminders fail | F26 |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `AbandonedCartsTask`-style reminders and the `Reminders` domain | Reminding about carts and pending orders | Plain jobs on the F29-A runner; sent through the F22-A queue; one record table instead of reminder rules |

## 6. Data model

Migration `202610270001 ReminderLogMigration`: `ReminderLog` (`Id` bigint identity, `Kind` nvarchar(50), `ReferenceId` int, `CustomerId` int, `SentOnUtc` datetime2), unique index `(Kind, ReferenceId)`, index `(SentOnUtc)`. `ReferenceId` is the order for an order reminder and the customer for a cart reminder. No other table changes.

## 7. Service contracts

```text
IReminderStore   UnpaidOrdersAsync(createdBefore, take); AbandonedCartsAsync(idleBefore, idleAfter, take)
                 TryRecordAsync(kind, ref, customer, now, renewIfBefore?) -> bool  (one statement, D2); ForgetAsync; DeleteOlderThanAsync
Jobs             RemindUnpaidOrdersJob, RemindAbandonedCartsJob, PurgeReminderLogJob
```

Settings (`Reminders:*`) are checked at start: every value at least 1, and `AbandonedCartMaxDays` longer than `AbandonedCartHours`.

## 8. API and Angular

None new. Jobs: `/api/v1/admin/jobs` and `/admin/jobs`. Emails: `/api/v1/admin/emails` and `/admin/emails`.

## 9. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Unpaid orders | One reminder with number, total, encoded name and link; never twice; no link without a frontend address; missing or inactive customer sends nothing; a queue failure is counted, the record is taken back and the next run sends; a refused email is not retried; nothing to do is a clean run |
| Abandoned carts | Only carts idle between 24 hours and 7 days; singular "1 item"; once until the cart changes again, then once more |
| Purge | Only old records go |
| Migration | Version ordering |

**Not automated:** SQL (the two candidate queries, the record statement), the hosted run, email delivery.

### Manual test guide

1. Run the migrator, start the API with `Email:Enabled=true`. In `/admin/jobs` the three jobs appear.
2. Place an order with the test gateway and leave the payment page. Set `Reminders:UnpaidOrderMinutes` to 1 (or move `CreatedOnUtc` back in the database) and run `reminders.unpaid_orders`: one email is queued (and sent by `email.send_queued`). Run it again: nothing more.
3. Pay another unpaid order, then run the job: no email for the paid order.
4. Add items to a cart, set `UpdatedOnUtc` of its lines 30 hours back, run `reminders.abandoned_carts`: one email. Run again: nothing. Change the cart (add an item), move it back 30 hours, run: a second email.
5. Set a cart 8 days back: no email.
6. Check the text with a `<script>` in a first name: encoded. With `Email:FrontendBaseUrl` empty: no link.
7. Run the same job from two tabs: one run, the other `409 job.already_running`; no duplicate email.

## 10. Rollout and compatibility

Run the migrator before the API. The first run after the release may remind about carts and orders that are already old enough (carts up to 7 days, in batches of 200 per run). Set `Reminders:*` in configuration if the defaults do not fit; switch a job off in `/admin/jobs` to stop it.

## 11. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| The two candidate queries and the record statement of `SqlReminderStore` are checked by hand only | F26 |
| Texts are fixed in code; no templates, no languages | F22-C |
| No reminders for guests | F16-B |
