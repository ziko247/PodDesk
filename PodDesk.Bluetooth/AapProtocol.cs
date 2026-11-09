using System.Text;

namespace PodDesk.Bluetooth;

public sealed record AapReport(IReadOnlyDictionary<Part, Reading>? Batteries = null,
    ListeningMode? Mode = null, byte? Setting = null, byte? Value = null,
    string? Model = null, string? Firmware = null);

public static class AapProtocol
{
    public const ushort Psm = 0x1001;

    // Packet formats from the published LibrePods AAP notes and NTPods wire contract.
    public static byte[] Handshake() => Convert.FromHexString("00000400010002000000000000000000");
    public static byte[] Features() => Convert.FromHexString("040004004D00FF00000000000000");
    public static byte[] Notifications() => Convert.FromHexString("040004000F00FFFFFFFF");
    public static byte[] SetMode(ListeningMode mode) => [4, 0, 4, 0, 9, 0, 0x0D, (byte)mode, 0, 0, 0];

    public static AapReport? Decode(ReadOnlySpan<byte> bytes, DateTimeOffset at)
    {
        if (bytes.Length < 6 || !bytes.StartsWith(new byte[] { 4, 0, 4, 0 }) || bytes[5] != 0)
            return null;

        if (bytes[4] == 4 && bytes.Length >= 7)
        {
            var count = bytes[6];
            if (count > (bytes.Length - 7) / 5) return null;
            var readings = new Dictionary<Part, Reading>();
            for (var entry = 0; entry < count; entry++)
            {
                var start = 7 + entry * 5;
                Part? part = bytes[start] switch { 2 => Part.Right, 4 => Part.Left, 8 => Part.Case, _ => null };
                var level = bytes[start + 2];
                var status = bytes[start + 3];
                if (part is not { } component || level > 100 || status == 4) continue;
                bool? charging = status switch { 1 or 5 => true, 2 or 3 => false, _ => null };
                readings[component] = new(level, charging, at, "AirPods control link");
            }
            return new(Batteries: readings);
        }
        if (bytes[4] == 9 && bytes.Length >= 8)
        {
            var value = bytes[7];
            return bytes[6] switch
            {
                0x0D when value is >= 1 and <= 4 => new(Mode: (ListeningMode)value),
                0x17 or 0x18 or 0x1F or 0x23 or 0x25 or 0x31 => new(Setting: bytes[6], Value: value),
                _ => null
            };
        }
        if (bytes[4] == 0x1D)
        {
            var fields = Encoding.ASCII.GetString(bytes[6..]).Split('\0')
                .Where(field => field.Length >= 3 && field.All(c => c is >= ' ' and <= '~')).ToArray();
            if (fields.Length >= 5 && fields[1].StartsWith('A'))
                return new(Model: fields[1], Firmware: fields[4]);
        }
        return null;
    }
}
