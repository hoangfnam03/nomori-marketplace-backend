# F20-B Carriers and parcel tracking

| | |
|---|---|
| **Module ID** | F20-B (slice of F20 "Shipping, pickup, shipment, warehouse and fulfilment"): a carrier list and the journey of a parcel |
| **Status** | Backend and Angular implemented; 957 service tests (26 new) and 43 data tests pass, the Angular build, lint and 37 unit tests pass. The migration, the store SQL and the HTTP flow were run against SQL Server LocalDB. The screens were not opened in a browser. |
| **Branch** | `feat/shipments/foundation` (backend and frontend) |
| **Depends on** | F18-A (orders, `Carrier` and `TrackingNumber` on the shop order), F03 (permissions) |
| **Unblocks** | F22-C (emails on tracking events), F20-C (rates by weight, partial shipments) |

## 1. Purpose

F18-A already stores a carrier name and a tracking number on a shipped shop order. What was missing: a list of carriers to pick from, a link to the carrier's own page, and the journey of the parcel so a customer can see where it is.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **No second shipment table.** A shop order has at most one parcel (the F18-A carrier and tracking number). Partial shipments of one shop order (several parcels, items per parcel) need changes to the order status rules and go to F20-C. |
| D2 | **The shop order keeps the carrier as text.** The `Carrier` table is a pick list plus an address template, not a foreign key: deleting a carrier never changes an order. The link is built by matching the order's carrier name to an active carrier. |
| D3 | **A tracking address is a template** with `{tracking}`; only `http` and `https` addresses that contain it are accepted, so a carrier cannot become a `javascript:` link. The tracking number is URL-escaped in the link. |
| D4 | **Events are append-only** and are added by the shop while the shop order is `shipped` (`409 tracking.not_shipped` otherwise). A wrong line is answered with a newer one. At most 50 per shop order, counted and inserted in one statement. |
| D5 | One read route for everyone who may look (`GET /shop-orders/{id}/tracking`): the customer who bought it, a member of the shop (not while the order awaits payment) or an administrator with `orders.manage`. Anyone else gets `404`. |
| D6 | The event time may not be in the future (5 minutes of tolerance) or before the order was placed. Without a time it is "now". |
| D7 | Carriers are managed with the existing permission `orders.manage`; no new permission. |

## 3. Authorization matrix

| Action | Customer | Shop member | Administrator (`orders.manage`) |
|---|---|---|---|
| See the parcel and its journey | own orders | own shop | all |
| Add a tracking event | no | own shop, while shipped | no |
| See the pick list of carriers | yes | yes | yes |
| Create, edit, delete carriers | no | no | Yes |

## 4. Included behavior

