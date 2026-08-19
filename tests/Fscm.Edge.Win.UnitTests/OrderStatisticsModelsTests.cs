// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class OrderStatisticsModelsTests
{
    [Fact]
    public void QueryUsesCenterMultiSelectContract()
    {
        var query = new OrderStatisticsQuery
        {
            From = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.FromHours(8)),
            To = new DateTimeOffset(2026, 8, 15, 23, 59, 59, TimeSpan.FromHours(8)),
            UserIds = [9, 2, 9],
            OperationTypeCodes = [" packing ", "PICKING", "packing"],
            Page = 2,
            PageSize = 500,
            Status = "accepted",
        };

        string result = query.ToQueryString();

        Assert.Contains("user_ids=2%2C9", result, StringComparison.Ordinal);
        Assert.Contains("operation_type_codes=PACKING%2CPICKING", result, StringComparison.Ordinal);
        Assert.Contains("page=2", result, StringComparison.Ordinal);
        Assert.Contains("page_size=100", result, StringComparison.Ordinal);
        Assert.Contains("status=accepted", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardDeserializesFscmContractAndFormatsValues()
    {
        const string json = """
            {"accepted_count":12,"marked_error_count":3,"total_scan_count":16,"average_daily_count":6,"error_rate":0.25,"operations":[{"operation_type_code":"PACKING","operation_type_name":"打包","accepted_count":12,"marked_error_count":3}],"groups":[{"user_id":7,"actor_name":"张三","operation_type_code":"PACKING","operation_type_name":"打包","accepted_count":12,"marked_error_count":3}]}
            """;

        OrderStatisticsDashboard? dashboard = JsonSerializer.Deserialize<OrderStatisticsDashboard>(json);

        Assert.NotNull(dashboard);
        Assert.Equal(12, dashboard.AcceptedCount);
        Assert.Equal("25.00%", dashboard.ErrorRateDisplay);
        OrderStatisticsOperationSummary operation = Assert.Single(dashboard.Operations);
        Assert.Equal("打包（PACKING）", operation.OperationDisplay);
        Assert.Equal("25.00%", operation.ErrorRateDisplay);
        Assert.Equal("张三", Assert.Single(dashboard.Groups).UserDisplay);
    }

    [Fact]
    public void RecordUsesStableFallbacksForCenterValues()
    {
        const string json = """
            {"id":1,"actor_user_id":8,"operation_type_code":"PICKING","client_scanned_at":"2026-08-15T02:30:00Z","tracking_no":"SPX123","status":"duplicate","is_marked_error":true,"marked_error_reason":"重复处理"}
            """;

        OrderStatisticsRecord? record = JsonSerializer.Deserialize<OrderStatisticsRecord>(json);

        Assert.NotNull(record);
        Assert.Equal("用户 #8", record.UserDisplay);
        Assert.Equal("PICKING", record.OperationDisplay);
        Assert.Equal("运单：SPX123", record.RecognizedCodeDisplay);
        Assert.Equal("重复", record.StatusDisplay);
        Assert.Equal("已标错：重复处理", record.QualityDisplay);
    }

    [Fact]
    public void ZeroAcceptedCountDoesNotProduceErrorRate()
    {
        var operation = new OrderStatisticsOperationSummary { MarkedErrorCount = 1, AcceptedCount = 0 };
        var dashboard = new OrderStatisticsDashboard { ErrorRate = 0 };

        Assert.Equal("-", operation.ErrorRateDisplay);
        Assert.Equal("-", dashboard.ErrorRateDisplay);
    }

    [Fact]
    public void RecordPageParserAcceptsEnvelopeAliasesAndStringPagination()
    {
        const string json = """
            {"code":"0","data":{"records":[{"id":4,"actor_user_id":8,"status":"accepted"}],"total_count":"41","current_page":"2","per_page":"10"}}
            """;

        using JsonDocument document = JsonDocument.Parse(json);
        OrderStatisticsRecordPage page = OrderStatisticsCenterClient.OrderStatisticsResponseParser.DeserializeRecordPage(document.RootElement);

        Assert.Equal(41, page.Total);
        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(4U, Assert.Single(page.Items).Id);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"data\":{\"items\":[]}}")]
    public void ListParserReturnsEmptyForEmptyCenterCollections(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        List<OrderStatisticsUserOption> users = OrderStatisticsCenterClient.OrderStatisticsResponseParser.DeserializeItems<OrderStatisticsUserOption>(
            document.RootElement,
            "items",
            "users");

        Assert.Empty(users);
    }

    [Theory]
    [InlineData("{\"code\":1}", true)]
    [InlineData("{\"code\":\"ERR\"}", true)]
    [InlineData("{\"code\":\"0\"}", false)]
    [InlineData("{\"success\":false}", true)]
    public void ResponseParserRecognizesCenterFailureShapes(string json, bool expectedFailure)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expectedFailure, OrderStatisticsCenterClient.OrderStatisticsResponseParser.IsFailure(document.RootElement));
    }
}

