// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Services;

public sealed class EdgeRuntimeStatus
{
    public bool BinaryExists { get; init; }

    public bool ConfigExists { get; init; }

    public bool IsRunning { get; init; }

    public bool IsHealthy { get; init; }

    public int Port { get; init; }

    public int? ProcessId { get; init; }

    public string Message { get; init; } = string.Empty;
}