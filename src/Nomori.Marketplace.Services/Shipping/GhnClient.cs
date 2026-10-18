using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Services.Shipping;

/// <summary>
/// The GHN public API over HTTP. GHN answers <c>{ code, message, data }</c>; any code other than 200, a network failure or an unreadable body
/// becomes a failed result with GHN's message. Places use the two-level units (v3 master data, <c>*_v2</c> ids with <c>is_new_*_address</c>).
/// </summary>
public sealed class GhnClient(HttpClient http, IOptions<GhnOptions> options) : IGhnClient
{
    private const int PageSize = 200;
    private static readonly StringComparer VietnameseOrder = StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true);

    private readonly GhnOptions settings = options.Value;

    public async Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken)
    {
        var answer = await PagedAsync("/v3/master-data/province/all?", cancellationToken);
        return MapItems<IReadOnlyList<GhnProvince>>(answer, items => items
            .Where(Active)
            .Select(p => new GhnProvince(Int(p, "_id"), Str(p, "name")))
            .Where(p => p.Id > 0 && p.Name.Length > 0)
            .OrderBy(p => p.Name, VietnameseOrder)
            .ToList());
    }

    public async Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int provinceId, CancellationToken cancellationToken)
    {
        var answer = await PagedAsync($"/v3/master-data/ward/all-by-province-id?province_id={provinceId}&", cancellationToken);
        return MapItems<IReadOnlyList<GhnWard>>(answer, items => items
            .Where(Active)
            .Select(w => new GhnWard(Int(w, "_id"), Int(w, "parent_id"), Str(w, "name")))
            .Where(w => w.Id > 0 && w.Name.Length > 0)
            .OrderBy(w => w.Name, VietnameseOrder)
            .ToList());
    }

    public async Task<GhnResult<long>> GetFeeAsync(GhnFeeRequest request, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/v2/shipping-order/fee", Priced(request), withShop: true, cancellationToken);
        return Map(answer, data => Long(data, "total"));
    }

    public async Task<GhnResult<GhnLeadtime>> GetLeadtimeAsync(GhnFeeRequest request, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/v2/shipping-order/leadtime", Priced(request), withShop: true, cancellationToken);
        return Map(answer, data =>
        {
            var order = data["leadtime_order"];
            var from = Date(order, "from_estimate_date");
            var to = Date(order, "to_estimate_date");
            // Only the single timestamp: both ends are that day.
            if (from is null && to is null && Long(data, "leadtime") is > 0 and var seconds)
                from = to = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            return new GhnLeadtime(from, to);
        });
    }

    public async Task<GhnResult<GhnCreatedOrder>> CreateOrderAsync(GhnCreateOrderRequest request, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/v2/shipping-order/create", new
        {
            // 1: the sender pays GHN. The customer already paid the shipping fee to the marketplace at checkout.
            payment_type_id = 1,
            note = request.Note,
            required_note = request.RequiredNote,
            client_order_code = request.ClientOrderCode,
            // The shop's own address: GHN picks up there, and returns there.
            is_new_from_address = true,
            from_name = request.From.Name,
            from_phone = request.From.Phone,
            from_address = request.From.Address,
            from_province_name = request.From.ProvinceName,
            from_ward_name = request.From.WardName,
            from_province_id_v2 = request.From.ProvinceId,
            from_ward_id_v2 = request.From.WardId,
            is_new_to_address = true,
            to_name = request.ToName,
            to_phone = request.ToPhone,
            to_address = request.ToAddress,
            to_province_name = request.ToProvinceName,
            to_ward_name = request.ToWardName,
            to_province_id_v2 = request.ToProvinceId,
            to_ward_id_v2 = request.ToWardId,
            cod_amount = request.CodAmountVnd,
            insurance_value = request.InsuranceVnd,
            content = request.Content,
            weight = request.Parcel.WeightGrams,
            length = request.Parcel.LengthCm,
            width = request.Parcel.WidthCm,
            height = request.Parcel.HeightCm,
            service_type_id = request.ServiceTypeId,
            items = request.Items.Select(i => new { name = i.Name, quantity = i.Quantity, weight = i.WeightGrams, price = i.PriceVnd }).ToList()
        }, withShop: true, cancellationToken);
        return Map(answer, data => new GhnCreatedOrder(Str(data, "order_code"), Long(data, "total_fee"), Date(data, "expected_delivery_time")));
    }

    public async Task<GhnResult<GhnOrderDetail>> GetOrderAsync(string orderCode, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/v2/shipping-order/detail", new { order_code = orderCode }, withShop: false, cancellationToken);
        return Map(answer, data =>
        {
            var logs = data["log"] is JsonArray array
                ? array.Select(l => (Status: Str(l, "status"), At: Date(l, "updated_date")))
                    .Where(l => l.Status.Length > 0 && l.At is not null)
                    .Select(l => new GhnLog(l.Status, l.At!.Value))
                    .OrderBy(l => l.UpdatedUtc)
                    .ToList()
                : [];
            return new GhnOrderDetail(Str(data, "order_code"), Str(data, "status"), logs);
        });
    }

    public async Task<GhnResult<string>> GetLabelUrlAsync(string orderCode, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/v2/a5/gen-token", new { order_codes = new[] { orderCode } }, withShop: false, cancellationToken);
        // The print page lives next to the API: .../shiip/public-api becomes .../a5/public-api/printA5.
        var root = settings.BaseUrl.TrimEnd('/');
        var printBase = root.EndsWith("/shiip/public-api", StringComparison.OrdinalIgnoreCase) ? root[..^"/shiip/public-api".Length] : root;
        return Map(answer, data => $"{printBase}/a5/public-api/printA5?token={Uri.EscapeDataString(Str(data, "token"))}");
    }

    // ---- Plumbing ----

    /// <summary>The body shared by the fee and the leadtime calls: both places in two-level ids, the parcel and its declared value.</summary>
    private static object Priced(GhnFeeRequest request) => new
    {
        service_type_id = request.ServiceTypeId,
        is_new_from_address = true,
        from_province_id_v2 = request.From.ProvinceId,
        from_ward_id_v2 = request.From.WardId,
        is_new_to_address = true,
        to_province_id_v2 = request.To.ProvinceId,
        to_ward_id_v2 = request.To.WardId,
        weight = request.Parcel.WeightGrams,
        length = request.Parcel.LengthCm,
        width = request.Parcel.WidthCm,
        height = request.Parcel.HeightCm,
        insurance_value = request.InsuranceVnd
    };

    private static bool Active(JsonNode? node) => node?["status"] is not JsonValue status || status.ToString() == "1";

    /// <summary>Reads every page of a master-data list (GHN gives at most 200 items a call).</summary>
    private async Task<GhnResult<JsonNode>> PagedAsync(string pathWithQuery, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await SendAsync(HttpMethod.Get, $"{pathWithQuery}offset={offset}&limit={PageSize}", null, withShop: true, cancellationToken);
            if (!page.Succeeded) return page;
            if (page.Value is not JsonArray items) break;
            foreach (var item in items.ToList())
            {
                items.Remove(item);
                all.Add(item);
            }
            if (all.Count < offset + PageSize) break;
        }
        return GhnResult.Ok<JsonNode>(all);
    }

    private async Task<GhnResult<JsonNode>> SendAsync(HttpMethod method, string path, object? body, bool withShop, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Token)) return GhnResult.Fail<JsonNode>("GHN is not configured.");

        using var request = new HttpRequestMessage(method, settings.BaseUrl.TrimEnd('/') + path);
        request.Headers.Add("Token", settings.Token);
        if (withShop) request.Headers.Add("ShopId", settings.ShopId.ToString(CultureInfo.InvariantCulture));
        if (body is not null) request.Content = JsonContent.Create(body);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            var code = json?["code"]?.GetValueKind() == JsonValueKind.Number ? json["code"]!.GetValue<int>() : (int)response.StatusCode;
            if (code != 200)
            {
                var message = json?["code_message_value"]?.ToString() ?? json?["message"]?.ToString();
                return GhnResult.Fail<JsonNode>(string.IsNullOrWhiteSpace(message) ? $"GHN answered {(int)response.StatusCode}." : message);
            }
            // A list with nothing in it comes back as data: null.
            return GhnResult.Ok<JsonNode>(json?["data"] ?? new JsonArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return GhnResult.Fail<JsonNode>($"GHN could not be reached: {ex.Message}");
        }
    }

    private static GhnResult<T> Map<T>(GhnResult<JsonNode> answer, Func<JsonNode, T> read)
    {
        if (!answer.Succeeded) return GhnResult.Fail<T>(answer.Error!);
        try
        {
            return GhnResult.Ok(read(answer.Value!));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or NullReferenceException)
        {
            return GhnResult.Fail<T>($"GHN gave an answer that could not be read: {ex.Message}");
        }
    }

    private static GhnResult<T> MapItems<T>(GhnResult<JsonNode> answer, Func<IEnumerable<JsonNode?>, T> readItems) =>
        Map(answer, data => readItems(data is JsonArray array ? array : []));

    private static string Str(JsonNode? node, string name) => node?[name]?.ToString().Trim() ?? string.Empty;

    private static int Int(JsonNode? node, string name) => (int)Long(node, name);

    private static long Long(JsonNode? node, string name) =>
        node?[name] is JsonValue value && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static DateTime? Date(JsonNode? node, string name) =>
        DateTime.TryParse(Str(node, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
