// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed class PrintSizePreset
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public double WidthMillimeters { get; init; }

    public double HeightMillimeters { get; init; }
}