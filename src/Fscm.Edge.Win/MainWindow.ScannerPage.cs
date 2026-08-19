// This Source Code Form is subject to the terms of the MIT License.
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;

namespace Fscm.Edge.Win;

public partial class MainWindow
{
    private ScannerCaptureCoordinator? _scannerCapture;
    private readonly ObservableCollection<ScannerDeviceRow> _scannerRows = [];
    private async Task StartScannerCaptureAsync()
    {
        _scannerCapture ??= new ScannerCaptureCoordinator(this, _runtime);
        _scannerCapture.CandidatesChanged += (_, _) => Dispatcher.Invoke(RefreshScannerManagement);
        _scannerCapture.StatusChanged += (_, _) => Dispatcher.Invoke(RefreshScannerManagement);
        await _scannerCapture.StartAsync(); RefreshScannerManagement();
    }
    private void RefreshScannerManagement()
    {
        if (_scannerCapture is null) return;
        ScannerEnabledCheckBox.IsChecked = _scannerCapture.Configuration.Enabled;
        ScannerStatusText.Text = _scannerCapture.StatusText;
        ScannerCandidateCountText.Text = _scannerCapture.Candidates.Count.ToString(); ScannerEnabledCountText.Text = _scannerCapture.Candidates.Count(item => ScannerDevicePolicy.IsActive(item.Configuration)).ToString(); ScannerRegisteredCountText.Text = _scannerCapture.RegisteredCount.ToString(); ScannerPendingText.Text = _scannerCapture.Pending.ToString(); ScannerDeadLetterText.Text = _scannerCapture.DeadLetters.ToString(); ScannerLastErrorText.Text = string.IsNullOrWhiteSpace(_scannerCapture.LastError) ? "None" : _scannerCapture.LastError;
        bool showExcluded = ScannerShowExcludedCheckBox.IsChecked == true;
        _scannerRows.Clear(); foreach (ScannerDeviceRow item in _scannerCapture.Candidates.Where(item => ScannerDevicePolicy.IsVisible(item.Configuration, showExcluded))) _scannerRows.Add(item); ScannerDevicesGrid.ItemsSource = _scannerRows;
    }
    private async void OnRefreshScannerDevicesClick(object sender, RoutedEventArgs e) { if (_scannerCapture is not null) { await _scannerCapture.RefreshAsync(); RefreshScannerManagement(); } }
    private async void OnSaveScannerConfigurationClick(object sender, RoutedEventArgs e) { if (_scannerCapture is not null) { ScannerDevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true); ScannerDevicesGrid.CommitEdit(DataGridEditingUnit.Row, true); await _scannerCapture.SaveAsync(ScannerEnabledCheckBox.IsChecked == true, _scannerRows); RefreshScannerManagement(); } }
    private void OnShowExcludedScannerDevicesChanged(object sender, RoutedEventArgs e) => RefreshScannerManagement();
}

internal sealed class ScannerDeviceRow
{
    public bool Enabled { get; set; } public bool Excluded { get; set; } public string Name { get; init; } = string.Empty; public string Transport { get; init; } = string.Empty; public string VidPid { get; init; } = string.Empty; public string WindowsState { get; init; } = string.Empty; public string Fingerprint { get; init; } = string.Empty; public string Identity { get; init; } = string.Empty; public string Registration { get; set; } = "Pending sync"; public string BoundUser { get; set; } = "-"; internal ScannerDeviceConfiguration Configuration { get; init; } = new();
}

