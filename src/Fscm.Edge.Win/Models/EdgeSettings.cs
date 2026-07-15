// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Models;

public sealed class EdgeSettings
{
    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = Environment.MachineName;

    public string CenterUrl { get; set; } = string.Empty;

    public string ApiToken { get; set; } = string.Empty;

    public string AdminToken { get; set; } = string.Empty;

    public uint NamespaceId { get; set; }

    public string LanBaseUrl { get; set; } = string.Empty;

    public List<string> Capabilities { get; set; } = ["proxy", "adaptive_cache", "catalog_cache", "local_print"];

    public string CacheMode { get; set; } = "standard";

    public int CacheMaxEntries { get; set; } = 5000;

    public int CacheMaxMemoryMegabytes { get; set; } = 256;

    public int CacheMaxObjectMegabytes { get; set; } = 5;

    public bool CacheStaleIfError { get; set; } = true;

    public int CacheMaxStaleHours { get; set; } = 24;

    public string DefaultPrinter { get; set; } = string.Empty;

    public string PrintTemplate { get; set; } = "label_60x40mm";

    public string SkuQrPrefix { get; set; } = "T";

    public double PrintWidthMillimeters { get; set; } = 60;

    public double PrintHeightMillimeters { get; set; } = 40;

    public string PrintOrientation { get; set; } = "portrait";

    public double PrintOffsetXMillimeters { get; set; }

    public string PrintMode { get; set; } = "fit";

    public int PrintCopies { get; set; } = 1;

    public int PrintPollIntervalSeconds { get; set; } = 5;
}
