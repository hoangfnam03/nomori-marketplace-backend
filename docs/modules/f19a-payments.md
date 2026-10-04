# F19-A Payment provider boundary and lifecycle

| | |
|---|---|
| **Module ID** | F19-A (slice of F19 "Payments") |
| **Status** | Backend and Angular implemented; 464 service tests pass and the Angular build and lint pass. SQL store, migration, HTTP and screens not yet run against a real database. |
| **Branch** | `feat/payments/foundation` (backend and frontend) |
| **Depends on** | F03 (permissions, audit), F07-A (primary currency), F02 (customer id) |
| **Unblocks** | F17 (checkout creates a payment), F18 (an order holds its payment), F21 (refunds), F19-B (real gateways) |
| **Feature map** | "Payment providers are adapters. Core order processing owns legal state transitions and idempotency; provider callbacks must be verified, replay-safe, and audited." "authorize/capture, sale, post-process redirect, refund, void, recurring payment, payment status, webhooks/callback validation." |

## 1. Purpose

There is no order yet (F18), so this slice builds what an order will stand on: a **boundary every payment gateway plugs into**, a **payment record with a strict lifecycle**, and **verified, replay-safe callbacks**. Checkout (F17) will call `IPaymentService.CreateAsync`; nothing here charges a real card.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Gateways are adapters** behind `IPaymentProvider`. Core code never names a gateway. Two adapters ship: `cod` (offline, "Pay on delivery") and `sandbox` (a test gateway that behaves like a real one, including signed callbacks). Real gateways are F19-B. |
| D2 | **The service owns the lifecycle.** A provider answers "did it work"; only `PaymentRules` decides which status change is legal and the store applies it with a compare-and-set (`WHERE Status = expected`), so two racing requests cannot both win. |
| D3 | **Statuses:** `pending`, `authorized`, `paid`, `partially_refunded`, `refunded`, `voided`, `failed`. Capture needs `pending` (offline) or `authorized`; void needs `pending` or `authorized`; refund needs `paid` or `partially_refunded`; `voided`, `failed` and `refunded` are final. |
| D4 | **Refunds can be partial** and the total refunded never exceeds the amount paid (also a database check). |
| D5 | **A payment is created with an idempotency key** (unique). The same key with the same method, amount and reference returns the existing payment; the same key with different values is a conflict. This is what makes "place order" retry-safe in F17. |
| D6 | **Amounts are in the primary currency** and the currency code is stored on the payment (F07-A rule: persist money with its currency). The amount must follow the currency decimals. |
| D7 | **Callbacks are anonymous but verified.** `POST /payments/callbacks/{provider}` is not cookie-authenticated, so it has no CSRF token; instead the provider checks a signature over the raw body (HMAC-SHA256, constant-time compare) before anything is read as trusted. Every accepted event is stored with the gateway's event id, which is unique per provider: a replay answers `200` and changes nothing. |
| D8 | **Which methods customers see is configured by an administrator** (`PaymentMethod`: enabled, display order). A provider that is not registered (the sandbox outside development) never appears, whatever the table says. |
| D9 | **There is no customer route to create or change a payment** in this slice; only F17 creates one, through the service. Administrators with `payments.manage` capture, void and refund. |
| D10 | **No card data and no secrets in the database or the logs.** The audit and event log hold ids, amounts, statuses and event types only. |

## 3. Actors and authorization matrix

| Action | Customer | Administrator (`payments.manage`) | Gateway |
|---|---|---|---|
| List the enabled methods | Yes | Yes | no |
| List payments and see one | no (`403`) | Yes | no |
| Enable, disable and order methods | no | Yes | no |
| Capture, void, refund | no | Yes | no |
| Send a callback | no | no | Only with a valid signature |

Guests get `401` on every route except the callback, which answers `401` for a bad signature.

## 4. Included behavior

