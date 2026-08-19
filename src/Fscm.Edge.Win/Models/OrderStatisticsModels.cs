using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class OrderStatisticsQuery
{
    public DateTimeOffset From { get; init; }

    public DateTimeOffset To { get; init; }

    public IReadOnlyCollection<uint> UserIds { get; init; } = [];

    public IReadOnlyCollection<string> OperationTypeCodes { get; init; } = [];

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public string? Status { get; init; }

    internal string ToQueryString()
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("from", From.ToString("O", CultureInfo.InvariantCulture)),
            new("to", To.ToString("O", CultureInfo.InvariantCulture)),
            new("page", Math.Max(1, Page).ToString(CultureInfo.InvariantCulture)),
            new("page_size", Math.Clamp(PageSize, 1, 100).ToString(CultureInfo.InvariantCulture)),
        };

        if (UserIds.Count > 0)
        {
            values.Add(new("user_ids", string.Join(',', UserIds.Order())));
        }

        string[] operationCodes = OperationTypeCodes
            .Select(code => code.Trim().ToUpperInvariant())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();
        if (operationCodes.Length > 0)
        {
            values.Add(new("operation_type_codes", string.Join(',', operationCodes)));
        }

        if (!string.IsNullOrWhiteSpace(Status))
        {
            values.Add(new("status", Status.Trim()));
        }

        var query = new StringBuilder();
        foreach ((string key, string value) in values)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(Uri.EscapeDataString(key));
            query.Append('=');
            query.Append(Uri.EscapeDataString(value));
        }

        return query.ToString();
    }
}

public sealed class OrderStatisticsDashboard
{
    [JsonPropertyName("accepted_count")]
    public long AcceptedCount { get; set; }

    [JsonPropertyName("marked_error_count")]
    public long MarkedErrorCount { get; set; }

    [JsonPropertyName("total_scan_count")]
    public long TotalScanCount { get; set; }

    [JsonPropertyName("average_daily_count")]
    public double AverageDailyCount { get; set; }

    [JsonPropertyName("error_rate")]
    public double ErrorRate { get; set; }

    [JsonPropertyName("operations")]
    public List<OrderStatisticsOperationSummary> Operations { get; set; } = [];

    [JsonPropertyName("groups")]
    public List<OrderStatisticsGroupSummary> Groups { get; set; } = [];

    public string ErrorRateDisplay => ErrorRate <= 0 ? "-" : $"{ErrorRate * 100:0.00}%";
}

public class OrderStatisticsOperationSummary
{
    [JsonPropertyName("operation_type_code")]
    public string OperationTypeCode { get; set; } = string.Empty;

    [JsonPropertyName("operation_type_name")]
    public string OperationTypeName { get; set; } = string.Empty;

    [JsonPropertyName("accepted_count")]
    public long AcceptedCount { get; set; }

    [JsonPropertyName("marked_error_count")]
    public long MarkedErrorCount { get; set; }

    public string OperationDisplay => OrderStatisticsDisplay.Operation(OperationTypeCode, OperationTypeName);

    public string ErrorRateDisplay => OrderStatisticsDisplay.Percentage(MarkedErrorCount, AcceptedCount);
}

public sealed class OrderStatisticsGroupSummary : OrderStatisticsOperationSummary
{
    [JsonPropertyName("user_id")]
    public uint UserId { get; set; }

    [JsonPropertyName("actor_name")]
    public string ActorName { get; set; } = string.Empty;

    public string UserDisplay => string.IsNullOrWhiteSpace(ActorName) ? $"用户 #{UserId}" : ActorName;
}

public sealed class OrderStatisticsRecordPage
{
    [JsonPropertyName("items")]
    public List<OrderStatisticsRecord> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; } = 20;
}

public sealed class OrderStatisticsRecord
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("actor_user_id")]
    public uint ActorUserId { get; set; }

    [JsonPropertyName("actor_name")]
    public string ActorName { get; set; } = string.Empty;

    [JsonPropertyName("operation_type_code")]
    public string OperationTypeCode { get; set; } = string.Empty;

    [JsonPropertyName("operation_type_name")]
    public string OperationTypeName { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("raw_code")]
    public string RawCode { get; set; } = string.Empty;

    [JsonPropertyName("order_no")]
    public string OrderNo { get; set; } = string.Empty;

    [JsonPropertyName("tracking_no")]
    public string TrackingNo { get; set; } = string.Empty;

    [JsonPropertyName("consolidation_no")]
    public string ConsolidationNo { get; set; } = string.Empty;

    [JsonPropertyName("client_scanned_at")]
    public DateTimeOffset ClientScannedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("result_message")]
    public string ResultMessage { get; set; } = string.Empty;

    [JsonPropertyName("is_marked_error")]
    public bool IsMarkedError { get; set; }

    [JsonPropertyName("marked_error_reason")]
    public string MarkedErrorReason { get; set; } = string.Empty;

    public string UserDisplay => string.IsNullOrWhiteSpace(ActorName) ? $"用户 #{ActorUserId}" : ActorName;

    public string OperationDisplay => OrderStatisticsDisplay.Operation(OperationTypeCode, OperationTypeName);

    public string RecognizedCodeDisplay => OrderStatisticsDisplay.RecognizedCode(OrderNo, TrackingNo, ConsolidationNo);

    public string StatusDisplay => Status switch
    {
        "accepted" => "有效",
        "duplicate" => "重复",
        "invalid" => "错误",
        "voided" => "已作废",
        _ => string.IsNullOrWhiteSpace(Status) ? "未知" : Status,
    };

    public string QualityDisplay => IsMarkedError
        ? $"已标错：{(string.IsNullOrWhiteSpace(MarkedErrorReason) ? "未填写原因" : MarkedErrorReason)}"
        : "正常";
}

public sealed class OrderStatisticsOperationType
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; set; }

    public string Display => OrderStatisticsDisplay.Operation(Code, Name);
}

public sealed class OrderStatisticsUserOption
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    public string Display => string.IsNullOrWhiteSpace(Name)
        ? Username
        : string.IsNullOrWhiteSpace(Username) ? Name : $"{Name}（{Username}）";
}

internal static class OrderStatisticsDisplay
{
    internal static string Operation(string code, string name) => string.IsNullOrWhiteSpace(name)
        ? (string.IsNullOrWhiteSpace(code) ? "未指定" : code)
        : string.IsNullOrWhiteSpace(code) ? name : $"{name}（{code}）";

    internal static string Percentage(long errors, long accepted) => accepted <= 0 ? "-" : $"{(double)errors / accepted * 100:0.00}%";

    internal static string RecognizedCode(string orderNo, string trackingNo, string consolidationNo)
    {
        var values = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(orderNo)) values.Add($"订单：{orderNo}");
        if (!string.IsNullOrWhiteSpace(trackingNo)) values.Add($"运单：{trackingNo}");
        if (!string.IsNullOrWhiteSpace(consolidationNo)) values.Add($"集运：{consolidationNo}");
        return values.Count == 0 ? "-" : string.Join("  ", values);
    }
}

