# F16-A Shopping cart

| | |
|---|---|
| **Module ID** | F16-A (slice of F16 "Cart, wishlist and checkout attributes") |
| **Status** | Backend and Angular implemented. SQL store and migration not yet run against a real database. |
| **Branch** | `feat/cart/foundation` (backend and frontend) |
| **Depends on** | F02 (accounts), F10 (products, visibility), F11-B (variants), F12-A (availability), F14-A (price quote), F07-A (primary currency) |
| **Unblocks** | F17 (checkout starts from the cart and reserves stock), F15 (discounts on cart lines), F18 (order lines) |
| **Feature map** | "Define whether a cart can contain multiple shops. If yes, group lines by shop for shipping, payout, cancellation and return while retaining one buyer cart." |

## 1. Purpose

The storefront had an "Add to cart" button that did nothing. This slice adds a real cart: lines with a quantity and the chosen variant, priced by the server (F14-A), checked against visibility and stock (F12-A), grouped by shop, with a subtotal.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **One cart per signed-in customer, with lines from several shops.** The response groups lines by shop, so shipping, payout and returns can be split later. |
| D2 | **Guests have no cart yet.** "Add to cart" asks a guest to sign in. Guest carts and merging them at sign-in need an anonymous identity and are F16-B. |
| D3 | **The cart does not hold stock.** Reserving at "add" would let anyone lock a shop's stock for nothing. The cart only checks availability when a line is added, changed or shown; checkout (F17) reserves stock with `IInventoryService.ReserveAsync` using the checkout as the reference. |
| D4 | **Prices are always computed by the server** with `IPriceCalculationService` for the line's quantity (tier prices change with it). The cart is in the **primary currency**; it is what the customer will pay, so it is never shown as a converted approximation. |
| D5 | A line is identified by customer, product and chosen variant values. Adding the same choice again adds to its quantity. |
| D6 | Members of a shop cannot buy products of their own shop. |
| D7 | The price a customer saw when adding a line is kept. If the current price differs, the cart says so and the customer accepts the new prices before checkout. |

## 3. Actors and authorization matrix

| Action | Signed-in customer | Guest |
|---|---|---|
| Read, add, change, remove, clear, accept prices | Their own cart only | `401` |
| Count of cart items (header badge) | Yes | `401` (the page treats it as 0) |

There is no way to address another customer's cart: the customer id always comes from the session, never from the request.

## 4. Included behavior

- **Add** `{ productId, quantity, valueIds }`: the product must be visible to customers (live, shop active, sale window open). A product with variants needs one value of every option (the price quote enforces it). Quantity 1 to 10,000 in total for the line; at most **50 lines** per cart. Tracked products cannot go above the available stock: `400 errors.quantity` with the number available. The line's total quantity (existing plus added) is what is checked and priced.
- **Change quantity** `PUT /cart/items/{lineId}` (1 to 10,000) with the same checks; **remove** a line; **clear** the cart.
- **View** (`GET /cart`): every line is priced again and checked again. A line carries its issues:
  - `unavailable`: the product is no longer visible (stopped, hidden, deleted, shop inactive, outside its window). Blocks checkout.
  - `variant_unavailable`: the chosen combination no longer exists (the shop changed its variants). Blocks checkout.
  - `out_of_stock`: nothing available. Blocks checkout.
  - `insufficient_stock`: less available than the quantity. Blocks checkout.
  - `price_changed`: the unit price differs from the price when the line was added. A warning; "Accept new prices" updates the saved prices.
- **Totals:** each shop group has a subtotal and the cart has a subtotal and an item count. Lines that cannot be priced (`unavailable`, `variant_unavailable`) are not counted. `canCheckout` is true when there is at least one line and no line has a blocking issue.
- **Line data:** product name, shop, main picture, variant label ("Red / S"), SKU, quantity, unit price, compare price, line total, the pricing rule applied, available quantity (null when not tracked) and the issues.
- **Own products:** adding a product of a shop the customer is a member of is refused (`409 cart.own_product`).
- **Audit:** none (a cart is not a business record; orders are).
- **Angular:**
  - "Add to cart" works on the product page (quantity and chosen variant), and on cards for products without variants (a product with variants opens its page).
  - Guests are sent to sign in.
  - Header link "Cart (n)".
  - Page `/storefront/cart`: lines grouped by shop, quantity editor, remove, issue badges, price-change notice with "Accept new prices", subtotal, and a disabled "Checkout" button that says checkout comes next.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Guest cart, merge on sign-in, cart in the browser | F16-B |
