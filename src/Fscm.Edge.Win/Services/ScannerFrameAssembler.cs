// This Source Code Form is subject to the terms of the MIT License.
namespace Fscm.Edge.Win.Services;
internal sealed class ScannerFrameAssembler { public string? Push(char value, DateTimeOffset now, TimeSpan timeout, int maxLength) => Push(value, now, (int)timeout.TotalMilliseconds, maxLength); private readonly System.Text.StringBuilder buffer = new(); private DateTimeOffset lastInput; public string? Push(char value, DateTimeOffset now, int timeoutMs, int maxLength) { if (lastInput != default && now - lastInput > TimeSpan.FromMilliseconds(timeoutMs)) buffer.Clear(); lastInput = now; if (value is '\r' or '\n') { string result = buffer.ToString(); buffer.Clear(); return result; } if (buffer.Length >= maxLength) { buffer.Clear(); return null; } if (!char.IsControl(value)) buffer.Append(value); return null; } }


