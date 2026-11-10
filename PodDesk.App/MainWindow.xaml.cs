using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PodDesk.Bluetooth;
using Forms = System.Windows.Forms;

namespace PodDesk;

public partial class MainWindow : Window
{
    private DeviceMonitor? monitor;
    private readonly DispatcherTimer timer;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip trayMenu;
    private DeviceChoice[] choices = [];
    private BatteryBroadcast[] batteryReports = [];
    private BatteryCard[] cards = [];
    private AccessRow[] rows = [];
    private bool rendering;
    private bool quitting;
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(54, 81, 121));

    public MainWindow()
    {
        InitializeComponent();
        monitor = new DeviceMonitor(Dispatcher);
        monitor.Changed += Render;

        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Opening += (_, _) => RenderTray();
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "PodDesk",
            ContextMenuStrip = trayMenu
        };
        tray.DoubleClick += (_, _) => ShowDashboard();
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            async (_, _) => { if (monitor != null) await monitor.TickAsync(); }, Dispatcher);
        SystemEvents.PowerModeChanged += PowerChanged;
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--demo")) DemoToggle.IsChecked = true;
            Render();
            if (monitor != null) await monitor.TickAsync();
        };
    }

    private void TitleBarPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleWindowSize();
        else DragMove();
    }

    private void TitleBarMenuRequested(object sender, MouseButtonEventArgs e) =>
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));

    private void MinimizeClicked(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeClicked(object sender, RoutedEventArgs e) => ToggleWindowSize();

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowSize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowSizeButton != null)
            WindowSizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Render()
    {
        if (monitor == null || quitting) return;
        rendering = true;
        try
        {
            var latest = monitor.Devices.ToArray();
            if (!latest.SequenceEqual(choices))
            {
                choices = latest;
                Devices.ItemsSource = choices;
            }
            if (!Equals(Devices.SelectedValue, monitor.Selected)) Devices.SelectedValue = monitor.Selected;
            ConnectionLabel.Text = monitor.Connection;
            ModelLabel.Text = monitor.Model;
            BatterySource.Text = monitor.BatterySource;
            RenderBatterySearch();
            var nextStatus = monitor.ModeStatus;
            if (ModeStatus.Text != nextStatus)
            {
                ModeStatus.Text = nextStatus;
                UIElementAutomationPeer.FromElement(ModeStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            ModeDetails.Text = monitor.ModeDetails;
            UpdateMode(OffButton, ListeningMode.Off);
            UpdateMode(TransparencyButton, ListeningMode.Transparency);
            UpdateMode(AdaptiveButton, ListeningMode.AdaptiveAudio);
            UpdateMode(AncButton, ListeningMode.NoiseCancellation);
            var newCards = Enum.GetValues<Part>().Select(monitor.Card).ToArray();
            if (!newCards.SequenceEqual(cards)) BatteryCards.ItemsSource = cards = newCards;
            var newRows = monitor.Accessibility().ToArray();
            if (!newRows.SequenceEqual(rows)) AccessibilityRows.ItemsSource = rows = newRows;
            DemoCase.Visibility = monitor.Demo ? Visibility.Visible : Visibility.Collapsed;
            DemoCase.IsChecked = monitor.DemoCaseOpen;
            Footer.Text = monitor.Demo ? "DEMO  ·  All readings and mode changes are simulated." :
                "Closing the window keeps PodDesk in the tray.";
            var trayText = (monitor.Demo ? "PodDesk Demo" : "PodDesk") + " · " +
                string.Join("  ", cards.Select(card => $"{card.Glyph}: {card.Value}" + (card.Stale ? " (stale)" : "")));
            tray.Text = trayText.Length > 63 ? trayText[..63] : trayText;
        }
        finally { rendering = false; }
    }

    private void UpdateMode(Button button, ListeningMode mode)
    {
        if (monitor == null) return;
        button.IsEnabled = monitor.CanSwitch && monitor.Active != mode;
        if (monitor.Active == mode || monitor.Pending == mode) button.Background = ActiveBrush;
        else button.ClearValue(BackgroundProperty);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, monitor.Active == mode
            ? "Active mode" : monitor.Pending == mode ? "Awaiting AirPods confirmation" : monitor.ModeDetails);
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && Devices.SelectedValue is string key) monitor?.Select(key);
    }

    private void LinkClicked(object sender, RoutedEventArgs e)
    {
        if (BatteryReport.SelectedValue is ulong address) monitor?.LinkBroadcast(address);
    }

    private void FindBatteryClicked(object sender, RoutedEventArgs e) => monitor?.ToggleBatterySearch();

    private void BatteryReportChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && UseBatteryReport != null)
            UseBatteryReport.IsEnabled = BatteryReport.SelectedValue is ulong;
    }

    private void RenderBatterySearch()
    {
        if (monitor == null) return;
        FindBattery.Visibility = monitor.CanFindBattery ? Visibility.Visible : Visibility.Collapsed;
        FindBattery.Content = monitor.FindingBattery ? "_Cancel search" : "_Find battery";
        BatterySearchPanel.Visibility = monitor.FindingBattery ? Visibility.Visible : Visibility.Collapsed;

        var reports = monitor.BatteryBroadcasts.ToArray();
        if (!BatteryReport.IsDropDownOpen && !reports.SequenceEqual(batteryReports))
        {
            var previouslySelected = BatteryReport.SelectedValue;
            BatteryReport.ItemsSource = batteryReports = reports;
            if (previouslySelected is ulong address && reports.Any(report => report.Address == address))
                BatteryReport.SelectedValue = address;
            else if (reports.Length == 1)
                BatteryReport.SelectedIndex = 0;
        }
        BatteryReport.IsEnabled = reports.Length > 0;
        UseBatteryReport.IsEnabled = BatteryReport.SelectedValue is ulong selected &&
            reports.Any(report => report.Address == selected);
        BatterySearchStatus.Text = reports.Length switch
        {
            0 => "Waiting for a fresh battery report…",
            1 => "Check the reading, then choose Use and remember.",
            _ => "Choose the report from your case, then choose Use and remember."
        };
    }

    private async void ModeClicked(object sender, RoutedEventArgs e)
    {
        if (monitor != null && sender is Button { Tag: string tag } && Enum.TryParse<ListeningMode>(tag, out var mode))
            await monitor.RequestModeAsync(mode);
    }

    private void DemoChanged(object sender, RoutedEventArgs e)
    {
        if (!rendering && sender is CheckBox checkBox) monitor?.SetDemo(checkBox.IsChecked == true);
    }

    private void DemoCaseChanged(object sender, RoutedEventArgs e)
    {
        if (!rendering && sender is CheckBox checkBox) monitor?.SetDemoCase(checkBox.IsChecked == true);
    }

    private void RenderTray()
    {
        if (monitor == null) return;
        var oldItems = trayMenu.Items.Cast<Forms.ToolStripItem>().ToArray();
        trayMenu.Items.Clear();
        foreach (var item in oldItems) item.Dispose();
        trayMenu.Items.Add("Open &PodDesk", null, (_, _) => ShowDashboard());
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add(new Forms.ToolStripMenuItem(monitor.Demo ? "Demo · simulated" :
            monitor.Selection?.Label ?? monitor.Connection) { Enabled = false });
        foreach (var card in cards)
            trayMenu.Items.Add(new Forms.ToolStripMenuItem($"{card.Name}   {card.Value}" +
                (card.Stale ? "  ·  stale" : card.Detail == "Charging" ? "  ·  charging" : "")) { Enabled = false });
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        var modes = new Forms.ToolStripMenuItem("&Listening modes");
        modes.DropDownItems.Add(new Forms.ToolStripMenuItem(monitor.ModeStatus) { Enabled = false });
        foreach (var mode in new[] { ListeningMode.Off, ListeningMode.Transparency,
                     ListeningMode.AdaptiveAudio, ListeningMode.NoiseCancellation })
        {
            var item = new Forms.ToolStripMenuItem(DeviceMonitor.ModeName(mode))
            {
                Checked = monitor.Active == mode, Enabled = monitor.CanSwitch && monitor.Active != mode,
                ToolTipText = monitor.ModeDetails
            };
            item.Click += async (_, _) => await monitor.RequestModeAsync(mode);
            modes.DropDownItems.Add(item);
        }
        trayMenu.Items.Add(modes);
        trayMenu.Items.Add("E&xit", null, (_, _) => Quit());
    }

    private void ShowDashboard()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            Dispatcher.BeginInvoke(() => monitor?.SetSuspended(e.Mode == PowerModes.Suspend));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!quitting && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void Quit()
    {
        quitting = true;
        timer.Stop();
        SystemEvents.PowerModeChanged -= PowerChanged;
        monitor?.Dispose();
        monitor = null;
        tray.Visible = false;
        tray.Dispose();
        trayMenu.Dispose();
        Application.Current.Shutdown();
    }

    private void ExitRequested(object sender, ExecutedRoutedEventArgs e) => Quit();
}
