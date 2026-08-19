// This Source Code Form is subject to the terms of the MIT License.
using System.IO;
using System.Text.Json;
using Fscm.Edge.Win.Models;
namespace Fscm.Edge.Win.Services;
internal sealed class ScannerConfigurationService(string path) { private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true }; public ScannerConfiguration Load() { try { return Normalize(JsonSerializer.Deserialize<ScannerConfiguration>(File.ReadAllText(path), Options) ?? new()); } catch (IOException) { return new(); } catch (JsonException) { return new(); } } public void Save(ScannerConfiguration value) { value = Normalize(value); Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options)); File.Move(temporary, path, true); } public static ScannerConfiguration Normalize(ScannerConfiguration value) { value.UserPrefix = string.IsNullOrWhiteSpace(value.UserPrefix) ? "USER_" : value.UserPrefix.Trim().ToUpperInvariant(); value.UnbindCode = string.IsNullOrWhiteSpace(value.UnbindCode) ? "UNBIND" : value.UnbindCode.Trim().ToUpperInvariant(); value.ScanTimeoutMs = Math.Clamp(value.ScanTimeoutMs <= 0 ? 2000 : value.ScanTimeoutMs, 100, 60000); value.UnbindConfirmSeconds = Math.Clamp(value.UnbindConfirmSeconds <= 0 ? 10 : value.UnbindConfirmSeconds, 1, 300); value.MaxScanLength = Math.Clamp(value.MaxScanLength <= 0 ? 2048 : value.MaxScanLength, 1, 8192); value.Devices ??= []; foreach (ScannerDeviceConfiguration device in value.Devices.Where(device => device.Excluded)) device.Enabled = false; return value; } }

internal static class ScannerDevicePolicy
{
    public static bool IsActive(ScannerDeviceConfiguration device) => device.Enabled && !device.Excluded;

    public static bool IsVisible(ScannerDeviceConfiguration device, bool showExcluded) => showExcluded || !device.Excluded;
}

