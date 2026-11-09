using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PodDesk.Bluetooth;

namespace PodDesk;

internal sealed record DeviceChoice(string Key, string Label, ushort Product, ulong Address)
{
    public override string ToString() => Label;
}
internal sealed record BatteryBroadcast(ulong Address, string Label)
{
    public override string ToString() => Label;
}
internal sealed record BatteryCard(string Name, string Value, double Level, string Detail, string Footnote,
    bool Stale, string Glyph);
internal sealed record AccessRow(string Name, string Description, string Value, string Status);

internal sealed class DeviceMonitor : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly WindowsBluetooth bluetooth = new();
    private readonly ConcurrentDictionary<ulong, Beacon> inbox = new();
    private readonly Dictionary<ulong, Beacon> nearby = new();
    private readonly Dictionary<ulong, DateTimeOffset> openedAt = new();
    private readonly Dictionary<ulong, (ulong? Pair, DateTimeOffset Expires)> resolved = new();
    private readonly Dictionary<ulong, ulong> manualLinks = new();
    private readonly Dictionary<string, Dictionary<Part, Reading>> battery = new();
    private readonly Dictionary<byte, byte> settings = new();
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<PairedPods> paired = [];
    private RememberedBattery? remembered = RememberedBattery.Load();
    private ulong? followedAddress;
    private Beacon? lastFollowedCase;
    private DateTimeOffset? reconnectedAt;
    private bool autoAmbiguous;
    private bool savedPreference = true;
    private DateTimeOffset lastPoll;
    private DateTimeOffset lastAapAttempt;
    private CancellationTokenSource? controlStop;
    private AapSession? control;
    private Task? controlTask;
    private DateTimeOffset pendingSince;
    private DateTimeOffset lastDemoReport;
    private DateTimeOffset? batterySearchStarted;
    private bool aapResponding;
    private bool paused;
    private bool refreshing;
    private bool disposed;
    private string discovery = "Looking for paired AirPods";
    private string link = "Looking for the Windows control driver";
    private string? commandResult;
    private string? metadata;

    public DeviceMonitor(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        bluetooth.BeaconReceived += beacon => inbox.AddOrUpdate(beacon.Address, beacon, (_, older) =>
        {
            var latest = older.Received > beacon.Received ? older : beacon;
            var combined = older.Batteries.Concat(beacon.Batteries).GroupBy(entry => entry.Key)
                .ToDictionary(group => group.Key, group => group.MaxBy(entry => entry.Value.Received).Value);
            return latest with { Batteries = combined };
        });
    }

    public event Action? Changed;
    public bool Demo { get; private set; }
    public bool DemoCaseOpen { get; private set; } = true;
    public string? Selected { get; private set; }
    public ListeningMode? Active { get; private set; }
    public ListeningMode? Pending { get; private set; }
    public DeviceChoice? Selection => Devices.FirstOrDefault(d => d.Key == Selected);
    public bool CanSwitch => Pending == null && (Demo || (aapResponding &&
        Selection is { } choice && AirPodsModel.IsAirPods5(choice.Product)));
    public bool CanFindBattery => !Demo && !paused && Selection is { } selected &&
        AirPodsModel.IsAirPods5(selected.Product);
    public bool FindingBattery => CanFindBattery && batterySearchStarted != null;

    public IReadOnlyList<DeviceChoice> Devices => Demo
        ? [new("demo", "AirPods 5 · demo device", 0x2030, 0)]
        : paired.Select(p => new DeviceChoice(Key(p.Address), $"{p.Name}  ·  {p.Address & 0xFFFF:X4}", p.Product, p.Address))
            .ToArray();

    public string Connection => Demo ? "Demo device connected" : paused ? "Windows is suspending Bluetooth" :
        Selection switch
        {
            { } item when paired.FirstOrDefault(p => p.Address == item.Address)?.Connected == true => "Bluetooth connected",
            { } => "Paired · disconnected",
            _ => discovery
        };

    public string Model => Selection is { } selected
        ? AirPodsModel.Name(selected.Product) + (metadata == null || Demo ? "" : $"  ·  {metadata}")
        : "Select a paired device";
    public string BatterySource
    {
        get
        {
            if (Demo) return "Simulated readings · toggle the case to test stale readings";
            if (Selection is not { } selected) return bluetooth.ScanStatus + ".";
            if (remembered?.Pair == selected.Address)
            {
                if (!savedPreference) return "Battery linked for this session. PodDesk could not save the remembered pair.";
                if (autoAmbiguous) return "Several reports could match. Use Find battery to choose your case.";
                return "Pair remembered. PodDesk checks for matching battery reports automatically.";
            }
            if (manualLinks.ContainsValue(selected.Address))
                return "Battery estimates linked.";
            if (battery.TryGetValue(selected.Key, out var readings) && readings.Count > 0)
                return "Last battery reports for this pair. Use Find battery if the readings stop updating.";
            if (paired.FirstOrDefault(pair => pair.Address == selected.Address)?.WindowsBattery is { } level)
                return $"Windows cached battery  {level}%  ·  whole device";
            return CanFindBattery
                ? "Use Find battery to confirm your case once and remember this pair."
                : bluetooth.ScanStatus + ".";
        }
    }
    public string ModeStatus => Pending is { } requested ? $"Waiting for {ModeName(requested)} confirmation…" :
        commandResult ?? (Active is { } current ? $"Active  ·  {ModeName(current)}" : "Active mode unknown");
    public string ModeDetails => Demo ? "Simulated confirmation · no AirPods are contacted." : link;

    public void Select(string? key)
    {
        if (key == Selected) return;
        Selected = key;
        batterySearchStarted = null;
        ResetControl();
        lastAapAttempt = default;
        Changed?.Invoke();
    }

    public void ToggleBatterySearch()
    {
        if (!CanFindBattery) return;
        batterySearchStarted = FindingBattery ? null : DateTimeOffset.Now;
        Changed?.Invoke();
    }

    public IReadOnlyList<BatteryBroadcast> BatteryBroadcasts
    {
        get
        {
            if (!CanFindBattery || batterySearchStarted is not { } started || Selection is not { } selected)
                return [];

            var cutoff = SearchCutoff(started);
            var choices = new List<(BatteryBroadcast Broadcast, int Parts, DateTimeOffset Seen)>();
            foreach (var beacon in nearby.Values)
            {
                if (beacon.Product != selected.Product || beacon.Received < cutoff ||
                    paired.Any(pair => pair.Address == beacon.Address) ||
                    (manualLinks.TryGetValue(beacon.Address, out var linked) && linked != selected.Address) ||
                    (resolved.TryGetValue(beacon.Address, out var identity) && identity.Pair != null))
                    continue;
                if (!battery.TryGetValue(Key(beacon.Address, true), out var stored)) continue;
                var fresh = stored.Where(entry => entry.Value.Received >= cutoff)
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
                if (fresh.Count == 0 || (!beacon.LidOpen && !fresh.ContainsKey(Part.Case))) continue;

                string Level(Part part) => fresh.TryGetValue(part, out var reading) ? $"≈{reading.Percent}%" : "—";
                var label = $"Left {Level(Part.Left)}  ·  Right {Level(Part.Right)}  ·  Case {Level(Part.Case)}  ·  {beacon.Address & 0xFFFFFF:X6}";
                choices.Add((new(beacon.Address, label), fresh.Count, beacon.Received));
            }
            return choices.OrderByDescending(choice => choice.Parts).ThenByDescending(choice => choice.Seen)
                .Select(choice => choice.Broadcast).ToArray();
        }
    }

    public void LinkBroadcast(ulong address)
    {
        if (batterySearchStarted is not { } started || Selection is not { } target ||
            !BatteryBroadcasts.Any(report => report.Address == address) ||
            !nearby.TryGetValue(address, out var beacon) ||
            !battery.TryGetValue(Key(address, true), out var values)) return;
        foreach (var previous in manualLinks.Where(entry => entry.Value == target.Address).Select(entry => entry.Key).ToArray())
            manualLinks.Remove(previous);
        manualLinks[address] = target.Address;
        var cutoff = SearchCutoff(started);
        var fresh = values.Where(entry => entry.Value.Received >= cutoff).ToDictionary(entry => entry.Key, entry => entry.Value);
        remembered = new(target.Address, target.Product, address, beacon.ColorCode);
        savedPreference = remembered.Save();
        followedAddress = null;
        lastFollowedCase = null;
        reconnectedAt = null;
        autoAmbiguous = false;
        ApplyFollowedBattery(remembered, beacon with { Batteries = fresh }, "Linked BLE estimate");
        batterySearchStarted = null;
        Changed?.Invoke();
    }

    private static DateTimeOffset SearchCutoff(DateTimeOffset started)
    {
        var fresh = DateTimeOffset.Now.AddSeconds(-45);
        return started > fresh ? started : fresh;
    }

    private static Dictionary<Part, Reading> WithSource(IReadOnlyDictionary<Part, Reading> readings, string source) =>
        readings.ToDictionary(entry => entry.Key, entry => entry.Value with { Source = source });

    public void SetDemo(bool enabled)
    {
        if (Demo == enabled) return;
        Demo = enabled;
        batterySearchStarted = null;
        ResetControl();
        if (enabled)
        {
            bluetooth.Pause();
            Selected = "demo";
            DemoCaseOpen = true;
            Active = ListeningMode.Transparency;
            RefreshDemo();
        }
        else
        {
            battery.Remove("demo");
            Selected = PreferredDevice();
            lastPoll = default;
        }
        Changed?.Invoke();
    }

    public void SetDemoCase(bool isOpen)
    {
        DemoCaseOpen = isOpen;
        if (Demo) RefreshDemo();
        Changed?.Invoke();
    }

    private void RefreshDemo()
    {
        lastDemoReport = DateTimeOffset.Now;
        var readings = new Dictionary<Part, Reading>
        {
            [Part.Left] = new(83, false, lastDemoReport, "Demo"),
            [Part.Right] = new(91, true, lastDemoReport, "Demo")
        };
        if (DemoCaseOpen) readings[Part.Case] = new(56, false, lastDemoReport, "Demo");
        Merge("demo", readings);
    }

    public async Task TickAsync()
    {
        if (disposed || paused || refreshing) return;
        var now = DateTimeOffset.Now;
        if (Pending != null && now - pendingSince > TimeSpan.FromSeconds(5))
        {
            commandResult = $"{ModeName(Pending.Value)} could not be confirmed.";
            Pending = null;
        }
        if (Demo)
        {
            if (now - lastDemoReport > TimeSpan.FromSeconds(4)) RefreshDemo();
            Changed?.Invoke();
            return;
        }

        refreshing = true;
        try
        {
            bluetooth.EnsureScanning();
            if (now - lastPoll > TimeSpan.FromSeconds(4))
            {
                lastPoll = now;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var devices = await WindowsBluetooth.FindPairedAsync(deadline.Token);
                if (disposed || paused || Demo) return;
                UpdatePaired(devices, DateTimeOffset.Now);
                discovery = paired.Count == 0 ? "No paired AirPods found" : "Select your AirPods";
                if (!Devices.Any(d => d.Key == Selected))
                    Select(PreferredDevice());
                UpdateControl(now);
            }

            foreach (var address in inbox.Keys)
            {
                if (!inbox.TryRemove(address, out var beacon) || !AirPodsModel.IsAirPods5(beacon.Product)) continue;
                if (beacon.LidOpen && (!nearby.TryGetValue(address, out var prior) || !prior.LidOpen ||
                    prior.LidCycle != beacon.LidCycle || beacon.Received - prior.Received > TimeSpan.FromSeconds(9)))
                    openedAt[address] = beacon.Received;
                nearby[address] = beacon;
                Merge(Key(address, true), beacon.Batteries);
                if (!resolved.TryGetValue(address, out var match) || match.Expires < now)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(700));
                    ulong? identity = null;
                    try { identity = await WindowsBluetooth.ResolvePairedBeaconAsync(address, paired, deadline.Token, beacon.AddressType); }
                    catch (Exception error) when (error is OperationCanceledException or System.Runtime.InteropServices.COMException) { }
                    if (disposed || paused || Demo) return;
                    match = (identity, now.AddSeconds(30));
                    resolved[address] = match;
                }
                if (match.Pair is { } pair && paired.Any(p => p.Address == pair && p.Product == beacon.Product))
                {
                    manualLinks.Remove(address);
                    if (followedAddress == address && remembered?.Pair != pair) followedAddress = null;
                    Merge(Key(pair), beacon.Batteries);
                    if (remembered is { } saved && saved.Pair == pair) RememberOpenCase(beacon);
                }
                else if (match.Pair != null)
                {
                    resolved[address] = (null, now.AddSeconds(30));
                }
                else if (remembered is { } saved && beacon.Product == saved.Product &&
                    beacon.ColorCode == saved.ColorCode && paired.Any(p => p.Address == saved.Pair) &&
                    (address == saved.Broadcast || (address == followedAddress && OnlyRememberedModel(saved))))
                {
                    ApplyFollowedBattery(saved, beacon, address == saved.Broadcast ? "Linked BLE estimate" : "Auto-matched BLE estimate");
                }
                else if (manualLinks.TryGetValue(address, out var linked) &&
                    paired.Any(p => p.Address == linked && p.Product == beacon.Product))
                {
                    Merge(Key(linked), WithSource(beacon.Batteries, "Linked BLE estimate"));
                }
            }

            FollowFreshCase(DateTimeOffset.Now);
            foreach (var address in nearby.Where(kv => now - kv.Value.Received > TimeSpan.FromMinutes(3))
                .Select(kv => kv.Key).ToArray())
            {
                nearby.Remove(address);
                openedAt.Remove(address);
                resolved.Remove(address);
                manualLinks.Remove(address);
                battery.Remove(Key(address, true));
            }
        }
        catch (OperationCanceledException) when (!disposed) { discovery = "Bluetooth discovery timed out · retrying"; }
        catch (Exception error) when (!disposed) { discovery = $"Bluetooth discovery retrying · {error.Message}"; }
        catch (Exception) when (disposed) { }
        finally
        {
            refreshing = false;
            if (!disposed) Changed?.Invoke();
        }
    }

    private string? PreferredDevice()
    {
        var preferred = remembered is { } saved
            ? paired.FirstOrDefault(pair => pair.Address == saved.Pair && pair.Product == saved.Product)
            : null;
        preferred ??= paired.FirstOrDefault();
        return preferred == null ? null : Key(preferred.Address);
    }

    private void UpdatePaired(IReadOnlyList<PairedPods> devices, DateTimeOffset now)
    {
        if (remembered is { } saved)
        {
            var before = paired.FirstOrDefault(pair => pair.Address == saved.Pair);
            var after = devices.FirstOrDefault(pair => pair.Address == saved.Pair);
            if (before is { Connected: false } && after is { Connected: true }) reconnectedAt = now;
            else if (after is not { Connected: true }) reconnectedAt = null;
        }
        paired = devices;
    }

    private bool OnlyRememberedModel(RememberedBattery saved) =>
        paired.Count(pair => pair.Product == saved.Product) == 1 &&
        paired.Any(pair => pair.Product == saved.Product && pair.Address == saved.Pair);

    private void FollowFreshCase(DateTimeOffset now)
    {
        autoAmbiguous = false;
        if (remembered is not { } saved || (FindingBattery && Selected == Key(saved.Pair))) return;
        if (!OnlyRememberedModel(saved))
        {
            autoAmbiguous = paired.Any(pair => pair.Address == saved.Pair);
            followedAddress = null;
            return;
        }

        // Keep a live linked address until its case reports stop.
        if (lastFollowedCase is { } recent && now - recent.Received < TimeSpan.FromSeconds(9) &&
            nearby.TryGetValue(recent.Address, out var latest) && latest.LidOpen) return;

        var reports = new List<OpenCaseReport>();
        foreach (var beacon in nearby.Values)
        {
            if (!beacon.LidOpen || beacon.Product != saved.Product ||
                beacon.Address == saved.Broadcast || beacon.Address == followedAddress ||
                !openedAt.TryGetValue(beacon.Address, out var firstSeen) ||
                now - firstSeen > TimeSpan.FromSeconds(14) ||
                (resolved.TryGetValue(beacon.Address, out var match) && match.Pair != null) ||
                (manualLinks.TryGetValue(beacon.Address, out var linked) && linked != saved.Pair) ||
                !battery.TryGetValue(Key(beacon.Address, true), out var values)) continue;

            var readings = values.Where(entry => entry.Value.Received >= firstSeen &&
                now - entry.Value.Received <= TimeSpan.FromSeconds(9))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            reports.Add(new(beacon with { Batteries = readings }, firstSeen));
        }

        var result = BatteryFollow.Find(reports, saved.ColorCode, now, reconnectedAt, lastFollowedCase);
        autoAmbiguous = result.Ambiguous;
        if (result.Match is not { } matched) return;
        followedAddress = matched.Address;
        ApplyFollowedBattery(saved, matched, "Auto-matched BLE estimate");
    }

    private void ApplyFollowedBattery(RememberedBattery saved, Beacon beacon, string source)
    {
        Merge(Key(saved.Pair), WithSource(beacon.Batteries, source));
        RememberOpenCase(beacon);
    }

    private void RememberOpenCase(Beacon beacon)
    {
        if (beacon.LidOpen && BatteryFollow.IsComplete(beacon) &&
            (lastFollowedCase == null || beacon.Received >= lastFollowedCase.Received))
            lastFollowedCase = beacon;
    }

    private void UpdateControl(DateTimeOffset now)
    {
        var selection = Selection;
        if (selection == null)
        {
            link = "Select your paired AirPods to check the Windows control link.";
            return;
        }
        if (!AirPodsModel.IsAirPods5(selection.Product))
        {
            link = "Listening-mode control is scoped to AirPods 5.";
            return;
        }
        var path = NtpodsTransport.FindInterface(selection.Address);
        if (path == null)
        {
            if (control != null) ResetControl("The driver became unavailable before the AirPods confirmed the mode.");
            link = "Mode control needs the optional NTPodsAAP driver. See README for compatibility. BLE battery stays available.";
            return;
        }
        if (paired.FirstOrDefault(p => p.Address == selection.Address)?.Connected != true)
        {
            if (control != null) ResetControl("The AirPods disconnected before confirming the mode.");
            link = "Connect the AirPods in Windows Bluetooth settings to start the control link.";
            return;
        }
        if (controlTask is { IsCompleted: false } || now - lastAapAttempt < TimeSpan.FromSeconds(8)) return;
        ResetControl();
        link = "Opening the AirPods control link…";
        lastAapAttempt = now;
        controlStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var session = new AapSession();
        control = session;
        controlTask = session.RunAsync(selection.Address, path,
            report => dispatcher.BeginInvoke(() => ApplyReport(session, selection.Key, report)),
            status => dispatcher.BeginInvoke(() =>
            {
                if (control != session || disposed) return;
                link = status;
                aapResponding = false;
                Active = null;
                settings.Clear();
                if (Pending != null) commandResult = "Connection lost before the AirPods confirmed the mode.";
                Pending = null;
                Changed?.Invoke();
            }), controlStop.Token);
    }

    private void ApplyReport(AapSession session, string key, AapReport report)
    {
        if (disposed || control != session || Selected != key) return;
        aapResponding = true;
        link = "AirPods control link responding";
        if (report.Batteries is { } readings) Merge(key, readings);
        if (report.Model != null) metadata = $"{report.Model}  ·  firmware {report.Firmware}";
        if (report.Setting is { } setting && report.Value is { } value) settings[setting] = value;
        if (report.Mode is { } mode) AcceptMode(mode);
        Changed?.Invoke();
    }

    public async Task RequestModeAsync(ListeningMode mode)
    {
        if (!CanSwitch || mode == Active) return;
        commandResult = null;
        Pending = mode;
        pendingSince = DateTimeOffset.Now;
        Changed?.Invoke();
        if (Demo)
        {
            var sent = pendingSince;
            await Task.Delay(750);
            if (Demo && !disposed && Pending == mode && pendingSince == sent)
            {
                AcceptMode(mode);
                Changed?.Invoke();
            }
            return;
        }
        control?.QueueMode(mode);
    }

    private void AcceptMode(ListeningMode mode)
    {
        if (mode != Active || mode == Pending) commandResult = null;
        Active = mode;
        if (mode == Pending) Pending = null;
    }

    private void ResetControl(string? interruption = null)
    {
        controlStop?.Cancel();
        controlStop?.Dispose();
        controlStop = null;
        control = null;
        aapResponding = false;
        settings.Clear();
        metadata = null;
        Active = null;
        commandResult = Pending != null ? interruption : null;
        Pending = null;
        link = "Checking the Windows control link…";
    }

    public void SetSuspended(bool suspended)
    {
        paused = suspended;
        if (suspended)
        {
            bluetooth.Pause();
            batterySearchStarted = null;
            followedAddress = null;
            lastFollowedCase = null;
            reconnectedAt = null;
            openedAt.Clear();
            ResetControl("Windows suspended before the AirPods confirmed the mode.");
            resolved.Clear();
        }
        else
        {
            lastPoll = default;
            lastAapAttempt = default;
            if (Demo) Active = ListeningMode.Transparency;
        }
        Changed?.Invoke();
    }

    private void Merge(string key, IReadOnlyDictionary<Part, Reading> update)
    {
        if (!battery.TryGetValue(key, out var saved)) battery[key] = saved = new();
        foreach (var (part, reading) in update)
        {
            if (saved.TryGetValue(part, out var previous))
            {
                if (reading.Received < previous.Received) continue;
                if (reading.Source.EndsWith("BLE estimate", StringComparison.Ordinal) && previous.Source == "AirPods control link" &&
                    reading.Received - previous.Received < TimeSpan.FromSeconds(part == Part.Case ? 45 : 120)) continue;
            }
            saved[part] = reading;
        }
    }

    public BatteryCard Card(Part part)
    {
        var name = part switch { Part.Left => "Left earbud", Part.Right => "Right earbud", _ => "Charging case" };
        var glyph = part switch { Part.Left => "L", Part.Right => "R", _ => "C" };
        var now = DateTimeOffset.Now;
        if (Selected == null || !battery.TryGetValue(Selected, out var parts) ||
            !parts.TryGetValue(part, out var value)) return new(name, "Unavailable", 0, "Waiting for report", "Never received", false, glyph);
        var age = now - value.Received;
        var stale = age.TotalSeconds > (part == Part.Case ? Demo ? 10 : 45 : 120);
        var percent = value.Source.EndsWith("BLE estimate", StringComparison.Ordinal) ? $"≈{value.Percent}%" : $"{value.Percent}%";
        var state = stale ? "Stale reading" : value.Charging == true ? "Charging" :
            value.Charging == false ? "Not charging" : "Charging state unknown";
        var local = value.Received.ToLocalTime();
        var time = local.Date == now.Date ? local.ToString("HH:mm:ss") : local.ToString("MMM d HH:mm:ss");
        return new(name, percent, value.Percent, state, $"{time}  ·  {value.Source}", stale, glyph);
    }

    public IReadOnlyList<AccessRow> Accessibility()
    {
        var list = new List<AccessRow>
        {
            Row("Press Speed", "Timing for double and triple presses.", 0x17,
                b => b switch { 0 => "Default", 1 => "Slower", 2 => "Slowest", _ => null }, "Slower"),
            Row("Press and Hold Duration", "How long you hold the stem to trigger an action.", 0x18,
                b => b switch { 0 => "Default", 1 => "Option 1", 2 => "Option 2", _ => null }, "Default"),
            Row("Tone Volume", "Volume of alerts and other AirPods sound effects.", 0x1F,
                b => b <= 100 ? $"{b}%" : null, "70%"),
            new("Spatial Audio Head Tracking", "Follow iPhone is applied by the iPhone during playback.",
                Demo ? "Follow iPhone on" : "Unknown", Demo ? "Demo value" : "iPhone setting")
        };
        if (Selection is { } chosen && AirPodsModel.IsWirelessCase(chosen.Product))
        {
            list.Add(Row("Volume Swipe", "Control volume by swiping along the stem.", 0x25,
                b => b switch { 1 => "On", 2 => "Off", _ => null }, "On"));
            list.Add(Row("Volume Swipe Speed", "Time allowed for a volume swipe.", 0x23,
                b => b switch { 1 => "Default", 2 => "Longer", 3 => "Longest", _ => null }, "Default"));
            list.Add(Row("Charging Case Sounds", "Related setting under Audio & Routing on iPhone.", 0x31,
                b => b switch { 1 => "On", 2 => "Off", _ => null }, "On"));
        }
        return list;
    }

    private AccessRow Row(string name, string description, byte setting, Func<byte, string?> format, string sample)
    {
        if (Demo) return new(name, description, sample, "Demo value");
        var value = settings.TryGetValue(setting, out var raw) ? format(raw) : null;
        return new(name, description, value ?? "Unknown",
            value == null ? "Use iPhone · Windows support unverified" : "Read-only · device reported");
    }

    public static string ModeName(ListeningMode mode) => mode switch
    {
        ListeningMode.Off => "Off",
        ListeningMode.Transparency => "Transparency",
        ListeningMode.AdaptiveAudio => "Adaptive Audio",
        _ => "Active Noise Cancellation"
    };

    private static string Key(ulong address, bool nearby = false) => $"{(nearby ? "ble" : "pair")}:{address:X12}";

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        ResetControl();
        bluetooth.Dispose();
        lifetime.Dispose();
    }
}
