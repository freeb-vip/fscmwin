// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Services;

public sealed class EdgeCenterRegistrationResult
{
    public bool Attempted { get; init; }

    public bool Succeeded { get; init; }

    public string Message { get; init; } = string.Empty;
}