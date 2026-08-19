// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class ScannerConfigurationServiceTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("\"42\"")]
    public void ScannerStatusAcceptsNumericUserIds(string userIdJson)
    {
        string json = $$"""
            {
              "bindings": [
                {
                  "device_fingerprint": "scanner-1",
                  "user_id": {{userIdJson}}
                }
              ]
            }
            """;

        ScannerStatus? status = JsonSerializer.Deserialize<ScannerStatus>(json);

        ScannerBindingStatus binding = Assert.Single(Assert.IsType<ScannerStatus>(status).Bindings);
        Assert.Equal(42U, binding.UserId);
    }

    [Fact]
    public void LegacyConfigurationTreatsDeviceAsScannerCandidate()
    {
        const string json = """
            {
              "enabled": true,
              "devices": [
                {
                  "enabled": true,
                  "fingerprint": "legacy-scanner"
                }
              ]
            }
            """;

        ScannerConfiguration? configuration = JsonSerializer.Deserialize<ScannerConfiguration>(json);

        ScannerDeviceConfiguration device = Assert.Single(Assert.IsType<ScannerConfiguration>(configuration).Devices);
        Assert.False(device.Excluded);
        Assert.True(ScannerDevicePolicy.IsActive(device));
        Assert.True(ScannerDevicePolicy.IsVisible(device, showExcluded: false));
    }

    [Fact]
    public void ExcludedDeviceIsPersistedAndForcedInactive()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fscm-scanner-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "scanner.json");
        try
        {
            var service = new ScannerConfigurationService(path);
            service.Save(new ScannerConfiguration
            {
                Enabled = true,
                Devices =
                [
                    new ScannerDeviceConfiguration
                    {
                        Enabled = true,
                        Excluded = true,
                        Fingerprint = "keyboard",
                    },
                ],
            });

            ScannerDeviceConfiguration device = Assert.Single(service.Load().Devices);
            Assert.True(device.Excluded);
            Assert.False(device.Enabled);
            Assert.False(ScannerDevicePolicy.IsActive(device));
            Assert.False(ScannerDevicePolicy.IsVisible(device, showExcluded: false));
            Assert.True(ScannerDevicePolicy.IsVisible(device, showExcluded: true));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RestoredDeviceRemainsDisabledUntilExplicitlyEnabled()
    {
        var device = new ScannerDeviceConfiguration { Enabled = false, Excluded = true };

        device.Excluded = false;
        ScannerConfigurationService.Normalize(new ScannerConfiguration { Devices = [device] });

        Assert.True(ScannerDevicePolicy.IsVisible(device, showExcluded: false));
        Assert.False(ScannerDevicePolicy.IsActive(device));
    }
}