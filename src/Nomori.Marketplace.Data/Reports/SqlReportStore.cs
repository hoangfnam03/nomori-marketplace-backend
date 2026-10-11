using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Reports;

namespace Nomori.Marketplace.Data.Reports;

/// <summary>
/// Read-only queries behind the dashboards. "Sales" are shop orders that are not cancelled and whose payment is confirmed; the amount is the
/// items less a discount the shop pays for, the same base the commission uses.
/// </summary>
public sealed class SqlReportStore(IOptions<DatabaseOptions> options) : IReportStore
{
    private const string SaleAmount = "(s.Subtotal - CASE WHEN s.DiscountFunding = 'shop' THEN s.DiscountAmount ELSE 0 END)";

    private const string SalesFrom = """
        FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId
        WHERE o.AwaitingPayment = 0 AND s.CreatedOnUtc >= @From AND s.CreatedOnUtc < @To
        """;

    public async Task<ShopSummary> GetShopSummaryAsync(int vendorId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        int orders, cancelled;
        decimal sales, shipping;
        await using (var cmd = Command(connection, $"""
            SELECT COUNT(CASE WHEN s.Status <> 5 THEN 1 END),
                   COALESCE(SUM(CASE WHEN s.Status <> 5 THEN {SaleAmount} END), 0),
                   COALESCE(SUM(CASE WHEN s.Status <> 5 THEN s.ShippingFee END), 0),
                   COUNT(CASE WHEN s.Status = 5 THEN 1 END)
            {SalesFrom} AND s.VendorId = @VendorId
            """, fromUtc, toExclusiveUtc))
        {
            cmd.Parameters.AddWithValue("@VendorId", vendorId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (orders, sales, shipping, cancelled) = (reader.GetInt32(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetInt32(3));
        }

        int units;
        await using (var cmd = Command(connection, $"""
            SELECT COALESCE(SUM(l.Quantity), 0)
            FROM OrderLine l INNER JOIN ShopOrder s ON s.Id = l.ShopOrderId INNER JOIN CustomerOrder o ON o.Id = s.OrderId
            WHERE o.AwaitingPayment = 0 AND s.Status <> 5 AND s.VendorId = @VendorId AND s.CreatedOnUtc >= @From AND s.CreatedOnUtc < @To
            """, fromUtc, toExclusiveUtc))
        {
            cmd.Parameters.AddWithValue("@VendorId", vendorId);
            units = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        int returns;
        await using (var cmd = Command(connection, "SELECT COUNT(*) FROM ReturnRequest WHERE VendorId = @VendorId AND CreatedOnUtc >= @From AND CreatedOnUtc < @To", fromUtc, toExclusiveUtc))
        {
            cmd.Parameters.AddWithValue("@VendorId", vendorId);
            returns = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        decimal refunded;
        await using (var cmd = Command(connection, """
            SELECT COALESCE(SUM(RefundAmount), 0) FROM ReturnRequest
            WHERE VendorId = @VendorId AND Status = 4 AND UpdatedOnUtc >= @From AND UpdatedOnUtc < @To
            """, fromUtc, toExclusiveUtc))
        {
            cmd.Parameters.AddWithValue("@VendorId", vendorId);
            refunded = (decimal)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        return new ShopSummary(orders, units, sales, shipping, cancelled, returns, refunded);
    }

    public async Task<IReadOnlyList<DailySales>> GetDailySalesAsync(int? vendorId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, $"""
            SELECT CONVERT(date, s.CreatedOnUtc), COUNT(*), COALESCE(SUM({SaleAmount}), 0)
            {SalesFrom} AND s.Status <> 5 AND (@VendorId IS NULL OR s.VendorId = @VendorId)
            GROUP BY CONVERT(date, s.CreatedOnUtc) ORDER BY 1
            """, fromUtc, toExclusiveUtc);
        cmd.Parameters.AddWithValue("@VendorId", vendorId is { } id ? id : DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DailySales>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new DailySales(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetInt32(1), reader.GetDecimal(2)));
        return rows;
    }

    public async Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(int vendorId, DateTime fromUtc, DateTime toExclusiveUtc, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, """
            SELECT TOP (@Take) l.ProductId, MAX(l.Name), SUM(l.Quantity), SUM(l.LineTotal)
            FROM OrderLine l INNER JOIN ShopOrder s ON s.Id = l.ShopOrderId INNER JOIN CustomerOrder o ON o.Id = s.OrderId
            WHERE o.AwaitingPayment = 0 AND s.Status <> 5 AND s.VendorId = @VendorId AND s.CreatedOnUtc >= @From AND s.CreatedOnUtc < @To
            GROUP BY l.ProductId ORDER BY SUM(l.LineTotal) DESC, l.ProductId
            """, fromUtc, toExclusiveUtc);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@Take", take);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<TopProduct>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new TopProduct(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetDecimal(3)));
        return rows;
    }

    public async Task<IReadOnlyList<LowStockProduct>> GetLowStockAsync(int vendorId, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@Take) Id, Name, Sku, StockQuantity, LowStockThreshold
            FROM Product
            WHERE VendorId = @VendorId AND Deleted = 0 AND TrackInventory = 1 AND Status = 1 AND StockQuantity <= LowStockThreshold
            ORDER BY StockQuantity, Id
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@Take", take);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<LowStockProduct>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LowStockProduct(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
        return rows;
    }

    public async Task<PlatformSummary> GetPlatformSummaryAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        int orders, shops, cancelled;
        decimal merchandise;
        await using (var cmd = Command(connection, $"""
            SELECT COUNT(DISTINCT CASE WHEN s.Status <> 5 THEN s.OrderId END),
                   COALESCE(SUM(CASE WHEN s.Status <> 5 THEN s.Total END), 0),
                   COUNT(DISTINCT CASE WHEN s.Status <> 5 THEN s.VendorId END),
                   COUNT(CASE WHEN s.Status = 5 THEN 1 END)
            {SalesFrom}
            """, fromUtc, toExclusiveUtc))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (orders, merchandise, shops, cancelled) = (reader.GetInt32(0), reader.GetDecimal(1), reader.GetInt32(2), reader.GetInt32(3));
        }

        int customers;
        await using (var cmd = Command(connection, "SELECT COUNT(*) FROM Customer WHERE CreatedOnUtc >= @From AND CreatedOnUtc < @To", fromUtc, toExclusiveUtc))
            customers = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;

        // What the platform kept: commission of sales, less the commission given back by refunds.
        decimal commission;
        await using (var cmd = Command(connection, """
            SELECT COALESCE(SUM(CASE WHEN Kind = 0 THEN Commission WHEN Kind = 1 THEN -Commission END), 0)
            FROM SettlementEntry WHERE CreatedOnUtc >= @From AND CreatedOnUtc < @To
            """, fromUtc, toExclusiveUtc))
            commission = (decimal)(await cmd.ExecuteScalarAsync(cancellationToken))!;

        decimal refunded;
        await using (var cmd = Command(connection, """
            SELECT COALESCE(SUM(RefundAmount), 0) FROM ReturnRequest WHERE Status = 4 AND UpdatedOnUtc >= @From AND UpdatedOnUtc < @To
            """, fromUtc, toExclusiveUtc))
            refunded = (decimal)(await cmd.ExecuteScalarAsync(cancellationToken))!;

        return new PlatformSummary(orders, merchandise, customers, shops, commission, refunded, cancelled);
    }

