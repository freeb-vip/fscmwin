// This Source Code Form is subject to the terms of the MIT License.
using System.Text.Json.Serialization;
namespace Fscm.Edge.Win.Models;
public sealed class ScannerConfiguration
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("user_prefix")] public string UserPrefix { get; set; } = "USER_";
    [JsonPropertyName("unbind_code")] public string UnbindCode { get; set; } = "UNBIND";
    [JsonPropertyName("scan_timeout_ms")] public int ScanTimeoutMs { get; set; } = 2000;
    [JsonIgnore] public int ScanTimeoutMilliseconds => ScanTimeoutMs;
    [JsonPropertyName("unbind_confirm_seconds")] public int UnbindConfirmSeconds { get; set; } = 10;
    [JsonPropertyName("max_scan_length")] public int MaxScanLength { get; set; } = 2048;
    [JsonPropertyName("devices")] public List<ScannerDeviceConfiguration> Devices { get; set; } = [];
}
public sealed class ScannerDeviceConfiguration
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("excluded")] public bool Excluded { get; set; }
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("transport")] public string Transport { get; set; } = "hid";
    [JsonPropertyName("system_path")] public string SystemPath { get; set; } = string.Empty;
    [JsonPropertyName("vendor_id")] public string VendorId { get; set; } = string.Empty;
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("identity_confidence")] public string IdentityConfidence { get; set; } = "stable";
    [JsonPropertyName("connection_state")] public string ConnectionState { get; set; } = "offline";
    [JsonPropertyName("baud_rate")] public int BaudRate { get; set; } = 9600;
}
public sealed class ScannerDeviceSnapshot { [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = string.Empty; [JsonPropertyName("transport")] public string Transport { get; set; } = string.Empty; [JsonPropertyName("system_path")] public string SystemPath { get; set; } = string.Empty; [JsonPropertyName("vendor_id")] public string VendorId { get; set; } = string.Empty; [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty; [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; [JsonPropertyName("state")] public string State { get; set; } = "offline"; [JsonPropertyName("identity_confidence")] public string IdentityConfidence { get; set; } = "stable"; }
public sealed class ScannerInputRequest { [JsonPropertyName("capture_id")] public string CaptureId { get; set; } = Guid.NewGuid().ToString(); [JsonPropertyName("device_fingerprint")] public string DeviceFingerprint { get; set; } = string.Empty; [JsonPropertyName("payload")] public string Payload { get; set; } = string.Empty; [JsonPropertyName("scanned_at")] public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class ScannerInputResult { [JsonPropertyName("outcome")] public string Outcome { get; set; } = string.Empty; [JsonPropertyName("message")] public string Message { get; set; } = string.Empty; [JsonPropertyName("event_id")] public string EventId { get; set; } = string.Empty; }
public sealed class ScannerBindingStatus { [JsonPropertyName("device_fingerprint")] public string DeviceFingerprint { get; set; } = string.Empty; [JsonPropertyName("user_id"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public uint UserId { get; set; } }
public sealed class ScannerStatus { [JsonPropertyName("enabled")] public bool Enabled { get; set; } [JsonPropertyName("health")] public string Health { get; set; } = string.Empty; [JsonPropertyName("devices")] public List<ScannerDeviceSnapshot> Devices { get; set; } = []; [JsonPropertyName("bindings")] public List<ScannerBindingStatus> Bindings { get; set; } = []; [JsonPropertyName("pending")] public int Pending { get; set; } [JsonPropertyName("dead_letters")] public int DeadLetters { get; set; } [JsonPropertyName("last_error")] public string LastError { get; set; } = string.Empty; }
public sealed record ScannerCapturedFrame(string CaptureId, string DeviceFingerprint, string Payload, DateTimeOffset ScannedAt);