internal sealed class ScannerCaptureCoordinator : IDisposable
{
    private readonly Window _window; private readonly EdgeRuntimeManager _runtime; private readonly ScannerConfigurationService _store;
    private RawInputScannerService? _raw; private SerialScannerService? _serial; private ScannerIngressDispatcher? _ingress; private bool _started;
    public ScannerCaptureCoordinator(Window window, EdgeRuntimeManager runtime) { _window = window; _runtime = runtime; _store = new ScannerConfigurationService(runtime.ScannerConfigPath); Configuration = _store.Load(); }
    public event EventHandler? CandidatesChanged; public event EventHandler? StatusChanged; public ScannerConfiguration Configuration { get; private set; } public List<ScannerDeviceRow> Candidates { get; } = []; public string StatusText { get; private set; } = "Starting Windows device discovery"; public string LastError { get; private set; } = string.Empty; public int RegisteredCount { get; private set; } public int Pending { get; private set; } public int DeadLetters { get; private set; }
    public async Task StartAsync()
    {
        if (_started) { await RefreshAsync(); return; }
        _ingress = new ScannerIngressDispatcher(_runtime); _raw = new RawInputScannerService(_window, Configuration); _raw.FrameCaptured += (_, frame) => _ingress.TrySubmit(frame); _raw.DevicesChanged += (_, _) => { RebuildCandidates(); _ = SynchronizeAsync(); }; RestartSerial(); _started = true; RebuildCandidates(); await SynchronizeAsync();
    }
    public async Task RefreshAsync() { _raw?.RefreshDevices(); RebuildCandidates(); await SynchronizeAsync(); }
    public async Task SaveAsync(bool enabled, IEnumerable<ScannerDeviceRow> rows)
    {
        Configuration = ScannerConfigurationService.Normalize(Configuration);
        Configuration.Enabled = enabled;
        foreach (ScannerDeviceRow row in (rows ?? []).Where(row => !string.IsNullOrWhiteSpace(row.Fingerprint))) { ScannerDeviceConfiguration? saved = Configuration.Devices.FirstOrDefault(item => string.Equals(item.Fingerprint, row.Fingerprint, StringComparison.OrdinalIgnoreCase)); if (saved is null) { row.Configuration.Excluded = row.Excluded; row.Configuration.Enabled = row.Enabled && !row.Excluded; Configuration.Devices.Add(row.Configuration); } else { saved.Excluded = row.Excluded; saved.Enabled = row.Enabled && !row.Excluded; } }
        Configuration = ScannerConfigurationService.Normalize(Configuration);
        _store.Save(Configuration); _raw?.ApplyConfiguration(Configuration); RestartSerial(); RebuildCandidates(); await SynchronizeAsync();
    }
    private void RestartSerial() { _serial?.Dispose(); _serial = new SerialScannerService(Configuration); _serial.FrameCaptured += (_, frame) => _ingress?.TrySubmit(frame); }
    private void RebuildCandidates()
    {
        Configuration = ScannerConfigurationService.Normalize(Configuration);
        Dictionary<string, ScannerDeviceConfiguration> saved = Configuration.Devices.Where(item => !string.IsNullOrWhiteSpace(item.Fingerprint)).ToDictionary(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase); List<ScannerDeviceConfiguration> devices = _raw?.DiscoveredDevices().ToList() ?? [];
        foreach (string port in SerialPort.GetPortNames()) devices.Add(new ScannerDeviceConfiguration { Fingerprint = RawInputScannerService.Fingerprint("serial:" + port), DisplayName = "Serial " + port, Transport = "serial", SystemPath = port, IdentityConfidence = "port_fallback", ConnectionState = "online" });
        Candidates.Clear();
        foreach (ScannerDeviceConfiguration item in devices.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)) { if (saved.TryGetValue(item.Fingerprint, out ScannerDeviceConfiguration? existing)) { item.Enabled = existing.Enabled; item.Excluded = existing.Excluded; if (!string.IsNullOrWhiteSpace(existing.DisplayName)) item.DisplayName = existing.DisplayName; } Candidates.Add(new ScannerDeviceRow { Enabled = item.Enabled, Excluded = item.Excluded, Name = item.DisplayName, Transport = item.Transport.ToUpperInvariant(), VidPid = string.IsNullOrWhiteSpace(item.VendorId) ? "-" : $"{item.VendorId}:{item.ProductId}", WindowsState = item.ConnectionState, Fingerprint = item.Fingerprint, Identity = item.IdentityConfidence, Configuration = item }); }
        CandidatesChanged?.Invoke(this, EventArgs.Empty);
    }
    private async Task SynchronizeAsync()
    {
        try
        {
            Configuration = ScannerConfigurationService.Normalize(Configuration);
            await _runtime.ApplyScannerConfigurationAsync(Configuration).ConfigureAwait(false); List<ScannerDeviceSnapshot> enabled = Candidates.Where(item => ScannerDevicePolicy.IsActive(item.Configuration)).Select(item => new ScannerDeviceSnapshot { Fingerprint = item.Fingerprint, Transport = item.Configuration.Transport, SystemPath = item.Configuration.SystemPath, VendorId = item.Configuration.VendorId, ProductId = item.Configuration.ProductId, Name = item.Name, State = item.Configuration.ConnectionState, IdentityConfidence = item.Identity }).ToList(); await _runtime.UpdateScannerDevicesAsync(enabled).ConfigureAwait(false); ScannerStatus status = await _runtime.GetScannerStatusAsync().ConfigureAwait(false);
            List<ScannerBindingStatus> bindingsSnapshot = status.Bindings ?? []; List<ScannerDeviceSnapshot> devicesSnapshot = status.Devices ?? []; RegisteredCount = devicesSnapshot.Count; Pending = status.Pending; DeadLetters = status.DeadLetters; LastError = status.LastError; Dictionary<string, string> bindings = bindingsSnapshot.ToDictionary(item => item.DeviceFingerprint, item => item.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase); HashSet<string> registered = devicesSnapshot.Select(item => item.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (ScannerDeviceRow item in Candidates) { item.Registration = ScannerDevicePolicy.IsActive(item.Configuration) ? (registered.Contains(item.Fingerprint) ? "Registered" : "Syncing") : "Disabled"; item.BoundUser = bindings.TryGetValue(item.Fingerprint, out string? user) ? user : "-"; } StatusText = $"Local edge: {status.Health}";
        }
        catch (Exception ex) { LastError = ex.Message; StatusText = "Windows discovery is active; local edge synchronization is unavailable"; foreach (ScannerDeviceRow item in Candidates) item.Registration = ScannerDevicePolicy.IsActive(item.Configuration) ? "Sync failed" : "Disabled"; }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose() { _raw?.Dispose(); _serial?.Dispose(); _ingress?.Dispose(); }
}
