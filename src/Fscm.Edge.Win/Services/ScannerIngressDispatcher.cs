// This Source Code Form is subject to the terms of the MIT License.

using System.Threading.Channels;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

internal sealed class ScannerIngressDispatcher : IDisposable
{
    private readonly EdgeRuntimeManager _runtime;
    private readonly Channel<ScannerCapturedFrame> _queue = Channel.CreateBounded<ScannerCapturedFrame>(1000);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    public ScannerIngressDispatcher(EdgeRuntimeManager runtime) { _runtime = runtime; _worker = Task.Run(DispatchAsync); }
    public bool TrySubmit(ScannerCapturedFrame frame) => _queue.Writer.TryWrite(frame);
    private async Task DispatchAsync()
    {
        await foreach (var frame in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
        {
            try { await _runtime.SubmitScannerInputAsync(new ScannerInputRequest { CaptureId = frame.CaptureId, DeviceFingerprint = frame.DeviceFingerprint, Payload = frame.Payload, ScannedAt = frame.ScannedAt }, _stopping.Token).ConfigureAwait(false); }
            catch { }
        }
    }
    public void Dispose() { _stopping.Cancel(); _queue.Writer.TryComplete(); try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { } _stopping.Dispose(); }
}
