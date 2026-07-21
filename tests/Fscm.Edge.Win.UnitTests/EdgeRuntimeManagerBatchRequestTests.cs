// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Net.Http.Headers;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class EdgeRuntimeManagerBatchRequestTests
{
    [Fact]
    public void CenterManagementRequestTargetsConfiguredCenterAndPreservesAuthContext()
    {
        var settings = new EdgeSettings
        {
            CenterUrl = "https://center.example.test/root/",
            ApiToken = "batch-token",
            NamespaceId = 42,
        };

        using HttpRequestMessage request = EdgeRuntimeManager.CreateCenterManagementRequest(
            HttpMethod.Post,
            "/api/edge/print-batches/preview",
            settings);

        Assert.Equal("https://center.example.test/root/api/edge/print-batches/preview", request.RequestUri?.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "batch-token"), request.Headers.Authorization);
        Assert.Equal("batch-token", Assert.Single(request.Headers.GetValues("X-Api-Token")));
        Assert.Equal("batch-token", Assert.Single(request.Headers.GetValues("X-Edge-Token")));
        Assert.Equal("42", Assert.Single(request.Headers.GetValues("X-Namespace-ID")));
    }

    [Fact]
    public void CenterManagementRequestRejectsMissingCenterUrl()
    {
        var settings = new EdgeSettings { ApiToken = "batch-token" };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        {
            using HttpRequestMessage request = EdgeRuntimeManager.CreateCenterManagementRequest(HttpMethod.Get, "/api/edge/nodes", settings);
        });

        Assert.Equal("请先配置中心地址。", error.Message);
    }
}
