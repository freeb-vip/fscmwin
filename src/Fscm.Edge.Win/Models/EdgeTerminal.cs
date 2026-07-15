// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgeTerminal
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("last_seen_at")]
    public DateTimeOffset LastSeenAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}