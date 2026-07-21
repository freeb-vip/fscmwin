// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

public sealed class EdgeRuntimeManager : IDisposable
{
    private const int PrintTemplatesSchemaVersion = 13;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly HttpClient _batchHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private Process? _process;

    public string LastCenterQueryMessage { get; private set; } = string.Empty;

    public string RuntimeDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "EdgeRuntime");

    public string BinaryPath => Path.Combine(RuntimeDirectory, "fscm-edge.exe");

    public string ConfigPath => Path.Combine(RuntimeDirectory, "edge.config.yaml");

    public string ManifestPath => Path.Combine(RuntimeDirectory, "edge-runtime-manifest.json");

    public string PrintTemplatesPath => Path.Combine(RuntimeDirectory, "print-templates.json");

    private string PrintTemplatesVersionPath => Path.Combine(RuntimeDirectory, "data", "print-templates.version");

    public string LogDirectory => Path.Combine(RuntimeDirectory, "logs");

    public string StdoutLogPath => Path.Combine(LogDirectory, "edge.stdout.log");

    public string StderrLogPath => Path.Combine(LogDirectory, "edge.stderr.log");

    public EdgeRuntimeManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        var json = File.ReadAllText(ManifestPath);
        return JsonSerializer.Deserialize<EdgeRuntimeManifest>(json, JsonOptions);
    }

    public IReadOnlyList<PrintTemplateProfile> LoadPrintTemplates()
    {
        EnsureDefaultConfig();
        if (!File.Exists(PrintTemplatesPath))
        {
            var defaults = DefaultPrintTemplates();
            SavePrintTemplates(defaults);
            return defaults;
        }

        try
        {
            var templates = JsonSerializer.Deserialize<List<PrintTemplateProfile>>(File.ReadAllText(PrintTemplatesPath), JsonOptions)
                ?? [];
            int sourceVersion = ReadPrintTemplatesSchemaVersion();
            if (PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion))
            {
                SavePrintTemplates(templates);
            }
            else if (sourceVersion < PrintTemplatesSchemaVersion)
            {
                WritePrintTemplatesSchemaVersion();
            }

            return templates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void SavePrintTemplates(IEnumerable<PrintTemplateProfile> templates)
    {
        EnsureDefaultConfig();
        var content = JsonSerializer.Serialize(templates, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        File.WriteAllText(PrintTemplatesPath, content);
        WritePrintTemplatesSchemaVersion();
    }

    private static IReadOnlyList<PrintTemplateProfile> DefaultPrintTemplates()
    {
        return PrintTemplatePolicy.CreateDefaultTemplates();
    }

    private int ReadPrintTemplatesSchemaVersion()
    {
        return File.Exists(PrintTemplatesVersionPath) &&
            int.TryParse(File.ReadAllText(PrintTemplatesVersionPath), out int version)
            ? version
            : 1;
    }

    private void WritePrintTemplatesSchemaVersion()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PrintTemplatesVersionPath)!);
        File.WriteAllText(PrintTemplatesVersionPath, PrintTemplatesSchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<EdgeRuntimeStatus> GetStatusAsync()
    {
        var port = ReadConfiguredPort();
        var healthy = await CheckHealthAsync(port).ConfigureAwait(false);
        return new EdgeRuntimeStatus
        {
            BinaryExists = File.Exists(BinaryPath),
            ConfigExists = File.Exists(ConfigPath),
            IsRunning = _process is { HasExited: false } || healthy,
            IsHealthy = healthy,
            Port = port,
            ProcessId = _process is { HasExited: false } ? _process.Id : null,
            Message = healthy ? "Edge service is healthy." : "Edge service is not ready.",
        };
    }

    public async Task<EdgeRuntimeStatus> StartAsync()
    {
        EnsureDefaultConfig();

        if (!File.Exists(BinaryPath))
        {
            return new EdgeRuntimeStatus
            {
                BinaryExists = false,
                ConfigExists = File.Exists(ConfigPath),
                Port = ReadConfiguredPort(),
                Message = "fscm-edge.exe is missing. Run scripts/build-edge-backend.ps1 first.",
            };
        }

        if (_process is { HasExited: false })
        {
            return await GetStatusAsync().ConfigureAwait(false);
        }

        var port = ReadConfiguredPort();
        if (!IsPortAvailable(port) && !await CheckHealthAsync(port).ConfigureAwait(false))
        {
            return new EdgeRuntimeStatus
            {
                BinaryExists = true,
                ConfigExists = true,
                Port = port,
                Message = $"Port {port} is already in use.",
            };
        }

        Directory.CreateDirectory(LogDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = BinaryPath,
            Arguments = "--mode=edge --config=edge.config.yaml",
            WorkingDirectory = RuntimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process?.Dispose();
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => AppendLine(StdoutLogPath, args.Data);
        _process.ErrorDataReceived += (_, args) => AppendLine(StderrLogPath, args.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await CheckHealthAsync(port).ConfigureAwait(false))
            {
                break;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return await GetStatusAsync().ConfigureAwait(false);
    }

    public async Task<EdgeRuntimeStatus> StopAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        return await GetStatusAsync().ConfigureAwait(false);
    }

    public async Task<EdgeRuntimeStatus> RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        return await StartAsync().ConfigureAwait(false);
    }

    public async Task<EdgeCenterRegistrationResult> RegisterWithCenterAsync()
    {
        var settings = LoadEdgeSettings();
        if (string.IsNullOrWhiteSpace(settings.CenterUrl))
        {
            return new EdgeCenterRegistrationResult
            {
                Attempted = false,
                Message = "Remote center URL is not configured.",
            };
        }

        if (string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            return new EdgeCenterRegistrationResult
            {
                Attempted = false,
                Message = "Remote center API token is not configured.",
            };
        }

        var nodeName = string.IsNullOrWhiteSpace(settings.NodeName)
            ? Environment.MachineName
            : settings.NodeName.Trim();
        var nodeId = string.IsNullOrWhiteSpace(settings.NodeId)
            ? Environment.MachineName
            : settings.NodeId.Trim();
        if (!string.Equals(settings.NodeName, nodeName, StringComparison.Ordinal) || !string.Equals(settings.NodeId, nodeId, StringComparison.Ordinal))
        {
            settings.NodeName = nodeName;
            settings.NodeId = nodeId;
            SaveEdgeSettings(settings);
        }

        var manifest = LoadManifest();
        var port = ReadConfiguredPort();
        var templates = LoadPrintTemplates();
        var printers = templates
            .Where(template => !string.IsNullOrWhiteSpace(template.Printer))
            .Select(template => template.Printer.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(printer => new { name = printer, is_default = string.Equals(printer, settings.DefaultPrinter, StringComparison.OrdinalIgnoreCase), status = "ready" })
            .ToList();
        var inventory = new
        {
            printers,
            templates = PrintTemplatePolicy.OrderTemplates(templates)
                .Select(template => new
            {
                code = template.Id,
                number = template.TemplateNumber,
                name = template.Name,
                sort_order = template.SortOrder,
                type = template.Type,
                printer_name = template.Printer,
                width_mm = template.WidthMillimeters,
                height_mm = template.HeightMillimeters,
                orientation = template.Orientation,
                layout_style = PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle),
                text_font_size_pt = PrintTemplatePolicy.GetTextFontSizePoints(template),
                max_display_length = PrintTemplatePolicy.GetMaxDisplayLength(template),
                label_qr_prefix = template.LabelQrPrefix,
                version = PrintTemplatePolicy.GetTemplateVersion(template),
                available = !string.IsNullOrWhiteSpace(template.Printer),
            }).ToList(),
        };
        var lanBaseUrl = ResolvePublishableLanBaseUrl(settings, port);
        if (string.IsNullOrWhiteSpace(lanBaseUrl))
        {
            return new EdgeCenterRegistrationResult
            {
                Attempted = false,
                Message = "No valid LAN IPv4 address was found. Connect to the LAN or set edge.lan_base_url in Advanced Settings before registering.",
            };
        }

        var payload = new
        {
            node_id = nodeId,
            node_name = nodeName,
            lan_base_url = lanBaseUrl,
            backend_version = manifest?.EdgeVersion ?? "dev",
            backend_commit = manifest?.EdgeCommit ?? "unknown",
            edge_api_version = manifest?.EdgeApiVersion ?? "edge.v1",
            schema_version = 1,
            capabilities = settings.Capabilities,
            namespace_id = settings.NamespaceId,
            cache_mode = settings.CacheMode,
            inventory,
        };

        try
        {
            var endpoint = $"{settings.CenterUrl.TrimEnd('/')}/api/edge/nodes/register";
            using var request = CreateAuthorizedRequest(HttpMethod.Post, endpoint, settings);
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new EdgeCenterRegistrationResult
                {
                    Attempted = true,
                    Succeeded = true,
                    Message = "Registered with remote center.",
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (errorBody.Length > 300)
            {
                errorBody = errorBody[..300];
            }

            return new EdgeCenterRegistrationResult
            {
                Attempted = true,
                Message = string.IsNullOrWhiteSpace(errorBody)
                    ? $"Remote center rejected registration: {(int)response.StatusCode} {response.ReasonPhrase}"
                    : $"Remote center rejected registration: {(int)response.StatusCode} {response.ReasonPhrase}. {errorBody}",
            };
        }
        catch (Exception ex)
        {
            return new EdgeCenterRegistrationResult
            {
                Attempted = true,
                Message = $"Remote center registration failed: {ex.Message}",
            };
        }
    }

    public async Task<RemoteCenterStatus> CheckRemoteCenterAsync()
    {
        var settings = LoadEdgeSettings();
        if (string.IsNullOrWhiteSpace(settings.CenterUrl))
        {
            return new RemoteCenterStatus
            {
                IsConfigured = false,
                Message = "Remote center URL is not configured.",
            };
        }

        if (string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            return new RemoteCenterStatus
            {
                CenterUrl = settings.CenterUrl.TrimEnd('/'),
                IsConfigured = true,
                Message = "Remote center API token is not configured.",
            };
        }

        var baseUrl = settings.CenterUrl.TrimEnd('/');
        var edgeTestEndpoint = $"{baseUrl}/api/edge/nodes/test";
        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, edgeTestEndpoint, settings);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new RemoteCenterStatus
                {
                    CenterUrl = baseUrl,
                    IsConfigured = true,
                    IsReachable = true,
                    Message = $"Remote edge node API is reachable via {edgeTestEndpoint}.",
                };
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new RemoteCenterStatus
                {
                    CenterUrl = baseUrl,
                    IsConfigured = true,
                    Message = $"Remote center rejected the API token: {(int)response.StatusCode} {response.ReasonPhrase}.",
                };
            }

            var detail = await BuildAuthDiagnosticAsync(baseUrl, settings).ConfigureAwait(false);
            return new RemoteCenterStatus
            {
                CenterUrl = baseUrl,
                IsConfigured = true,
                Message = $"Remote edge node API check failed: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}",
            };
        }
        catch (Exception ex)
        {
            var detail = await BuildAuthDiagnosticAsync(baseUrl, settings).ConfigureAwait(false);
            return new RemoteCenterStatus
            {
                CenterUrl = baseUrl,
                IsConfigured = true,
                Message = $"Remote edge node API check failed: {ex.Message}. {detail}",
            };
        }
    }

    private async Task<string> BuildAuthDiagnosticAsync(string baseUrl, EdgeSettings settings)
    {
        try
        {
            var endpoint = $"{baseUrl}/api/auth/me";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, endpoint, settings);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return "API token is valid, but /api/edge/nodes/test is not available on this backend.";
            }

            return $"API token diagnostic returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
        catch (Exception ex)
        {
            return $"API token diagnostic failed: {ex.Message}.";
        }
    }

    public async Task<IReadOnlyList<EdgeTerminal>> GetTerminalsAsync()
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Get, "/edge/terminals");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<EdgeTerminalListResponse>(JsonOptions).ConfigureAwait(false);
            return payload?.Terminals ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<EdgePrintJob>> GetPrintJobsAsync()
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Get, "/edge/print-jobs");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<EdgePrintJobListResponse>(JsonOptions).ConfigureAwait(false);
            return payload?.Jobs ?? [];
        }
        catch
        {
            return [];
        }
    }

    public Task<EdgeTerminalCommandResult> FindTerminalAsync(string terminalId)
    {
        return SendTerminalCommandAsync(terminalId, stop: false);
    }

    public Task<EdgeTerminalCommandResult> StopFindingTerminalAsync(string terminalId)
    {
        return SendTerminalCommandAsync(terminalId, stop: true);
    }

    private async Task<EdgeTerminalCommandResult> SendTerminalCommandAsync(string terminalId, bool stop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        var suffix = stop ? "/find/stop" : "/find";
        var path = $"/edge/terminals/{Uri.EscapeDataString(terminalId)}{suffix}";
        using var request = CreateLocalAdminRequest(HttpMethod.Post, path);
        using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<EdgeTerminalCommandResult>(JsonOptions).ConfigureAwait(false)
            ?? new EdgeTerminalCommandResult();
        if (!response.IsSuccessStatusCode)
        {
            var detail = string.IsNullOrWhiteSpace(payload.Message) ? payload.Code : payload.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"Terminal command failed with HTTP {(int)response.StatusCode}."
                : detail);
        }

        return payload;
    }

    // The edge process owns registration and heartbeat. The desktop app only
    // observes that state so it cannot overwrite the published LAN address.
    public async Task<EdgeCenterRegistrationResult> GetRuntimeRegistrationAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync(LocalEndpoint("/edge/health")).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new EdgeCenterRegistrationResult
                {
                    Attempted = false,
                    Message = "The local edge service is not ready.",
                };
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var registration = document.RootElement.TryGetProperty("registration", out var value) ? value : default;
            var registered = registration.ValueKind == JsonValueKind.Object
                && registration.TryGetProperty("registered", out var registeredValue)
                && registeredValue.ValueKind == JsonValueKind.True;
            var lastError = registration.ValueKind == JsonValueKind.Object
                && registration.TryGetProperty("last_error", out var errorValue)
                ? errorValue.GetString()
                : null;

            return new EdgeCenterRegistrationResult
            {
                Attempted = true,
                Succeeded = registered,
                Message = registered
                    ? "Registered with remote center by the local edge service."
                    : string.IsNullOrWhiteSpace(lastError)
                        ? "Waiting for the local edge service to register with the remote center."
                        : $"Local edge registration failed: {lastError}",
            };
        }
        catch (Exception ex)
        {
            return new EdgeCenterRegistrationResult
            {
                Attempted = false,
                Message = $"Unable to read local edge registration status: {ex.Message}",
            };
        }
    }

    public async Task<bool> SyncPrintInventoryAsync(IEnumerable<string> printers)
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Put, "/edge/print-inventory");
            request.Content = JsonContent.Create(new { printers = printers.Where(static name => !string.IsNullOrWhiteSpace(name)).ToArray() });
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PullRemotePrintJobAsync(uint? batchId = null)
    {
        try
        {
            string path = batchId is > 0 ? $"/edge/print-jobs/pull?batch_id={batchId}" : "/edge/print-jobs/pull";
            using var request = CreateLocalAdminRequest(HttpMethod.Post, path);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>(JsonOptions).ConfigureAwait(false);
            return payload?.TryGetValue("claimed", out bool claimed) == true && claimed;
        }
        catch
        {
            return false;
        }
    }

    public async Task<EdgePrintJob?> UpdatePrintJobStatusAsync(string jobId, string status, string? error = null)
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Post, $"/edge/print-jobs/{Uri.EscapeDataString(jobId)}/status");
            request.Content = JsonContent.Create(new { status, error = error ?? string.Empty });
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<EdgePrintJobResponse>(JsonOptions).ConfigureAwait(false);
            return payload?.Job;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProductSummary>> GetProductsAsync(string keyword)
    {
        return (await GetProductsPageAsync(keyword, 1, 100).ConfigureAwait(false)).Items;
    }

    public async Task<BatchPrintResult<IReadOnlyList<EdgePrintJob>>> StopLocalBatchPrintAsync(uint batchId)
    {
        if (batchId == 0)
        {
            return new BatchPrintResult<IReadOnlyList<EdgePrintJob>> { Message = "批次 ID 无效。" };
        }

        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Post, $"/edge/print-batches/{batchId}/stop");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new BatchPrintResult<IReadOnlyList<EdgePrintJob>>
                {
                    Message = $"本地边缘服务停止批次失败：{(int)response.StatusCode} {response.ReasonPhrase}",
                };
            }
            EdgePrintJobListResponse? payload = await response.Content.ReadFromJsonAsync<EdgePrintJobListResponse>(JsonOptions).ConfigureAwait(false);
            return new BatchPrintResult<IReadOnlyList<EdgePrintJob>> { Succeeded = true, Data = payload?.Jobs ?? [] };
        }
        catch (Exception ex)
        {
            return new BatchPrintResult<IReadOnlyList<EdgePrintJob>> { Message = $"本地边缘服务停止批次异常：{ex.Message}" };
        }
    }

    public async Task<IReadOnlyList<SkuSummary>> GetSkusAsync(string keyword, uint? productId = null)
    {
        return (await GetSkusPageAsync(keyword, 1, 100, productId).ConfigureAwait(false)).Items;
    }

    public async Task<CatalogSearchResult<ProductSummary>> GetProductsPageAsync(string keyword, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var local = await GetLocalCatalogPageAsync<ProductSummary>("/edge/catalog/products/search", keyword, page, pageSize).ConfigureAwait(false);
        if (local.Succeeded && local.Result.Items.Count > 0)
        {
            return local.Result;
        }

        var center = await GetCenterCatalogPageAsync<ProductSummary>("/api/products", keyword, page, pageSize, "&match_mode=prefix").ConfigureAwait(false);
        if (center.Succeeded)
        {
            var cached = await CacheCatalogItemsAsync("/edge/catalog/products/cache", center.Result.Items).ConfigureAwait(false);
            return WithCatalogMessage(center.Result, cached);
        }

        return CatalogFallback(local, "本地和中心均未返回产品数据。");
    }

    public async Task<CatalogSearchResult<SkuSummary>> GetSkusPageAsync(string keyword, int page, int pageSize, uint? productId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var localPath = productId is null
            ? "/edge/catalog/skus/search"
            : $"/edge/catalog/skus/search?product_id={productId.Value}";
        var local = await GetLocalCatalogPageAsync<SkuSummary>(localPath, keyword, page, pageSize).ConfigureAwait(false);
        if (local.Succeeded && local.Result.Items.Count > 0)
        {
            return local.Result;
        }

        var suffix = productId is null ? "&match_mode=prefix" : $"&product_id={productId.Value}&match_mode=prefix";
        var center = await GetCenterCatalogPageAsync<SkuSummary>("/api/skus", keyword, page, pageSize, suffix).ConfigureAwait(false);
        if (center.Succeeded)
        {
            var cached = await CacheCatalogItemsAsync("/edge/catalog/skus/cache", center.Result.Items).ConfigureAwait(false);
            return WithCatalogMessage(center.Result, cached);
        }

        return CatalogFallback(local, "本地和中心均未返回 SKU 数据。");
    }

    public async Task<BoxLabelSearchResult> GetBoxLabelsAsync(BoxLabelQuery query)
    {
        var local = await GetLocalBoxLabelsAsync(query).ConfigureAwait(false);
        if (local.Succeeded && local.Result.Items.Count > 0)
        {
            return local.Result;
        }

        var center = await GetCenterBoxLabelsAsync(query).ConfigureAwait(false);
        if (center.Succeeded)
        {
            var cached = await CacheBoxLabelsAsync(center.Result.Items).ConfigureAwait(false);
            return new BoxLabelSearchResult
            {
                Items = center.Result.Items,
                Total = center.Result.Total,
                Source = "center",
                Message = cached ? "本地无匹配数据，已从中心查询并同步到本地。" : "本地无匹配数据，当前显示中心结果；本地目录尚未就绪。",
            };
        }

        return new BoxLabelSearchResult
        {
            Items = local.Result.Items,
            Total = local.Result.Total,
            Source = local.Succeeded ? "local" : string.Empty,
            Message = string.IsNullOrWhiteSpace(LastCenterQueryMessage) ? "本地和中心均未返回箱唛数据。" : LastCenterQueryMessage,
        };
    }

    private async Task<(BoxLabelSearchResult Result, bool Succeeded)> GetLocalBoxLabelsAsync(BoxLabelQuery query)
    {
        try
        {
            var values = BuildBoxLabelQueryValues(query);
            using var request = CreateLocalAdminRequest(HttpMethod.Get, "/edge/catalog/box-labels/search?" + string.Join("&", values));
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (new BoxLabelSearchResult(), false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = document.RootElement;
#pragma warning disable IDISP004
            var items = root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Select(item => item.Deserialize<BoxLabelSummary>(JsonOptions)).Where(item => item is not null).Select(item => item!).ToList()
                : [];
#pragma warning restore IDISP004
            var total = root.TryGetProperty("total", out var totalValue) ? totalValue.GetInt64() : items.Count;
            return (new BoxLabelSearchResult { Items = items, Total = total, Source = "local" }, true);
        }
        catch
        {
            return (new BoxLabelSearchResult(), false);
        }
    }

    public async Task<BoxLabelSummary?> GetBoxLabelAsync(uint id)
    {
        var local = await GetLocalBoxLabelAsync(id).ConfigureAwait(false);
        if (local is not null)
        {
            return local;
        }

        var center = await GetCenterBoxLabelAsync(id).ConfigureAwait(false);
        if (center is not null)
        {
            await CacheBoxLabelsAsync([center]).ConfigureAwait(false);
        }

        return center;
    }

    private async Task<BoxLabelSummary?> GetLocalBoxLabelAsync(uint id)
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Get, $"/edge/catalog/box-labels/{id}");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return document.RootElement.TryGetProperty("item", out var item) ? item.Deserialize<BoxLabelSummary>(JsonOptions) : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(BoxLabelSearchResult Result, bool Succeeded)> GetCenterBoxLabelsAsync(BoxLabelQuery query)
    {
        LastCenterQueryMessage = string.Empty;
        try
        {
            var settings = LoadEdgeSettings();
            if (settings.NamespaceId == 0)
            {
                var detectedNamespaceId = await DetectNamespaceIdAsync(settings).ConfigureAwait(false);
                if (detectedNamespaceId > 0)
                {
                    settings.NamespaceId = detectedNamespaceId;
                    SaveEdgeSettings(settings);
                }
            }

            using var request = CreateLocalAuthorizedRequest(HttpMethod.Get, "/api/box-labels?" + string.Join("&", BuildBoxLabelQueryValues(query)), settings);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastCenterQueryMessage = $"中心箱唛查询失败：{(int)response.StatusCode} {response.ReasonPhrase}。{ExtractErrorMessage(responseBody)}";
                return (new BoxLabelSearchResult(), false);
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var payload = root.TryGetProperty("data", out var envelopeData) && envelopeData.ValueKind == JsonValueKind.Object
                ? envelopeData
                : root;
            if (!payload.TryGetProperty("data", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                LastCenterQueryMessage = "中心箱唛响应不包含分页列表。";
                return (new BoxLabelSearchResult(), false);
            }

#pragma warning disable IDISP004
            var items = array.EnumerateArray().Select(item => item.Deserialize<BoxLabelSummary>(JsonOptions)).Where(item => item is not null).Select(item => item!).ToList();
#pragma warning restore IDISP004
            var total = payload.TryGetProperty("total", out var totalValue) ? totalValue.GetInt64() : items.Count;
            return (new BoxLabelSearchResult { Items = items, Total = total, Source = "center" }, true);
        }
        catch (Exception ex)
        {
            LastCenterQueryMessage = $"中心箱唛查询异常：{ex.Message}";
            return (new BoxLabelSearchResult(), false);
        }
    }

    private async Task<BoxLabelSummary?> GetCenterBoxLabelAsync(uint id)
    {
        try
        {
            var settings = LoadEdgeSettings();
            using var request = CreateLocalAuthorizedRequest(HttpMethod.Get, $"/api/box-labels/{id}", settings);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return document.RootElement.TryGetProperty("data", out var item) && item.ValueKind == JsonValueKind.Object
                ? item.Deserialize<BoxLabelSummary>(JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> CacheBoxLabelsAsync(IReadOnlyList<BoxLabelSummary> items)
    {
        if (items.Count == 0)
        {
            return true;
        }

        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Post, "/edge/catalog/box-labels/cache");
            request.Content = JsonContent.Create(new { items });
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> BuildBoxLabelQueryValues(BoxLabelQuery query)
    {
        var values = new List<string>
        {
            $"keyword={Uri.EscapeDataString(query.Keyword.Trim())}",
            $"page={Math.Max(1, query.Page)}",
            $"page_size={Math.Clamp(query.PageSize, 1, 100)}",
        };
        AddQueryValue(values, "product_id", query.ProductId);
        AddQueryValue(values, "sku_id", query.SkuId);
        AddQueryValue(values, "consolidation_order_id", query.ConsolidationOrderId);
        AddQueryValue(values, "consolidation_order_code", query.ConsolidationOrderCode);
        AddQueryValue(values, "status_group", query.StatusGroup);
        AddQueryValue(values, "receiving_status", query.ReceivingStatus);
        return values;
    }

    public Task<IReadOnlyList<ConsolidationOrderSummary>> GetConsolidationOrdersAsync(string keyword)
    {
        return GetCenterListAsync<ConsolidationOrderSummary>("/api/consolidation-orders", keyword, "&page_size=20");
    }

    public async Task<bool> SubmitBoxMarkPrintJobAsync(PrintTemplateProfile template, IReadOnlyList<BoxLabelSummary> labels, int copies)
    {
        if (!PrintJobDispatchPolicy.IsCopiesAllowed(copies))
        {
            return false;
        }
        List<ManufacturerBoxMark> marks;
        try
        {
            marks = ManufacturerBoxMarkPrintService.PrepareMarksForTemplate(template, labels).ToList();
        }
        catch
        {
            return false;
        }
        if (marks.Count == 0 || marks.Count > 100)
        {
            return false;
        }

        try
        {
            var idempotencyKey = $"box-mark-{Guid.NewGuid():N}";
            var payload = new
            {
                template_id = template.Id,
                source = "windows-box-label-management",
                job_id = idempotencyKey,
                idempotency_key = idempotencyKey,
                kind = "manufacturer_box_mark",
                copies,
                box_marks = marks,
                payload_snapshot = new
                {
                    document_version = PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle) == PrintTemplatePolicy.BoxMarkQuadLayoutStyle
                        ? "manufacturer_box_mark.v2"
                        : "manufacturer_box_mark.v1",
                    items = marks,
                },
            };
            using var request = CreateLocalAdminRequest(HttpMethod.Post, "/edge/print-jobs");
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void AddQueryValue(List<string> values, string key, uint? value)
    {
        if (value is > 0)
        {
            values.Add($"{key}={value.Value}");
        }
    }

    private static void AddQueryValue(List<string> values, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    public async Task<EdgeCatalogStatus?> GetCatalogStatusAsync()
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Get, "/edge/catalog/status");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<EdgeCatalogStatusResponse>(JsonOptions).ConfigureAwait(false))?.Catalog;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> RefreshCatalogAsync()
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Post, "/edge/catalog/refresh");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(CatalogSearchResult<T> Result, bool Succeeded)> GetLocalCatalogPageAsync<T>(string path, string keyword, int page, int pageSize)
    {
        LastCenterQueryMessage = string.Empty;
        try
        {
            var separator = path.Contains('?') ? "&" : "?";
            var query = $"keyword={Uri.EscapeDataString(keyword.Trim())}&page={page}&page_size={pageSize}";
            using var request = CreateLocalAdminRequest(HttpMethod.Get, path + separator + query);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (new CatalogSearchResult<T> { Page = page, PageSize = pageSize }, false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = document.RootElement;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return (new CatalogSearchResult<T> { Page = page, PageSize = pageSize, Source = "local" }, true);
            }

#pragma warning disable IDISP004
            var result = DeserializeCatalogItems<T>(items);
#pragma warning restore IDISP004
            var total = root.TryGetProperty("total", out var totalValue) && totalValue.TryGetInt64(out var parsedTotal) ? parsedTotal : result.Count;
            return (new CatalogSearchResult<T> { Items = result, Total = total, Page = page, PageSize = pageSize, Source = "local" }, true);
        }
        catch
        {
            return (new CatalogSearchResult<T> { Page = page, PageSize = pageSize }, false);
        }
    }

    private async Task<(CatalogSearchResult<T> Result, bool Succeeded)> GetCenterCatalogPageAsync<T>(string path, string keyword, int page, int pageSize, string suffix)
    {
        LastCenterQueryMessage = string.Empty;
        try
        {
            var settings = LoadEdgeSettings();
            if (settings.NamespaceId == 0)
            {
                var detectedNamespaceId = await DetectNamespaceIdAsync(settings).ConfigureAwait(false);
                if (detectedNamespaceId > 0)
                {
                    settings.NamespaceId = detectedNamespaceId;
                    SaveEdgeSettings(settings);
                }
            }

            var query = $"?keyword={Uri.EscapeDataString(keyword.Trim())}&page={page}&pageSize={pageSize}{suffix}";
            using var request = CreateLocalAuthorizedRequest(HttpMethod.Get, path + query, settings);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastCenterQueryMessage = $"中心查询失败：{(int)response.StatusCode} {response.ReasonPhrase}。{ExtractErrorMessage(responseBody)}";
                return (new CatalogSearchResult<T> { Page = page, PageSize = pageSize }, false);
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var payload = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
            var items = payload.TryGetProperty("items", out var nestedItems) && nestedItems.ValueKind == JsonValueKind.Array
                ? nestedItems
                : payload.TryGetProperty("data", out var nestedData) && nestedData.ValueKind == JsonValueKind.Array
                    ? nestedData
                    : root.TryGetProperty("data", out var rootData) && rootData.ValueKind == JsonValueKind.Array
                        ? rootData
                        : root.ValueKind == JsonValueKind.Array ? root : default;
            if (items.ValueKind != JsonValueKind.Array)
            {
                LastCenterQueryMessage = "中心返回的数据格式不包含分页列表。";
                return (new CatalogSearchResult<T> { Page = page, PageSize = pageSize }, false);
            }

#pragma warning disable IDISP004
            var result = DeserializeCatalogItems<T>(items);
#pragma warning restore IDISP004
            var total = payload.TryGetProperty("total", out var totalValue) && totalValue.TryGetInt64(out var parsedTotal) ? parsedTotal : result.Count;
            return (new CatalogSearchResult<T> { Items = result, Total = total, Page = page, PageSize = pageSize, Source = "center" }, true);
        }
        catch (Exception ex)
        {
            LastCenterQueryMessage = $"中心查询异常：{ex.Message}";
            return (new CatalogSearchResult<T> { Page = page, PageSize = pageSize }, false);
        }
    }

    private static List<T> DeserializeCatalogItems<T>(JsonElement items)
    {
        var result = new List<T>();
#pragma warning disable IDISP004
        foreach (var element in items.EnumerateArray())
        {
            var item = element.Deserialize<T>(JsonOptions);
            if (item is null)
            {
                continue;
            }
            if (item is SkuSummary sku && string.IsNullOrWhiteSpace(sku.ProductCode)
                && element.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.Object
                && product.TryGetProperty("code", out var productCode))
            {
                sku.ProductCode = productCode.GetString() ?? string.Empty;
            }
            result.Add(item);
        }
#pragma warning restore IDISP004
        return result;
    }

    private async Task<bool> CacheCatalogItemsAsync<T>(string path, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return true;
        }
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Post, path);
            request.Content = JsonContent.Create(new { items });
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private CatalogSearchResult<T> CatalogFallback<T>((CatalogSearchResult<T> Result, bool Succeeded) local, string emptyMessage)
    {
        return new CatalogSearchResult<T>
        {
            Items = local.Result.Items,
            Total = local.Result.Total,
            Page = local.Result.Page,
            PageSize = local.Result.PageSize,
            Source = local.Succeeded ? "local" : string.Empty,
            Message = string.IsNullOrWhiteSpace(LastCenterQueryMessage) ? emptyMessage : LastCenterQueryMessage,
        };
    }

    private static CatalogSearchResult<T> WithCatalogMessage<T>(CatalogSearchResult<T> result, bool cached)
    {
        return new CatalogSearchResult<T>
        {
            Items = result.Items,
            Total = result.Total,
            Page = result.Page,
            PageSize = result.PageSize,
            Source = result.Source,
            Message = cached ? "本地无匹配数据，已从中心查询并同步到本地。" : "本地无匹配数据，当前显示中心结果；本地目录尚未就绪。",
        };
    }

    public async Task<bool> SubmitSkuPrintJobAsync(string templateId, IReadOnlyList<SkuSummary> skus)
    {
        try
        {
            var payload = new
            {
                template_id = templateId,
                source = "windows-product-management",
                job_id = $"sku-print-{Guid.NewGuid():N}",
                idempotency_key = $"sku-print-{Guid.NewGuid():N}",
                items = skus.Select(sku => new { sku_id = sku.Id, sku_code = sku.Code, quantity = 1 }).ToList(),
            };
            using var request = CreateLocalAdminRequest(HttpMethod.Post, "/edge/print-jobs");
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<T>> GetCenterListAsync<T>(string path, string keyword, string suffix = "")
    {
        LastCenterQueryMessage = string.Empty;
        try
        {
            var settings = LoadEdgeSettings();
            if (settings.NamespaceId == 0)
            {
                var detectedNamespaceId = await DetectNamespaceIdAsync(settings).ConfigureAwait(false);
                if (detectedNamespaceId > 0)
                {
                    settings.NamespaceId = detectedNamespaceId;
                    SaveEdgeSettings(settings);
                }
            }

            var query = $"?keyword={Uri.EscapeDataString(keyword.Trim())}&page=1&pageSize=100{suffix}";
            using var request = CreateLocalAuthorizedRequest(HttpMethod.Get, path + query, settings);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastCenterQueryMessage = $"中心查询失败：{(int)response.StatusCode} {response.ReasonPhrase}。{ExtractErrorMessage(responseBody)}";
                return [];
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("items", out var rootItems) && rootItems.ValueKind == JsonValueKind.Array
                    ? rootItems
                    : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                        ? data
                        : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var nestedItems) && nestedItems.ValueKind == JsonValueKind.Array
                            ? nestedItems
                            : default;
            if (array.ValueKind != JsonValueKind.Array)
            {
                LastCenterQueryMessage = "中心返回的数据格式不包含列表项，请检查接口版本或代理配置。";
                return [];
            }

#pragma warning disable IDISP004
            var result = array.EnumerateArray()
                .Select(item => item.Deserialize<T>(JsonOptions))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();
#pragma warning restore IDISP004
            return result;
        }
        catch (Exception ex)
        {
            LastCenterQueryMessage = $"中心查询异常：{ex.Message}";
            return [];
        }
    }

    private async Task<uint> DetectNamespaceIdAsync(EdgeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            LastCenterQueryMessage = "未配置 API Token，无法查询产品和 SKU。";
            return 0;
        }

        try
        {
            using var request = CreateLocalAuthorizedRequest(HttpMethod.Get, "/api/auth/me", settings);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastCenterQueryMessage = $"认证上下文获取失败：{(int)response.StatusCode} {response.ReasonPhrase}。{ExtractErrorMessage(body)}";
                return 0;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (TryGetNamespaceId(root, "current_namespace", out var currentId))
            {
                return currentId;
            }

            if (root.TryGetProperty("namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
            {
#pragma warning disable IDISP004
                foreach (var item in namespaces.EnumerateArray())
                {
                    if (TryGetNamespaceId(item, null, out var namespaceId))
                    {
                        return namespaceId;
                    }
                }
#pragma warning restore IDISP004
            }
        }
        catch (Exception ex)
        {
            LastCenterQueryMessage = $"无法识别工作空间：{ex.Message}";
        }

        LastCenterQueryMessage = "未识别到可用工作空间，请在高级设置中填写工作空间 ID。";
        return 0;
    }

    private static bool TryGetNamespaceId(JsonElement root, string? propertyName, out uint namespaceId)
    {
        namespaceId = 0;
        var value = root;
        if (propertyName is not null && (!root.TryGetProperty(propertyName, out value) || value.ValueKind != JsonValueKind.Object))
        {
            return false;
        }

        if (!value.TryGetProperty("id", out var id))
        {
            return false;
        }

        return id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out namespaceId) && namespaceId > 0;
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var key in new[] { "message", "msg", "error" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // Keep non-JSON upstream responses out of the UI message.
        }

        return string.Empty;
    }

    public async Task<EdgeCacheStatusResponse?> GetCacheStatusAsync()
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Get, "/edge/cache/status");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<EdgeCacheStatusResponse>(JsonOptions).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ClearCacheAsync()
    {
        try
        {
            using var request = CreateLocalAdminRequest(HttpMethod.Post, "/edge/cache/clear");
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public EdgeSettings LoadEdgeSettings()
    {
        EnsureDefaultConfig();
        var settings = new EdgeSettings();
        var inEdgeBlock = false;
        var inCapabilities = false;
        var capabilities = new List<string>();

        foreach (var rawLine in File.ReadLines(ConfigPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(rawLine[0]))
            {
                inEdgeBlock = line == "edge:";
                inCapabilities = false;
                continue;
            }

            if (!inEdgeBlock)
            {
                continue;
            }

            if (line == "capabilities:")
            {
                inCapabilities = true;
                continue;
            }

            if (inCapabilities && line.StartsWith('-'))
            {
                var capability = line[1..].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(capability))
                {
                    capabilities.Add(capability);
                }

                continue;
            }

            inCapabilities = false;
            SetEdgeSetting(settings, line);
        }

        settings.Capabilities = capabilities.Count == 0 ? DefaultCapabilities() : capabilities;
        if (string.IsNullOrWhiteSpace(settings.NodeName))
        {
            settings.NodeName = Environment.MachineName;
        }

        if (string.IsNullOrWhiteSpace(settings.NodeId))
        {
            settings.NodeId = Environment.MachineName;
        }

        NormalizePrintSettings(settings);
        if (string.IsNullOrWhiteSpace(settings.AdminToken))
        {
            settings.AdminToken = CreateAdminToken();
            SaveEdgeSettings(settings);
        }

        return settings;
    }

    public void SaveEdgeSettings(EdgeSettings settings)
    {
        EnsureDefaultConfig();
        settings.NodeName = string.IsNullOrWhiteSpace(settings.NodeName) ? Environment.MachineName : settings.NodeName.Trim();
        settings.NodeId = string.IsNullOrWhiteSpace(settings.NodeId) ? Environment.MachineName : settings.NodeId.Trim();
        settings.CenterUrl = settings.CenterUrl.Trim();
        settings.ApiToken = settings.ApiToken.Trim();
        settings.LanBaseUrl = settings.LanBaseUrl.Trim();
        settings.AdminToken = string.IsNullOrWhiteSpace(settings.AdminToken) ? CreateAdminToken() : settings.AdminToken.Trim();
        settings.CacheMode = settings.CacheMode is "disabled" or "aggressive" ? settings.CacheMode : "standard";
        settings.CacheMaxEntries = Math.Clamp(settings.CacheMaxEntries, 100, 100000);
        settings.CacheMaxMemoryMegabytes = Math.Clamp(settings.CacheMaxMemoryMegabytes, 16, 4096);
        settings.CacheMaxObjectMegabytes = Math.Clamp(settings.CacheMaxObjectMegabytes, 1, 100);
        settings.CacheMaxStaleHours = Math.Clamp(settings.CacheMaxStaleHours, 1, 168);
        settings.DefaultPrinter = settings.DefaultPrinter.Trim();
        settings.PrintPollIntervalSeconds = Math.Clamp(settings.PrintPollIntervalSeconds, 1, 300);
        NormalizePrintSettings(settings);
        settings.Capabilities = settings.Capabilities.Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (settings.Capabilities.Count == 0)
        {
            settings.Capabilities = DefaultCapabilities();
        }

        var lines = File.ReadAllLines(ConfigPath).ToList();
        var edgeStart = lines.FindIndex(static line => line.Trim() == "edge:");
        if (edgeStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.AddRange(BuildEdgeBlock(settings));
        }
        else
        {
            var edgeEnd = edgeStart + 1;
            while (edgeEnd < lines.Count && (lines[edgeEnd].Length == 0 || char.IsWhiteSpace(lines[edgeEnd][0])))
            {
                edgeEnd++;
            }

            lines.RemoveRange(edgeStart, edgeEnd - edgeStart);
            lines.InsertRange(edgeStart, BuildEdgeBlock(settings));
        }

        File.WriteAllLines(ConfigPath, lines);
    }

    public string ResolveLanBaseUrl()
    {
        var port = ReadConfiguredPort();
        return ResolvePublishableLanBaseUrl(LoadEdgeSettings(), port) ?? $"http://127.0.0.1:{port}";
    }

    public void EnsureDefaultConfig()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "data"));
        Directory.CreateDirectory(LogDirectory);

        if (File.Exists(ConfigPath))
        {
            return;
        }

        var content = """
runtime:
  mode: edge

database:
  type: sqlite
  sqlite_path: data/edge.db
  run_migrations: false

server:
  port: "8089"
  frontend_url: "http://localhost"

log:
  db_level: error

edge:
  node_id: ""
  node_name: "FSCM Edge Node"
  center_url: "https://fscm.freeb.vip"
  api_token: ""
  admin_token: "__ADMIN_TOKEN__"
  namespace_id: 0
  lan_base_url: ""
  heartbeat_seconds: 15
  cache_mode: "standard"
  cache_max_entries: 5000
  cache_max_memory_mb: 256
  cache_max_object_mb: 5
  cache_stale_if_error: true
  cache_max_stale_hours: 24
  cache:
    whitelist:
      - /api/products
      - /api/skus
      - /api/case-specs
      - /api/purchase-orders
  media_cache:
    path: "data/media-cache"
    max_disk_mb: 2048
    max_object_mb: 10
  default_printer: ""
  sku_qr_prefix: "T"
  print_template: "label_60x40mm"
  print_width_mm: 60
  print_height_mm: 40
  print_orientation: "portrait"
  print_offset_x_mm: 0
  print_mode: "fit"
  print_copies: 1
  capabilities:
    - proxy
    - adaptive_cache
    - catalog_cache
    - local_print
""";
        File.WriteAllText(ConfigPath, content.Replace("__ADMIN_TOKEN__", CreateAdminToken(), StringComparison.Ordinal));
    }

    private static string CreateAdminToken()
    {
        return RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
    }

    public int ReadConfiguredPort()
    {
        if (!File.Exists(ConfigPath))
        {
            return 8089;
        }

        var inServerBlock = false;
        foreach (var rawLine in File.ReadLines(ConfigPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(rawLine[0]))
            {
                inServerBlock = line == "server:";
                continue;
            }

            if (inServerBlock && line.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["port:".Length..].Trim().Trim('"', '\'');
                if (int.TryParse(value, out var port) && port > 0)
                {
                    return port;
                }
            }
        }

        return 8089;
    }

    private static void SetEdgeSetting(EdgeSettings settings, string line)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim().Trim('"', '\'');
        switch (key)
        {
            case "node_id":
                settings.NodeId = value;
                break;
            case "node_name":
                settings.NodeName = value;
                break;
            case "center_url":
                settings.CenterUrl = value;
                break;
            case "api_token":
                settings.ApiToken = value;
                break;
            case "admin_token":
                settings.AdminToken = value;
                break;
            case "namespace_id":
                _ = uint.TryParse(value, out var namespaceId);
                settings.NamespaceId = namespaceId;
                break;
            case "lan_base_url":
                settings.LanBaseUrl = value;
                break;
            case "cache_mode":
                settings.CacheMode = value;
                break;
            case "cache_max_entries":
                _ = int.TryParse(value, out var maxEntries);
                settings.CacheMaxEntries = maxEntries;
                break;
            case "cache_max_memory_mb":
                _ = int.TryParse(value, out var maxMemory);
                settings.CacheMaxMemoryMegabytes = maxMemory;
                break;
            case "cache_max_object_mb":
                _ = int.TryParse(value, out var maxObject);
                settings.CacheMaxObjectMegabytes = maxObject;
                break;
            case "cache_stale_if_error":
                settings.CacheStaleIfError = bool.TryParse(value, out var staleIfError) && staleIfError;
                break;
            case "cache_max_stale_hours":
                _ = int.TryParse(value, out var maxStaleHours);
                settings.CacheMaxStaleHours = maxStaleHours;
                break;
            case "default_printer":
                settings.DefaultPrinter = value;
                break;
            case "sku_qr_prefix":
                settings.SkuQrPrefix = value;
                break;
            case "print_template":
                settings.PrintTemplate = value;
                break;
            case "print_width_mm":
                _ = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width);
                settings.PrintWidthMillimeters = width;
                break;
            case "print_height_mm":
                _ = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height);
                settings.PrintHeightMillimeters = height;
                break;
            case "print_orientation":
                settings.PrintOrientation = value;
                break;
            case "print_offset_x_mm":
                _ = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offsetX);
                settings.PrintOffsetXMillimeters = offsetX;
                break;
            case "print_offset_y_mm":
                _ = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offsetY);
                settings.PrintOffsetYMillimeters = offsetY;
                break;
            case "print_safety_inset_mm":
                _ = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var safetyInset);
                settings.PrintSafetyInsetMillimeters = safetyInset;
                break;
            case "print_mode":
                settings.PrintMode = value;
                break;
            case "print_copies":
                _ = int.TryParse(value, out var copies);
                settings.PrintCopies = copies;
                break;
            case "print_poll_seconds":
                _ = int.TryParse(value, out var printPollSeconds);
                settings.PrintPollIntervalSeconds = printPollSeconds;
                break;
        }
    }

    private static List<string> DefaultCapabilities()
    {
        return ["proxy", "adaptive_cache", "catalog_cache", "local_print"];
    }

    private static void NormalizePrintSettings(EdgeSettings settings)
    {
        settings.PrintTemplate = string.IsNullOrWhiteSpace(settings.PrintTemplate) ? "label_60x40mm" : settings.PrintTemplate.Trim();
        settings.SkuQrPrefix = string.IsNullOrWhiteSpace(settings.SkuQrPrefix) ? "T" : settings.SkuQrPrefix.Trim();
        settings.PrintWidthMillimeters = settings.PrintWidthMillimeters > 0 ? settings.PrintWidthMillimeters : 60;
        settings.PrintHeightMillimeters = settings.PrintHeightMillimeters > 0 ? settings.PrintHeightMillimeters : 40;
        settings.PrintOrientation = string.Equals(settings.PrintOrientation, "landscape", StringComparison.OrdinalIgnoreCase)
            ? "landscape"
            : "portrait";
        settings.PrintMode = "fit";
        settings.PrintCopies = Math.Clamp(settings.PrintCopies, 1, PrintJobDispatchPolicy.MaxCopiesPerContent);
        settings.PrintOffsetXMillimeters = Math.Clamp(settings.PrintOffsetXMillimeters, -5, 5);
        settings.PrintOffsetYMillimeters = Math.Clamp(settings.PrintOffsetYMillimeters, -5, 5);
        settings.PrintSafetyInsetMillimeters = settings.PrintSafetyInsetMillimeters > 0
            ? Math.Clamp(settings.PrintSafetyInsetMillimeters, 0.5, 5)
            : PrintPageContextFactory.DefaultSafetyInsetMillimeters;
    }

    private static IReadOnlyList<string> BuildEdgeBlock(EdgeSettings settings)
    {
        var lines = new List<string>
        {
            "edge:",
        };

        if (!string.IsNullOrWhiteSpace(settings.NodeId))
        {
            lines.Add($"  node_id: \"{EscapeYamlValue(settings.NodeId)}\"");
        }

        lines.Add($"  node_name: \"{EscapeYamlValue(settings.NodeName)}\"");
        lines.Add($"  center_url: \"{EscapeYamlValue(settings.CenterUrl)}\"");
        lines.Add($"  api_token: \"{EscapeYamlValue(settings.ApiToken)}\"");
        lines.Add($"  admin_token: \"{EscapeYamlValue(settings.AdminToken)}\"");
        lines.Add($"  namespace_id: {settings.NamespaceId}");
        lines.Add($"  lan_base_url: \"{EscapeYamlValue(settings.LanBaseUrl)}\"");
        lines.Add("  heartbeat_seconds: 15");
        lines.Add($"  print_poll_seconds: {settings.PrintPollIntervalSeconds}");
        lines.Add($"  cache_mode: \"{EscapeYamlValue(settings.CacheMode)}\"");
        lines.Add($"  cache_max_entries: {settings.CacheMaxEntries}");
        lines.Add($"  cache_max_memory_mb: {settings.CacheMaxMemoryMegabytes}");
        lines.Add($"  cache_max_object_mb: {settings.CacheMaxObjectMegabytes}");
        lines.Add($"  cache_stale_if_error: {settings.CacheStaleIfError.ToString().ToLowerInvariant()}");
        lines.Add($"  cache_max_stale_hours: {settings.CacheMaxStaleHours}");
        lines.Add("  cache:");
        lines.Add("    whitelist:");
        lines.Add("      - /api/products");
        lines.Add("      - /api/skus");
        lines.Add("      - /api/case-specs");
        lines.Add("      - /api/purchase-orders");
        lines.Add("  media_cache:");
        lines.Add("    path: \"data/media-cache\"");
        lines.Add("    max_disk_mb: 2048");
        lines.Add("    max_object_mb: 10");
        lines.Add($"  default_printer: \"{EscapeYamlValue(settings.DefaultPrinter)}\"");
        lines.Add($"  sku_qr_prefix: \"{EscapeYamlValue(settings.SkuQrPrefix)}\"");
        lines.Add($"  print_template: \"{EscapeYamlValue(settings.PrintTemplate)}\"");
        lines.Add($"  print_width_mm: {settings.PrintWidthMillimeters.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"  print_height_mm: {settings.PrintHeightMillimeters.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"  print_orientation: \"{EscapeYamlValue(settings.PrintOrientation)}\"");
        lines.Add($"  print_offset_x_mm: {settings.PrintOffsetXMillimeters.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"  print_offset_y_mm: {settings.PrintOffsetYMillimeters.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"  print_safety_inset_mm: {settings.PrintSafetyInsetMillimeters.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"  print_mode: \"{EscapeYamlValue(settings.PrintMode)}\"");
        lines.Add($"  print_copies: {settings.PrintCopies}");
        lines.Add("  capabilities:");
        lines.AddRange(settings.Capabilities.Select(capability => $"    - {EscapeYamlValue(capability)}"));
        return lines;
    }

    private static string EscapeYamlValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string? ResolvePublishableLanBaseUrl(EdgeSettings settings, int port)
    {
        if (!string.IsNullOrWhiteSpace(settings.LanBaseUrl))
        {
            return settings.LanBaseUrl.TrimEnd('/');
        }

        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && network.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses
                .Where(address => IsPublishableLanAddress(address.Address))
                .Select(address => new
                {
                    Address = address.Address,
                    IsPrivate = IsPrivateIPv4(address.Address),
                    HasGateway = network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork),
                }))
            .OrderByDescending(candidate => candidate.IsPrivate)
            .ThenByDescending(candidate => candidate.HasGateway)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .ToList();

        return candidates.Count == 0 ? null : $"http://{candidates[0].Address}:{port}";
    }

    public async Task<BatchPrintResult<uint>> ResolveCurrentCenterNodeIdAsync()
    {
        try
        {
            EdgeSettings settings = LoadEdgeSettings();
            if (string.IsNullOrWhiteSpace(settings.CenterUrl) || string.IsNullOrWhiteSpace(settings.ApiToken) || string.IsNullOrWhiteSpace(settings.NodeId))
            {
                return new BatchPrintResult<uint> { Message = "请先在高级设置中配置中心地址、API Token 和 Node ID。" };
            }
            if (settings.NamespaceId == 0)
            {
                settings.NamespaceId = await DetectNamespaceIdAsync(settings).ConfigureAwait(false);
                if (settings.NamespaceId > 0)
                {
                    SaveEdgeSettings(settings);
                }
            }
            using var request = CreateCenterManagementRequest(HttpMethod.Get, "/api/edge/nodes?page=1&page_size=100&include_offline=true", settings);
            using var response = await _batchHttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new BatchPrintResult<uint> { Message = CenterBatchError(response, body, "读取中心边缘节点失败") };
            }
            using var document = JsonDocument.Parse(body);
            JsonElement data = EnvelopeData(document.RootElement);
            if (!data.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                return new BatchPrintResult<uint> { Message = "中心返回的节点列表格式无效。" };
            }
#pragma warning disable IDISP004
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.TryGetProperty("node_id", out JsonElement identity) &&
                    string.Equals(identity.GetString(), settings.NodeId, StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("id", out JsonElement id) && id.TryGetUInt32(out uint nodeId))
                {
                    return new BatchPrintResult<uint> { Succeeded = true, Data = nodeId };
                }
            }
