namespace PodDesk.Bluetooth;

public sealed record OpenCaseReport(Beacon Beacon, DateTimeOffset OpenedAt);

public static class BatteryFollow
{
    public static (Beacon? Match, bool Ambiguous) Find(
        IEnumerable<OpenCaseReport> reports, byte colorCode, DateTimeOffset now,
        DateTimeOffset? reconnectedAt, Beacon? previous)
    {
        var nearby = reports.Where(report => report.Beacon.Received >= now.AddSeconds(-9)
            && report.Beacon.LidOpen && IsComplete(report.Beacon)).ToArray();
        if (nearby.Length == 0) return (null, false);

        // The two earbuds can send separate addresses for the same lid opening.
        // Require matching values and opposite sides before treating those as one event.
        if (nearby.Length > 2 || (nearby.Length == 2 && !SameOpening(nearby[0], nearby[1])))
            return (null, true);

        var firstSeen = nearby.Min(report => report.OpenedAt);
        if (now - firstSeen < TimeSpan.FromSeconds(2)) return (null, false);

        var best = nearby.OrderByDescending(report => report.Beacon.Rssi).First();
        var beacon = best.Beacon;
        if (beacon.ColorCode != colorCode || beacon.Rssi < -70) return (null, false);

        var reconnectMatches = reconnectedAt is { } reconnect &&
            now - reconnect <= TimeSpan.FromSeconds(14) &&
            (firstSeen - reconnect).Duration() <= TimeSpan.FromSeconds(9);
        var continuesCase = previous is { } prior && firstSeen > prior.Received &&
            now - prior.Received <= TimeSpan.FromMinutes(2) &&
            beacon.LidCycle == ((prior.LidCycle + 1) & 7) &&
            SimilarBattery(beacon, prior);

        return (reconnectMatches || continuesCase ? beacon : null, false);
    }

    public static bool IsComplete(Beacon beacon) =>
        beacon.Batteries.ContainsKey(Part.Left) && beacon.Batteries.ContainsKey(Part.Right) &&
        beacon.Batteries.ContainsKey(Part.Case);

    private static bool SameOpening(OpenCaseReport first, OpenCaseReport second)
    {
        var left = first.Beacon;
        var right = second.Beacon;
        return left.Primary != right.Primary && left.ColorCode == right.ColorCode &&
            left.LidCycle == right.LidCycle && (first.OpenedAt - second.OpenedAt).Duration() <= TimeSpan.FromSeconds(3) &&
            left.Batteries.All(entry => right.Batteries.TryGetValue(entry.Key, out var value) &&
                entry.Value.Percent == value.Percent);
    }

    private static bool SimilarBattery(Beacon current, Beacon previous) =>
        IsComplete(previous) && current.Batteries.All(entry => previous.Batteries.TryGetValue(entry.Key, out var old) &&
            Math.Abs(entry.Value.Percent - old.Percent) <= (entry.Key == Part.Case ? 10 : 20));
}
