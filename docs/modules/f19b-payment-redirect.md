# F19-B Redirect payments (hosted payment pages)

| | |
|---|---|
| **Module ID** | F19-B (slice of F19 "Payments"): the redirect flow and a test gateway that uses it |
| **Status** | Backend and Angular implemented; 729 service tests and 37 data tests pass, the Angular build, lint and 30 unit tests pass. SQL stores, the migration and the screens have not been run against a real database. No real gateway yet (D9): it needs an account and keys. |
| **Branch** | `feat/payments-redirect/foundation` (backend and frontend) |
| **Depends on** | F19-A (payment boundary), F17-A (checkout), F18-A (orders), F15-A (discount use), F12-A (stock) |
| **Unblocks** | F19-B2 (a real gateway such as VNPay, Stripe or MoMo: only an adapter), F22 (payment emails), F29 (expiry of unpaid orders), F21 (refunds from the order) |
| **Feature map** | "post-process redirect ... webhooks/callback validation." "provider callbacks must be verified, replay-safe, and audited." |

## 1. Purpose

F19-A made every payment succeed at once (cash on delivery, or a test gateway that holds the money in the same request). Real online payments are different: the customer leaves the shop, pays on the gateway's page, and the gateway tells the platform later. This slice builds that flow end to end, so a real gateway becomes one more adapter, and proves it with a **test gateway that has a hosted payment page** of its own.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **The flow is gateway-independent.** A new provider kind, `Hosted`, answers "pay here" with a page address instead of a result. The payment stays `pending` until the gateway reports `captured` or `failed` by a verified callback (F19-A D7). No gateway name appears in checkout or orders. |
| D2 | **An order waiting for its payment is "awaiting payment".** It is created (stock is taken, the discount is used) and the customer is sent to pay, but **shops cannot see it or act on it** until the money is confirmed. Shops only ever work on paid orders (PRD). |
| D3 | **Paid clears the wait; failed or cancelled by the gateway cancels the order**: every shop order is cancelled by the system, stock goes back and the discount use is given back, exactly like a refused payment in F17-A. |
| D4 | **Money that arrives for an order that was cancelled meanwhile is refunded automatically** (the customer cancelled while on the gateway page, then paid). The payment service asks the provider for a full refund; nothing is kept. |
| D5 | **The customer can come back and pay**: a payment that is still pending keeps its page address, and "Pay now" on the order sends the customer there again. A payment the gateway failed is final; the customer orders again. |
| D6 | **The gateway tells the platform; the browser's return does not.** Coming back to the shop (the return address) only shows the order; the order changes only on the verified callback. If the callback is late, the order page says the payment is being confirmed and can be refreshed. |
| D7 | **The order service and the payment service do not know each other.** The payment service tells registered handlers that a callback was applied; the order side handles it. The handler can ask for a follow-up (refund), which the payment service performs. This avoids a dependency cycle and keeps both testable. |
| D8 | **Sandbox only exists where configured**: the test gateway is registered only with `Payments:Sandbox:Secret` and `Payments:Sandbox:PageBaseUrl`. Its pages and routes answer `404` otherwise, so production cannot expose it by accident. |
| D9 | **No real gateway is integrated in this slice**, because it needs an account and keys that the project does not have yet. Adding one means one adapter (initiate, page address, callback parsing, refund) and its keys in configuration; checkout, orders and screens stay as they are. |

## 3. Actors and authorization matrix

| Action | Customer | Shop member | Administrator | Gateway |
|---|---|---|---|---|
| Choose a redirect method at checkout and be sent to pay | Yes | Yes | Yes | no |
| See that an order awaits payment, and pay again | Own order | no | Yes (sees it) | no |
| See, open or act on an order awaiting payment as a shop | no | `404` (it does not exist for the shop) | no | no |
| Report the result of a payment | no | no | no | Signed callback only |
| Test gateway page and its two routes | Anyone with the payment reference (development only) | | | |

## 4. Included behavior

