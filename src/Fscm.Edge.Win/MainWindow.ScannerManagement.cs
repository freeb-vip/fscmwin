// This Source Code Form is subject to the terms of the MIT License.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO.Ports;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;

namespace Fscm.Edge.Win;

public partial class MainWindow
{
    private void OnOpenScannerManagementClick(object sender, RoutedEventArgs e)
    {
        NavigationList.SelectedItem = NavigationList.Items.OfType<ListBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), "ScannerManagement", StringComparison.Ordinal));
        SetPage("ScannerManagement");
    }
}

internal sealed class ScannerManagementWindow : Window
{
    private readonly EdgeRuntimeManager _runtime;
    private readonly TextBlock _summary = new();
    private readonly DataGrid _devices = new() { AutoGenerateColumns = false, IsReadOnly = true, MinHeight = 280 };

    public ScannerManagementWindow(EdgeRuntimeManager runtime)
    {
        _runtime = runtime;
        Title = "Scanner device management";
        Width = 980;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        _devices.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _devices.Columns.Add(new DataGridTextColumn { Header = "Transport", Binding = new System.Windows.Data.Binding("Transport"), Width = 110 });
        _devices.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new System.Windows.Data.Binding("State"), Width = 110 });
        _devices.Columns.Add(new DataGridTextColumn { Header = "Fingerprint", Binding = new System.Windows.Data.Binding("Fingerprint"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _devices.Columns.Add(new DataGridTextColumn { Header = "Identity", Binding = new System.Windows.Data.Binding("IdentityConfidence"), Width = 130 });

        var refresh = new Button { Content = "Refresh", MinWidth = 100, Margin = new Thickness(0, 0, 0, 12) };
        refresh.Click += async (_, _) => await RefreshAsync();
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Registered scanner devices", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Devices register through this edge node. Account bindings are managed in the Web console.", Margin = new Thickness(0, 6, 0, 12), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(refresh);
        panel.Children.Add(_summary);
        panel.Children.Add(_devices);
        Content = panel;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var status = await _runtime.GetScannerStatusAsync();
            var devices = status.Devices ?? [];
            _summary.Text = $"Health: {status.Health}   Devices: {devices.Count}   Pending sync: {status.Pending}   Dead letters: {status.DeadLetters}";
            _devices.ItemsSource = devices;
        }
        catch (Exception ex)
        {
            _summary.Text = "Unable to load local scanner status: " + ex.Message;
            _devices.ItemsSource = null;
        }
    }
}

internal sealed class ScannerCaptureHost : IDisposable
{
    private readonly Window _window;
    private readonly EdgeRuntimeManager _runtime;
    private RawInputScannerService? _raw;
    private SerialScannerService? _serial;
    private ScannerIngressDispatcher? _ingress;
    private ScannerConfiguration? _configuration;
    private bool _started;

    public ScannerCaptureHost(Window window, EdgeRuntimeManager runtime) { _window = window; _runtime = runtime; }

    public async Task StartAsync()
    {
        if (_started) { await SyncDevicesAsync(); return; }
        _configuration = ScannerConfigurationService.Normalize(new ScannerConfigurationService(_runtime.ScannerConfigPath).Load());
        await _runtime.ApplyScannerConfigurationAsync(_configuration);
        _ingress = new ScannerIngressDispatcher(_runtime);
        _raw = new RawInputScannerService(_window, _configuration);
        _raw.FrameCaptured += (_, frame) => _ingress.TrySubmit(frame);
        _raw.DevicesChanged += async (_, _) => await SyncDevicesAsync();
        _serial = new SerialScannerService(_configuration);
        _serial.FrameCaptured += (_, frame) => _ingress.TrySubmit(frame);
        _started = true;
        await SyncDevicesAsync();
    }

    private async Task SyncDevicesAsync()
    {
        if (_raw is null || _configuration is null) return;
        var devices = _raw.DiscoveredDevices().Select(device => new ScannerDeviceSnapshot
        {
            Fingerprint = device.Fingerprint, Transport = device.Transport, SystemPath = device.SystemPath,
            VendorId = device.VendorId, ProductId = device.ProductId, Name = device.DisplayName,
            State = device.ConnectionState, IdentityConfidence = device.IdentityConfidence,
        }).ToList();
        foreach (var serial in (_configuration.Devices ?? []).Where(device => device.Enabled && device.Transport == "serial"))
        {
            devices.Add(new ScannerDeviceSnapshot { Fingerprint = serial.Fingerprint, Transport = "serial", SystemPath = serial.SystemPath, Name = serial.DisplayName, State = SerialPort.GetPortNames().Contains(serial.SystemPath, StringComparer.OrdinalIgnoreCase) ? "online" : "offline", IdentityConfidence = serial.IdentityConfidence });
        }
        await _runtime.UpdateScannerDevicesAsync(devices);
    }

    public void Dispose() { _raw?.Dispose(); _serial?.Dispose(); _ingress?.Dispose(); }
}