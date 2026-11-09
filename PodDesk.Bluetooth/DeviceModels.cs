using Windows.Devices.Bluetooth;

namespace PodDesk.Bluetooth;

public enum Part { Left, Right, Case }
public enum ListeningMode : byte { Off = 1, NoiseCancellation = 2, Transparency = 3, AdaptiveAudio = 4 }

public sealed record Reading(int Percent, bool? Charging, DateTimeOffset Received, string Source);
public sealed record PairedPods(ulong Address, string Name, ushort Product, bool Connected,
    Guid Container, int? WindowsBattery)
{
    public string Model => AirPodsModel.Name(Product);
}

public static class AirPodsModel
{
    public static string Name(ushort product) => product switch
    {
        0x2036 or 0x2037 => "AirPods 5 · USB-C case",
        0x2030 or 0x2032 => "AirPods 5 · wireless case",
        0x2019 => "AirPods 4",
        0x201B => "AirPods 4 · ANC",
        0x200E => "AirPods Pro",
        0x2014 or 0x2024 => "AirPods Pro 2",
        0x2027 => "AirPods Pro 3",
        0x2002 or 0x200F or 0x2013 => "AirPods",
        0x200A or 0x201F => "AirPods Max",
        _ => $"Apple accessory · {product:X4}"
    };

    public static bool IsAirPods5(ushort product) => product is 0x2036 or 0x2037 or 0x2030 or 0x2032;
    public static bool IsWirelessCase(ushort product) => product is 0x2030 or 0x2032;
}

public sealed record Beacon(ulong Address, ushort Product, short Rssi, DateTimeOffset Received,
    bool LidOpen, IReadOnlyDictionary<Part, Reading> Batteries)
{
    public BluetoothAddressType AddressType { get; init; } = BluetoothAddressType.Unspecified;
    public Part Primary { get; init; }
    public byte LidCycle { get; init; }
    public byte ColorCode { get; init; }
}
