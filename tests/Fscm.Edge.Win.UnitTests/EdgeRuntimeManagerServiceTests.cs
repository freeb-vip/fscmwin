// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class EdgeRuntimeManagerServiceTests
{
    [Theory]
    [InlineData("STATE              : 1  STOPPED", 1)]
    [InlineData("STATE              : 2  START_PENDING", 2)]
    [InlineData("STATE              : 4  RUNNING", 4)]
    [InlineData("TYPE : 10 WIN32_OWN_PROCESS\r\nSTATE : 4 RUNNING", 4)]
    public void ParsesScServiceStateWithoutDependingOnStateValueText(string output, int expected)
    {
        Assert.Equal(expected, EdgeRuntimeManager.ParseWindowsServiceState(output));
    }

    [Fact]
    public void RejectsMissingOrInvalidScServiceState()
    {
        Assert.Null(EdgeRuntimeManager.ParseWindowsServiceState(string.Empty));
        Assert.Null(EdgeRuntimeManager.ParseWindowsServiceState("TYPE : 10 WIN32_OWN_PROCESS"));
    }

    [Theory]
    [InlineData("PID                : 4312", 4312)]
    [InlineData("STATE : 4 RUNNING\r\nPID : 901", 901)]
    public void ParsesScServiceProcessId(string output, int expected)
    {
        Assert.Equal(expected, EdgeRuntimeManager.ParseWindowsServiceProcessId(output));
    }

    [Fact]
    public void RejectsMissingOrStoppedServiceProcessId()
    {
        Assert.Null(EdgeRuntimeManager.ParseWindowsServiceProcessId(string.Empty));
        Assert.Null(EdgeRuntimeManager.ParseWindowsServiceProcessId("PID : 0"));
    }

    [Fact]
    public void ReturnsLastMeaningfulServiceErrorLine()
    {
        const string content = "first error\r\n\r\n2026-08-18 service startup failed: listen tcp :8089: bind failed\r\n";

        Assert.Equal(
            "2026-08-18 service startup failed: listen tcp :8089: bind failed",
            EdgeRuntimeManager.LastMeaningfulLogLine(content));
    }
}
