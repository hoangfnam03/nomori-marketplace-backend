using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Data.Catalog;

namespace Nomori.Marketplace.Data.Orders;

public sealed class SqlOrderStore(IOptions<DatabaseOptions> options) : IOrderStore
{
    private const string StoreOrderColumns = """
        so.Id, so.OrderId, so.VendorId, v.Name, so.SubOrderNumber, so.Status, so.PaymentStatus, so.ItemsTotal, so.ShippingFee, so.Total,
        so.CustomerNote, so.Carrier, so.TrackingNumber, so.ConfirmByUtc, so.ConfirmedOnUtc, so.ShippedOnUtc, so.DeliveredOnUtc,
        so.CompletedOnUtc, so.CancelledOnUtc, so.CancelReason, so.CancelNote, so.CancelledBy, so.CreatedOnUtc, so.UpdatedOnUtc, o.OrderNumber, o.CustomerId
        """;

    private const string StoreOrderFrom = "FROM StoreOrder so INNER JOIN CustomerOrder o ON o.Id = so.OrderId INNER JOIN Vendor v ON v.Id = so.VendorId";

    public async Task<PlaceOrderStoreResult> PlaceAsync(CustomerOrder order, IReadOnlyList<int> cartItemIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var now = order.CreatedOnUtc;

        // The range lock makes a second request with the same key wait here, then find the first order.
        var existing = await ScalarAsync(connection, transaction,
            "SELECT Id FROM CustomerOrder WITH (UPDLOCK, HOLDLOCK) WHERE CustomerId = @C AND IdempotencyKey = @K",
            cancellationToken, ("@C", order.CustomerId), ("@K", order.IdempotencyKey));
        if (existing is { } existingId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PlaceOrderStoreResult(PlaceOrderOutcome.Duplicate, existingId);
        }

        // Taking the cart lines first also locks them: an order placed from another tab with the same lines waits, then finds them gone.
        var deleted = await NonQueryAsync(connection, transaction,
            $"DELETE FROM CartItem WHERE CustomerId = @C AND Id IN ({string.Join(',', cartItemIds.Select((_, i) => $"@I{i}"))})",
            cancellationToken, [("@C", order.CustomerId), .. cartItemIds.Select((id, i) => ($"@I{i}", (object?)id))]);
        if (deleted != cartItemIds.Distinct().Count())
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PlaceOrderStoreResult(PlaceOrderOutcome.CartItemMissing);
        }

        order.OrderNumber = OrderRules.OrderNumber(now.Date, await NextSequenceAsync(connection, transaction, now.Date, cancellationToken));
        for (var i = 0; i < order.StoreOrders.Count; i++)
            order.StoreOrders[i].SubOrderNumber = OrderRules.SubOrderNumber(order.OrderNumber, i + 1);

        // Stock of tracked lines, grouped per stock row and locked in a fixed order so two orders never deadlock.
        var wanted = order.StoreOrders
            .SelectMany(s => s.Items.Where(i => i.StockDeducted).Select(i => (StoreOrder: s, Item: i)))
            .GroupBy(x => (x.Item.ProductId, x.Item.CombinationId))
            .OrderBy(g => g.Key.ProductId).ThenBy(g => g.Key.CombinationId ?? 0);
        foreach (var row in wanted)
        {
            var quantity = row.Sum(x => x.Item.Quantity);
            var onHand = await SqlInventoryStore.LockAsync(connection, transaction, row.Key.ProductId, row.Key.CombinationId, cancellationToken);
            var reserved = onHand is null ? 0
                : await SqlInventoryStore.ReservedAsync(connection, transaction, row.Key.ProductId, row.Key.CombinationId, null, now, cancellationToken);
            if (onHand is null || StockRules.Available(onHand.Value, reserved) < quantity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PlaceOrderStoreResult(PlaceOrderOutcome.InsufficientStock, ProductId: row.Key.ProductId);
            }

            await SqlInventoryStore.ApplyDeltaAsync(connection, transaction, row.Key.ProductId, row.Key.CombinationId, -quantity, now, cancellationToken);
            await SqlInventoryStore.InsertMovementAsync(connection, transaction, new StockMovement
            {
                ProductId = row.Key.ProductId, CombinationId = row.Key.CombinationId, Delta = -quantity, QuantityAfter = onHand.Value - quantity,
                Reason = StockReasons.Sale, Reference = row.First().StoreOrder.SubOrderNumber, ActorCustomerId = order.CustomerId, CreatedOnUtc = now
            }, cancellationToken);
        }

