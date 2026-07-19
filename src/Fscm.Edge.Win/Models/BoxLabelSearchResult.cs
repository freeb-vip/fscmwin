// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed class BoxLabelSearchResult
{
    public IReadOnlyList<BoxLabelSummary> Items { get; init; } = [];

    public long Total { get; init; }

    public string Source { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
