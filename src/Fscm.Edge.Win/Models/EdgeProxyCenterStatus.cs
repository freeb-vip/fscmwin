// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgeProxyCenterStatus
{
    [JsonPropertyName("reachable")]
    public bool Reachable { get; set; }

    [JsonPropertyName("last_error")]
    public string LastError { get; set; } = string.Empty;
}