        var a = order.ShippingAddress;
        order.Id = (await ScalarAsync(connection, transaction, """
            INSERT INTO CustomerOrder (OrderNumber, CustomerId, CurrencyCode, ItemsTotal, ShippingTotal, Total, PaymentMethod,
                ShipFirstName, ShipLastName, ShipCompany, ShipAddress1, ShipAddress2, ShipCity, ShipStateProvince, ShipCountryCode, ShipZipPostalCode, ShipPhoneNumber,
                IdempotencyKey, CreatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Number, @C, @Currency, @Items, @Shipping, @Total, @Method,
                @First, @Last, @Company, @A1, @A2, @City, @State, @Country, @Zip, @Phone, @Key, @Now)
            """, cancellationToken,
            ("@Number", order.OrderNumber), ("@C", order.CustomerId), ("@Currency", order.CurrencyCode), ("@Items", order.ItemsTotal),
            ("@Shipping", order.ShippingTotal), ("@Total", order.Total), ("@Method", (int)order.PaymentMethod),
            ("@First", a.FirstName), ("@Last", a.LastName), ("@Company", a.Company), ("@A1", a.Address1), ("@A2", a.Address2), ("@City", a.City),
            ("@State", a.StateProvince), ("@Country", a.CountryCode), ("@Zip", a.ZipPostalCode), ("@Phone", a.PhoneNumber),
            ("@Key", order.IdempotencyKey), ("@Now", now)))!.Value;

