// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json;
using Fscm.Edge.Win.Models;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class EdgeCatalogStatusTests
{
    [Fact]
    public void DeserializeManualRefreshRequiredPreservesStaleCatalogState()
    {
        const string Payload = """
            {
              "ready": true,
              "state": "manual_refresh_required",
              "revision": 42,
              "active_generation": 3,
              "last_error": "catalog change history is incomplete",
              "manual_refresh_required": true
            }
            """;

        EdgeCatalogStatus? status = JsonSerializer.Deserialize<EdgeCatalogStatus>(Payload);

        Assert.NotNull(status);
        Assert.True(status.Ready);
        Assert.True(status.ManualRefreshRequired);
        Assert.Equal("manual_refresh_required", status.State);
        Assert.Equal(42UL, status.Revision);
        Assert.Equal("catalog change history is incomplete", status.LastError);
    }
}