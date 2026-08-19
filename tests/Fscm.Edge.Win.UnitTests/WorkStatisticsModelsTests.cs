// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class WorkStatisticsModelsTests
{
    [Fact]
    public void WorkJobQrCodePolicyUsesScannerCommandProtocol()
    {
        Assert.Equal("FSCM_JOB:PACKING", WorkJobQrCodePolicy.BuildStartPayload(" packing "));
        Assert.Equal("FSCM_JOB:END", WorkJobQrCodePolicy.EndPayload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("end")]
    public void WorkJobQrCodePolicyRejectsInvalidOperationCodes(string code)
    {
        Assert.Throws<ArgumentException>(() => WorkJobQrCodePolicy.BuildStartPayload(code));
    }

    [Fact]
    public void CenterPathsMatchEdgeScannerContract()
    {
        Assert.Equal("/api/edge/scanners/bindings/active", EdgeRuntimeManager.ActiveScannerBindingsPath);
        Assert.Equal("/api/edge/scanners/work-sessions", EdgeRuntimeManager.ScannerWorkSessionsPath);
        Assert.Equal("/api/edge/scanners/jobs", EdgeRuntimeManager.OrderScanJobsPath);
    }

    [Fact]
    public void WorkSessionDeserializesCenterContract()
    {
        const string json = """
            {"id":99,"binding_id":7,"user_id":42,"operation_type_code":"PACKING","operation_type_name":"打包","expires_at":"2026-08-14T08:30:00Z"}
            """;

        ActiveWorkSession? session = JsonSerializer.Deserialize<ActiveWorkSession>(json);

        Assert.NotNull(session);
        Assert.Equal(7U, session.BindingId);
        Assert.Equal("PACKING", session.OperationTypeCode);
        Assert.Equal("打包", session.OperationTypeName);
        Assert.Equal(42U, session.UserId);
    }

    [Fact]
    public void BindingUsesCenterUsernameAndDeviceNameForDisplay()
    {
        const string json = """
            {"id":7,"user_id":42,"username":"zhangsan","device_name":"包装台扫码枪","device_fingerprint":"HID-VID_1234-PID_5678-VERY-LONG-DEVICE-IDENTIFIER"}
            """;

        WorkScannerBinding? binding = JsonSerializer.Deserialize<WorkScannerBinding>(json);

        Assert.NotNull(binding);
        Assert.Equal("zhangsan", binding.UserDisplayName);
        Assert.Equal("包装台扫码枪", binding.DeviceDisplayName);
        Assert.Equal("HID-VID_1234...ENTIFIER", binding.ShortFingerprint);
    }

    [Fact]
    public void WorkSessionSupportsNestedCenterUserDisplayName()
    {
        const string json = """
            {"id":99,"binding_id":7,"user_id":42,"user":{"username":"lisi","display_name":"李四"},"operation_type_code":"PACKING","operation_type_name":"打包","expires_at":"2026-08-14T08:30:00Z"}
            """;

        ActiveWorkSession? session = JsonSerializer.Deserialize<ActiveWorkSession>(json);

        Assert.NotNull(session);
        Assert.Equal("lisi", session.UserDisplayName);
    }

    [Fact]
    public void DisplayValuesFallBackWhenCenterOmitsNames()
    {
        var binding = new WorkScannerBinding { Id = 7, UserId = 42, BindingCode = "USER_EMP-20", DeviceFingerprint = "short-id" };
        var session = new ActiveWorkSession { UserId = 42, ResolvedUserName = binding.UserDisplayName };
        var unresolvedSession = new ActiveWorkSession { UserId = 43 };

        Assert.Equal("EMP-20", binding.UserDisplayName);
        Assert.Equal("short-id", binding.DeviceDisplayName);
        Assert.Equal("EMP-20", session.UserDisplayName);
        Assert.Equal("用户 #43", unresolvedSession.UserDisplayName);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("sessions")]
    [InlineData("work_sessions")]
    [InlineData("tasks")]
    public void WorkStatisticsParserAcceptsCenterCollectionNames(string collectionName)
    {
        string json = $"{{\"data\":{{\"{collectionName}\":[{{\"id\":99,\"binding_id\":7,\"user_id\":42,\"operation_type_code\":\"PACKING\"}}]}}}}";
        using JsonDocument document = JsonDocument.Parse(json);

        List<ActiveWorkSession> sessions = EdgeRuntimeManager.DeserializeWorkStatisticsItems<ActiveWorkSession>(
            document.RootElement.GetProperty("data"),
            "items",
            "sessions",
            "work_sessions",
            "tasks");

        ActiveWorkSession session = Assert.Single(sessions);
        Assert.Equal(99U, session.Id);
        Assert.Equal(7U, session.BindingId);
    }

    [Fact]
    public void WorkStatisticsParserAcceptsDirectArray()
    {
        using JsonDocument document = JsonDocument.Parse("[{\"id\":99,\"binding_id\":7}]");

        List<ActiveWorkSession> sessions = EdgeRuntimeManager.DeserializeWorkStatisticsItems<ActiveWorkSession>(
            document.RootElement,
            "items");

        Assert.Equal(99U, Assert.Single(sessions).Id);
    }

    [Fact]
    public void OrderScanJobDeserializesFrontendContract()
    {
        const string json = """
            {"id":21,"job_no":"SCAN-20260814-21","source":"web","status":"open","actor_name":"zhangsan","operation_type_code":"PACKING","operation_type_name":"打包","device_name":"包装台扫码枪","started_at":"2026-08-14T08:00:00Z","last_scanned_at":"2026-08-14T08:20:00Z","accepted_count":12,"duplicate_count":2,"invalid_count":1,"voided_count":1}
            """;

        OrderScanJob? job = JsonSerializer.Deserialize<OrderScanJob>(json);

        Assert.NotNull(job);
        Assert.Equal("SCAN-20260814-21", job.JobNo);
        Assert.Equal("进行中", job.StatusDisplay);
        Assert.Equal("打包（PACKING）", job.OperationDisplay);
        Assert.Equal("包装台扫码枪", job.DeviceDisplay);
        Assert.Equal("12 / 2 / 1", job.ScanCountsDisplay);
        Assert.Equal(16, job.TotalScanCount);
        Assert.Equal(DateTimeOffset.Parse("2026-08-14T08:20:00Z"), job.LastActivityAt);
    }

    [Fact]
    public void WorkSessionResolvesDeviceAndUserFromBinding()
    {
        var binding = new WorkScannerBinding
        {
            Id = 7,
            UserId = 42,
            Username = "zhangsan",
            DeviceName = "包装台扫码枪",
            DeviceFingerprint = "HID-1234567890",
        };
        var session = new ActiveWorkSession
        {
            BindingId = 7,
            UserId = 42,
            OperationTypeCode = "PACKING",
            Status = "open",
        };

        EdgeRuntimeManager.ResolveWorkSessionBindings([binding], [session]);

        Assert.Equal("zhangsan", session.UserDisplayName);
        Assert.Equal("包装台扫码枪", session.DeviceDisplay);
        Assert.Equal("进行中", session.StatusDisplay);
    }

    [Fact]
    public void WorkSessionUsesExpirationWhenStatusIsMissing()
    {
        var session = new ActiveWorkSession { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

        Assert.Equal("已过期", session.StatusDisplay);
    }
}

