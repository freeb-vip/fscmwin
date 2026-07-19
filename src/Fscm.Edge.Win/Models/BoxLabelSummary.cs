// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class BoxLabelSummary : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("label_code")]
    public string LabelCode { get; set; } = string.Empty;

    [JsonPropertyName("box_uid")]
    public string BoxUid { get; set; } = string.Empty;

    [JsonPropertyName("box_no")]
    public string BoxNo { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("status_group")]
    public string StatusGroup { get; set; } = string.Empty;

    [JsonPropertyName("status_label")]
    public string StatusLabel { get; set; } = string.Empty;

    [JsonPropertyName("is_mixed")]
    public bool IsMixed { get; set; }

    [JsonPropertyName("sku_items")]
    public List<BoxLabelSkuItem> SkuItems { get; set; } = [];

    [JsonPropertyName("planned_box_qty")]
    public int PlannedBoxQuantity { get; set; }

    [JsonPropertyName("case_spec_name")]
    public string CaseSpecName { get; set; } = string.Empty;

    [JsonPropertyName("manufacturer_name")]
    public string ManufacturerName { get; set; } = string.Empty;

    [JsonPropertyName("supplier_order")]
    public BoxLabelDocumentRef SupplierOrder { get; set; } = new();

    [JsonPropertyName("purchase_order")]
    public BoxLabelDocumentRef? PurchaseOrder { get; set; }

    [JsonPropertyName("central_receipt")]
    public BoxLabelDocumentRef? CentralReceipt { get; set; }

    [JsonPropertyName("consolidation_order")]
    public BoxLabelDocumentRef? ConsolidationOrder { get; set; }

    [JsonPropertyName("consolidation_container")]
    public string ConsolidationContainer { get; set; } = string.Empty;

    [JsonPropertyName("receiving")]
    public BoxLabelReceivingInfo Receiving { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("printable")]
    public bool Printable { get; set; }

    [JsonPropertyName("print_snapshot")]
    public ManufacturerBoxMark? PrintSnapshot { get; set; }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    [JsonIgnore]
    public string SkuDisplay => string.Join(" / ", SkuItems.Select(item => $"{item.SkuCode} x {item.QuantityPerBox}"));

    [JsonIgnore]
    public string ConsolidationDisplay => ConsolidationOrder?.Code ?? string.Empty;

    [JsonIgnore]
    public string ReceivingDisplay => Receiving.Status switch
    {
        "pending" => "待收货",
        "scanning" => "扫码中",
        "received" => "已收货",
        "damaged" => "破损",
        _ => Receiving.Status,
    };
}