- Carriers: name (unique, case-insensitive), optional tracking address, active flag, order; seeded with DHL, FedEx, UPS (with links) and Viettel Post, Giao Hang Nhanh, Giao Hang Tiet Kiem (no link).
- Events: status `picked_up`, `in_transit`, `out_for_delivery`, `delivery_attempted`, `exception`, `info`; optional location (100) and note (500); time.
- The shop's ship form offers the carriers as suggestions; a shop may still type another name.
- The customer, shop and admin order screens show the carrier, a link on the tracking number and the journey; the shop also gets the form to add an update.
- Audit: `carrier.created`, `carrier.updated`, `carrier.deleted`, `shipping.tracking_event`.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Several parcels per shop order, items per parcel, partial shipment | F20-C |
| Rates by weight or size, live carrier rates and labels (carrier APIs) | F20-C / F28 |
| Pickup points, warehouses | F20-C / F12-B |
| Email or notification to the customer on a tracking event | F22-C |
| Editing or deleting an event | not planned (append-only) |
| Automatic events from a carrier (webhooks) | F28 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Shipment` (`TrackingNumber`, `ShippedDateUtc`, `DeliveryDateUtc`, `ShipmentItem`) | Tracking number and dates | Kept on the shop order (F18-A); events instead of one delivery date |
| `IShipmentTracker` (plugin per carrier, `GetUrl`, `GetShipmentEvents`) | Link and events | A URL template per carrier instead of a plugin; events written by the shop |
| `ShippingMethod`/`IShippingRateComputationMethod` | Carrier choice | Not the same thing: rates stay per shop (F20-A); this is only the carrier name |

## 7. Data model (migration `202611050001 TrackingMigration`)

- `Carrier(Id, Name unique, TrackingUrlTemplate null, Active, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)` with the six seed rows.
- `ShopOrderTrackingEvent(Id bigint, ShopOrderId FK, Status, Location null, Note null, OccurredOnUtc, CreatedByCustomerId, CreatedOnUtc)`, index `(ShopOrderId, OccurredOnUtc DESC)`.

## 8. Contracts

```text
TrackingRules      Statuses, IsValidTemplate, UrlFor(template, number)
ITrackingStore     carriers (list, get, find active by name, count, name exists, insert, update, delete), TryAddEvent(max), ListEvents
ITrackingService   GetCarriers, Create/Update/DeleteCarrier, GetTracking(caller, shopOrderId), AddEvent(vendorId, shopOrderId, command, actor)
```

Business codes (`409`): `carrier.name_exists`, `carrier.limit`, `tracking.not_shipped`, `tracking.event_limit`. Field errors: `name`, `trackingUrlTemplate`, `displayOrder`, `status`, `location`, `note`, `occurredOnUtc`.

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/shipping/carriers` | signed in: active carriers (id, name) |
| 2 | `GET` `POST` `PUT` `DELETE` | `/api/v1/admin/shipping/carriers[/{id}]` | `orders.manage` |
| 3 | `GET` | `/api/v1/shop-orders/{id}/tracking` | customer, shop member, admin (D5) |
| 4 | `POST` | `/api/v1/vendors/{vendorId}/orders/{id}/tracking-events` | shop member, CSRF |

## 10. Angular

`/admin/carriers` (permission `orders.manage`); the shared `app-parcel-tracking` component inside the shop order panel (customer `/customer/orders/{id}`, shop `/vendor/orders/{id}`, admin orders); the ship form of `/vendor/orders/{id}` suggests the carriers. States: loading, empty, saving, validation, `409`, forbidden.

## 11. Events, jobs, cache

None.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | template accepted only as http(s) with the placeholder; the number is escaped |
| Carriers | validation, unique name, limit, update, delete, only active ones offered |
| Visibility | buyer, shop and admin see the same parcel with a link; another buyer or shop gets not found; unpaid order hidden from the shop; no link without a template or when the carrier is inactive |
| Events | newest first, default time, validation, only while shipped, only the owning shop, limit |
| Migration | version ordering |

Run by hand against SQL Server LocalDB over HTTP: migration on top of the earlier ones, shipping a COD order, events before shipping (`409`), events while shipped, a bad status and a future time (`400`), the buyer, the shop and the admin reading the parcel with the DHL link, another buyer and another shop (`404`), carrier create with a `javascript:` address (`400`), duplicate name (`409`), a seller on the admin route (`403`), and events after delivery (`409`).

### Manual test guide

1. Run the migrator and start the API. As admin open `/admin/carriers`: six carriers.
2. As a shop, confirm an order and ship it with carrier `DHL`: the carrier name is suggested while typing.
3. On the order add updates (picked up, in transit): they appear newest first. As the customer open the order: same journey, and the tracking number is a link to DHL.
4. Mark the order delivered: the add form disappears and the API refuses new events.
5. As admin add a carrier with the address `https://x.example/?n={tracking}`; an address without `{tracking}` or with `javascript:` is refused.

## 13. Rollout and compatibility

Run the migrator before the API. Orders already shipped have no events and get a link when their carrier name matches one of the seeded names (for example `DHL`).

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Partial shipments, several parcels per shop order | F20-C |
| Email on `out_for_delivery` or `exception` | F22-C |
| Carrier webhooks and label printing | F28 |
| A carrier name is matched by exact text, so `dhl` on an old order has no link | F20-C |
