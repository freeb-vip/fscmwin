// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed class LocalPrinter
{
    public string Name { get; init; } = string.Empty;

    public bool IsDefault { get; init; }

    public bool IsAvailable { get; init; }

    public string StatusCode { get; init; } = "unknown";

    public string StatusText { get; init; } = "状态未知";

    public string DisplayName => $"{Name}{(IsDefault ? " (Windows 默认)" : string.Empty)} · {StatusText}";
}
