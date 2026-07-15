// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed record AppUpdateRelease(
    Version Version,
    string ReleaseNotes,
    string OriginalName,
    long SizeBytes,
    string ChecksumSha256,
    DateTimeOffset? PublishedAt,
    string DownloadPath);