| Wishlist and custom wishlists | F16-B |
| Checkout attributes, gift options, order notes | F16-C |
| Checkout, stock reservation for payment, order creation | F17, F18 |
| Discount codes, free shipping, gift cards | F15 |
| Tax and shipping estimates in the totals | F07-E, F20 |
| Minimum order amount or quantity per product, "increments of N" | F16-B |
| Removal of old carts | F29 |
| Customer-entered price products | F14-B |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Orders/ShoppingCartItem` (`CustomerId`, `ProductId`, `AttributesXml`, `Quantity`, `ShoppingCartType`) | Cart line | Variant values as ids; one cart type (wishlist later) |
| `Nop.Services/Orders/ShoppingCartService.AddToCartAsync`, `GetShoppingCartItemWarningsAsync` | Validation | Warnings are structured issues; stock checked against available quantity |
| `ShoppingCartSettings.MaximumShoppingCartItems` | Line limit | 50 |
| Multi-shop carts | Not in nopCommerce | Lines grouped by shop (feature map requirement) |
| `ShoppingCartService.MigrateShoppingCartAsync` | Guest merge | Deferred |

## 7. Data model

Migration `202610110001 CartMigration`:

**`CartItem`**

| Column | Type | Notes |
|---|---|---|
| `Id` | `int` identity | |
| `CustomerId` | `int` FK to `Customer` | Cascade: a deleted customer loses the cart |
| `ProductId` | `int` FK to `Product` | No action (products are soft-deleted) |
| `ValueIds` | `nvarchar(200)` | The chosen variant value ids, sorted, comma separated; empty for a plain product |
| `Quantity` | `int` | Check 1 to 10,000 |
| `AddedUnitPrice` | `decimal(18,4)` | The unit price when the line was added or last changed (D7) |
| `CreatedOnUtc`, `UpdatedOnUtc` | `datetime2` | |

Unique index `UX_CartItem_Customer_Product_Values (CustomerId, ProductId, ValueIds)`; index on `ProductId`. `Down()` drops the table.

## 8. Use cases and service contracts

```text
CartRules (pure)    ValueKey(ids), Issues(...), CanCheckout(lines)
ICartStore          GetLines, GetLine, Find, Insert (false when the line exists), UpdateLine, Delete, Clear, SetAddedPrices, CountLines
ICartService        GetAsync, AddAsync, SetQuantityAsync, RemoveAsync, ClearAsync, AcceptPricesAsync, CountAsync     (all take the customer id)
CartView            lines grouped by shop, subtotal, item count, currency, canCheckout
```

Field errors (`400`): `errors.quantity`, `errors.valueIds`, `errors.productId`. Business codes (`409`): `cart.line_limit`, `cart.own_product`. Not found: product not visible, line not in this customer's cart.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/cart` | the full view |
| 2 | `GET` | `/api/v1/cart/count` | `{ count }` number of units |
| 3 | `POST` | `/api/v1/cart/items` body `{ productId, quantity, valueIds }` | answers the view |
| 4 | `PUT` | `/api/v1/cart/items/{lineId}` body `{ quantity }` | answers the view |
| 5 | `DELETE` | `/api/v1/cart/items/{lineId}` | answers the view |
| 6 | `DELETE` | `/api/v1/cart` | answers the empty view |
| 7 | `POST` | `/api/v1/cart/accept-prices` | answers the view |

All routes need a signed-in customer; write routes need the CSRF token like every other write.

## 10. Angular

- `core/cart/cart-api.service.ts`, `cart.service.ts` (count signal for the header, add helper that handles guests and variants).
- `storefront/pages/cart.page.ts`, route `/storefront/cart`; header link; product page and card buttons.
- States: loading, empty cart, guest, issues per line, saving, conflict (limit, own product), network error.

## 11. Events, jobs, cache

None. Cart rows are small and read by their owner only. Old carts are cleaned by F29; stock is not held.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Add | Visible product only; quantity limits; line limit; same choice adds up; variants need full selection; foreign values refused; own shop refused; price snapshot |
| Stock | Above available refused with the number; tracked and untracked; variant stock; total quantity of an existing line is checked |
| Change | Quantity checks; another customer's line is not found; remove; clear |
| View | Grouped by shop; subtotal excludes unavailable lines; each issue appears (unavailable, variant gone, out of stock, insufficient, price changed); `canCheckout` |
| Pricing | Tier price follows the quantity; accept prices refreshes the snapshot |
| Isolation | Customers only see their own lines |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (unique index, upsert race), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check `CartItem` and its unique index.
2. As a guest click "Add to cart" on a card: you are sent to sign in. `GET /api/v1/cart` anonymously: `401`.
3. Sign in as a customer. Add a plain product from its page with quantity 3, then add it again: one line with quantity 6.
4. Add a product with variants from its page after choosing values; from a card it opens the product page instead.
5. Add products from two shops: the cart shows two groups with their own subtotals.
6. Set a quantity above the available stock: refused and the message says how many are available. Set a tier quantity: the unit price drops.
7. In the seller portal change the price of a line's product: the cart shows a price notice; "Accept new prices" clears it. Stop selling a product, or reduce its stock to 0: the line shows its issue and "Checkout" stays disabled.
8. As a member of a shop try to add that shop's product: refused.

## 13. Rollout and compatibility

Run the migrator before the API. No existing screen or API changes meaning; the product page and the cards get a working button and the header a link.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Guest cart and merge, wishlist, min/max/step quantities | F16-B |
| Checkout attributes and notes | F16-C |
| Reservation at checkout, order creation | F17, F18 |
| Tax and shipping estimates | F07-E, F20 |
| Clean up old carts | F29 |
