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
