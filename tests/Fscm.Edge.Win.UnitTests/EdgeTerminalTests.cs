// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Fscm.Edge.Win.Models;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class EdgeTerminalTests
{
    [Fact]
    public void FindActions_RequireOnlineCapableTerminal()
    {
        EdgeTerminal terminal = new()
        {
            Status = "online",
            Capabilities = ["mobile-app", "find-device"],
        };

        Assert.True(terminal.CanStartFind);
        Assert.False(terminal.CanStopFind);

        terminal.Finding = true;

        Assert.False(terminal.CanStartFind);
        Assert.True(terminal.CanStopFind);
    }

    [Fact]
    public void FindActions_AreDisabledForProbeAndOfflineRows()
    {
        EdgeTerminal probe = new() { Status = "online" };
        EdgeTerminal offline = new() { Status = "offline", Capabilities = ["find-device"], Finding = true };

        Assert.False(probe.CanStartFind);
        Assert.False(probe.CanStopFind);
        Assert.False(offline.CanStartFind);
        Assert.False(offline.CanStopFind);
    }
}