        foreach (var storeOrder in order.StoreOrders)
        {
            storeOrder.OrderId = order.Id;
            storeOrder.Id = (await ScalarAsync(connection, transaction, """
                INSERT INTO StoreOrder (OrderId, VendorId, SubOrderNumber, Status, PaymentStatus, ItemsTotal, ShippingFee, Total, CustomerNote,
                    ConfirmByUtc, CreatedOnUtc, UpdatedOnUtc)
                OUTPUT INSERTED.Id
                VALUES (@Order, @Vendor, @Number, @Status, @Payment, @Items, @Fee, @Total, @Note, @ConfirmBy, @Now, @Now)
                """, cancellationToken,
                ("@Order", order.Id), ("@Vendor", storeOrder.VendorId), ("@Number", storeOrder.SubOrderNumber), ("@Status", (int)storeOrder.Status),
                ("@Payment", (int)storeOrder.PaymentStatus), ("@Items", storeOrder.ItemsTotal), ("@Fee", storeOrder.ShippingFee), ("@Total", storeOrder.Total),
                ("@Note", storeOrder.CustomerNote), ("@ConfirmBy", storeOrder.ConfirmByUtc), ("@Now", now)))!.Value;

            foreach (var item in storeOrder.Items)
            {
                item.StoreOrderId = storeOrder.Id;
                item.Id = (await ScalarAsync(connection, transaction, """
                    INSERT INTO OrderItem (StoreOrderId, ProductId, CombinationId, ValueIds, ProductName, VariantDescription, Sku, PictureId,
                        UnitPrice, Quantity, LineTotal, StockDeducted)
                    OUTPUT INSERTED.Id
                    VALUES (@SO, @P, @Comb, @Values, @Name, @Variant, @Sku, @Picture, @Unit, @Qty, @Line, @Deducted)
                    """, cancellationToken,
                    ("@SO", storeOrder.Id), ("@P", item.ProductId), ("@Comb", item.CombinationId), ("@Values", item.ValueIds), ("@Name", item.ProductName),
                    ("@Variant", item.VariantDescription), ("@Sku", item.Sku), ("@Picture", item.PictureId), ("@Unit", item.UnitPrice),
                    ("@Qty", item.Quantity), ("@Line", item.LineTotal), ("@Deducted", item.StockDeducted)))!.Value;
            }

            foreach (var e in storeOrder.Events)
            {
                e.StoreOrderId = storeOrder.Id;
                await InsertEventAsync(connection, transaction, e, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new PlaceOrderStoreResult(PlaceOrderOutcome.Created, order.Id);
    }

    public async Task<int?> FindIdByIdempotencyKeyAsync(int customerId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarAsync(connection, null, "SELECT Id FROM CustomerOrder WHERE CustomerId = @C AND IdempotencyKey = @K",
            cancellationToken, ("@C", customerId), ("@K", idempotencyKey));
    }

    public async Task<CustomerOrder?> GetAsync(int orderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        CustomerOrder? order;
        await using (var cmd = Command(connection, null, """
            SELECT Id, OrderNumber, CustomerId, CurrencyCode, ItemsTotal, ShippingTotal, Total, PaymentMethod,
                   ShipFirstName, ShipLastName, ShipCompany, ShipAddress1, ShipAddress2, ShipCity, ShipStateProvince, ShipCountryCode, ShipZipPostalCode, ShipPhoneNumber,
                   IdempotencyKey, CreatedOnUtc
            FROM CustomerOrder WHERE Id = @Id
            """, [("@Id", orderId)]))
        await using (var r = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await r.ReadAsync(cancellationToken)) return null;
            order = new CustomerOrder
            {
                Id = r.GetInt32(0), OrderNumber = r.GetString(1), CustomerId = r.GetInt32(2), CurrencyCode = r.GetString(3).Trim(),
                ItemsTotal = r.GetDecimal(4), ShippingTotal = r.GetDecimal(5), Total = r.GetDecimal(6), PaymentMethod = (PaymentMethod)r.GetInt32(7),
                ShippingAddress = new OrderAddress
                {
                    FirstName = r.GetString(8), LastName = r.GetString(9), Company = NullableString(r, 10), Address1 = r.GetString(11),
                    Address2 = NullableString(r, 12), City = r.GetString(13), StateProvince = NullableString(r, 14), CountryCode = r.GetString(15),
                    ZipPostalCode = NullableString(r, 16), PhoneNumber = r.GetString(17)
                },
                IdempotencyKey = r.GetString(18), CreatedOnUtc = r.GetDateTime(19)
            };
        }

        order.StoreOrders = await ReadStoreOrdersAsync(connection, $"SELECT {StoreOrderColumns} {StoreOrderFrom} WHERE so.OrderId = @Id ORDER BY so.Id",
            [("@Id", orderId)], cancellationToken);
        await LoadItemsAsync(connection, order.StoreOrders, cancellationToken);
        await LoadEventsAsync(connection, order.StoreOrders, cancellationToken);
        return order;
    }

    public async Task<(StoreOrder StoreOrder, int CustomerId)?> GetStoreOrderAsync(int storeOrderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var customerId = await ScalarAsync(connection, null,
            "SELECT o.CustomerId FROM StoreOrder so INNER JOIN CustomerOrder o ON o.Id = so.OrderId WHERE so.Id = @Id",
            cancellationToken, ("@Id", storeOrderId));
        if (customerId is null) return null;

        var list = await ReadStoreOrdersAsync(connection, $"SELECT {StoreOrderColumns} {StoreOrderFrom} WHERE so.Id = @Id", [("@Id", storeOrderId)], cancellationToken);
        await LoadItemsAsync(connection, list, cancellationToken);
        return (list[0], customerId.Value);
    }

    public async Task<StoreOrderPage> ListForCustomerAsync(StoreOrderQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var where = new List<string> { "o.CustomerId = @C" };
        var parameters = new List<(string, object?)> { ("@C", query.CustomerId) };
        if (OrderTabs.Statuses(query.Tab) is { } statuses)
        {
            where.Add($"so.Status IN ({string.Join(',', statuses.Select((_, i) => $"@S{i}"))})");
            parameters.AddRange(statuses.Select((s, i) => ($"@S{i}", (object?)(int)s)));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("""
                (so.SubOrderNumber LIKE @Q ESCAPE '\' OR v.Name LIKE @Q ESCAPE '\'
                 OR EXISTS (SELECT 1 FROM OrderItem i WHERE i.StoreOrderId = so.Id AND i.ProductName LIKE @Q ESCAPE '\'))
                """);
            parameters.Add(("@Q", $"%{EscapeLike(query.Search.Trim())}%"));
        }
        var filter = string.Join(" AND ", where);

        var total = await ScalarAsync(connection, null, $"SELECT COUNT(*) {StoreOrderFrom} WHERE {filter}", cancellationToken, [.. parameters]) ?? 0;
        var items = await ReadStoreOrdersAsync(connection,
            $"SELECT {StoreOrderColumns} {StoreOrderFrom} WHERE {filter} ORDER BY so.CreatedOnUtc DESC, so.Id DESC OFFSET @Offset ROWS FETCH NEXT @Size ROWS ONLY",
            [.. parameters, ("@Offset", (query.Page - 1) * query.PageSize), ("@Size", query.PageSize)], cancellationToken);
        await LoadItemsAsync(connection, items, cancellationToken);

        // Tab counts ignore the search, so the tabs keep showing how many orders each status holds.
        var byStatus = new Dictionary<StoreOrderStatus, int>();
        await using (var cmd = Command(connection, null,
            "SELECT so.Status, COUNT(*) FROM StoreOrder so INNER JOIN CustomerOrder o ON o.Id = so.OrderId WHERE o.CustomerId = @C GROUP BY so.Status",
            [("@C", query.CustomerId)]))
        await using (var r = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken)) byStatus[(StoreOrderStatus)r.GetInt32(0)] = r.GetInt32(1);
        }
        var tabCounts = OrderTabs.Ordered.ToDictionary(
            tab => tab,
            tab => OrderTabs.Statuses(tab) is { } s ? s.Sum(status => byStatus.GetValueOrDefault(status)) : byStatus.Values.Sum());

