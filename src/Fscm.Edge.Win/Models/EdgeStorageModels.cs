// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

#pragma warning disable SA1402, SA1649

public sealed class EdgeStorageConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("local_path")]
    public string LocalPath { get; set; } = string.Empty;

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 7;

    [JsonPropertyName("reserve_free_gb")]
    public int ReserveFreeGigabytes { get; set; } = 10;

    [JsonPropertyName("share_name")]
    public string ShareName { get; set; } = "FscmRecordings";

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("smb_compatibility_mode")]
    public string SmbCompatibilityMode { get; set; } = "system_default";
}

public sealed class EdgeStorageConfigResponse
{
    [JsonPropertyName("config")]
    public EdgeStorageConfig Config { get; set; } = new();

    [JsonPropertyName("share_paths")]
    public List<string> SharePaths { get; set; } = [];
}

public sealed class EdgeStorageCleanupResult
{
    [JsonPropertyName("finished_at")]
    public DateTimeOffset FinishedAt { get; set; }

    [JsonPropertyName("deleted_files")]
    public int DeletedFiles { get; set; }

    [JsonPropertyName("freed_bytes")]
    public long FreedBytes { get; set; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];
}

public sealed class EdgeStorageStatus
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "disabled";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("share_paths")]
    public List<string> SharePaths { get; set; } = [];

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("local_path")]
    public string LocalPath { get; set; } = string.Empty;

    [JsonPropertyName("volume_total_bytes")]
    public long VolumeTotalBytes { get; set; }

    [JsonPropertyName("volume_free_bytes")]
    public long VolumeFreeBytes { get; set; }

    [JsonPropertyName("last_write_at")]
    public DateTimeOffset? LastWriteAt { get; set; }

    [JsonPropertyName("last_cleanup")]
    public EdgeStorageCleanupResult? LastCleanup { get; set; }

    [JsonPropertyName("share_ready")]
    public bool ShareReady { get; set; }

    [JsonPropertyName("account_ready")]
    public bool AccountReady { get; set; }

    [JsonPropertyName("firewall_ready")]
    public bool FirewallReady { get; set; }

    [JsonPropertyName("smb_compatibility_mode")]
    public string SmbCompatibilityMode { get; set; } = "system_default";

    [JsonPropertyName("smb1_enabled")]
    public bool Smb1Enabled { get; set; }

    [JsonPropertyName("smb2_enabled")]
    public bool Smb2Enabled { get; set; }

    [JsonPropertyName("smb_signing_required")]
    public bool SmbSigningRequired { get; set; }

    [JsonPropertyName("signing_override_managed")]
    public bool SigningOverrideManaged { get; set; }

    [JsonPropertyName("compatibility_ready")]
    public bool CompatibilityReady { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}

public sealed class EdgeStorageStatusResponse
{
    [JsonPropertyName("storage")]
    public EdgeStorageStatus Storage { get; set; } = new();
}

public sealed class EdgeStorageCredentials
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("share_paths")]
    public List<string> SharePaths { get; set; } = [];
}

public sealed class EdgeStorageApplyResponse
{
    [JsonPropertyName("config")]
    public EdgeStorageConfig Config { get; set; } = new();

    [JsonPropertyName("status")]
    public EdgeStorageStatus Status { get; set; } = new();

    [JsonPropertyName("credentials")]
    public EdgeStorageCredentials? Credentials { get; set; }
}

public sealed class EdgeStorageCredentialsResponse
{
    [JsonPropertyName("credentials")]
    public EdgeStorageCredentials Credentials { get; set; } = new();
}

#pragma warning restore SA1402, SA1649
