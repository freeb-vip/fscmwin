// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

public sealed class PrintTemplateProfile
{
    public string Id { get; set; } = string.Empty;

    public string TemplateNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "label";

    public string Printer { get; set; } = string.Empty;

    public double WidthMillimeters { get; set; }

    public double HeightMillimeters { get; set; }

    public string Orientation { get; set; } = "portrait";

    public string Mode { get; set; } = "fit";

    public int Copies { get; set; } = 1;

    public double OffsetXMillimeters { get; set; }

    public double OffsetYMillimeters { get; set; }

    public double SafetyInsetMillimeters { get; set; } = 1.5;

    public string SkuQrPrefix { get; set; } = "T";

    public string LabelQrPrefix { get; set; } = string.Empty;

    public string LayoutStyle { get; set; } = "stacked";

    public double TextFontSizePoints { get; set; }

    public int MaxDisplayLength { get; set; } = 16;

    [JsonIgnore]
    public bool IsPrinterAvailable { get; set; }

    [JsonIgnore]
    public string PrinterStatusText { get; set; } = "未配置打印机";

    [JsonIgnore]
    public bool IsSelectedForLabelPrint { get; set; }

    [JsonIgnore]
    public string PaperDisplayText => $"{WidthMillimeters:0.##} x {HeightMillimeters:0.##} mm";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(TemplateNumber)
        ? Name
        : $"{TemplateNumber} · {Name}";

    [JsonIgnore]
    public string OrientationDisplayText => string.Equals(Orientation, "landscape", StringComparison.OrdinalIgnoreCase)
        ? "横向"
        : "纵向";

    [JsonIgnore]
    public string LayoutDisplayText => LayoutStyle switch
    {
        "qr_left_text_right" => "左右排版",
        "location_code_quad_qr" => "库位码四码排版",
        _ => "上下排版",
    };

    [JsonIgnore]
    public string PrinterDisplayText => string.IsNullOrWhiteSpace(Printer) ? "使用默认打印机" : Printer;
}
