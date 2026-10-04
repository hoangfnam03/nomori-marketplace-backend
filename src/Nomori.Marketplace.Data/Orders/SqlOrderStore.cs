using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Data.Orders;

public sealed class SqlOrderStore(IOptions<DatabaseOptions> options) : IOrderStore
{
    private const string OrderColumns =
        "o.Id, o.Number, o.CustomerId, o.PlacementKey, o.CurrencyCode, o.Subtotal, o.ShippingTotal, o.Total, o.PaymentMethod, o.CustomerNote, " +
        "o.RecipientName, o.RecipientPhone, o.Address1, o.Address2, o.City, o.StateProvince, o.PostalCode, o.CountryCode, o.CreatedOnUtc";

    private const string ShopColumns =
        "s.Id, s.OrderId, s.Number, s.VendorId, s.ShopName, s.Status, s.Subtotal, s.ShippingFee, s.Total, s.ShippingMethodName, s.ShippingRateId, " +
        "s.Carrier, s.TrackingNumber, s.CancelReason, s.CreatedOnUtc, s.UpdatedOnUtc, " +
        "(SELECT COALESCE(SUM(l.Quantity), 0) FROM OrderLine l WHERE l.ShopOrderId = s.Id)";

    private const string LineColumns =
        "Id, ShopOrderId, ProductId, CombinationId, Name, VariantLabel, Sku, PictureId, Quantity, UnitPrice, LineTotal";

    // 2601 and 2627: a unique index says the row already exists.
    private static bool IsDuplicate(SqlException ex) => ex.Number is 2601 or 2627;

    // ---- Writes ----

