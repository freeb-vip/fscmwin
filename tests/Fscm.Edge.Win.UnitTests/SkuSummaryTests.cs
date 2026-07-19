// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Fscm.Edge.Win.Models;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class SkuSummaryTests
{
    [Fact]
    public void IsSelectedRaisesPropertyChangedOnlyWhenValueChanges()
    {
        var sku = new SkuSummary();
        var changes = new List<string?>();
        sku.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        sku.IsSelected = true;
        sku.IsSelected = true;
        sku.IsSelected = false;

        Assert.Equal([nameof(SkuSummary.IsSelected), nameof(SkuSummary.IsSelected)], changes);
    }
}
