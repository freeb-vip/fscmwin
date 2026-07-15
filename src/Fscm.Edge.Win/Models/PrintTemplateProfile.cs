// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed class PrintTemplateProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "label";

    public string Printer { get; set; } = string.Empty;

    public double WidthMillimeters { get; set; }

    public double HeightMillimeters { get; set; }

    public string Orientation { get; set; } = "portrait";

    public string Mode { get; set; } = "fit";

    public int Copies { get; set; } = 1;

    public double OffsetXMillimeters { get; set; }

    public string SkuQrPrefix { get; set; } = "T";

    public string LabelQrPrefix { get; set; } = string.Empty;

    public int MaxDisplayLength { get; set; } = 16;
}
