using System.Net.Http;
using System.Text.Json;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

public sealed class OrderStatisticsCenterClient
{
    private const string OrderStatisticsPath = "/api/order-statistics";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly EdgeRuntimeManager _runtime;

    public OrderStatisticsCenterClient(EdgeRuntimeManager runtime)
    {
        _runtime = runtime;
    }

    public Task<OrderStatisticsDashboard> GetDashboardAsync(OrderStatisticsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetDataAsync<OrderStatisticsDashboard>($"{OrderStatisticsPath}/dashboard?{query.ToQueryString()}", "读取订单统计看板失败", ct);
    }

    public async Task<OrderStatisticsRecordPage> GetRecordsAsync(OrderStatisticsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        JsonElement response = await GetJsonDataAsync($"{OrderStatisticsPath}/records?{query.ToQueryString()}", "读取订单统计明细失败", ct).ConfigureAwait(false);
        return OrderStatisticsResponseParser.DeserializeRecordPage(response);
    }

    public async Task<List<OrderStatisticsOperationType>> GetOperationTypesAsync(CancellationToken ct = default)
    {
        JsonElement response = await GetJsonDataAsync(
            $"{OrderStatisticsPath}/operation-types",
            "读取订单统计业务类型失败",
            ct).ConfigureAwait(false);
        return OrderStatisticsResponseParser.DeserializeItems<OrderStatisticsOperationType>(response, "items", "operation_types", "operations");
    }

    public Task<List<OrderStatisticsUserOption>> SearchUsersAsync(string? keyword, CancellationToken ct = default)
    {
        string query = string.IsNullOrWhiteSpace(keyword)
            ? "limit=50"
            : $"q={Uri.EscapeDataString(keyword.Trim())}&limit=50";
        return GetListAsync<OrderStatisticsUserOption>($"/api/user/search?{query}", "读取订单统计人员失败", ct);
    }

    private async Task<T> GetDataAsync<T>(string path, string fallback, CancellationToken ct)
    {
        JsonElement response = await GetJsonDataAsync(path, fallback, ct).ConfigureAwait(false);
        return OrderStatisticsResponseParser.Deserialize<T>(response, fallback);
    }

    private async Task<JsonElement> GetJsonDataAsync(string path, string fallback, CancellationToken ct)
    {
        using HttpRequestMessage request = EdgeRuntimeManager.CreateCenterManagementRequest(HttpMethod.Get, path, _runtime.LoadEdgeSettings());
        using HttpResponseMessage response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowForError(response, body, fallback);

        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task<List<T>> GetListAsync<T>(string path, string fallback, CancellationToken ct)
    {
        JsonElement response = await GetJsonDataAsync(path, fallback, ct).ConfigureAwait(false);
        return OrderStatisticsResponseParser.DeserializeItems<T>(response, "items", "users", "records", "results");
    }

    private static void ThrowForError(HttpResponseMessage response, string body, string fallback)
    {
        string message = ExtractMessage(body);
        if (!response.IsSuccessStatusCode)
        {
            string status = $"{(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? $"{fallback}：{status}" : $"{fallback}：{status}，{message}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (OrderStatisticsResponseParser.IsFailure(document.RootElement))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? fallback : $"{fallback}：{message}");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{fallback}：中心响应不是有效 JSON。", ex);
        }
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            foreach (string propertyName in new[] { "msg", "message", "error" })
            {
                if (OrderStatisticsResponseParser.TryGetProperty(root, propertyName, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    internal static class OrderStatisticsResponseParser
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        internal static T Deserialize<T>(JsonElement root, string fallback)
        {
            JsonElement data = UnwrapData(root);
            if (typeof(T) == typeof(OrderStatisticsRecordPage))
            {
                return (T)(object)DeserializeRecordPage(data);
            }

            return data.Deserialize<T>(Options) ?? throw new InvalidOperationException($"{fallback}：中心响应没有有效数据。");
        }

        internal static List<T> DeserializeItems<T>(JsonElement root, params string[] collectionNames)
        {
            JsonElement data = UnwrapData(root);
            if (data.ValueKind == JsonValueKind.Array)
            {
                return data.Deserialize<List<T>>(Options) ?? [];
            }

            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (string name in collectionNames)
                {
                    if (TryGetProperty(data, name, out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                    {
                        return items.Deserialize<List<T>>(Options) ?? [];
                    }
                }
            }

            return [];
        }

        internal static OrderStatisticsRecordPage DeserializeRecordPage(JsonElement root)
        {
            JsonElement data = UnwrapData(root);
            var page = new OrderStatisticsRecordPage
            {
                Items = DeserializeItems<OrderStatisticsRecord>(data, "items", "records", "results"),
            };
            page.Total = ReadInt64(data, "total", "total_count", "count") ?? page.Items.Count;
            page.Page = Math.Max(1, (int)(ReadInt64(data, "page", "current_page") ?? 1));
            page.PageSize = Math.Clamp((int)(ReadInt64(data, "page_size", "per_page", "limit") ?? 20), 1, 100);
            return page;
        }

        internal static bool IsFailure(JsonElement root)
        {
            if (TryGetProperty(root, "success", out JsonElement success)
                && success.ValueKind == JsonValueKind.False)
            {
                return true;
            }

            if (!TryGetProperty(root, "code", out JsonElement code))
            {
                return false;
            }

            if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int numericCode))
            {
                return numericCode != 0;
            }

            string? textCode = code.ValueKind == JsonValueKind.String ? code.GetString() : null;
            return !string.IsNullOrWhiteSpace(textCode)
                && !string.Equals(textCode, "0", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(textCode, "ok", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(textCode, "success", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static long? ReadInt64(JsonElement data, params string[] names)
        {
            foreach (string name in names)
            {
                if (!TryGetProperty(data, name, out JsonElement value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
                {
                    return number;
                }
            }

            return null;
        }

        private static JsonElement UnwrapData(JsonElement root)
        {
            return root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "data", out JsonElement data)
                ? data
                : root;
        }
    }
}