#pragma warning restore IDISP004
            return new BatchPrintResult<uint> { Message = $"中心未找到当前节点 {settings.NodeId}，请确认节点已注册。" };
        }
        catch (Exception ex)
        {
            return new BatchPrintResult<uint> { Message = $"读取中心节点失败：{ex.Message}" };
        }
    }

    public Task<BatchPrintResult<BatchPrintPreview>> PreviewBatchPrintAsync(BatchPrintRequest payload)
    {
        return SendCenterBatchRequestAsync<BatchPrintPreview>(HttpMethod.Post, "/api/edge/print-batches/preview", payload);
    }

    public Task<BatchPrintResult<CenterPrintBatch>> CreateBatchPrintAsync(BatchPrintRequest payload, string idempotencyKey)
    {
        return SendCenterBatchRequestAsync<CenterPrintBatch>(HttpMethod.Post, "/api/edge/print-batches", payload, idempotencyKey);
    }

    public async Task<BatchPrintResult<IReadOnlyList<CenterPrintBatch>>> GetCenterPrintBatchesAsync()
    {
        BatchPrintResult<CenterPrintBatchList> result = await SendCenterBatchRequestAsync<CenterPrintBatchList>(HttpMethod.Get, "/api/edge/print-batches?page=1&page_size=100").ConfigureAwait(false);
        return new BatchPrintResult<IReadOnlyList<CenterPrintBatch>> { Succeeded = result.Succeeded, Message = result.Message, Data = result.Data?.Items ?? [] };
    }

    public Task<BatchPrintResult<CenterPrintBatch>> UpdateCenterPrintBatchAsync(uint batchId, string action)
    {
        return SendCenterBatchRequestAsync<CenterPrintBatch>(HttpMethod.Post, $"/api/edge/print-batches/{batchId}/{Uri.EscapeDataString(action)}");
    }

    private async Task<BatchPrintResult<T>> SendCenterBatchRequestAsync<T>(HttpMethod method, string path, object? payload = null, string? idempotencyKey = null)
    {
        try
        {
            EdgeSettings settings = LoadEdgeSettings();
            using var request = CreateCenterManagementRequest(method, path, settings);
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload);
            }
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }
            using var response = await _batchHttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new BatchPrintResult<T> { Message = CenterBatchError(response, body, "中心批量打印请求失败") };
            }
            using var document = JsonDocument.Parse(body);
            JsonElement data = EnvelopeData(document.RootElement);
            T? value = data.Deserialize<T>(JsonOptions);
            return value is null
                ? new BatchPrintResult<T> { Message = "中心批量打印响应没有有效数据。" }
                : new BatchPrintResult<T> { Succeeded = true, Data = value };
        }
        catch (Exception ex)
        {
            return new BatchPrintResult<T> { Message = $"中心批量打印请求异常：{ex.Message}" };
        }
    }

    private static JsonElement EnvelopeData(JsonElement root)
    {
        return root.TryGetProperty("data", out JsonElement data) ? data : root;
    }

    private static string CenterBatchError(HttpResponseMessage response, string body, string fallback)
    {
        string detail = ExtractErrorMessage(body);
        string status = $"{(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        return string.IsNullOrWhiteSpace(detail)
            ? $"{fallback}：{status}"
            : $"{fallback}：{status}，{detail}";
    }

    private static bool IsPublishableLanAddress(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(address)
            && !IPAddress.Any.Equals(address)
            && !address.IsIPv6LinkLocal
            && !IsLinkLocalIPv4(address);
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsLinkLocalIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private string LocalEndpoint(string path)
    {
        return $"http://127.0.0.1:{ReadConfiguredPort()}{path}";
    }

    private HttpRequestMessage CreateLocalAdminRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, LocalEndpoint(path));
        var token = LoadEdgeSettings().AdminToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("X-Edge-Admin-Token", token);
        }

        return request;
    }

    private HttpRequestMessage CreateLocalAuthorizedRequest(HttpMethod method, string path, EdgeSettings settings)
    {
        var request = new HttpRequestMessage(method, LocalEndpoint(path));
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
            request.Headers.TryAddWithoutValidation("X-API-Token", settings.ApiToken);
        }

        if (settings.NamespaceId > 0)
        {
            request.Headers.TryAddWithoutValidation("X-Namespace-ID", settings.NamespaceId.ToString(CultureInfo.InvariantCulture));
        }

        return request;
    }

    internal static HttpRequestMessage CreateCenterManagementRequest(HttpMethod method, string path, EdgeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CenterUrl))
        {
            throw new InvalidOperationException("请先配置中心地址。");
        }

        string endpoint = $"{settings.CenterUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        return CreateAuthorizedRequest(method, endpoint, settings);
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string endpoint, EdgeSettings settings)
    {
        var request = new HttpRequestMessage(method, endpoint);
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
            request.Headers.TryAddWithoutValidation("X-Api-Token", settings.ApiToken);
            request.Headers.TryAddWithoutValidation("X-Edge-Token", settings.ApiToken);
        }

        if (settings.NamespaceId > 0)
        {
            request.Headers.TryAddWithoutValidation("X-Namespace-ID", settings.NamespaceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return request;
    }

    private async Task<bool> CheckHealthAsync(int port)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"http://127.0.0.1:{port}/edge/health").ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendLine(string path, string? line)
    {
        if (line is null)
        {
            return;
        }

        File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _batchHttpClient.Dispose();
        _process?.Dispose();
    }

    private sealed class EdgeTerminalListResponse
    {
        public List<EdgeTerminal> Terminals { get; set; } = [];
    }

    private sealed class EdgePrintJobListResponse
    {
        public List<EdgePrintJob> Jobs { get; set; } = [];
    }

    private sealed class EdgePrintJobResponse
    {
        public EdgePrintJob? Job { get; set; }
    }

    private sealed class CenterPrintBatchList
    {
        [JsonPropertyName("items")]
        public List<CenterPrintBatch> Items { get; set; } = [];
    }
}