    public async Task<(Order Order, bool Created)> InsertAsync(Order order, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var cmd = Command(connection, transaction, """
                INSERT INTO CustomerOrder (Number, CustomerId, PlacementKey, CurrencyCode, Subtotal, ShippingTotal, Total, PaymentMethod, CustomerNote,
                    RecipientName, RecipientPhone, Address1, Address2, City, StateProvince, PostalCode, CountryCode, CreatedOnUtc)
                OUTPUT INSERTED.Id
                -- The real number needs the id; until then a throw-away value keeps the unique index happy.
                VALUES (REPLACE(CONVERT(nvarchar(36), NEWID()), '-', ''), @CustomerId, @PlacementKey, @CurrencyCode, @Subtotal, @ShippingTotal, @Total,
                    @PaymentMethod, @CustomerNote, @RecipientName, @RecipientPhone, @Address1, @Address2, @City, @StateProvince, @PostalCode,
                    @CountryCode, @CreatedOnUtc)
                """))
            {
                cmd.Parameters.AddWithValue("@CustomerId", order.CustomerId);
                cmd.Parameters.AddWithValue("@PlacementKey", order.PlacementKey);
                cmd.Parameters.AddWithValue("@CurrencyCode", order.CurrencyCode);
                cmd.Parameters.AddWithValue("@Subtotal", order.Subtotal);
                cmd.Parameters.AddWithValue("@ShippingTotal", order.ShippingTotal);
                cmd.Parameters.AddWithValue("@Total", order.Total);
                cmd.Parameters.AddWithValue("@PaymentMethod", order.PaymentMethod);
                cmd.Parameters.AddWithValue("@CustomerNote", (object?)order.CustomerNote ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RecipientName", order.RecipientName);
                cmd.Parameters.AddWithValue("@RecipientPhone", order.RecipientPhone);
                cmd.Parameters.AddWithValue("@Address1", order.Address1);
                cmd.Parameters.AddWithValue("@Address2", (object?)order.Address2 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@City", order.City);
                cmd.Parameters.AddWithValue("@StateProvince", (object?)order.StateProvince ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PostalCode", (object?)order.PostalCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CountryCode", order.CountryCode);
                cmd.Parameters.AddWithValue("@CreatedOnUtc", order.CreatedOnUtc);
                order.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            order.Number = OrderRules.NumberFor(order.CreatedOnUtc, order.Id);
            await using (var cmd = Command(connection, transaction, "UPDATE CustomerOrder SET Number = @Number WHERE Id = @Id"))
            {
                cmd.Parameters.AddWithValue("@Number", order.Number);
                cmd.Parameters.AddWithValue("@Id", order.Id);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var position = 1;
            foreach (var shop in order.ShopOrders)
            {
                shop.OrderId = order.Id;
                shop.Number = OrderRules.ShopNumberFor(order.Number, position++);
                await InsertShopOrderAsync(connection, transaction, shop, order.CustomerId, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return (order, true);
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            // The placement key is used already: nothing of this attempt stays, and the order that used the key is the answer.
            await transaction.RollbackAsync(cancellationToken);
            var existing = await GetByKeyAsync(order.PlacementKey, cancellationToken);
            if (existing is null) throw;
            return (existing, false);
        }
    }

    private static async Task InsertShopOrderAsync(SqlConnection connection, SqlTransaction transaction, ShopOrder shop, int customerId, CancellationToken cancellationToken)
    {
        await using (var cmd = Command(connection, transaction, """
            INSERT INTO ShopOrder (OrderId, VendorId, Number, ShopName, Status, Subtotal, ShippingFee, Total, ShippingMethodName, ShippingRateId, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@OrderId, @VendorId, @Number, @ShopName, @Status, @Subtotal, @ShippingFee, @Total, @ShippingMethodName, @ShippingRateId, @CreatedOnUtc, @CreatedOnUtc)
            """))
        {
            cmd.Parameters.AddWithValue("@OrderId", shop.OrderId);
            cmd.Parameters.AddWithValue("@VendorId", shop.VendorId);
            cmd.Parameters.AddWithValue("@Number", shop.Number);
            cmd.Parameters.AddWithValue("@ShopName", shop.ShopName);
            cmd.Parameters.AddWithValue("@Status", (int)shop.Status);
            cmd.Parameters.AddWithValue("@Subtotal", shop.Subtotal);
            cmd.Parameters.AddWithValue("@ShippingFee", shop.ShippingFee);
            cmd.Parameters.AddWithValue("@Total", shop.Total);
            cmd.Parameters.AddWithValue("@ShippingMethodName", shop.ShippingMethodName);
            cmd.Parameters.AddWithValue("@ShippingRateId", (object?)shop.ShippingRateId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedOnUtc", shop.CreatedOnUtc);
            shop.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        foreach (var line in shop.Lines)
        {
            line.ShopOrderId = shop.Id;
            await using var cmd = Command(connection, transaction, """
                INSERT INTO OrderLine (ShopOrderId, ProductId, CombinationId, Name, VariantLabel, Sku, PictureId, Quantity, UnitPrice, LineTotal)
                OUTPUT INSERTED.Id
                VALUES (@ShopOrderId, @ProductId, @CombinationId, @Name, @VariantLabel, @Sku, @PictureId, @Quantity, @UnitPrice, @LineTotal)
                """);
            cmd.Parameters.AddWithValue("@ShopOrderId", shop.Id);
            cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
            cmd.Parameters.AddWithValue("@CombinationId", (object?)line.CombinationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Name", line.Name);
            cmd.Parameters.AddWithValue("@VariantLabel", (object?)line.VariantLabel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Sku", (object?)line.Sku ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PictureId", line.PictureId);
            cmd.Parameters.AddWithValue("@Quantity", line.Quantity);
            cmd.Parameters.AddWithValue("@UnitPrice", line.UnitPrice);
            cmd.Parameters.AddWithValue("@LineTotal", line.LineTotal);
            line.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await AddHistoryAsync(connection, transaction, shop.Id, null, ShopOrderStatus.Pending, OrderActor.Customer, customerId, null, shop.CreatedOnUtc, cancellationToken);
    }

    public async Task<bool> TryTransitionAsync(ShopOrderTransition transition, CancellationToken cancellationToken)
    {
        var t = transition;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int changed;
        await using (var cmd = Command(connection, transaction, """
            UPDATE ShopOrder
            SET Status = @Target, Carrier = COALESCE(@Carrier, Carrier), TrackingNumber = COALESCE(@Tracking, TrackingNumber),
                CancelReason = COALESCE(@CancelReason, CancelReason), UpdatedOnUtc = @Now
            WHERE Id = @Id AND Status = @Expected
            """))
        {
            cmd.Parameters.AddWithValue("@Id", t.ShopOrderId);
            cmd.Parameters.AddWithValue("@Expected", (int)t.Expected);
            cmd.Parameters.AddWithValue("@Target", (int)t.Target);
            cmd.Parameters.AddWithValue("@Carrier", (object?)t.Carrier ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Tracking", (object?)t.TrackingNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CancelReason", (object?)t.CancelReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Now", t.NowUtc);
            // The expected status is part of the condition: of two racing requests only one changes the row.
            changed = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await AddHistoryAsync(connection, transaction, t.ShopOrderId, t.Expected, t.Target, t.Actor, t.ActorCustomerId, t.Note, t.NowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryUpdateTrackingAsync(
        int shopOrderId, string carrier, string trackingNumber, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int changed;
        await using (var cmd = Command(connection, transaction,
            "UPDATE ShopOrder SET Carrier = @Carrier, TrackingNumber = @Tracking, UpdatedOnUtc = @Now WHERE Id = @Id AND Status = @Shipped"))
        {
            cmd.Parameters.AddWithValue("@Id", shopOrderId);
            cmd.Parameters.AddWithValue("@Shipped", (int)ShopOrderStatus.Shipped);
            cmd.Parameters.AddWithValue("@Carrier", carrier);
            cmd.Parameters.AddWithValue("@Tracking", trackingNumber);
            cmd.Parameters.AddWithValue("@Now", nowUtc);
            changed = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await AddHistoryAsync(connection, transaction, shopOrderId, ShopOrderStatus.Shipped, ShopOrderStatus.Shipped, OrderActor.Shop, actorCustomerId,
            $"{carrier} {trackingNumber}", nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task AddHistoryAsync(
        SqlConnection connection, SqlTransaction transaction, int shopOrderId, ShopOrderStatus? from, ShopOrderStatus to, OrderActor actor,
        int? actorCustomerId, string? note, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var cmd = Command(connection, transaction, """
            INSERT INTO ShopOrderHistory (ShopOrderId, FromStatus, ToStatus, ActorType, ActorCustomerId, Note, CreatedOnUtc)
            VALUES (@ShopOrderId, @From, @To, @Actor, @ActorCustomerId, @Note, @Now)
            """);
        cmd.Parameters.AddWithValue("@ShopOrderId", shopOrderId);
        cmd.Parameters.AddWithValue("@From", from is null ? DBNull.Value : (int)from.Value);
        cmd.Parameters.AddWithValue("@To", (int)to);
        cmd.Parameters.AddWithValue("@Actor", OrderRules.ToWire(actor));
        cmd.Parameters.AddWithValue("@ActorCustomerId", (object?)actorCustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Now", nowUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- Reads ----

    public async Task<Order?> GetOrderAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadOrderAsync(connection, "o.Id = @P", id, cancellationToken);
    }

    private async Task<Order?> GetByKeyAsync(string placementKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadOrderAsync(connection, "o.PlacementKey = @P", placementKey, cancellationToken);
    }

    private static async Task<Order?> LoadOrderAsync(SqlConnection connection, string condition, object value, CancellationToken cancellationToken)
    {
        Order? order;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {OrderColumns} FROM CustomerOrder o WHERE {condition}";
            cmd.Parameters.AddWithValue("@P", value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            order = await reader.ReadAsync(cancellationToken) ? ReadOrder(reader, 0) : null;
        }
        if (order is null) return null;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ShopColumns} FROM ShopOrder s WHERE s.OrderId = @OrderId ORDER BY s.Id";
            cmd.Parameters.AddWithValue("@OrderId", order.Id);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) order.ShopOrders.Add(ReadShopOrder(reader, 0));
        }

        var lines = await LoadLinesAsync(connection, "ShopOrderId IN (SELECT Id FROM ShopOrder WHERE OrderId = @P)", order.Id, cancellationToken);
        foreach (var shop in order.ShopOrders) shop.Lines = lines.Where(l => l.ShopOrderId == shop.Id).ToList();
        return order;
    }

    public async Task<ShopOrder?> GetShopOrderAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        ShopOrder? shop;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ShopColumns}, {OrderColumns} FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId WHERE s.Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            shop = ReadShopOrder(reader, 0);
            shop.Order = ReadOrder(reader, 17);
        }
        shop.Lines = (await LoadLinesAsync(connection, "ShopOrderId = @P", id, cancellationToken)).ToList();
        return shop;
    }

    public async Task<PagedResult<Order>> GetOrdersAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        // The filters look at the shop orders an order contains: an order matches when one of them does.
        await using var count = connection.CreateCommand();
        var where = OrderWhere(query, count);
        count.CommandText = $"SELECT COUNT(*) FROM CustomerOrder o {where}";
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var orders = new List<Order>();
        await using (var cmd = connection.CreateCommand())
        {
            var pagedWhere = OrderWhere(query, cmd);
            cmd.CommandText = $"SELECT {OrderColumns} FROM CustomerOrder o {pagedWhere} ORDER BY o.Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
            cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
            cmd.Parameters.AddWithValue("@Take", query.PageSize);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) orders.Add(ReadOrder(reader, 0));
        }

        if (orders.Count > 0)
        {
            await using var cmd = connection.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < orders.Count; i++)
            {
                names.Add("@O" + i);
                cmd.Parameters.AddWithValue("@O" + i, orders[i].Id);
            }
            cmd.CommandText = $"SELECT {ShopColumns} FROM ShopOrder s WHERE s.OrderId IN ({string.Join(',', names)}) ORDER BY s.Id";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var shop = ReadShopOrder(reader, 0);
                orders.First(o => o.Id == shop.OrderId).ShopOrders.Add(shop);
            }
        }
        return new PagedResult<Order>(orders, total, query.Page, query.PageSize);
    }

    public async Task<PagedResult<ShopOrder>> GetShopOrdersAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var count = connection.CreateCommand();
        var where = ShopWhere(query, count);
        count.CommandText = $"SELECT COUNT(*) FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId {where}";
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        var pagedWhere = ShopWhere(query, cmd);
        cmd.CommandText = $"""
            SELECT {ShopColumns}, {OrderColumns}
            FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId
            {pagedWhere}
            ORDER BY s.Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            """;
        cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@Take", query.PageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<ShopOrder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var shop = ReadShopOrder(reader, 0);
            shop.Order = ReadOrder(reader, 17);
            items.Add(shop);
        }
        return new PagedResult<ShopOrder>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyDictionary<ShopOrderStatus, int>> CountByStatusAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Status, COUNT(*) FROM ShopOrder WHERE VendorId = @VendorId GROUP BY Status";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var counts = Enum.GetValues<ShopOrderStatus>().ToDictionary(s => s, _ => 0);
        while (await reader.ReadAsync(cancellationToken)) counts[(ShopOrderStatus)reader.GetInt32(0)] = reader.GetInt32(1);
        return counts;
    }

    public async Task<IReadOnlyList<OrderHistoryEntry>> GetHistoryAsync(IReadOnlyCollection<int> shopOrderIds, CancellationToken cancellationToken)
    {
        if (shopOrderIds.Count == 0) return [];

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var names = new List<string>();
        var index = 0;
        foreach (var id in shopOrderIds)
        {
            var name = "@S" + index++;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, id);
        }
        cmd.CommandText = $"""
            SELECT Id, ShopOrderId, FromStatus, ToStatus, ActorType, ActorCustomerId, Note, CreatedOnUtc
            FROM ShopOrderHistory WHERE ShopOrderId IN ({string.Join(',', names)}) ORDER BY Id
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var entries = new List<OrderHistoryEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new OrderHistoryEntry(
                reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : (ShopOrderStatus)reader.GetInt32(2), (ShopOrderStatus)reader.GetInt32(3),
                OrderRules.ParseActor(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));
        }
        return entries;
    }

    // ---- Filters ----

    /// <summary>Escapes what LIKE treats as a pattern, so a search for "50%" finds "50%" and not everything.</summary>
    private static string Like(string text) => "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";

    private static string OrderWhere(OrderListQuery q, SqlCommand cmd)
    {
        var conditions = new List<string>();
        if (q.CustomerId is { } customer)
        {
            conditions.Add("o.CustomerId = @CustomerId");
            cmd.Parameters.AddWithValue("@CustomerId", customer);
        }
        if (q.FromUtc is { } from)
        {
            conditions.Add("o.CreatedOnUtc >= @From");
            cmd.Parameters.AddWithValue("@From", from);
        }
        if (q.ToUtc is { } to)
        {
            conditions.Add("o.CreatedOnUtc < @To");
            cmd.Parameters.AddWithValue("@To", to);
        }

        var inner = new List<string>();
        if (q.VendorId is { } vendor)
        {
            inner.Add("s.VendorId = @VendorId");
            cmd.Parameters.AddWithValue("@VendorId", vendor);
        }
        if (q.Status is { } status)
        {
            inner.Add("s.Status = @Status");
            cmd.Parameters.AddWithValue("@Status", (int)status);
        }
        if (inner.Count > 0) conditions.Add($"EXISTS (SELECT 1 FROM ShopOrder s WHERE s.OrderId = o.Id AND {string.Join(" AND ", inner)})");

        if (q.Search is { } search)
        {
            conditions.Add("(o.Number LIKE @Search ESCAPE '\\' OR o.RecipientName LIKE @Search ESCAPE '\\')");
            cmd.Parameters.AddWithValue("@Search", Like(search));
        }
        return conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
    }

    private static string ShopWhere(OrderListQuery q, SqlCommand cmd)
    {
        var conditions = new List<string>();
        if (q.VendorId is { } vendor)
        {
            conditions.Add("s.VendorId = @VendorId");
            cmd.Parameters.AddWithValue("@VendorId", vendor);
        }
        if (q.Status is { } status)
        {
            conditions.Add("s.Status = @Status");
            cmd.Parameters.AddWithValue("@Status", (int)status);
        }
        if (q.FromUtc is { } from)
        {
            conditions.Add("s.CreatedOnUtc >= @From");
            cmd.Parameters.AddWithValue("@From", from);
        }
        if (q.ToUtc is { } to)
        {
            conditions.Add("s.CreatedOnUtc < @To");
            cmd.Parameters.AddWithValue("@To", to);
        }
        if (q.Search is { } search)
        {
            conditions.Add("(s.Number LIKE @Search ESCAPE '\\' OR o.RecipientName LIKE @Search ESCAPE '\\' OR o.RecipientPhone LIKE @Search ESCAPE '\\')");
            cmd.Parameters.AddWithValue("@Search", Like(search));
        }
        return conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
    }

    // ---- Mapping ----

    private static async Task<IReadOnlyList<OrderLine>> LoadLinesAsync(SqlConnection connection, string condition, object value, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {LineColumns} FROM OrderLine WHERE {condition} ORDER BY Id";
        cmd.Parameters.AddWithValue("@P", value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var lines = new List<OrderLine>();
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new OrderLine
            {
                Id = reader.GetInt32(0), ShopOrderId = reader.GetInt32(1), ProductId = reader.GetInt32(2),
                CombinationId = reader.IsDBNull(3) ? null : reader.GetInt32(3), Name = reader.GetString(4),
                VariantLabel = reader.IsDBNull(5) ? null : reader.GetString(5), Sku = reader.IsDBNull(6) ? null : reader.GetString(6),
                PictureId = reader.GetInt32(7), Quantity = reader.GetInt32(8), UnitPrice = reader.GetDecimal(9), LineTotal = reader.GetDecimal(10)
            });
        }
        return lines;
    }

    private static Order ReadOrder(SqlDataReader r, int o) => new()
    {
        Id = r.GetInt32(o), Number = r.GetString(o + 1), CustomerId = r.GetInt32(o + 2), PlacementKey = r.GetString(o + 3),
        CurrencyCode = r.GetString(o + 4), Subtotal = r.GetDecimal(o + 5), ShippingTotal = r.GetDecimal(o + 6), Total = r.GetDecimal(o + 7),
        PaymentMethod = r.GetString(o + 8), CustomerNote = r.IsDBNull(o + 9) ? null : r.GetString(o + 9),
        RecipientName = r.GetString(o + 10), RecipientPhone = r.GetString(o + 11), Address1 = r.GetString(o + 12),
        Address2 = r.IsDBNull(o + 13) ? null : r.GetString(o + 13), City = r.GetString(o + 14),
        StateProvince = r.IsDBNull(o + 15) ? null : r.GetString(o + 15), PostalCode = r.IsDBNull(o + 16) ? null : r.GetString(o + 16),
        CountryCode = r.GetString(o + 17), CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(o + 18), DateTimeKind.Utc)
    };

    private static ShopOrder ReadShopOrder(SqlDataReader r, int o) => new()
    {
        Id = r.GetInt32(o), OrderId = r.GetInt32(o + 1), Number = r.GetString(o + 2), VendorId = r.GetInt32(o + 3), ShopName = r.GetString(o + 4),
        Status = (ShopOrderStatus)r.GetInt32(o + 5), Subtotal = r.GetDecimal(o + 6), ShippingFee = r.GetDecimal(o + 7), Total = r.GetDecimal(o + 8),
        ShippingMethodName = r.GetString(o + 9), ShippingRateId = r.IsDBNull(o + 10) ? null : r.GetInt32(o + 10),
        Carrier = r.IsDBNull(o + 11) ? null : r.GetString(o + 11), TrackingNumber = r.IsDBNull(o + 12) ? null : r.GetString(o + 12),
        CancelReason = r.IsDBNull(o + 13) ? null : r.GetString(o + 13),
        CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(o + 14), DateTimeKind.Utc), UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(o + 15), DateTimeKind.Utc),
        ItemCount = r.GetInt32(o + 16)
    };

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string text)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = text;
        return cmd;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
