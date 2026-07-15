// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class SkuSummary
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("spec")]
    public string Spec { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;

    [JsonPropertyName("product_id")]
    public uint ProductId { get; set; }

    [JsonPropertyName("product_code")]
    public string ProductCode { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsSelected { get; set; }
}