// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json;
using Fscm.Edge.Win.Models;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class BoxLabelSummaryTests
{
    [Fact]
    public void IsSelectedRaisesPropertyChangedOnlyWhenValueChanges()
    {
        var label = new BoxLabelSummary();
        var changes = new List<string?>();
        label.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        label.IsSelected = true;
        label.IsSelected = true;
        label.IsSelected = false;

        Assert.Equal([nameof(BoxLabelSummary.IsSelected), nameof(BoxLabelSummary.IsSelected)], changes);
    }

    [Fact]
    public void DeserializeMixedBoxLabelPreservesRelationsAndPrintSnapshot()
    {
        const string Payload = """
            {
              "id": 7,
              "label_code": "BX-7",
              "status_group": "normal",
              "status_label": "正常",
              "is_mixed": true,
              "sku_items": [
                { "sku_id": 11, "sku_code": "SKU-A", "product_id": 21, "product_code": "P-A", "qty_per_box": 3 },
                { "sku_id": 12, "sku_code": "SKU-B", "product_id": 22, "product_code": "P-B", "qty_per_box": 5 }
              ],
              "supplier_order": { "id": 31, "code": "SO-31", "status": "producing" },
              "consolidation_order": { "id": 41, "code": "CON-41", "status": "loading" },
              "receiving": { "status": "pending", "warehouse_code": "WH-1" },
              "printable": true,
              "print_snapshot": { "box_plan_id": 7, "sku_lines": ["SKU-A x 3", "SKU-B x 5"], "box_qr_payload": "BX-7" }
            }
            """;

        BoxLabelSummary? label = JsonSerializer.Deserialize<BoxLabelSummary>(Payload);

        Assert.NotNull(label);
        Assert.Equal("SKU-A x 3 / SKU-B x 5", label.SkuDisplay);
        Assert.Equal("CON-41", label.ConsolidationDisplay);
        Assert.Equal("待收货", label.ReceivingDisplay);
        Assert.Equal(7u, label.PrintSnapshot?.BoxPlanId);
        Assert.Equal("BX-7", label.PrintSnapshot?.BoxQrPayload);
    }
}
