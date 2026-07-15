// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Net;
using System.Text;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_ReturnsNewerOnlineDesktopRelease()
    {
        const string body = """
            {"code":0,"data":{"items":[{"app_name":"FSCM Edge","platform":"desktop","version":"99.0.0","release_notes":"Update","original_name":"FSCM-Edge-Setup-99.0.0.exe","size_bytes":123,"checksum_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","is_online":true,"download_path":"/api/app-downloads/token"}]}}
        """;
        using StubHttpMessageHandler handler = new(body);
        using HttpClient client = new(handler, disposeHandler: false);
        using AppUpdateService service = new(client);

        AppUpdateCheckResult result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result.Release);
        Assert.Equal(new Version(99, 0, 0), result.Release.Version);
        Assert.Equal("Update", result.Release.ReleaseNotes);
    }

    [Fact]
    public async Task CheckAsync_RejectsInvalidReleaseMetadata()
    {
        const string body = """
            {"code":0,"data":{"items":[{"app_name":"FSCM Edge","platform":"desktop","version":"next","original_name":"update.exe","size_bytes":123,"checksum_sha256":"bad","is_online":true,"download_path":"https://unsafe.example/update.exe"}]}}
        """;
        using StubHttpMessageHandler handler = new(body);
        using HttpClient client = new(handler, disposeHandler: false);
        using AppUpdateService service = new(client);

        AppUpdateCheckResult result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Release);
        Assert.False(result.IsServiceAvailable);
    }

    private sealed class StubHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }
}
