using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace AdaptiveMedia;

public partial class MainWindow : Window
{
    private readonly BackendBridge _backend = new();
    private AppSettings _settings = SettingsStore.Load();
    private readonly string[] _startupItems;
    private readonly SemaphoreSlim _prepareGate = new(1, 1);
    private CancellationTokenSource? _previewCancellation;
    private string[] _pendingItems = [];
    private string? _pendingFormat;
    private PlaybackPlan? _preparedPlan;
    private PlaybackOptions? _preparedOptions;
    private string? _preparedSourceStamp;
    private bool _displayChanged;
    private long _generation;
    private bool _playing, _uiReady, _closed, _closeAfterPlayback;
    private readonly System.Windows.Threading.DispatcherTimer _truthRefresh = new() { Interval = TimeSpan.FromSeconds(2) };

    private void RefreshTruth()
    {
        if (_backend.Playback.CurrentTruth() is not { } truth) return;
        TruthText.Text = truth.Format();
        TruthExpander.Visibility = Visibility.Visible;
    }

    private void TruthExpander_Expanded(object sender, RoutedEventArgs e) => RefreshTruth();

    public MainWindow(string[] startupItems)
    {
        InitializeComponent();
        _backend.Playback.StatusChanged += text => Dispatcher.InvokeAsync(() =>
        {
            if (!_closed && _playing) RuntimeText.Text = text;
        });
        // The detail surface refreshes at a calm pace, and only while someone is
        // looking at it; the concise activity text stays the normal view.
        _truthRefresh.Tick += (_, _) => { if (TruthExpander.IsExpanded) RefreshTruth(); };
        _startupItems = startupItems.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
        Loaded += MainWindow_Loaded;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Closing += (_, e) => { if (_playing) { e.Cancel = true; _closeAfterPlayback = true; Hide(); } };
        Closed += (_, _) => { _closed = true; _previewCancellation?.Cancel(); SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged; };
        foreach (var box in new[] { ProfileBox, AutomaticGoalBox, AutomaticStrengthBox, AutomaticPerformanceBox,
                     EnhancedDetailBox, EnhancedMotionBox, EnhancedCleanupBox, EnhancedPerformanceBox })
            box.SelectionChanged += OptionsChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi();
        _uiReady = true;
        ShowSettingsWarning();
        await RefreshSystemAsync();
        if (_startupItems.Length > 0 && !_closed)
        {
            await SelectMediaAsync(_startupItems);
            if (_pendingItems.SequenceEqual(_startupItems)) await PlayPreparedAsync();
        }
    }

    private static string ComboValue(ComboBox box) => box.SelectedItem is ComboBoxItem item
        ? item.Tag?.ToString() ?? item.Content?.ToString() ?? "Off" : "Off";

