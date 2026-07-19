// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Printing;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class LocalPrinterServiceTests
{
    public static TheoryData<PrintQueueStatus, string> UnavailableStatuses => new()
    {
        { PrintQueueStatus.Offline, "offline" },
        { PrintQueueStatus.Error, "error" },
        { PrintQueueStatus.Paused, "paused" },
        { PrintQueueStatus.PaperJam, "paper_jam" },
        { PrintQueueStatus.PaperOut, "paper_out" },
        { PrintQueueStatus.PaperProblem, "paper_problem" },
        { PrintQueueStatus.ManualFeed, "manual_feed" },
        { PrintQueueStatus.NoToner, "no_toner" },
        { PrintQueueStatus.DoorOpen, "door_open" },
        { PrintQueueStatus.OutputBinFull, "output_bin_full" },
        { PrintQueueStatus.UserIntervention, "user_intervention" },
        { PrintQueueStatus.NotAvailable, "not_available" },
        { PrintQueueStatus.ServerUnknown, "server_unknown" },
    };

    public static TheoryData<PrintQueueStatus> AvailableStatuses => new()
    {
        PrintQueueStatus.None,
        PrintQueueStatus.Busy,
        PrintQueueStatus.Printing,
        PrintQueueStatus.Processing,
        PrintQueueStatus.WarmingUp,
        PrintQueueStatus.PowerSave,
        PrintQueueStatus.TonerLow,
    };

    [Theory]
    [MemberData(nameof(UnavailableStatuses))]
    public void EvaluateStatusRejectsUnavailableQueues(PrintQueueStatus queueStatus, string expectedCode)
    {
        PrinterStatus status = LocalPrinterService.EvaluateStatus(queueStatus);

        Assert.False(status.IsAvailable);
        Assert.Equal(expectedCode, status.Code);
        Assert.False(string.IsNullOrWhiteSpace(status.Text));
    }

    [Theory]
    [MemberData(nameof(AvailableStatuses))]
    public void EvaluateStatusAcceptsQueuesThatCanReceiveJobs(PrintQueueStatus queueStatus)
    {
        PrinterStatus status = LocalPrinterService.EvaluateStatus(queueStatus);

        Assert.True(status.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(status.Text));
    }

    [Fact]
    public void EvaluateStatusPrefersSpecificBlockingReason()
    {
        PrinterStatus status = LocalPrinterService.EvaluateStatus(PrintQueueStatus.Error | PrintQueueStatus.PaperOut);

        Assert.False(status.IsAvailable);
        Assert.Equal("paper_out", status.Code);
        Assert.Equal("缺纸", status.Text);
    }

    [Fact]
    public void DisplayNameIncludesDefaultAndCurrentStatus()
    {
        var printer = new LocalPrinter
        {
            Name = "Zebra",
            IsDefault = true,
            IsAvailable = false,
            StatusCode = "offline",
            StatusText = "离线",
        };

        Assert.Equal("Zebra (Windows 默认) · 离线", printer.DisplayName);
    }

    [Fact]
    public void AvailablePrinterNamesExcludesOfflinePrinters()
    {
        LocalPrinter[] printers =
        [
            new() { Name = "Online", IsAvailable = true, StatusText = "在线" },
            new() { Name = "Offline", IsAvailable = false, StatusText = "离线" },
        ];

        IReadOnlySet<string> names = LocalPrinterService.AvailablePrinterNames(printers);

        Assert.Contains("Online", names);
        Assert.DoesNotContain("Offline", names);
    }
}
