# F22-A Email queue and order emails

| | |
|---|---|
| **Module ID** | F22-A (slice of F22 "Messaging, notification and customer engagement"): a persistent email queue, a sending job and the first customer emails |
| **Status** | Backend and Angular implemented and merged. 832 service tests (all F22-A tests included), 40 data tests, the Angular build, lint and 30 unit tests pass. SQL store, migration, hosted run and screen have not been run against a real database (see 12). |
| **Branch** | `feat/email-queue/foundation` (backend and frontend) |
| **Depends on** | F29-A (jobs), F18-A (orders), F03 (permissions), existing `IEmailSender` (SMTP) |
| **Unblocks** | F22-B (templates, abandoned cart and pending order reminders), F21 (refund and return emails), F26 (email log) |
| **Feature map** | "publish domain events, then have notification handlers enqueue delivery ... raw secrets and payment data never enter message tokens or logs." |

## 1. Purpose

Until now every email was sent inside the request that caused it: a slow or broken mail server slowed or failed the request, and a failed email was lost. Customers also heard nothing about their orders. This slice adds a **queue** (written in the same request, sent by a job, tried again when it fails, visible to administrators) and the first **order emails**.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Queue in the database, sent by a job.** `QueuedEmail` rows are written by `IEmailQueueService.EnqueueAsync`; the job `email.send_queued` (every minute, F29-A) sends them. No broker, no new library. |
| D2 | **Atomic claim.** The job takes emails with one `UPDATE ... OUTPUT` (`ROWLOCK, UPDLOCK, READPAST`) that marks them `sending`, sets a lease and counts the attempt. Two nodes, or a manual run during a scheduled run, never send the same email. An email whose lease ended (its node died) is due again. |
| D3 | **Retry with back-off, then stop.** A failed send goes back to `pending` after 2, 10, 30, then 120 minutes. After `Email:QueueMaxAttempts` (default 5) attempts it is `failed` and stays so until an administrator sends it again. A crash during sending counts as an attempt, so an email that crashes its sender cannot loop for ever. |
| D4 | **Delivery off means wait.** With `Email:Enabled=false` the job takes nothing; queued mail waits and goes out when delivery is switched on. |
| D5 | **Best effort for the business action.** An order is never refused or rolled back because its email could not be queued (`IOrderNotifier` catches and logs). The email is queued after the order change is saved. |
| D6 | **No secrets in the queue.** Password reset links, verification links and one-time codes keep going straight through `IEmailSender`: a queued body stays in the table for days. Order emails carry the number, items, totals and the delivery address the customer gave, no payment detail, no link. |
| D7 | **Validate at the door.** A wrong address, an empty subject or an empty body is refused when queuing, so a bad customer record cannot fill the queue with mail that can never go. |
| D8 | **Everything written into an email is HTML-encoded** (names of shops and products, reasons, carrier, tracking number). |
| D9 | **Permission-aware.** `emails.manage` (Administrator by default) for the list, view, retry and delete. Retry and delete are audited (`email.retry`, `email.deleted`). |
| D10 | **The admin screen shows a body as text**, never as HTML, so a stored mail cannot run in the admin session. |

## 3. Actors and authorization matrix

| Action | Customer | Shop member | Administrator (`emails.manage`) | System |
|---|---|---|---|---|
| Receive order emails | Yes (own orders) | no | | |
| See the queue, open an email | no | no | Yes | |
| Send a failed email again, delete an email | no | no | Yes | |
| Send queued emails, clean the queue | | | | Yes (jobs) |

Anyone else gets `403` (`401` when not signed in).

## 4. Included behavior