        return new StoreOrderPage(items, total, query.Page, query.PageSize, tabCounts);
    }

    public async Task<bool> TransitionAsync(StoreOrderTransition transition, CancellationToken cancellationToken)
    {
        var t = transition;
        // Constant column names chosen from the target status; never user input.
        var stampColumn = t.To switch
        {
            StoreOrderStatus.Confirmed => "ConfirmedOnUtc",
            StoreOrderStatus.Shipped => "ShippedOnUtc",
            StoreOrderStatus.Delivered => "DeliveredOnUtc",
            StoreOrderStatus.Completed => "CompletedOnUtc",
            StoreOrderStatus.Cancelled => "CancelledOnUtc",
            _ => throw new ArgumentOutOfRangeException(nameof(transition), "A shop order cannot move back to pending.")
        };
        var cancelling = t.To == StoreOrderStatus.Cancelled;
        var shipping = t.To == StoreOrderStatus.Shipped;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var updated = await NonQueryAsync(connection, transaction, $"""
            UPDATE StoreOrder SET Status = @To, {stampColumn} = @Now, UpdatedOnUtc = @Now,
                PaymentStatus = COALESCE(@Payment, PaymentStatus),
                CancelReason = CASE WHEN @Cancelling = 1 THEN @Reason ELSE CancelReason END,
                CancelNote = CASE WHEN @Cancelling = 1 THEN @Note ELSE CancelNote END,
                CancelledBy = CASE WHEN @Cancelling = 1 THEN @Actor ELSE CancelledBy END,
                Carrier = CASE WHEN @Shipping = 1 THEN @Carrier ELSE Carrier END,
                TrackingNumber = CASE WHEN @Shipping = 1 THEN @Tracking ELSE TrackingNumber END
            WHERE Id = @Id AND Status = @From
            """, cancellationToken,
            ("@To", (int)t.To), ("@Now", t.NowUtc), ("@Payment", t.NewPaymentStatus is { } p ? (int)p : null), ("@Cancelling", cancelling),
            ("@Reason", t.Reason), ("@Note", t.Note), ("@Actor", (int)t.ActorType), ("@Id", t.StoreOrderId), ("@From", (int)t.From),
            ("@Shipping", shipping), ("@Carrier", t.Carrier), ("@Tracking", t.TrackingNumber));
        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (cancelling && t.Restock) await RestockAsync(connection, transaction, t, cancellationToken);

        await InsertEventAsync(connection, transaction, new StoreOrderEvent
        {
            StoreOrderId = t.StoreOrderId, FromStatus = t.From, ToStatus = t.To, ActorType = t.ActorType, ActorCustomerId = t.ActorCustomerId,
            Reason = t.Reason, Note = t.Note, CreatedOnUtc = t.NowUtc
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<StoreOrderPage> ListForVendorAsync(VendorOrderQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var where = new List<string> { "so.VendorId = @V" };
        var parameters = new List<(string, object?)> { ("@V", query.VendorId) };
        if (VendorOrderTabs.Statuses(query.Tab) is { } statuses)
        {
            where.Add("so.Status = @S");
            parameters.Add(("@S", (int)statuses[0]));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("""
                (so.SubOrderNumber LIKE @Q ESCAPE '\' OR o.ShipPhoneNumber LIKE @Q ESCAPE '\'
                 OR CONCAT(o.ShipLastName, ' ', o.ShipFirstName) LIKE @Q ESCAPE '\' OR CONCAT(o.ShipFirstName, ' ', o.ShipLastName) LIKE @Q ESCAPE '\')
                """);
            parameters.Add(("@Q", $"%{EscapeLike(query.Search.Trim())}%"));
        }
        if (query.FromUtc is { } from) { where.Add("so.CreatedOnUtc >= @From"); parameters.Add(("@From", from)); }
        if (query.ToUtc is { } to) { where.Add("so.CreatedOnUtc < @To"); parameters.Add(("@To", to)); }
        var filter = string.Join(" AND ", where);

        var total = await ScalarAsync(connection, null, $"SELECT COUNT(*) {StoreOrderFrom} WHERE {filter}", cancellationToken, [.. parameters]) ?? 0;
        var items = await ReadStoreOrdersAsync(connection,
            $"SELECT {StoreOrderColumns}, CONCAT(o.ShipLastName, ' ', o.ShipFirstName), o.ShipPhoneNumber {StoreOrderFrom} WHERE {filter} ORDER BY so.CreatedOnUtc DESC, so.Id DESC OFFSET @Offset ROWS FETCH NEXT @Size ROWS ONLY",
            [.. parameters, ("@Offset", (query.Page - 1) * query.PageSize), ("@Size", query.PageSize)], cancellationToken);
        await LoadItemsAsync(connection, items, cancellationToken);

        var byStatus = new Dictionary<StoreOrderStatus, int>();
        await using (var cmd = Command(connection, null, "SELECT Status, COUNT(*) FROM StoreOrder WHERE VendorId = @V GROUP BY Status", [("@V", query.VendorId)]))
        await using (var r = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken)) byStatus[(StoreOrderStatus)r.GetInt32(0)] = r.GetInt32(1);
        }
        var tabCounts = VendorOrderTabs.Ordered.ToDictionary(
            tab => tab,
            tab => VendorOrderTabs.Statuses(tab) is { } s ? byStatus.GetValueOrDefault(s[0]) : byStatus.Values.Sum());

        return new StoreOrderPage(items, total, query.Page, query.PageSize, tabCounts);
    }

    public async Task<bool> UpdateShipmentAsync(
        int storeOrderId, string carrier, string trackingNumber, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var updated = await NonQueryAsync(connection, transaction, """
            UPDATE StoreOrder SET Carrier = @Carrier, TrackingNumber = @Tracking, UpdatedOnUtc = @Now
            WHERE Id = @Id AND Status = @Shipped
            """, cancellationToken,
            ("@Carrier", carrier), ("@Tracking", trackingNumber), ("@Now", nowUtc), ("@Id", storeOrderId), ("@Shipped", (int)StoreOrderStatus.Shipped));
        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await InsertEventAsync(connection, transaction, new StoreOrderEvent
        {
            StoreOrderId = storeOrderId, FromStatus = StoreOrderStatus.Shipped, ToStatus = StoreOrderStatus.Shipped, ActorType = OrderActorType.Vendor,
            ActorCustomerId = actorCustomerId, Reason = SystemCancelReasons.TrackingUpdated, Note = $"{carrier} {trackingNumber}", CreatedOnUtc = nowUtc
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<int>> FindDueAsync(StoreOrderStatus status, DateTime dueBeforeUtc, int take, CancellationToken cancellationToken)
    {
        // Constant column names chosen from the status; never user input.
        var column = status switch
        {
            StoreOrderStatus.Pending => "ConfirmByUtc",
            StoreOrderStatus.Shipped => "ShippedOnUtc",
            StoreOrderStatus.Delivered => "DeliveredOnUtc",
            _ => throw new ArgumentOutOfRangeException(nameof(status), "Only pending, shipped and delivered orders move on by themselves.")
        };

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, null,
            $"SELECT TOP (@Take) Id FROM StoreOrder WHERE Status = @S AND {column} < @Due ORDER BY {column}, Id",
            [("@Take", take), ("@S", (int)status), ("@Due", dueBeforeUtc)]);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await r.ReadAsync(cancellationToken)) ids.Add(r.GetInt32(0));
        return ids;
    }

    // ---- Helpers ----

    /// <summary>Puts back the stock the lines of a cancelled shop order took, with a ledger row per stock row.</summary>
    private static async Task RestockAsync(SqlConnection connection, SqlTransaction transaction, StoreOrderTransition t, CancellationToken cancellationToken)
    {
        string subOrderNumber;
        var rows = new List<(int ProductId, int? CombinationId, int Quantity)>();
        await using (var cmd = Command(connection, transaction, """
            SELECT so.SubOrderNumber, i.ProductId, i.CombinationId, SUM(i.Quantity)
            FROM OrderItem i INNER JOIN StoreOrder so ON so.Id = i.StoreOrderId
            WHERE i.StoreOrderId = @Id AND i.StockDeducted = 1
            GROUP BY so.SubOrderNumber, i.ProductId, i.CombinationId
            ORDER BY i.ProductId, i.CombinationId
            """, [("@Id", t.StoreOrderId)]))
        await using (var r = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            subOrderNumber = string.Empty;
            while (await r.ReadAsync(cancellationToken))
            {
                subOrderNumber = r.GetString(0);
                rows.Add((r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetInt32(3)));
            }
        }

        foreach (var (productId, combinationId, quantity) in rows)
        {
            // A product or variant deleted since the order has no stock row left to put the units back into.
            var onHand = await SqlInventoryStore.LockAsync(connection, transaction, productId, combinationId, cancellationToken);
            if (onHand is null) continue;

            await SqlInventoryStore.ApplyDeltaAsync(connection, transaction, productId, combinationId, quantity, t.NowUtc, cancellationToken);
            await SqlInventoryStore.InsertMovementAsync(connection, transaction, new StockMovement
            {
                ProductId = productId, CombinationId = combinationId, Delta = quantity, QuantityAfter = onHand.Value + quantity,
                Reason = StockReasons.OrderCancelled, Reference = subOrderNumber, ActorCustomerId = t.ActorCustomerId, CreatedOnUtc = t.NowUtc
            }, cancellationToken);
        }
    }

    private static async Task<int> NextSequenceAsync(SqlConnection connection, SqlTransaction transaction, DateTime day, CancellationToken cancellationToken)
    {
        // Upsert under a range lock: orders placed at the same moment take numbers one after the other.
        await NonQueryAsync(connection, transaction, """
            IF NOT EXISTS (SELECT 1 FROM OrderNumberSequence WITH (UPDLOCK, HOLDLOCK) WHERE Day = @Day)
                INSERT INTO OrderNumberSequence (Day, LastValue) VALUES (@Day, 0);
            """, cancellationToken, ("@Day", day));
        return (await ScalarAsync(connection, transaction,
            "UPDATE OrderNumberSequence SET LastValue = LastValue + 1 OUTPUT INSERTED.LastValue WHERE Day = @Day",
            cancellationToken, ("@Day", day)))!.Value;
    }

    private static Task<int> InsertEventAsync(SqlConnection connection, SqlTransaction transaction, StoreOrderEvent e, CancellationToken cancellationToken) =>
        NonQueryAsync(connection, transaction, """
            INSERT INTO StoreOrderEvent (StoreOrderId, FromStatus, ToStatus, ActorType, ActorCustomerId, Reason, Note, CreatedOnUtc)
            VALUES (@SO, @From, @To, @Type, @Actor, @Reason, @Note, @Now)
            """, cancellationToken,
            ("@SO", e.StoreOrderId), ("@From", e.FromStatus is { } f ? (int)f : null), ("@To", (int)e.ToStatus), ("@Type", (int)e.ActorType),
            ("@Actor", e.ActorCustomerId), ("@Reason", e.Reason), ("@Note", e.Note), ("@Now", e.CreatedOnUtc));

    private static async Task<List<StoreOrder>> ReadStoreOrdersAsync(
        SqlConnection connection, string sql, (string, object?)[] parameters, CancellationToken cancellationToken)
    {
        var list = new List<StoreOrder>();
        await using var cmd = Command(connection, null, sql, parameters);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new StoreOrder
            {
                Id = r.GetInt32(0), OrderId = r.GetInt32(1), VendorId = r.GetInt32(2), VendorName = r.GetString(3), SubOrderNumber = r.GetString(4),
                Status = (StoreOrderStatus)r.GetInt32(5), PaymentStatus = (PaymentStatus)r.GetInt32(6), ItemsTotal = r.GetDecimal(7),
                ShippingFee = r.GetDecimal(8), Total = r.GetDecimal(9), CustomerNote = NullableString(r, 10), Carrier = NullableString(r, 11),
                TrackingNumber = NullableString(r, 12), ConfirmByUtc = r.GetDateTime(13), ConfirmedOnUtc = NullableDate(r, 14),
                ShippedOnUtc = NullableDate(r, 15), DeliveredOnUtc = NullableDate(r, 16), CompletedOnUtc = NullableDate(r, 17),
                CancelledOnUtc = NullableDate(r, 18), CancelReason = NullableString(r, 19), CancelNote = NullableString(r, 20),
                CancelledBy = r.IsDBNull(21) ? null : (OrderActorType)r.GetInt32(21), CreatedOnUtc = r.GetDateTime(22), UpdatedOnUtc = r.GetDateTime(23),
                OrderNumber = r.GetString(24),
                RecipientName = r.FieldCount > 27 ? r.GetString(26).Trim() : null,
                RecipientPhone = r.FieldCount > 27 ? r.GetString(27) : null
            });
        }
        return list;
    }

    private static async Task LoadItemsAsync(SqlConnection connection, List<StoreOrder> storeOrders, CancellationToken cancellationToken)
    {
        if (storeOrders.Count == 0) return;
        var byId = storeOrders.ToDictionary(s => s.Id);
        var ids = storeOrders.Select((s, i) => ($"@SO{i}", (object?)s.Id)).ToArray();
        await using var cmd = Command(connection, null, $"""
            SELECT Id, StoreOrderId, ProductId, CombinationId, ValueIds, ProductName, VariantDescription, Sku, PictureId, UnitPrice, Quantity, LineTotal, StockDeducted
            FROM OrderItem WHERE StoreOrderId IN ({string.Join(',', ids.Select(p => p.Item1))}) ORDER BY Id
            """, ids);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            byId[r.GetInt32(1)].Items.Add(new OrderItem
            {
                Id = r.GetInt32(0), StoreOrderId = r.GetInt32(1), ProductId = r.GetInt32(2), CombinationId = r.IsDBNull(3) ? null : r.GetInt32(3),
                ValueIds = r.GetString(4), ProductName = r.GetString(5), VariantDescription = NullableString(r, 6), Sku = NullableString(r, 7),
                PictureId = r.GetInt32(8), UnitPrice = r.GetDecimal(9), Quantity = r.GetInt32(10), LineTotal = r.GetDecimal(11), StockDeducted = r.GetBoolean(12)
            });
        }
    }

    private static async Task LoadEventsAsync(SqlConnection connection, List<StoreOrder> storeOrders, CancellationToken cancellationToken)
    {
        if (storeOrders.Count == 0) return;
        var byId = storeOrders.ToDictionary(s => s.Id);
        var ids = storeOrders.Select((s, i) => ($"@SO{i}", (object?)s.Id)).ToArray();
        await using var cmd = Command(connection, null, $"""
            SELECT Id, StoreOrderId, FromStatus, ToStatus, ActorType, ActorCustomerId, Reason, Note, CreatedOnUtc
            FROM StoreOrderEvent WHERE StoreOrderId IN ({string.Join(',', ids.Select(p => p.Item1))}) ORDER BY CreatedOnUtc, Id
            """, ids);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            byId[r.GetInt32(1)].Events.Add(new StoreOrderEvent
            {
                Id = r.GetInt32(0), StoreOrderId = r.GetInt32(1), FromStatus = r.IsDBNull(2) ? null : (StoreOrderStatus)r.GetInt32(2),
                ToStatus = (StoreOrderStatus)r.GetInt32(3), ActorType = (OrderActorType)r.GetInt32(4), ActorCustomerId = r.IsDBNull(5) ? null : r.GetInt32(5),
                Reason = NullableString(r, 6), Note = NullableString(r, 7), CreatedOnUtc = r.GetDateTime(8)
            });
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal).Replace("[", @"\[", StringComparison.Ordinal);

    private static string? NullableString(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime? NullableDate(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);

    private static async Task<int?> ScalarAsync(
        SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(connection, transaction, sql, parameters);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> NonQueryAsync(
        SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(connection, transaction, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
