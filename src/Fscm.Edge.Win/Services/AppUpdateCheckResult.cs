// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

public sealed record AppUpdateCheckResult(AppUpdateRelease? Release, string Message, bool IsServiceAvailable)
{
    public static AppUpdateCheckResult Available(AppUpdateRelease release) => new(release, "Update is available.", true);

    public static AppUpdateCheckResult Current(string message) => new(null, message, true);

    public static AppUpdateCheckResult Unavailable(string message) => new(null, message, false);
}