- **Queue:** `QueuedEmail` with kind, address, subject, HTML and text body, status (`pending`, `sending`, `sent`, `failed`), attempts, next attempt time, lease, last error (one line, no stack trace).
- **Job `email.send_queued`** (every 1 min): up to 50 emails per run; one failing email does not stop the others; a run with failures is `partial`.
- **Job `email.purge_queue`** (daily): deletes sent emails older than `Email:SentRetentionDays` (30) and failed ones older than `Email:FailedRetentionDays` (90). Pending emails are never deleted by a job.
- **Order emails to the customer** (kinds `order.placed`, `order.shipped`, `order.delivered`, `order.cancelled`; subjects from `Email:Order*Subject`, `{number}` replaced):
  - placed: when an order that needs no payment is created, or, for an order that waits for its payment, once when the payment is confirmed (a repeated gateway callback sends nothing more);
  - shipped (with carrier and tracking number), delivered, cancelled (with the reason), per shop order. Confirmed and completed send nothing. A system cancel (unpaid order expired by F29-A) sends the cancelled email.
  - A customer who no longer exists or is inactive gets nothing.
- **API and screen** `/admin/emails`: filter by status (with counts), search by address, subject or kind, page, view body, send again (failed only), delete (not while being sent).

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Moving the existing direct emails (verification, OTP, password recovery, vendor emails) into the queue; they carry secrets or need to be immediate | F22-B, case by case |
| Templates stored in the database, tokens, versions and languages, a template editor | F22-B |
| Abandoned cart and pending order reminders | F22-B |
| Emails to shop members about new orders; refund and return emails | F22-B, F21 |
| Several SMTP accounts, priorities, per-email schedule, attachments, bounce handling | F22-B |
| SMS, newsletter, campaigns | F22-C |
| A domain-event bus (the order service calls `IOrderNotifier` directly) | F29-B |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `QueuedEmail` (`SentTries`, `SentOnUtc`, `DontSendBeforeDateUtc`), `QueuedEmailService`, `QueuedMessagesSendTask` | The queue, the tries, the send task | One `Status` column; the lease is in the row; back-off grows; the batch is claimed in one statement |
| `QueuedEmailController` | Admin grid | `/admin/emails`; the body is shown as text |
| `WorkflowMessageService.SendOrderPlacedCustomerNotification` and the shipped, delivered, cancelled variants | Order emails | Fixed, encoded text in code until templates exist (F22-B); no link or payment data |
| `MessageTemplate`, `EmailAccount`, tokens | Templates and accounts | Not in this slice |

## 7. Data model

Migration `202610250001 EmailQueueMigration`:

`QueuedEmail`
- `Id` bigint identity, `Kind` nvarchar(50), `ToAddress` nvarchar(320), `Subject` nvarchar(255), `HtmlBody` nvarchar(max), `TextBody` nvarchar(max) null.
- `Status` nvarchar(20) with a check (`pending`, `sending`, `sent`, `failed`), `Attempts` int, `NextAttemptUtc` datetime2, `LockedUntilUtc` datetime2 null, `LastError` nvarchar(500) null, `CreatedOnUtc`, `SentOnUtc` datetime2.
- Index `(Status, NextAttemptUtc)` for the sender; index `(CreatedOnUtc DESC)` for the list.

Permission `emails.manage` (category "Email") given to `Administrator`, as `jobs.manage` was.

## 8. Use cases and service contracts

```text
IEmailQueueStore    InsertAsync; ClaimDueAsync(now, leaseUntil, take); MarkSentAsync; MarkAttemptFailedAsync(id, failure, nextAttempt?)
                    RetryAsync; DeleteAsync; DeleteOldAsync(sentBefore, failedBefore, take); ListAsync; GetAsync; CountByStatusAsync
IEmailQueueService  EnqueueAsync(kind, message) -> id; ListAsync; GetCountsAsync; GetAsync; RetryAsync(id, actor); DeleteAsync(id, actor)
EmailQueueRules     pure: NextAttempt(now, attempts), IsValidAddress, DescribeFailure
IOrderNotifier      OrderPlacedAsync(order); ShopOrderChangedAsync(shopOrder)     (never throws)
```

