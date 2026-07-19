// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgeCatalogStatus
{
    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public ulong Revision { get; set; }

    [JsonPropertyName("last_full_sync_at")]
    public DateTimeOffset? LastFullSyncAt { get; set; }

    [JsonPropertyName("last_error")]
    public string LastError { get; set; } = string.Empty;

    [JsonPropertyName("box_label_count")]
    public long BoxLabelCount { get; set; }

    [JsonPropertyName("box_labels_ready")]
    public bool BoxLabelsReady { get; set; }
}
