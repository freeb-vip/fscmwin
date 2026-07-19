// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class BoxLabelSkuItem
{
    [JsonPropertyName("sku_id")]
    public uint SkuId { get; set; }

    [JsonPropertyName("sku_code")]
    public string SkuCode { get; set; } = string.Empty;

    [JsonPropertyName("sku_name")]
    public string SkuName { get; set; } = string.Empty;

    [JsonPropertyName("product_id")]
    public uint ProductId { get; set; }

    [JsonPropertyName("product_code")]
    public string ProductCode { get; set; } = string.Empty;

    [JsonPropertyName("product_name")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("qty_per_box")]
    public int QuantityPerBox { get; set; }
}
