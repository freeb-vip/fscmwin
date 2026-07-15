// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class EdgePrintJobItem
{
    [JsonPropertyName("sku_id")]
    public uint SkuId { get; set; }

    [JsonPropertyName("sku_code")]
    public string SkuCode { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("qr_code_content")]
    public string QrCodeContent { get; set; } = string.Empty;
}