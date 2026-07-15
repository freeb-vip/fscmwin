// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgeCacheStatus
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public int Entries { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("hits")]
    public ulong Hits { get; set; }

    [JsonPropertyName("misses")]
    public ulong Misses { get; set; }

    [JsonPropertyName("stale_hits")]
    public ulong StaleHits { get; set; }

    [JsonPropertyName("evictions")]
    public ulong Evictions { get; set; }
}