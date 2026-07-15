// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgeCacheStatusResponse
{
    [JsonPropertyName("cache")]
    public EdgeCacheStatus Cache { get; set; } = new();

    [JsonPropertyName("center")]
    public EdgeProxyCenterStatus Center { get; set; } = new();
}