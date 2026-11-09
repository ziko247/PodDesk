using System.Runtime.InteropServices;
using PodDesk.Bluetooth;
using Windows.Devices.Bluetooth;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    PacketChecks.Run();
    return;
}

var seconds = args.Length > 0 && int.TryParse(args[0], out var n) ? Math.Clamp(n, 5, 900) : 120;
using var log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "probe-results.txt")) { AutoFlush = true };
var logLock = new object();
void Write(string line)
{
    lock (logLock)
    {
        Console.WriteLine(line);
        log.WriteLine(line);
    }
}
using var end = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
Write($"[{DateTimeOffset.Now:O}] Hardware probe ({seconds}s)");
var adapter = await BluetoothAdapter.GetDefaultAsync();
Write(adapter == null ? "Bluetooth adapter unavailable" :
    $"Bluetooth adapter: Classic={adapter.IsClassicSupported}; LE={adapter.IsLowEnergySupported}");
Write($"Classic L2CAP socket result: {L2capSocketResult()}");
var seen = new Dictionary<ulong, string>();
using var bluetooth = new WindowsBluetooth();
bluetooth.BeaconReceived += beacon =>
{
    var values = string.Join(", ", beacon.Batteries.Select(entry =>
        $"{entry.Key}={entry.Value.Percent}%{(entry.Value.Charging == true ? " charging" : "")}"));
    var summary = $"{beacon.Product:X4} {beacon.Address:X12} lid={(beacon.LidOpen ? "open" : "closed")} {values}";
    lock (seen)
    {
        if (seen.GetValueOrDefault(beacon.Address) == summary) return;
        seen[beacon.Address] = summary;
        Write($"[{beacon.Received:O}] BLE {summary} RSSI={beacon.Rssi}");
    }
};
bluetooth.EnsureScanning();
Write(bluetooth.ScanStatus);
var gattTried = new HashSet<ulong>();
string? last = null;
try
{
    while (true)
    {
        var pairs = await WindowsBluetooth.FindPairedAsync(end.Token);
        var current = string.Join("\n", pairs.Select(pair =>
            $"{pair.Name} ({pair.Address:X12}, PID {pair.Product:X4}) — {pair.Model}; " +
            $"connected={pair.Connected}; Windows battery={pair.WindowsBattery?.ToString() ?? "unavailable"}"));
        if (current != last)
        {
            Write($"[{DateTimeOffset.Now:O}] {(current.Length == 0 ? "No paired AAP devices" : current)}");
            last = current;
        }
        ulong[] addresses;
        lock (seen) addresses = seen.Keys.ToArray();
        foreach (var address in addresses)
        {
            using var resolution = CancellationTokenSource.CreateLinkedTokenSource(end.Token);
            resolution.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var matched = await WindowsBluetooth.ResolvePairedBeaconAsync(address, pairs, resolution.Token);
                if (matched != null) Write($"BLE {address:X12} resolved to paired {matched:X12}");
            }
            catch (OperationCanceledException) when (!end.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Write($"BLE resolution failed: {ex.Message}");
            }
        }
        foreach (var pair in pairs.Where(p => gattTried.Add(p.Address)))
        {
            using var gatt = CancellationTokenSource.CreateLinkedTokenSource(end.Token);
            gatt.CancelAfter(TimeSpan.FromSeconds(8));
            Write(NtpodsTransport.FindInterface(pair.Address) == null ?
                "NTPodsAAP interface missing for this pair" : "NTPodsAAP interface available for this pair");
            try { Write(await WindowsBluetooth.ProbeGattAsync(pair, gatt.Token)); }
            catch (OperationCanceledException) when (!end.IsCancellationRequested) { Write("GATT probe timed out"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Write($"GATT error: {ex.Message}"); }
        }
        await Task.Delay(3500, end.Token);
    }
}
catch (OperationCanceledException) when (end.IsCancellationRequested) { }
bluetooth.Pause();
lock (seen) Write($"Finished. Unique Apple proximity addresses: {seen.Count}. {bluetooth.ScanStatus}");

static string L2capSocketResult()
{
    if (Native.WSAStartup(0x0202, new byte[512]) != 0) return "Winsock startup failed";
    try
    {
        var socket = Native.socket(32, 5, 0x100);
        if (socket == -1) return $"Winsock {Native.WSAGetLastError()}";
        Native.closesocket(socket);
        return "socket opened (connection untested)";
    }
    finally { Native.WSACleanup(); }
}

static class Native
{
    [DllImport("ws2_32.dll")] public static extern int WSAStartup(ushort version, byte[] data);
    [DllImport("ws2_32.dll")] public static extern nint socket(int family, int type, int protocol);
    [DllImport("ws2_32.dll")] public static extern int WSAGetLastError();
    [DllImport("ws2_32.dll")] public static extern int closesocket(nint socket);
    [DllImport("ws2_32.dll")] public static extern int WSACleanup();
}
