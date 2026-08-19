// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgeTerminal
{
    [JsonPropertyName("terminal_id")]
    public string TerminalId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("connected_at")]
    public DateTimeOffset? ConnectedAt { get; set; }

    [JsonPropertyName("last_seen_at")]
    public DateTimeOffset LastSeenAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("lan_status")]
    public string LanStatus { get; set; } = string.Empty;

    [JsonPropertyName("health_status")]
    public string HealthStatus { get; set; } = string.Empty;

    [JsonPropertyName("health_reason")]
    public string HealthReason { get; set; } = string.Empty;

    [JsonPropertyName("is_alert")]
    public bool IsAlert { get; set; }

    [JsonPropertyName("assigned_edge_node_name")]
    public string AssignedEdgeNodeName { get; set; } = string.Empty;

    [JsonPropertyName("observed_edge_node_name")]
    public string ObservedEdgeNodeName { get; set; } = string.Empty;

    [JsonPropertyName("lan_online_since")]
    public DateTimeOffset? LanOnlineSince { get; set; }

    [JsonPropertyName("lan_last_seen_at")]
    public DateTimeOffset? LanLastSeenAt { get; set; }

    [JsonPropertyName("online_duration_seconds")]
    public long OnlineDurationSeconds { get; set; }

    [JsonPropertyName("offline_duration_seconds")]
    public long OfflineDurationSeconds { get; set; }

    [JsonIgnore]
    public string HealthDisplay => HealthStatus switch
    {
        "area_online" => "区域在线",
        "temporarily_unreachable" => "暂不可达",
        "terminal_alert" => "终端异常",
        "out_of_area" => "离开本区域",
        "monitoring_interrupted" => "检测中断",
        "unassigned" => "未分配",
        _ => string.IsNullOrWhiteSpace(HealthStatus) ? Status : HealthStatus,
    };

    [JsonIgnore]
    public string OnlineDurationDisplay => FormatDuration(OnlineDurationSeconds);

    [JsonIgnore]
    public string OfflineDurationDisplay => FormatDuration(OfflineDurationSeconds);

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "-";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}天 {duration:hh\\:mm}" : $"{(int)duration.TotalHours:00}:{duration:mm\\:ss}";
    }

    [JsonPropertyName("finding")]
    public bool Finding { get; set; }

    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [JsonIgnore]
    public bool CanStartFind => !Finding &&
        Status.Equals("online", StringComparison.OrdinalIgnoreCase) &&
        Capabilities.Any(value => value.Equals("find-device", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public bool CanStopFind => Finding && Status.Equals("online", StringComparison.OrdinalIgnoreCase);
}
