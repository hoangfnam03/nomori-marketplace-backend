using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Services.Shipping;

/// <summary>
/// The GHN public API over HTTP. GHN answers <c>{ code, message, data }</c>; any code other than 200, a network failure or an unreadable body
/// becomes a failed result with GHN's message.
/// </summary>
public sealed class GhnClient(HttpClient http, IOptions<GhnOptions> options) : IGhnClient
{
    private readonly GhnOptions settings = options.Value;

    public async Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Get, "/master-data/province", null, withShop: false, cancellationToken);
        return Map<IReadOnlyList<GhnProvince>>(answer, data => data.AsArray()
            .Select(p => new GhnProvince(Int(p, "ProvinceID"), Str(p, "ProvinceName")))
            .Where(p => p.Id > 0 && p.Name.Length > 0)
            .OrderBy(p => p.Name, StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true))
            .ToList());
    }

    public async Task<GhnResult<IReadOnlyList<GhnDistrict>>> GetDistrictsAsync(int provinceId, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/master-data/district", new { province_id = provinceId }, withShop: false, cancellationToken);
        return Map<IReadOnlyList<GhnDistrict>>(answer, data => data.AsArray()
            .Select(d => new GhnDistrict(Int(d, "DistrictID"), Int(d, "ProvinceID"), Str(d, "DistrictName")))
            .Where(d => d.Id > 0 && d.Name.Length > 0)
            .OrderBy(d => d.Name, StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true))
            .ToList());
    }

    public async Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int districtId, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/master-data/ward", new { district_id = districtId }, withShop: false, cancellationToken);
        return Map<IReadOnlyList<GhnWard>>(answer, data => data.AsArray()
            .Select(w => new GhnWard(Str(w, "WardCode"), Int(w, "DistrictID"), Str(w, "WardName")))
            .Where(w => w.Code.Length > 0 && w.Name.Length > 0)
            .OrderBy(w => w.Name, StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true))
            .ToList());
    }

    public async Task<GhnResult<long>> GetFeeAsync(GhnFeeRequest request, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, "/v2/shipping-order/fee", new
        {
            service_type_id = request.ServiceTypeId,
            from_district_id = request.FromDistrictId,
            from_ward_code = request.FromWardCode,
            to_district_id = request.ToDistrictId,
            to_ward_code = request.ToWardCode,
            weight = request.WeightGrams,
            length = request.LengthCm,
            width = request.WidthCm,
            height = request.HeightCm,
            insurance_value = 0
        }, withShop: true, cancellationToken);
        return Map(answer, data => Long(data, "total"));
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
            from_name = request.From.Name,
            from_phone = request.From.Phone,
            from_address = request.From.Address,
            from_ward_name = request.From.WardName,
            from_district_name = request.From.DistrictName,
            from_province_name = request.From.ProvinceName,
            to_name = request.ToName,
            to_phone = request.ToPhone,
            to_address = request.ToAddress,
            to_ward_code = request.ToWardCode,
            to_district_id = request.ToDistrictId,
            cod_amount = request.CodAmountVnd,
            content = request.Content,
            weight = request.WeightGrams,
            length = request.LengthCm,
            width = request.WidthCm,
            height = request.HeightCm,
            insurance_value = 0,
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
            if (code != 200 || json?["data"] is not { } data)
            {
                var message = json?["code_message_value"]?.ToString() ?? json?["message"]?.ToString();
                return GhnResult.Fail<JsonNode>(string.IsNullOrWhiteSpace(message) ? $"GHN answered {(int)response.StatusCode}." : message);
            }
            return GhnResult.Ok<JsonNode>(data);
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
            return GhnResult.Ok<T>(read(answer.Value!));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or NullReferenceException)
        {
            return GhnResult.Fail<T>($"GHN gave an answer that could not be read: {ex.Message}");
        }
    }

    private static string Str(JsonNode? node, string name) => node?[name]?.ToString().Trim() ?? string.Empty;

    private static int Int(JsonNode? node, string name) => (int)Long(node, name);

    private static long Long(JsonNode? node, string name) =>
        node?[name] is JsonValue value && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static DateTime? Date(JsonNode? node, string name) =>
        DateTime.TryParse(Str(node, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