    private static void SelectCombo(ComboBox box, string value)
    {
        foreach (var entry in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(entry.Tag?.ToString() ?? entry.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = entry; return; }
        box.SelectedIndex = 0;
    }

    private void ApplySettingsToUi()
    {
        bool ready = _uiReady;
        _uiReady = false;
        SelectCombo(ProfileBox, _settings.Profile);
        SelectCombo(AutomaticGoalBox, _settings.AutomaticGoal);
        SelectCombo(AutomaticStrengthBox, _settings.AutomaticStrength);
        SelectCombo(AutomaticPerformanceBox, _settings.EnhancementPerformance);
        SelectCombo(EnhancedDetailBox, _settings.EnhancedDetail);
        SelectCombo(EnhancedMotionBox, _settings.EnhancedMotion);
        SelectCombo(EnhancedCleanupBox, _settings.EnhancedCleanup);
        SelectCombo(EnhancedPerformanceBox, _settings.EnhancementPerformance);
        UpdatePreferenceVisibility();
        _uiReady = ready;
    }

    private async Task RefreshSystemAsync()
    {
        try
        {
            var summary = await _backend.GetSystemSummaryAsync();
            if (_closed) return;
            SystemText.Text = $"GPU: {summary.Gpu}\nDisplays: {summary.Displays}\nAudio: {summary.Audio}\nPower: {summary.Power}";
        }
        catch (Exception ex)
        {
            if (!_closed) SystemText.Text = "Hardware summary unavailable. The plan will explain conservative fallbacks.\n" + ex.Message;
        }
    }

    private PlaybackOptions CurrentOptions()
    {
        string profile = ComboValue(ProfileBox);
        string performance = profile == "Enhanced" ? ComboValue(EnhancedPerformanceBox) : ComboValue(AutomaticPerformanceBox);
        var intent = EnhancementPreferences.IntentFor(profile, ComboValue(AutomaticGoalBox), ComboValue(AutomaticStrengthBox),
            ComboValue(EnhancedDetailBox), ComboValue(EnhancedMotionBox), ComboValue(EnhancedCleanupBox), performance);
        return new(profile, "Off", "Off", false, _settings.DefaultRtxHdr, _pendingFormat,
            AutoHdrSwitch: _settings.AutoHdrSwitch, CleanupMode: "Off", Intent: intent);
    }

    private void ShowSettingsWarning()
    {
        SettingsWarningText.Text = SettingsStore.LastWarning ?? "";
        SettingsWarningText.Visibility = string.IsNullOrEmpty(SettingsWarningText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SavePlaybackDefaults()
    {
        _settings.Profile = ComboValue(ProfileBox);
        _settings.AutomaticGoal = ComboValue(AutomaticGoalBox);
        _settings.AutomaticStrength = ComboValue(AutomaticStrengthBox);
        _settings.EnhancedDetail = ComboValue(EnhancedDetailBox);
        _settings.EnhancedMotion = ComboValue(EnhancedMotionBox);
        _settings.EnhancedCleanup = ComboValue(EnhancedCleanupBox);
        _settings.EnhancementPerformance = _settings.Profile == "Enhanced"
            ? ComboValue(EnhancedPerformanceBox) : ComboValue(AutomaticPerformanceBox);
        SettingsStore.Save(_settings);
        ShowSettingsWarning();
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(async () =>
    {
        if (_closed) return;
        _displayChanged = true;
        if (_uiReady && !_playing) await RefreshPreviewAsync(debounce: true);
    });

    private string SourceStamp(PlaybackPlan plan)
    {
        var paths = _pendingItems.Concat(plan.Arguments.SkipWhile(x => x != "--").Skip(1));
        return string.Join("|", paths.Select(path =>
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    var file = new System.IO.FileInfo(path);
                    return $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
                }
                if (System.IO.Directory.Exists(path)) return $"{path}:{System.IO.Directory.GetLastWriteTimeUtc(path).Ticks}";
            }
            catch (System.IO.IOException) { return path + ":unavailable"; }
            catch (UnauthorizedAccessException) { return path + ":unavailable"; }
            return path + ":remote-or-missing";
        }));
    }

    private async void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ProfileBox)) UpdatePreferenceVisibility();
        if (_uiReady && !_playing) await RefreshPreviewAsync(debounce: true);
    }

    private void UpdatePreferenceVisibility()
    {
        string profile = ComboValue(ProfileBox);
        AutomaticChoices.Visibility = AutomaticPerformanceRow.Visibility = profile == "Automatic" ? Visibility.Visible : Visibility.Collapsed;
        EnhancedChoices.Visibility = profile == "Enhanced" ? Visibility.Visible : Visibility.Collapsed;
        ModeExplanation.Text = profile switch
        {
            "Reference" => "Preserves source character and avoids discretionary enhancement. Required format and display conversion remains available.",
            "Enhanced" => "Choose each result independently. The plan explains any supported fallback before playback.",
            "Compatibility" => "Uses the conservative fallback renderer for troublesome files or drivers.",
            _ => "DemiMedia combines your priority, strength, source, display, and supported hardware into one explainable plan.",
        };
    }

    private async Task SelectMediaAsync(IReadOnlyList<string> items, string? format = null)
    {
        if (_playing || _closed) return;
        _pendingItems = items.ToArray();
        _pendingFormat = format;
        SelectedMediaText.Text = items.Count == 1 ? items[0] : $"{items.Count} selected items\n" + string.Join("\n", items);
        RuntimeText.Text = "Playback has not started. Driver activity is checked during playback where observable.";
        await RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync(bool debounce = false)
    {
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        long generation = ++_generation;
        _preparedPlan = null;
        _preparedOptions = null;
        PlayButton.IsEnabled = false;
        if (_pendingItems.Length == 0 || _closed) { _previewCancellation = null; cancellation.Dispose(); return; }
        string[] items = _pendingItems.ToArray();
        PlaybackOptions options = CurrentOptions();
        // Settings dialogs mutate their model: capture the complete request before awaiting.
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        StatusText.Text = "Preparing plan…";
        PlanText.Text = "Checking this media and your playback choices…";
        try
        {
            if (debounce) await Task.Delay(300, cancellation.Token);
            await _prepareGate.WaitAsync(cancellation.Token);
            PlaybackPlan plan;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                plan = await _backend.GetPlaybackPlanAsync(items, options, settings: settings);
            }
            finally { _prepareGate.Release(); }
            if (cancellation.IsCancellationRequested || generation != _generation || _closed) return;
            _preparedPlan = plan;
            _preparedOptions = options;
            _preparedSourceStamp = SourceStamp(plan);
            _displayChanged = false;
            PlanText.Text = plan.Summary;
            StatusText.Text = "Plan ready — review your choices, then Play";
            PlayButton.IsEnabled = !_playing;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != _generation || _closed) return;
            PlanText.Text = "Could not prepare this media. Choose another source or adjust your choices.\n" + ex.Message;
            StatusText.Text = "Plan unavailable";
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayPreparedAsync();

    private async Task PlayPreparedAsync()
    {
        if (_playing || _closed || _preparedPlan is not { } plan) return;
        if (_preparedOptions != CurrentOptions() || _displayChanged || _preparedSourceStamp != SourceStamp(plan)) { await RefreshPreviewAsync(); return; }
        _playing = true;
        MediaActions.IsEnabled = PlaybackChoices.IsEnabled = SettingsButton.IsEnabled = PlayButton.IsEnabled = false;
        if (RememberCheck.IsChecked == true) SavePlaybackDefaults();
        try
        {
            StatusText.Text = "Playing — close the player to return";
            RuntimeText.Text = "Starting the reviewed playback plan…";
            TruthExpander.Visibility = Visibility.Visible;
            TruthText.Text = "Waiting for the player to report.";
            _truthRefresh.Start();
            int exitCode = await _backend.Playback.LaunchAsync(plan);
            StatusText.Text = exitCode == 0 ? "Playback ended — ready to play again" : $"Playback ended with an error ({exitCode})";
            RuntimeText.Text = _backend.Playback.LastReport?.Summary ?? "Playback ended.";
            RefreshTruth();
        }
        catch (Exception ex)
        {
            DiagnosticsStore.Event("error", "playback", ex.ToString());
            RuntimeText.Text = "Playback could not start.\n" + ex.Message;
            StatusText.Text = "Playback failed";
        }
        finally
        {
            _truthRefresh.Stop();
            _playing = false;
            if (_closeAfterPlayback) Close();
            if (!_closed)
            {
                MediaActions.IsEnabled = PlaybackChoices.IsEnabled = SettingsButton.IsEnabled = PlayButton.IsEnabled = true;
                Show();
                WindowState = WindowState.Normal;
                Activate();
            }
        }
    }

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var dialog = new OpenFileDialog { Title = "Choose media", Multiselect = true,
            Filter = "Media files|*.mkv;*.mp4;*.m4v;*.avi;*.mov;*.webm;*.ts;*.m2ts;*.flv;*.wmv;*.mp3;*.flac;*.m4a;*.aac;*.opus;*.wav;*.ogg|All files|*.*" };
        if (dialog.ShowDialog(this) == true) await SelectMediaAsync(dialog.FileNames);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var dialog = new OpenFolderDialog { Title = "Choose a media folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await SelectMediaAsync(new[] { dialog.FolderName });
    }

    private async void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var dialog = new UrlDialog { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.MediaUrl))
            await SelectMediaAsync(new[] { dialog.MediaUrl }, dialog.YtdlFormat);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.ResultSettings;
            SettingsStore.Save(_settings);
            ShowSettingsWarning();
            ApplySettingsToUi();
            await RefreshPreviewAsync();
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _backend.OpenDiagnostics();
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = System.IO.Path.Combine(DiagnosticsStore.DirectoryPath, "latest.json");
            if (System.IO.File.Exists(path)) { Clipboard.SetText(System.IO.File.ReadAllText(path)); StatusText.Text = "Diagnostics copied"; }
            else StatusText.Text = "Play a file first to collect diagnostics";
        }
        catch (Exception ex) { StatusText.Text = "Could not copy diagnostics: " + ex.Message; }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_playing && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_playing && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            await SelectMediaAsync(paths);
    }
}




