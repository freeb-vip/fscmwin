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
}
