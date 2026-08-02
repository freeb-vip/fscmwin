// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json;
using Fscm.Edge.Win.Models;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class EdgeStorageModelsTests
{
    [Fact]
    public void SerializesXiaomiCompatibilityMode()
    {
        var config = new EdgeStorageConfig
        {
            Enabled = true,
            LocalPath = @"D:\recordings",
            RetentionDays = 7,
            ReserveFreeGigabytes = 10,
            SmbCompatibilityMode = "xiaomi_smb2",
        };

        string json = JsonSerializer.Serialize(config);

        Assert.Contains(@"""smb_compatibility_mode"":""xiaomi_smb2""", json);
    }

    [Fact]
    public void DeserializesProtocolAndSigningStatus()
    {
        const string json = """
            {
              "state": "ready",
              "smb_compatibility_mode": "xiaomi_smb2",
              "smb1_enabled": false,
              "smb2_enabled": true,
              "smb_signing_required": false,
              "signing_override_managed": true,
              "compatibility_ready": true
            }
            """;

        EdgeStorageStatus? status = JsonSerializer.Deserialize<EdgeStorageStatus>(json);

        Assert.NotNull(status);
        Assert.False(status.Smb1Enabled);
        Assert.True(status.Smb2Enabled);
        Assert.False(status.SmbSigningRequired);
        Assert.True(status.SigningOverrideManaged);
        Assert.True(status.CompatibilityReady);
    }
}
