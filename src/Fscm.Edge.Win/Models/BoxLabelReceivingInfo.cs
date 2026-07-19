// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class BoxLabelReceivingInfo
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("warehouse_code")]
    public string WarehouseCode { get; set; } = string.Empty;

    [JsonPropertyName("location_code")]
    public string LocationCode { get; set; } = string.Empty;

    [JsonPropertyName("session")]
    public BoxLabelDocumentRef? Session { get; set; }

    [JsonPropertyName("scan_status")]
    public string ScanStatus { get; set; } = string.Empty;

    [JsonPropertyName("scanned_at")]
    public DateTimeOffset? ScannedAt { get; set; }

    [JsonPropertyName("actual_received_qty")]
    public int ActualReceivedQuantity { get; set; }

    [JsonPropertyName("receiving_record")]
    public BoxLabelDocumentRef? ReceivingRecord { get; set; }
}
