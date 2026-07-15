// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgeRuntimeManifest
{
    [JsonPropertyName("edge_version")]
    public string EdgeVersion { get; set; } = string.Empty;

    [JsonPropertyName("edge_commit")]
    public string EdgeCommit { get; set; } = string.Empty;

    [JsonPropertyName("edge_api_version")]
    public string EdgeApiVersion { get; set; } = string.Empty;

    [JsonPropertyName("runtime_kind")]
    public string RuntimeKind { get; set; } = string.Empty;

    [JsonPropertyName("binary")]
    public string Binary { get; set; } = "fscm-edge.exe";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("built_at")]
    public string BuiltAt { get; set; } = string.Empty;
}