Business codes (`409`): `email.not_retryable`, `email.busy`. Not found: an unknown email. Field errors (`400`, queuing only): `kind`, `toAddress`, `message`.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/admin/emails?status=&search=&page=&pageSize=` | Page of emails without bodies, plus the count per status |
| 2 | `GET` | `/api/v1/admin/emails/{id}` | One email with its bodies |
| 3 | `POST` | `/api/v1/admin/emails/{id}/retry` | Failed to pending; CSRF |
| 4 | `DELETE` | `/api/v1/admin/emails/{id}` | `204`; CSRF |

## 10. Angular

- `/admin/emails` (permission `emails.manage`), menu entry "Emails", English and Vietnamese texts.
- States: loading, empty, load error with retry, busy, `409` (list is reloaded), network error. The body is shown in a `<pre>` as text.

## 11. Events, jobs, cache

Two jobs on the F29-A runner (`email.send_queued`, `email.purge_queue`). Domain events are not introduced; `IOrderNotifier` is the seam a later event handler can take over (F29-B).

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Back-off steps, address check, one-line failure |
| Queue service | Queued as pending and due now; bad address, subject, body or kind refused with nothing queued; long subject cut; list clamps; retry only for failed, audited; delete refused while sending, audited |
| Sending job | Sends due emails; waits for its time; failure goes back with back-off and the next email still goes; gives up after the last attempt and a manual retry sends it; expired lease taken again, live lease not; delivery off takes nothing; empty queue is a clean run |
| Purge job | Old sent and old failed deleted, pending kept |
| Order notifier | Customer address, subject with number, every value encoded; shipped, delivered, cancelled; other statuses send nothing; inactive or missing customer sends nothing; a failure never reaches the caller |
| Order service | Announced at once when no payment is needed, once when a waiting order is paid (repeat sends nothing); ship, deliver, cancel announced, confirm not; a failed transition announces nothing |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (the claim `UPDATE ... OUTPUT`, the list), the hosted run, SMTP, HTTP, Angular.

### Manual test guide

1. Run the migrator, start the API with `Email:Enabled=true` and a real or test SMTP server (for example a local MailHog). In `/admin/jobs` `email.send_queued` and `email.purge_queue` appear.
2. Place an order paid on delivery: within a minute the customer receives "Your Nomori Marketplace order ..."; `/admin/emails` shows it as Sent with one attempt.
3. Place an order with the test gateway and pay: the "placed" email goes out once, after the payment; repeat the gateway callback: no second email.
4. As the shop, confirm (no email), ship with carrier and tracking number (email with both), mark delivered (email). Cancel another shop order with a reason (email with the reason).
5. Stop the SMTP server and place an order: the email shows Waiting with a next try time and the error; start the server: it goes out at the next try. Keep it stopped for five attempts (or set `Email:QueueMaxAttempts` to 1): it shows Failed; start the server, "Send again": it is sent.
6. Set `Email:Enabled=false`: new emails stay Waiting; set it back: they are sent.
7. Put `<script>` in a product name and place an order: the email text shows it encoded; the admin screen shows the body as text.
8. Delete a waiting email: it is never sent. Sign in as a customer or a shop member: `/admin/emails` and its API answer `403`.
9. Run `email.send_queued` twice at once (two tabs, "Run now"): one run, the other answers `409 job.already_running`; no email is sent twice.

## 13. Rollout and compatibility

Run the migrator before the API. Existing emails are unchanged (still direct). Orders placed before the release get no email. New settings (`Email:QueueMaxAttempts`, `QueueLeaseMinutes`, `SentRetentionDays`, `FailedRetentionDays`, `Order*Subject`) have defaults; the app refuses to start when one is out of range. Emails are only sent while `Email:Enabled=true` and `Jobs:Enabled=true` on at least one node.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Templates, tokens, versions and languages; the text is fixed in `OrderNotifier` | F22-B |
| Reminders (abandoned cart, unpaid order); emails to shop members and for refunds and returns | F22-B, F21 |
| Direct emails (verification, OTP, recovery, vendor) still bypass the queue and are lost on failure | F22-B |
| The claim `UPDATE`, the list query and the deletes of `SqlEmailQueueStore` are checked by hand only | F26 |
| Alert when emails keep failing; a log of sent mail per customer | F26 |
