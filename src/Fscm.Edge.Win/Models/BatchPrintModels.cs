// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Text.Json.Serialization;

namespace Fscm.Edge.Win.Models;

#pragma warning disable SA1402, SA1649

public sealed class BatchPrintItem
{
    [JsonPropertyName("sequence_no")]
    public int SequenceNo { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("copies")]
    public int Copies { get; set; } = 1;

    [JsonIgnore]
    public string Remark { get; set; } = string.Empty;

    [JsonIgnore]
    public string Source { get; set; } = string.Empty;

    [JsonIgnore]
    public string ValidationStatus { get; set; } = "有效";
}

public sealed class BatchPrintRangeSource
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;

    [JsonPropertyName("step")]
    public int Step { get; set; } = 1;
}

public sealed class BatchPrintSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "range";

    [JsonPropertyName("range")]
    public BatchPrintRangeSource? Range { get; set; }

    [JsonPropertyName("items")]
    public List<BatchPrintItem>? Items { get; set; }
}

public sealed class BatchPrintRequest
{
    [JsonPropertyName("edge_node_id")]
    public uint EdgeNodeId { get; set; }

    [JsonPropertyName("template_code")]
    public string TemplateCode { get; set; } = string.Empty;

    [JsonPropertyName("default_copies")]
    public int DefaultCopies { get; set; } = 1;

    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; } = 1;

    [JsonPropertyName("failure_policy")]
    public string FailurePolicy { get; set; } = "pause";

    [JsonPropertyName("source")]
    public BatchPrintSource Source { get; set; } = new();
}

public sealed class BatchPrintPreview
{
    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<BatchPrintItem> Items { get; set; } = [];

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

public sealed class CenterPrintBatch
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("edge_node_id")]
    public uint EdgeNodeId { get; set; }

    [JsonPropertyName("template_code")]
    public string TemplateCode { get; set; } = string.Empty;

    [JsonPropertyName("printer_name")]
    public string PrinterName { get; set; } = string.Empty;

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = string.Empty;

    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; }

    [JsonPropertyName("failure_policy")]
    public string FailurePolicy { get; set; } = string.Empty;

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("succeeded_count")]
    public int SucceededCount { get; set; }

    [JsonPropertyName("failed_count")]
    public int FailedCount { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonIgnore]
    public string ProgressText => $"{SucceededCount + FailedCount} / {TotalCount}";

    [JsonIgnore]
    public string FailurePolicyText => FailurePolicy == "continue" ? "失败后继续" : "失败后暂停";

    [JsonIgnore]
    public string StatusText => Status switch
    {
        "running" => "执行中",
        "paused" => "已暂停",
        "completed" => "已完成",
        "cancelled" => "已取消",
        _ => Status,
    };
}

public sealed class BatchPrintResult<T>
{
    public bool Succeeded { get; init; }

    public string Message { get; init; } = string.Empty;

    public T? Data { get; init; }
}

#pragma warning restore SA1402, SA1649
