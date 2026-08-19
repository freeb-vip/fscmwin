// This Source Code Form is subject to the terms of the MIT License.

using System.IO.Ports;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

internal sealed class SerialScannerService : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _workers = [];
    public SerialScannerService(ScannerConfiguration configuration)
    {
        foreach (var device in (configuration.Devices ?? []).Where(device => configuration.Enabled && ScannerDevicePolicy.IsActive(device) && device.Transport == "serial" && !string.IsNullOrWhiteSpace(device.SystemPath)))
        {
            _workers.Add(Task.Run(() => ReadAsync(device, configuration, _stopping.Token)));
        }
    }

    public event EventHandler<ScannerCapturedFrame>? FrameCaptured;

    private async Task ReadAsync(ScannerDeviceConfiguration device, ScannerConfiguration configuration, CancellationToken cancellationToken)
    {
        var delaySeconds = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(device.SystemPath, device.BaudRate <= 0 ? 9600 : device.BaudRate, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, Encoding = System.Text.Encoding.UTF8, ReadTimeout = 1000 };
                port.Open();
                delaySeconds = 1;
                var assembler = new ScannerFrameAssembler();
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var frame = assembler.Push((char)port.ReadChar(), DateTimeOffset.UtcNow, configuration.ScanTimeoutMs, configuration.MaxScanLength);
                        if (!string.IsNullOrWhiteSpace(frame)) FrameCaptured?.Invoke(this, new ScannerCapturedFrame(Guid.NewGuid().ToString(), device.Fingerprint, frame, DateTimeOffset.UtcNow));
                    }
                    catch (TimeoutException) { }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                delaySeconds = Math.Min(delaySeconds * 2, 30);
            }
        }
    }

    public void Dispose() { _stopping.Cancel(); try { Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(2)); } catch (AggregateException) { } _stopping.Dispose(); }
}
