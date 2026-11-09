using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PodDesk.Bluetooth;

public sealed class WindowsBluetooth : IDisposable
{
    public const string AapService = "{74ec2172-0bad-4d01-8f77-997b2be0722a}";
    private const string ClassicDevices = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";
    private const string ContainerKey = "System.Devices.ContainerId";
    private BluetoothLEAdvertisementWatcher? watcher;

    public event Action<Beacon>? BeaconReceived;
    public string ScanStatus { get; private set; } = "BLE scanner starting";

    public void EnsureScanning()
    {
        watcher ??= CreateWatcher();
        if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or
            BluetoothLEAdvertisementWatcherStatus.Stopping)
            return;

        watcher.Start();
        if (watcher.Status != BluetoothLEAdvertisementWatcherStatus.Aborted)
            ScanStatus = "Listening for BLE battery reports";
    }

    private BluetoothLEAdvertisementWatcher CreateWatcher()
    {
        var apple = new BluetoothLEManufacturerData { CompanyId = 0x004C };
        var filter = new BluetoothLEAdvertisementFilter();
        filter.Advertisement.ManufacturerData.Add(apple);
        var scan = new BluetoothLEAdvertisementWatcher(filter) { ScanningMode = BluetoothLEScanningMode.Passive };
        scan.Received += OnAdvertisement;
        scan.Stopped += (_, args) => ScanStatus = $"BLE scanner stopped ({args.Error})";
        return scan;
    }

    private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var item in args.Advertisement.ManufacturerData)
        {
            if (item.CompanyId != 0x004C || item.Data.Length != 27) continue;
            using var reader = DataReader.FromBuffer(item.Data);
            var bytes = new byte[item.Data.Length];
            reader.ReadBytes(bytes);
            var beacon = DecodeBeacon(bytes, args.BluetoothAddress, args.RawSignalStrengthInDBm, args.Timestamp);
            if (beacon != null) BeaconReceived?.Invoke(beacon with { AddressType = args.BluetoothAddressType });
        }
    }

    public static Beacon? DecodeBeacon(ReadOnlySpan<byte> bytes, ulong address, short rssi, DateTimeOffset time)
    {
        if (bytes.Length != 27 || bytes[0] != 0x07 || bytes[1] != 25 || bytes[2] != 1)
            return null;

        var product = BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]);
        var primary = (bytes[5] & 0x20) != 0 ? Part.Left : Part.Right;
        var secondary = primary == Part.Left ? Part.Right : Part.Left;
        var lidOpen = (bytes[8] & 8) == 0;
        var readings = new Dictionary<Part, Reading>(3);
        Add(primary, bytes[6] & 15, (bytes[7] & 0x10) != 0);
        Add(secondary, bytes[6] >> 4, (bytes[7] & 0x20) != 0);
        if (lidOpen) Add(Part.Case, bytes[7] & 15, (bytes[7] & 0x40) != 0);
        return new(address, product, rssi, time, lidOpen, readings)
        {
            Primary = primary,
            LidCycle = (byte)(bytes[8] & 7),
            ColorCode = bytes[9]
        };

        void Add(Part part, int step, bool charging)
        {
            if (step <= 10) readings[part] = new(step * 10, charging, time, "BLE estimate");
        }
    }

    public static async Task<IReadOnlyList<PairedPods>> FindPairedAsync(CancellationToken stop)
    {
        var identities = ReadClassicIdentities();
        var devices = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            [ContainerKey]).AsTask(stop);
        var pairs = new List<PairedPods>();
        foreach (var info in devices)
        {
            stop.ThrowIfCancellationRequested();
            using var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(stop);
            if (device == null || !identities.TryGetValue(device.BluetoothAddress, out var identity)) continue;
            var connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            var container = info.Properties.TryGetValue(ContainerKey, out var value) && value is Guid id
                ? id : identity.Container;
            var battery = connected ? ReadWindowsBattery(identity.Nodes) : null;
            pairs.Add(new(device.BluetoothAddress, device.Name, identity.Product, connected, container, battery));
        }
        return pairs.OrderBy(p => p.Name).ThenBy(p => p.Address).ToArray();
    }

    private sealed class Identity(ushort product)
    {
        public ushort Product { get; } = product;
        public Guid Container { get; set; }
        public List<string> Nodes { get; } = [];
    }

    private static Dictionary<ulong, Identity> ReadClassicIdentities()
    {
        using var root = Registry.LocalMachine.OpenSubKey(ClassicDevices);
        var pairs = new Dictionary<ulong, Identity>();
        if (root == null) return pairs;
        var names = root.GetSubKeyNames();
        foreach (var name in names.Where(n => n.StartsWith(AapService, StringComparison.OrdinalIgnoreCase)))
        {
            using var node = root.OpenSubKey(name);
            var suffix = name.LastIndexOf("_PID&", StringComparison.OrdinalIgnoreCase);
            var product = suffix >= 0 && ushort.TryParse(name.AsSpan(suffix + 5),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : (ushort)0;
            foreach (var instance in node?.GetSubKeyNames() ?? [])
            {
                var token = instance.Split('&').Last().Split('_')[0];
                if (token.Length == 12 && ulong.TryParse(token, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var address))
                    pairs.TryAdd(address, new(product));
            }
        }
        foreach (var (address, identity) in pairs)
        {
            var hex = address.ToString("X12");
            foreach (var name in names)
            {
                if (!name.StartsWith('{') && !name.Equals($"Dev_{hex}", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var node = root.OpenSubKey(name);
                foreach (var instance in node?.GetSubKeyNames() ?? [])
                {
                    if (!instance.Contains(hex, StringComparison.OrdinalIgnoreCase)) continue;
                    identity.Nodes.Add($@"BTHENUM\{name}\{instance}");
                    if (name.StartsWith("Dev_", StringComparison.OrdinalIgnoreCase))
                    {
                        using var device = node?.OpenSubKey(instance);
                        if (Guid.TryParse(device?.GetValue("ContainerID") as string, out var container))
                            identity.Container = container;
                    }
                }
            }
        }
        return pairs;
    }

    public static async Task<ulong?> ResolvePairedBeaconAsync(ulong address, IReadOnlyList<PairedPods> pairs,
        CancellationToken stop, BluetoothAddressType addressType = BluetoothAddressType.Unspecified)
    {
        if (pairs.Any(p => p.Address == address)) return address;
        using var device = await (addressType == BluetoothAddressType.Unspecified
            ? BluetoothLEDevice.FromBluetoothAddressAsync(address)
            : BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType)).AsTask(stop);
        if (device == null || !device.DeviceInformation.Pairing.IsPaired) return null;
        if (pairs.Any(pair => pair.Address == device.BluetoothAddress)) return device.BluetoothAddress;
        var info = await DeviceInformation.CreateFromIdAsync(device.DeviceId, [ContainerKey],
            DeviceInformationKind.AssociationEndpoint).AsTask(stop);
        if (info == null || !info.Properties.TryGetValue(ContainerKey, out var value) ||
            value is not Guid container || container == Guid.Empty) return null;
        var matches = pairs.Where(pair => pair.Container == container).ToArray();
        return matches.Length == 1 ? matches[0].Address : null;
    }

    public static async Task<string> ProbeGattAsync(PairedPods pair, CancellationToken stop)
    {
        var endpoints = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), [ContainerKey]).AsTask(stop);
        var endpoint = endpoints.FirstOrDefault(info =>
            pair.Container != Guid.Empty && info.Properties.TryGetValue(ContainerKey, out var container) &&
            container is Guid id && id == pair.Container);
        using var device = endpoint != null
            ? await BluetoothLEDevice.FromIdAsync(endpoint.Id).AsTask(stop)
            : await BluetoothLEDevice.FromBluetoothAddressAsync(pair.Address, BluetoothAddressType.Public).AsTask(stop);
        if (device == null) return "No LE endpoint for this paired identity";
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask(stop);
        var values = new List<byte>();
        var count = services.Services.Count;
        foreach (var service in services.Services)
        {
            using (service)
            {
                var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                    BluetoothCacheMode.Uncached).AsTask(stop);
                foreach (var characteristic in chars.Characteristics)
                {
                    var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(stop);
                    if (result.Status != GattCommunicationStatus.Success || result.Value.Length != 1) continue;
                    using var reader = DataReader.FromBuffer(result.Value);
                    var level = reader.ReadByte();
                    if (level <= 100) values.Add(level);
                }
            }
        }
        return $"GATT Battery Service: {services.Status}, {count} service(s), " +
            (values.Count == 0 ? "no battery values" : $"unlabelled values {string.Join(", ", values)}%");
    }

    private static int? ReadWindowsBattery(IEnumerable<string> nodes)
    {
        var key = new DevPropKey(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
        foreach (var instance in nodes)
        {
            if (CM_Locate_DevNodeW(out var devInst, instance, 0) != 0) continue;
            uint size = 1;
            if (CM_Get_DevNode_PropertyW(devInst, in key, out var kind, out var value, ref size, 0) == 0
                && kind == 3 && size == 1 && value <= 100)
                return value;
        }
        return null;
    }

    public void Pause()
    {
        if (watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
    }

    public void Dispose()
    {
        Pause();
        if (watcher != null) watcher.Received -= OnAdvertisement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DevPropKey(Guid Format, uint Id);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string instance, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, in DevPropKey key, out uint type,
        out byte buffer, ref uint size, uint flags);
}