- **Methods:** `GET /payment/methods` answers the enabled and registered methods in display order. Seed: `cod` enabled, `sandbox` disabled.
- **Create (service only):** checks the method is enabled and registered, the amount (above 0, at most 1,000,000,000, currency decimals), the idempotency key (1 to 100 characters), then asks the provider to initiate. `cod` answers `pending`; `sandbox` answers `authorized` with a provider reference. A provider failure stores the payment as `failed` with a short reason code.
- **Capture:** `pending` or `authorized` becomes `paid` (the provider is asked first; `cod` only records that cash was received).
- **Void:** `pending` or `authorized` becomes `voided`.
- **Refund** `{ amount }`: above 0 and not above what is left; the provider is asked; status becomes `partially_refunded` or `refunded`.
- **Callbacks:** the sandbox accepts `captured`, `failed` and `voided` events. An event for an unknown payment is stored as `ignored`; an event the state machine does not allow is stored as `ignored`; replays are a no-op.
- **Audit:** `payment.created`, `payment.captured`, `payment.voided`, `payment.refunded`, `payment.failed`, `payment.method_updated`, `payment.callback_applied`, `payment.callback_ignored`.
- **Angular (admin):** page `/admin/payments` with the methods (enable switch, order) and the payment list with filter by status, capture, void and refund actions.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Real gateways (card, wallet, bank), redirect and hosted pages, 3-D Secure | F19-B |
| Customer payment screens and "pay again" | F17 |
| Orders holding the payment, order status from payment status | F18 |
| Method restrictions by country, amount, shop or customer role | F19-B |
| Recurring payments, saved cards, tokens | F19-C |
| Payout to shops, commission, settlement | F05 settlement PRD |
| Refund requests from customers and restocking | F21 |
| Currency conversion of payments | F07 follow-up |
| Cleaning up old pending payments | F29 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Payments/PaymentStatus` | Statuses | Adds `failed`; `voided` and refunds are explicit; one table for the payment |
| `Nop.Services/Payments/IPaymentMethod`, `PaymentPluginManager` | Provider contract and discovery | A small `IPaymentProvider` registered in DI; no dynamic assembly loading (F28) |
| `ProcessPaymentRequest/Result`, `CapturePaymentRequest/Result`, `VoidPaymentRequest/Result`, `RefundPaymentRequest/Result` | Operations | One `PaymentProviderResult` shape |
| `PaymentSettings` (active methods) | Which methods are on | `PaymentMethod` table |
| `Plugins/Payments.CheckMoneyOrder` | Offline method | `cod` |
| Plugin webhooks (PayPal Commerce) | Callbacks | Signature, event id, replay-safe log are mandatory |
| `OrderProcessingService` payment transitions | Legal state changes | `PaymentRules` now, order rules in F18 |

## 7. Data model

Migration `202610170001 PaymentMigration`:

**`PaymentMethod`**: `SystemName nvarchar(50)` PK, `Enabled bit`, `DisplayOrder int`, `UpdatedOnUtc datetime2`. Seed `cod` (enabled) and `sandbox` (disabled).

**`PaymentTransaction`**

| Column | Type | Notes |
|---|---|---|
| `Id` | `int` identity | |
| `ReferenceType` | `nvarchar(30)` | `checkout` now, `order` later |
| `ReferenceId` | `int` | The thing being paid |
| `IdempotencyKey` | `nvarchar(100)` | Unique |
| `Method` | `nvarchar(50)` | Provider system name |
| `CustomerId` | `int` null FK `Customer` | No action |
| `Amount` | `decimal(18,4)` | Check above 0 |
| `CurrencyCode` | `nvarchar(3)` | |
| `Status` | `int` | Check 0 to 6 |
| `RefundedAmount` | `decimal(18,4)` | Check 0 to `Amount` |
| `ProviderReference` | `nvarchar(200)` null | The gateway's id |
| `FailureCode` | `nvarchar(100)` null | |
| `CreatedOnUtc`, `UpdatedOnUtc` | `datetime2` | |

Unique `UX_PaymentTransaction_IdempotencyKey`; index `(ReferenceType, ReferenceId)`; unique filtered `UX_PaymentTransaction_Provider (Method, ProviderReference)` where the reference is not null.

**`PaymentEvent`**: `Id`, `TransactionId` FK (null when unknown), `Provider nvarchar(50)`, `ProviderEventId nvarchar(200)`, `Type nvarchar(50)`, `Outcome nvarchar(20)` (`applied`, `ignored`), `CreatedOnUtc`. Unique `UX_PaymentEvent_Provider_Event (Provider, ProviderEventId)`.

Permission `payments.manage` is added and given to the Administrator role. `Down()` drops the three tables and the permission.

## 8. Use cases and service contracts

```text
PaymentRules (pure)       CanCapture/CanVoid/CanRefund(status), AfterRefund(status, paid, refunded, amount), IsFinal
IPaymentProvider          SystemName, DisplayName, Kind, InitiateAsync, CaptureAsync, VoidAsync, RefundAsync, ParseCallback
IPaymentStore             methods, GetTransaction/ByKey/ByProviderReference, Insert, TryChangeStatus (compare-and-set),
                          TryAddEvent (false on replay), list (page, status filter)
