using System.Collections.Concurrent;
using System.Diagnostics;

namespace PodDesk.Bluetooth;

public sealed class AapSession
{
    private readonly ConcurrentQueue<ListeningMode> commands = new();

    public void QueueMode(ListeningMode mode) => commands.Enqueue(mode);

    public Task RunAsync(ulong address, string path, Action<AapReport> onReport,
        Action<string> onStatus, CancellationToken stop) => Task.Run(() =>
    {
        try
        {
            stop.ThrowIfCancellationRequested();
            using var driver = NtpodsTransport.Open(path);
            driver.Connect(address);
            foreach (var packet in new[] { AapProtocol.Handshake(), AapProtocol.Features(), AapProtocol.Notifications() })
            {
                stop.ThrowIfCancellationRequested();
                driver.Send(packet);
                if (stop.WaitHandle.WaitOne(200)) return;
            }
            onStatus("Control link open · waiting for AirPods");
            var buffer = new byte[2048];
            var health = Stopwatch.StartNew();
            var firstReport = false;
            while (!stop.IsCancellationRequested)
            {
                if (commands.TryDequeue(out var mode)) driver.Send(AapProtocol.SetMode(mode));
                var size = driver.Receive(buffer, 250);
                if (size > 0 && AapProtocol.Decode(buffer.AsSpan(0, size), DateTimeOffset.Now) is { } report)
                {
                    firstReport = true;
                    onReport(report);
                }
                if (health.Elapsed < TimeSpan.FromSeconds(3)) continue;
                if (!driver.IsConnectedTo(address)) throw new IOException("AirPods control link disconnected.");
                if (!firstReport && health.Elapsed >= TimeSpan.FromSeconds(9))
                    throw new IOException("AirPods did not respond to the control handshake.");
                if (firstReport) health.Restart();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            onStatus($"Control link unavailable. {error.Message}");
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    });
}
