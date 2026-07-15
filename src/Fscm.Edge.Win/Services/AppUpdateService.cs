// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

public sealed class AppUpdateService : IDisposable
{
    public const string ApplicationName = "FSCM Edge";
    public const string Platform = "desktop";

    private static readonly Uri FeedUri = new("https://fscm.freeb.vip/api/app-downloads/releases");
    private static readonly Regex ReleaseVersionPattern = new("^\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?$", RegexOptions.CultureInvariant);
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "IDisposableAnalyzers.Correctness",
        "IDISP008",
        Justification = "The default client belongs to this service; injected clients remain caller-owned.")]
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AppUpdateService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Uri requestUri = new($"{FeedUri}?app_name={Uri.EscapeDataString(ApplicationName)}&platform={Platform}");
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return AppUpdateCheckResult.Unavailable($"Update check failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            ReleaseEnvelope? envelope = await response.Content.ReadFromJsonAsync<ReleaseEnvelope>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (envelope is null || envelope.Code != 0 || envelope.Data?.Items is null)
            {
                return AppUpdateCheckResult.Unavailable("Update service returned an invalid response.");
            }

            ReleaseItem? item = envelope.Data.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.AppName, ApplicationName, StringComparison.Ordinal) &&
                string.Equals(candidate.Platform, Platform, StringComparison.OrdinalIgnoreCase) &&
                candidate.IsOnline);
            if (item is null)
            {
                return AppUpdateCheckResult.Current("No online update is available.");
            }

            if (!TryParseVersion(item.Version, out Version? version) ||
                !IsValidChecksum(item.ChecksumSha256) ||
                item.SizeBytes <= 0 ||
                !IsValidDownloadPath(item.DownloadPath) ||
                !string.Equals(Path.GetExtension(item.OriginalName), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return AppUpdateCheckResult.Unavailable("Update service returned an invalid release.");
            }

            string checksum = item.ChecksumSha256!;
            string downloadPath = item.DownloadPath!;
            string originalName = item.OriginalName!;
            Version parsedVersion = version!;

            if (parsedVersion.CompareTo(CurrentVersion) <= 0)
            {
                return AppUpdateCheckResult.Current($"FSCM Edge {CurrentVersion} is current.");
            }

            return AppUpdateCheckResult.Available(new AppUpdateRelease(
                parsedVersion,
                item.ReleaseNotes?.Trim() ?? string.Empty,
                Path.GetFileName(originalName),
                item.SizeBytes,
                checksum.ToLowerInvariant(),
                item.PublishedAt,
                downloadPath));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return AppUpdateCheckResult.Unavailable($"Update check failed: {ex.Message}");
        }
    }

    public async Task<string> DownloadInstallerAsync(AppUpdateRelease release, CancellationToken cancellationToken = default)
    {
        string updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FSCM Edge",
            "Updates",
            release.Version.ToString());
        Directory.CreateDirectory(updateDirectory);

        string installerPath = Path.Combine(updateDirectory, release.OriginalName);
        string partialPath = installerPath + ".part";
        DeleteIfExists(partialPath);

        Uri downloadUri = new(FeedUri, release.DownloadPath);
        using HttpResponseMessage response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Unable to download the update installer securely.");
        }

        if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.SizeBytes)
        {
            throw new InvalidOperationException("Downloaded update size does not match the release metadata.");
        }

        try
        {
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream destination = new(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using IncrementalHash checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > release.SizeBytes)
                {
                    throw new InvalidOperationException("Downloaded update exceeds the expected size.");
                }

                checksum.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (written != release.SizeBytes)
            {
                throw new InvalidOperationException("Downloaded update size does not match the release metadata.");
            }

            string actualChecksum = Convert.ToHexString(checksum.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualChecksum),
                    Convert.FromHexString(release.ChecksumSha256)))
            {
                throw new InvalidOperationException("Downloaded update checksum does not match the release metadata.");
            }

            destination.Close();
            DeleteIfExists(installerPath);
            File.Move(partialPath, installerPath);
            return installerPath;
        }
        catch
        {
            DeleteIfExists(partialPath);
            throw;
        }
    }

    public void StartUpdateLauncher(string installerPath, int parentProcessId)
    {
        string sourceDirectory = Path.Combine(AppContext.BaseDirectory, "UpdateLauncher");
        string sourceLauncher = Path.Combine(sourceDirectory, "Fscm.Edge.UpdateLauncher.exe");
        if (!File.Exists(sourceLauncher))
        {
            throw new FileNotFoundException("Update launcher is missing from the application installation.", sourceLauncher);
        }

        string launcherDirectory = Path.Combine(Path.GetDirectoryName(installerPath)!, "UpdateLauncher");
        Directory.CreateDirectory(launcherDirectory);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(sourceFile, Path.Combine(launcherDirectory, Path.GetFileName(sourceFile)), overwrite: true);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(launcherDirectory, "Fscm.Edge.UpdateLauncher.exe"),
            WorkingDirectory = launcherDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--installer");
        startInfo.ArgumentList.Add(installerPath);
        using Process launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the update launcher.");
    }

    internal static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;
        return !string.IsNullOrWhiteSpace(value) &&
            ReleaseVersionPattern.IsMatch(value) &&
            Version.TryParse(value, out version);
    }

    private static bool IsValidChecksum(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }

        return value.All(Uri.IsHexDigit);
    }

    private static bool IsValidDownloadPath(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith("/api/app-downloads/", StringComparison.Ordinal) &&
            !Uri.TryCreate(value, UriKind.Absolute, out _);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class ReleaseEnvelope
    {
        public int Code { get; init; }

        public ReleaseData? Data { get; init; }
    }

    private sealed class ReleaseData
    {
        public List<ReleaseItem>? Items { get; init; }
    }

    private sealed class ReleaseItem
    {
        [JsonPropertyName("app_name")]
        public string? AppName { get; init; }

        [JsonPropertyName("platform")]
        public string? Platform { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("release_notes")]
        public string? ReleaseNotes { get; init; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; init; }

        [JsonPropertyName("size_bytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("checksum_sha256")]
        public string? ChecksumSha256 { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("is_online")]
        public bool IsOnline { get; init; }

        [JsonPropertyName("download_path")]
        public string? DownloadPath { get; init; }
    }
}