- **Provider kind `Hosted`:** `GetRedirectUrl(payment)` gives the page address while a payment is pending; `Initiate` returns the gateway's reference and leaves the payment `pending`.
- **Checkout:** a redirect method creates the order as awaiting payment and the payment as pending; the answer carries `paymentRedirectUrl` and the screen sends the customer there. Cash on delivery and the immediate test gateway behave as before.
- **Callbacks:** the existing verified, replay-safe callback (F19-A) now also drives orders: `captured` marks the payment paid and the order paid; `failed` and `voided` cancel the order.
- **Late money:** `captured` for a payment whose order is fully cancelled is refunded in full at once (D4).
- **Pay again:** `GET /checkout/orders/{id}/payment` answers whether the order awaits payment, the payment status and, when pending, the page address.
- **Shops:** list, counts, detail and every action treat an order awaiting payment as not found.
- **Customer and administrator screens:** the order shows "Awaiting payment" with "Pay now"; after the return it shows "We are confirming your payment" until the callback arrives. The administrator sees the flag.
- **Test gateway:** a payment page of the Angular app (`/payments/sandbox/{reference}`) shows the amount and offers "Pay", "Fail" and "Back to the shop". Its two routes read the payment and send a callback with a valid signature, exactly like a real gateway would, then the page returns the customer to the order.
- **Methods:** a new method `sandbox_redirect` ("Test gateway with payment page"), seeded disabled; the public list says which methods redirect.
- **Audit:** `payment.callback_applied`, `payment.refunded`, `order.payment_received`, `order.shop_order_changed` (the system cancel), `discount.released`.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| A real gateway adapter (VNPay, Stripe, MoMo, PayPal) and its keys | F19-B2 |
| Cancelling unpaid orders after a time (the gateway never answered) | F29 |
| Voiding the gateway session when the customer cancels the order | F19-B2 (needs the real API) |
| Authorize then capture later for hosted payments (a hosted payment is a sale) | F19-B2 |
| Payment methods per country, amount or shop | F19-B2 |
| Saved cards and recurring payments | F19-C |
| Emails about payment | F22 |
| 3-D Secure and other challenges (they happen on the gateway's page) | the real adapter |
| Partial refunds from the order screens, refund requests | F21 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `IPaymentMethod.PostProcessPaymentAsync`, `PaymentMethodType.Redirection` | Redirect after placing | A provider answers a page address; the platform does the redirect |
| `Order.PaymentStatus = Pending` and `OrderStatus.Pending` until paid | Unpaid orders | `AwaitingPayment` flag hides the order from shops |
| `PayPalCommerce` webhook, `IPaymentMethod.CanRePostProcessPayment`, `RePostProcessPaymentAsync` | Paying again | "Pay now" on a pending payment |
| `OrderProcessingService.MarkOrderAsPaid`, `CancelOrderAsync` on failure | Result handling | Handlers react to verified callbacks only |
| Payment plugin webhooks | Callbacks | Signature, event id and replay safety come from F19-A |

## 7. Data model

Migration `202610210001 PaymentRedirectMigration`:

- `CustomerOrder` gains `AwaitingPayment bit` (default 0), with an index `(AwaitingPayment)` filtered to rows where it is 1.
- `PaymentMethod` gets the row `sandbox_redirect` (disabled).

No new table. `PaymentTransaction` is unchanged: the page address is derived from the provider reference, never stored.

## 8. Use cases and service contracts

```text
IPaymentProvider            + GetRedirectUrl(payment)                       null unless the provider is Hosted and the payment is pending
PaymentProviderKind         + Hosted
IPaymentOutcomeHandler      OnCallbackAppliedAsync(payment) -> PaymentFollowUp (None or Refund)
IPaymentService             + GetRedirectUrl(payment), FindByProviderReferenceAsync(method, reference)
                            callbacks tell the handlers and perform the follow-up
IOrderService               + MarkPaidAsync(orderId); shop reads and actions treat an awaiting order as not found
IOrderStore                 + ClearAwaitingPaymentAsync(orderId); shop lists and counts skip awaiting orders
ICheckoutService            + GetPaymentStatusAsync(customerId, orderId)
OrderPaymentOutcomeHandler  paid: clear the wait, or ask for a refund when the order is fully cancelled; failed or voided: cancel the order and give the discount use back
```

Business codes (`409`): none new. Not found: an order that is not the customer's, a payment page for an unknown reference.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `POST` | `/api/v1/checkout/place` | The answer gains `awaitingPayment` and `paymentRedirectUrl` |
| 2 | `GET` | `/api/v1/checkout/orders/{orderId}/payment` | Signed-in customer, own order; `{ awaitingPayment, paymentStatus, redirectUrl }` |
| 3 | `GET` | `/api/v1/payments/sandbox/{reference}` | Test gateway page data; `404` unless the sandbox is configured |
| 4 | `POST` | `/api/v1/payments/sandbox/{reference}/complete` | `{ outcome: "paid" \| "failed" }`; sends a signed callback; answers where to return to |
| 5 | `GET` | `/api/v1/payment/methods` | Each method says whether it `redirects` |

Route 4 has no CSRF token and no cookie requirement: it stands for the gateway's side, exists only in development, is rate limited, and the reference is unguessable.

## 10. Angular

- Checkout: sends the customer to the page address after placing; a redirect method is marked.
- Order page of the customer: awaiting-payment banner, "Pay now", "confirming" message after the return, refresh.
- Test gateway page `/payments/sandbox/:reference`.
- Customer order list and the administrator list show the awaiting state.
- States: loading, not found, saving, network error, payment confirmed, payment failed.

## 11. Events, jobs, cache

None. An order whose payment never completes stays awaiting until a job cancels it (F29).

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Provider | Hosted initiate gives a reference and a pending payment; page address only while pending; signature checks as F19-A; the sandbox needs secret and page address |
| Callbacks | Captured pays and tells the handlers; failed and voided too; replay does nothing twice; a follow-up refund is performed |
| Order handler | Paid clears the wait; paid on a fully cancelled order asks for a refund; failed cancels every shop order, returns stock and the discount use; other references are ignored |
| Orders | Awaiting orders are hidden from shops (list, counts, detail, every action) and shown to customer and administrator; mark paid; a paid order is visible |
| Checkout | A redirect method makes an awaiting order and a pending payment with an address and clears the cart; other methods unchanged; failed provider still cancels; payment status for the owner only |
| Money | An order cancelled while the customer paid is refunded in full; nothing is refunded twice |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL, HTTP, Angular, a real gateway. Manual guide below.

### Manual test guide

1. Set `Payments:Sandbox:Secret` (16+ characters) and `Payments:Sandbox:PageBaseUrl` (the Angular address, for example `http://localhost:4200`). Run the migrator.
2. In `/admin/payments` switch on "Test gateway with payment page".
3. As a customer place an order with it: you land on the gateway page with the amount. Open the seller portal of a shop in the order: the order is not there yet.
4. Click "Pay": you return to the order, which turns paid; the shop now sees it and can confirm it.
5. Place another order and click "Fail": the order is cancelled, stock is back, a used discount code is available again.
6. Place a third order, close the tab on the gateway page, open the order: "Awaiting payment" and "Pay now" take you back to the gateway page.
7. Place a fourth order, go to the gateway page in one tab, cancel the order in another, then click "Pay": the payment is refunded in full (see `/admin/payments`) and the order stays cancelled.
8. Remove the sandbox secret and restart: the method disappears and the gateway routes answer `404`.

## 13. Rollout and compatibility

Run the migrator before the API. Existing orders are not awaiting payment. Without the sandbox settings nothing changes for customers.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| A real gateway adapter; voiding the gateway session on cancel; per-method restrictions | F19-B2 |
| Cancelling orders whose payment never came | F29 |
| Payment emails; refund requests and partial refunds from the order | F22, F21 |
| Saved cards and recurring payments | F19-C |
