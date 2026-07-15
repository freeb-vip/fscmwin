// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed class RemoteCenterStatus
{
    public string CenterUrl { get; init; } = string.Empty;

    public bool IsConfigured { get; init; }

    public bool IsReachable { get; init; }

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public string Message { get; init; } = string.Empty;
}