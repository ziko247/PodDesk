using PodDesk.Bluetooth;

internal static class PacketChecks
{
    public static void Run()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var ble = new byte[27];
        byte[] open = [0x07, 25, 1, 0x36, 0x20, 0x20, 0x67, 0x31, 0];
        open.CopyTo(ble, 0);
        var report = WindowsBluetooth.DecodeBeacon(ble, 0x456789ABCDE0, -50, timestamp);
        Require(report is { Product: 0x2036, LidOpen: true }, "BLE model and lid");
        var battery = report!.Batteries;
        Require(battery[Part.Left] == new Reading(70, true, timestamp, "BLE estimate"), "BLE left");
        Require(battery[Part.Right].Percent == 60 && battery[Part.Case].Percent == 10, "BLE right and case");
        FollowChecks(report, timestamp);
        ble[5] = 0;
        ble[6] = 0x76;
        ble[8] = 8;
        report = WindowsBluetooth.DecodeBeacon(ble, 0x456789ABCDE0, -50, timestamp);
        Require(report is { LidOpen: false } && report.Batteries[Part.Left].Percent == 70 &&
            !report.Batteries.ContainsKey(Part.Case), "Closed lid and swapped primary");
        Require(WindowsBluetooth.DecodeBeacon(ble.AsSpan(0, 12), 0, 0, timestamp) == null, "Truncated BLE");

        var payload = Convert.FromHexString("04000400040003020163020104015005010801FF0401");
        var exact = AapProtocol.Decode(payload, timestamp)?.Batteries;
        Require(exact != null && exact[Part.Left].Percent == 80 && exact[Part.Left].Charging == true &&
            exact[Part.Right].Percent == 99 && !exact.ContainsKey(Part.Case), "AAP component and unavailable case");
        Require(AapProtocol.Decode(payload.AsSpan(0, payload.Length - 1), timestamp) == null, "Truncated AAP");
        foreach (var mode in Enum.GetValues<ListeningMode>())
            Require(AapProtocol.Decode(AapProtocol.SetMode(mode), timestamp)?.Mode == mode, $"AAP mode {mode}");
        Require(AapProtocol.Decode(Convert.FromHexString("0400040009001702"), timestamp) is { Setting: 0x17, Value: 2 },
            "Accessibility report");
        Console.WriteLine("Simulated packet and automatic battery checks passed.");
    }

    private static void FollowChecks(Beacon baseline, DateTimeOffset now)
    {
        var open = now.AddSeconds(-3);
        var left = new OpenCaseReport(baseline with { LidCycle = 0 }, open);
        var right = new OpenCaseReport(left.Beacon with { Address = 2, Primary = Part.Right, Rssi = -45 }, open);
        var prior = baseline with { Address = 3, Received = now.AddSeconds(-15), LidCycle = 7 };
        Require(BatteryFollow.Find([left], baseline.ColorCode, now, null, null).Match == null,
            "Nearby case without matching context");
        Require(BatteryFollow.Find([left, right], baseline.ColorCode, now, open, null).Match?.Address == 2,
            "Two earbuds reporting the same case after reconnect");
        Require(BatteryFollow.Find([left], baseline.ColorCode, now, null, prior).Match?.Address == baseline.Address,
            "Lid counter wrap and recent battery continuity");
        var other = right with { Beacon = right.Beacon with { LidCycle = 3 } };
        var conflict = BatteryFollow.Find([left, other], baseline.ColorCode, now, open, prior);
        Require(conflict.Match == null && conflict.Ambiguous, "Competing case reports stay separate");
        Require(BatteryFollow.Find([left with { OpenedAt = now }], baseline.ColorCode, now, now, null).Match == null,
            "Wait for competing reports");
        Require(BatteryFollow.Find([left], (byte)(baseline.ColorCode + 1), now, open, null).Match == null,
            "Different color fingerprint");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Packet check failed: {label}");
    }
}