    public async Task<IReadOnlyList<TopShop>> GetTopShopsAsync(DateTime fromUtc, DateTime toExclusiveUtc, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, $"""
            SELECT TOP (@Take) s.VendorId, MAX(v.Name), COUNT(*), COALESCE(SUM({SaleAmount}), 0)
            FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId INNER JOIN Vendor v ON v.Id = s.VendorId
            WHERE o.AwaitingPayment = 0 AND s.Status <> 5 AND s.CreatedOnUtc >= @From AND s.CreatedOnUtc < @To
            GROUP BY s.VendorId ORDER BY SUM({SaleAmount}) DESC, s.VendorId
            """, fromUtc, toExclusiveUtc);
        cmd.Parameters.AddWithValue("@Take", take);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<TopShop>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new TopShop(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetDecimal(3)));
        return rows;
    }

    public async Task<Operations> GetOperationsAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.Add(value is DateTime date ? new SqlParameter(name, SqlDbType.DateTime2) { Value = date } : new SqlParameter(name, value));
            return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        int pendingPayouts;
        decimal pendingAmount;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(Amount), 0) FROM SettlementPayout WHERE Status = 0";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (pendingPayouts, pendingAmount) = (reader.GetInt32(0), reader.GetDecimal(1));
        }

        return new Operations(
            pendingPayouts, pendingAmount,
            // Requested, approved and received are open; rejected, refunded and withdrawn are closed.
            await CountAsync("SELECT COUNT(*) FROM ReturnRequest WHERE Status IN (0, 1, 3)"),
            await CountAsync("""
                SELECT COUNT(*) FROM ShopOrder s INNER JOIN CustomerOrder o ON o.Id = s.OrderId
                WHERE s.Status = 0 AND o.AwaitingPayment = 0 AND s.CreatedOnUtc < @Cutoff
                """, ("@Cutoff", nowUtc.AddHours(-ReportLimits.UnconfirmedHours))),
            await CountAsync("SELECT COUNT(*) FROM ScheduledTask WHERE LastStatus IN ('failed', 'partial') AND LastFinishedUtc >= @Since",
                ("@Since", nowUtc.AddHours(-ReportLimits.RecentJobHours))),
            await CountAsync("SELECT COUNT(*) FROM QueuedEmail WHERE Status = 'failed'"),
            await CountAsync("SELECT COUNT(*) FROM ShopOrder s WHERE s.Status = 4 AND NOT EXISTS (SELECT 1 FROM SettlementEntry e WHERE e.ShopOrderId = s.Id AND e.Kind = 0)"),
            await CountAsync("SELECT COUNT(*) FROM ReturnRequest r WHERE r.Status = 4 AND NOT EXISTS (SELECT 1 FROM SettlementEntry e WHERE e.ReturnRequestId = r.Id AND e.Kind = 1)"),
            await CountAsync("""
                SELECT COUNT(*) FROM Product p INNER JOIN Vendor v ON v.Id = p.VendorId
                WHERE p.Deleted = 0 AND v.Deleted = 0 AND p.TrackInventory = 1 AND p.Status = 1 AND p.StockQuantity <= p.LowStockThreshold
                """));
    }

    public async Task<Reconciliation> GetReconciliationAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        int saleLines;
        decimal items, shopDiscount, platformDiscount, shipping, tax, paid, revenue, commission;
        await using (var cmd = Command(connection, """
            SELECT COUNT(*), COALESCE(SUM(s.Subtotal), 0),
                   COALESCE(SUM(CASE WHEN s.DiscountFunding = 'shop' THEN s.DiscountAmount ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN s.DiscountFunding = 'platform' THEN s.DiscountAmount ELSE 0 END), 0),
                   COALESCE(SUM(s.ShippingFee), 0), COALESCE(SUM(s.TaxAmount), 0), COALESCE(SUM(s.Total), 0),
                   COALESCE(SUM(e.Amount), 0), COALESCE(SUM(e.Commission), 0)
            FROM SettlementEntry e INNER JOIN ShopOrder s ON s.Id = e.ShopOrderId
            WHERE e.Kind = 0 AND e.CreatedOnUtc >= @From AND e.CreatedOnUtc < @To
            """, fromUtc, toExclusiveUtc))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            saleLines = reader.GetInt32(0);
            (items, shopDiscount, platformDiscount, shipping) = (reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4));
            (tax, paid, revenue, commission) = (reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8));
        }

        int refundLines;
        decimal refundedFromShops, commissionReturned;
        await using (var cmd = Command(connection, """
            SELECT COUNT(*), COALESCE(SUM(-Amount), 0), COALESCE(SUM(Commission), 0)
            FROM SettlementEntry WHERE Kind = 1 AND CreatedOnUtc >= @From AND CreatedOnUtc < @To
            """, fromUtc, toExclusiveUtc))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (refundLines, refundedFromShops, commissionReturned) = (reader.GetInt32(0), reader.GetDecimal(1), reader.GetDecimal(2));
        }

        int mismatches;
        await using (var cmd = connection.CreateCommand())
        {
            // A payout that is pending or paid must equal the sum of the lines it claimed.
            cmd.CommandText = """
                SELECT COUNT(*) FROM SettlementPayout p
                WHERE p.Status IN (0, 1) AND p.Amount <> COALESCE((SELECT SUM(e.Amount) FROM SettlementEntry e WHERE e.PayoutId = p.Id), 0)
                """;
            mismatches = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        return new Reconciliation(
            string.Empty, fromUtc, toExclusiveUtc, saleLines, items, shopDiscount, platformDiscount, shipping, tax, paid, revenue, commission,
            ReportRules.Difference(paid, revenue, commission, tax, platformDiscount), refundLines, refundedFromShops, commissionReturned, mismatches);
    }

    private static SqlCommand Command(SqlConnection connection, string sql, DateTime fromUtc, DateTime toExclusiveUtc)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@From", SqlDbType.DateTime2) { Value = fromUtc });
        cmd.Parameters.Add(new SqlParameter("@To", SqlDbType.DateTime2) { Value = toExclusiveUtc });
        return cmd;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
