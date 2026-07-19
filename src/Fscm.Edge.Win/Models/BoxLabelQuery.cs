// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed class BoxLabelQuery
{
    public string Keyword { get; set; } = string.Empty;

    public uint? ProductId { get; set; }

    public uint? SkuId { get; set; }

    public uint? ConsolidationOrderId { get; set; }

    public string ConsolidationOrderCode { get; set; } = string.Empty;

    public string StatusGroup { get; set; } = string.Empty;

    public string ReceivingStatus { get; set; } = string.Empty;

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}
