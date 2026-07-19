// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class ManufacturerBoxMark
{
    [JsonPropertyName("box_plan_id")]
    public uint BoxPlanId { get; set; }

    [JsonPropertyName("sea_mark")]
    public string SeaMark { get; set; } = string.Empty;

    [JsonPropertyName("shop")]
    public string Shop { get; set; } = string.Empty;

    [JsonPropertyName("style_code")]
    public string StyleCode { get; set; } = string.Empty;

    [JsonPropertyName("sku_lines")]
    public List<string> SkuLines { get; set; } = [];

    [JsonPropertyName("spec")]
    public string Spec { get; set; } = string.Empty;

    [JsonPropertyName("pcs")]
    public string Pcs { get; set; } = string.Empty;

    [JsonPropertyName("sku_boxs")]
    public string SkuBoxs { get; set; } = string.Empty;

    [JsonPropertyName("batch")]
    public string Batch { get; set; } = string.Empty;

    [JsonPropertyName("sku_qr_payload")]
    public string SkuQrPayload { get; set; } = string.Empty;

    [JsonPropertyName("box_qr_payload")]
    public string BoxQrPayload { get; set; } = string.Empty;

    [JsonPropertyName("box_uid")]
    public string BoxUid { get; set; } = string.Empty;

    [JsonPropertyName("inbound_code")]
    public string InboundCode { get; set; } = string.Empty;
}
