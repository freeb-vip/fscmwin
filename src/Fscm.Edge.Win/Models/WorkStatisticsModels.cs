using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class WorkScannerBinding
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("user_id")]
    public uint UserId { get; set; }

    [JsonPropertyName("binding_code")]
    public string BindingCode { get; set; } = string.Empty;

    [JsonPropertyName("device_fingerprint")]
    public string DeviceFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("user_name")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("user_display_name")]
    public string CenterUserDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public WorkStatisticsUser? User { get; set; }

    public string DeviceDisplayName => FirstNonEmpty(DeviceName, ShortFingerprint) ?? $"扫描器 #{Id}";

    public string ShortFingerprint => FormatFingerprint(DeviceFingerprint);

    public string DeviceStatus => "已绑定";

    public string UserDisplayName => FirstNonEmpty(
        Username,
        UserName,
        CenterUserDisplayName,
        DisplayName,
        User?.Username,
        User?.DisplayName,
        BindingUsername) ?? $"用户 #{UserId}";

    private string? BindingUsername => BindingCode.StartsWith("USER_", StringComparison.OrdinalIgnoreCase)
        ? FirstNonEmpty(BindingCode[5..])
        : null;

    internal static string FormatFingerprint(string value)
    {
        const int prefixLength = 12;
        const int suffixLength = 8;
        const int maximumLength = prefixLength + suffixLength + 3;
        string fingerprint = value.Trim();
        return fingerprint.Length <= maximumLength
            ? fingerprint
            : $"{fingerprint[..prefixLength]}...{fingerprint[^suffixLength..]}";
    }

    internal static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }
}

public sealed class ActiveWorkSession
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("binding_id")]
    public uint BindingId { get; set; }

    [JsonPropertyName("user_id")]
    public uint UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("user_name")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("user_display_name")]
    public string CenterUserDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public WorkStatisticsUser? User { get; set; }

    [JsonPropertyName("operation_type_code")]
    public string OperationTypeCode { get; set; } = string.Empty;

    [JsonPropertyName("operation_type_name")]
    public string OperationTypeName { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonIgnore]
    public string DeviceFingerprint { get; set; } = string.Empty;

    [JsonIgnore]
    public string ResolvedUserName { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    public string ShortFingerprint => WorkScannerBinding.FormatFingerprint(DeviceFingerprint);

    public string DeviceDisplay => WorkScannerBinding.FirstNonEmpty(DeviceName, DeviceId, DeviceFingerprint) ?? $"扫描器 #{BindingId}";

    public string OperationDisplay => WorkScannerBinding.FirstNonEmpty(
        string.IsNullOrWhiteSpace(OperationTypeName) ? null : OperationTypeName,
        string.IsNullOrWhiteSpace(OperationTypeCode) ? null : OperationTypeCode) ?? "未指定";

    public string StatusDisplay => Status.Trim().ToLowerInvariant() switch
    {
        "active" or "open" => "进行中",
        "expired" => "已过期",
        "ended" or "finished" => "已结束",
        _ => ExpiresAt != default && ExpiresAt <= DateTimeOffset.Now ? "已过期" : "进行中",
    };

    public string UserDisplayName => WorkScannerBinding.FirstNonEmpty(
        Username,
        UserName,
        CenterUserDisplayName,
        DisplayName,
        User?.Username,
        User?.DisplayName,
        ResolvedUserName) ?? $"用户 #{UserId}";
}

public sealed class WorkStatisticsUser
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;
}

public sealed record WorkStatisticsResult(
    IReadOnlyList<WorkScannerBinding> Bindings,
    IReadOnlyList<ActiveWorkSession> Sessions,
    IReadOnlyList<OrderScanJob> ActiveJobs,
    IReadOnlyList<OrderScanJob> FinishedJobs);

public sealed class OrderScanJob
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("job_no")]
    public string JobNo { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("actor_name")]
    public string ActorName { get; set; } = string.Empty;

    [JsonPropertyName("operation_type_code")]
    public string OperationTypeCode { get; set; } = string.Empty;

    [JsonPropertyName("operation_type_name")]
    public string OperationTypeName { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("device_fingerprint")]
    public string DeviceFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("paused_at")]
    public DateTimeOffset? PausedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonPropertyName("last_scanned_at")]
    public DateTimeOffset? LastScannedAt { get; set; }

    [JsonPropertyName("accepted_count")]
    public int AcceptedCount { get; set; }

    [JsonPropertyName("duplicate_count")]
    public int DuplicateCount { get; set; }

    [JsonPropertyName("invalid_count")]
    public int InvalidCount { get; set; }

    [JsonPropertyName("voided_count")]
    public int VoidedCount { get; set; }

    public string StatusDisplay => Status.Trim().ToLowerInvariant() switch
    {
        "pending" => "待开始",
        "open" => "进行中",
        "paused" => "已暂停",
        "finished" => "已结束",
        _ => string.IsNullOrWhiteSpace(Status) ? "未知" : Status,
    };

    public string OperationDisplay => string.IsNullOrWhiteSpace(OperationTypeName)
        ? OperationTypeCode
        : $"{OperationTypeName}（{OperationTypeCode}）";

    public string DeviceDisplay => WorkScannerBinding.FirstNonEmpty(DeviceName, DeviceId, DeviceFingerprint) ?? "-";

    public string ScanCountsDisplay => $"{AcceptedCount} / {DuplicateCount} / {InvalidCount}";

    public int TotalScanCount => AcceptedCount + DuplicateCount + InvalidCount + VoidedCount;

    public DateTimeOffset? LastActivityAt => LastScannedAt ?? PausedAt ?? (StartedAt == default ? null : StartedAt);
}