IPaymentService           GetAvailableMethodsAsync, CreateAsync, CaptureAsync, VoidAsync, RefundAsync,
                          HandleCallbackAsync, admin: GetMethods, UpdateMethod, GetPayments, GetPayment
```

Field errors (`400`): `amount`, `method`, `idempotencyKey`, `referenceId`. Business codes (`409`): `payment.method_unavailable`, `payment.idempotency_conflict`, `payment.invalid_state`, `payment.refund_exceeds`, `payment.provider_failed`. Not found: payment id.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/payment/methods` | Signed-in customer |
| 2 | `GET` | `/api/v1/admin/payment-methods` | `payments.manage` |
| 3 | `PUT` | `/api/v1/admin/payment-methods/{systemName}` | `{ enabled, displayOrder }` |
| 4 | `GET` | `/api/v1/admin/payments?status=&page=&pageSize=` | |
| 5 | `GET` | `/api/v1/admin/payments/{id}` | |
| 6 | `POST` | `/api/v1/admin/payments/{id}/capture` | |
| 7 | `POST` | `/api/v1/admin/payments/{id}/void` | |
| 8 | `POST` | `/api/v1/admin/payments/{id}/refund` | `{ amount }` |
| 9 | `POST` | `/api/v1/payments/callbacks/{provider}` | Anonymous, signature header `X-Nomori-Signature`, raw body at most 64 KB, rate limited |

Admin write routes need the CSRF token like every other write. The callback does not (it has no cookie).

## 10. Angular

- `core/payments/payment-api.service.ts` and models; admin page `/admin/payments` (link in the admin home).
- States: loading, empty, saving, refund amount error, conflict (invalid state), network error.

## 11. Events, jobs, cache

None. Callbacks are handled in the request. Reminders and cleanup of old pending payments are F29.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Every status and operation; refund totals; final statuses |
| Create | Method enabled and registered; amount and decimals; idempotent repeat; conflicting repeat; provider failure becomes `failed`; `cod` pending, `sandbox` authorized |
| Capture, void, refund | Legal and illegal states; provider failure keeps the status; partial then full refund; over-refund refused; race lost returns invalid state |
| Callbacks | Bad signature refused and nothing stored; replay is a no-op; unknown payment ignored; illegal transition ignored; valid event applied and audited |
| Methods | Disabled and unregistered hidden; order |
| Sandbox provider | Signature accepted and rejected (wrong secret, altered body, missing header) |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL, HTTP, Angular. Manual guide below.

### Manual test guide

1. Run the migrator; check the three tables, the seeds and `payments.manage` on the Administrator role.
2. As an administrator open `/admin/payments`: `cod` is on, `sandbox` is off. Enable `sandbox` (it is only registered when `Payments:Sandbox:Secret` is set, so in development) and reorder.
3. As a customer `GET /api/v1/payment/methods`: `cod` and (when enabled) `sandbox`.
4. There is no screen that creates a payment yet. Create one from a test or a temporary call to the service, then capture, refund 3 of 10, refund 7, try 1 more (`payment.refund_exceeds`).
5. Send a signed `captured` callback twice with the same event id: the second one changes nothing. Alter one byte of the body: `401`.

## 13. Rollout and compatibility

Run the migrator before the API. `Payments:Sandbox:Secret` must be set for the sandbox to be registered; production leaves it off. No existing screen or API changes meaning.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Real gateways, redirect flows, method restrictions | F19-B |
| Recurring payments and saved methods | F19-C |
| Checkout creates the payment; order status follows it | F17, F18 |
| Customer refund requests | F21 |
| Old pending payments, reminders | F29 |
| Shop settlement of captured payments | F05 settlement PRD |
