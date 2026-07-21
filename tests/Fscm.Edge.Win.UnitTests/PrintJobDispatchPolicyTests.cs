// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class PrintJobDispatchPolicyTests
{
    [Theory]
    [InlineData("manual_text", true)]
    [InlineData("MANUAL_TEXT", true)]
    [InlineData("batch_content", true)]
    [InlineData("BATCH_CONTENT", true)]
    [InlineData("sku_qr", false)]
    [InlineData("manufacturer_box_mark", false)]
    [InlineData("", false)]
    public void IsTextLabelOnlyMatchesTextLabelKinds(string kind, bool expected)
    {
        Assert.Equal(expected, PrintJobDispatchPolicy.IsTextLabel(kind));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    public void IsCopiesAllowedEnforcesFiveCopyLimit(int copies, bool expected)
    {
        Assert.Equal(expected, PrintJobDispatchPolicy.IsCopiesAllowed(copies));
    }

    [Fact]
    public void EnsureContentCopiesAllowedRejectsSplitDuplicateContent()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PrintJobDispatchPolicy.EnsureContentCopiesAllowed([("A001", 3), (" A001 ", 3)]));

        Assert.Contains("A001", error.Message);
        Assert.Contains("5", error.Message);
    }

    [Fact]
    public void EnsureContentCopiesAllowedAllowsDistinctContentAtLimit()
    {
        PrintJobDispatchPolicy.EnsureContentCopiesAllowed([("A001", 5), ("A002", 5)]);
    }

    [Fact]
    public void SelectNextQueuedJobKeepsActiveBatchInSequence()
    {
        EdgePrintJob? selected = PrintJobDispatchPolicy.SelectNextQueuedJob(
        [
            new() { Id = "local", Status = "queued" },
            new() { Id = "batch-3", Status = "queued", RemoteBatchId = 7, RemoteSequenceNo = 3 },
            new() { Id = "batch-2", Status = "queued", RemoteBatchId = 7, RemoteSequenceNo = 2 },
            new() { Id = "other-batch", Status = "queued", RemoteBatchId = 8, RemoteSequenceNo = 1 },
        ],
        7);

        Assert.Equal("batch-2", selected?.Id);
    }

    [Fact]
    public void SelectNextQueuedJobStartsBatchBeforeOrdinaryWork()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        EdgePrintJob? selected = PrintJobDispatchPolicy.SelectNextQueuedJob(
        [
            new() { Id = "local", Status = "queued", SubmittedAt = now.AddSeconds(-10) },
            new() { Id = "newer-batch", Status = "queued", RemoteBatchId = 8, RemoteSequenceNo = 1, SubmittedAt = now },
            new() { Id = "older-batch", Status = "queued", RemoteBatchId = 7, RemoteSequenceNo = 1, SubmittedAt = now.AddSeconds(-1) },
        ],
        null);

        Assert.Equal("older-batch", selected?.Id);
    }
}
