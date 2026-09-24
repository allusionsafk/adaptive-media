using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

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
    private WatchLaterOffer? _resumeOffer;
    private PlaybackOptions? _preparedOptions;
    private string? _preparedSourceStamp;
    private bool _displayChanged;
    private long _generation;
    private bool _playing, _uiReady, _closed, _closeAfterPlayback;
    // The truth chain describes the last playback; it is shown only while the media on
    // screen is the media that playback was of.
    private bool _truthIsCurrent;
    private PlaybackPlan? _launchedPlan;
    private readonly System.Windows.Threading.DispatcherTimer _truthRefresh = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow(string[] startupItems)
    {
        InitializeComponent();
        WindowTheme.UseDarkFrame(this);
        _backend.Playback.StatusChanged += text => Dispatcher.InvokeAsync(() =>
        {
            if (!_closed && _playing) RuntimeText.Text = PlaybackPresentation.ActivityText(text, _launchedPlan?.Summary ?? "");
        });
        SizeChanged += (_, _) => UpdatePageLayout();
        AutomaticChoices.SizeChanged += (_, e) => LayoutGoalChoice(e.NewSize.Width);
        // Health and the detail surface refresh at a calm pace while playing; the
        // details are only rebuilt while someone is looking at them.
        _truthRefresh.Tick += (_, _) => RefreshPlaybackState();
        _startupItems = startupItems.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Closing += (_, e) => { if (_playing) { e.Cancel = true; _closeAfterPlayback = true; Hide(); } };
        Closed += (_, _) => { _closed = true; _previewCancellation?.Cancel(); SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged; };
        foreach (var box in new Selector[] { ProfileBox, AutomaticGoalBox, AutomaticStrengthBox, AutomaticPerformanceBox,
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
            if (_pendingItems.SequenceEqual(_startupItems) && _resumeOffer is null) await PlayPreparedAsync(false);
        }
    }

    // Choice values are the items' Tags (the persisted settings vocabulary), so the
    // visible wording can change without changing what is stored.
    private static string ComboValue(Selector box) => box.SelectedItem is ListBoxItem item
        ? item.Tag?.ToString() ?? item.Content?.ToString() ?? "Off" : "Off";

    private static void SelectCombo(Selector box, string value)
    {
        foreach (var entry in box.Items.OfType<ListBoxItem>())
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
        ShowGoalExplanation();
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
        SettingsWarning.Visibility = string.IsNullOrEmpty(SettingsWarningText.Text) ? Visibility.Collapsed : Visibility.Visible;
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

    private async void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        // A choice group always has an answer: Ctrl+click must not leave it empty.
        if (sender is Selector box && box.SelectedItem is null && e.RemovedItems.Count > 0)
        {
            box.SelectedItem = e.RemovedItems[0];
            return;
        }
        if (ReferenceEquals(sender, ProfileBox)) UpdatePreferenceVisibility();
        if (ReferenceEquals(sender, AutomaticGoalBox)) ShowGoalExplanation();
        if (_uiReady && !_playing) await RefreshPreviewAsync(debounce: true);
    }

    /// <summary>The chosen result is explained once, beside the list, rather than on
    /// every option.</summary>
    private void ShowGoalExplanation() => GoalExplanation.Text = AutomaticGoalBox.SelectedItem is ListBoxItem item
        ? System.Windows.Automation.AutomationProperties.GetHelpText(item) : "";

    /// <summary>Side by side when there is room; the explanation and strength move
    /// under the list on a narrow page.</summary>
    private void LayoutGoalChoice(double width)
    {
        bool stacked = width < 680;
        Grid.SetColumn(GoalDetail, stacked ? 0 : 1);
        Grid.SetRow(GoalDetail, stacked ? 1 : 0);
        GoalColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(380);
        GoalDetail.Margin = stacked ? new Thickness(0, 24, 0, 0) : new Thickness(8, 8, 0, 0);
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
        _truthIsCurrent = false;
        ShowSelectedMedia();
        RuntimeText.Text = "Playback has not started. Driver activity is checked during playback where observable.";
        await RefreshPreviewAsync();
    }

    /// <summary>Switches the hero from the open prompt to the chosen media. The open
    /// actions stay available but step back to quiet tools.</summary>
    private void ShowSelectedMedia()
    {
        Page.VerticalAlignment = VerticalAlignment.Top;
        DetailsToggle.Visibility = Visibility.Visible;
        HeroEyebrow.Text = "Ready to play";
        HeroEyebrow.Visibility = Visibility.Visible;
        HeroTitle.Text = PlaybackPresentation.MediaTitle(_pendingItems);
        HeroTitle.FontSize = 38;
        HeroBody.Text = "Checking this media…";
        SelectedMediaText.Text = _pendingItems.Length == 1 ? _pendingItems[0] : string.Join("   ·   ", _pendingItems);
        SelectedMediaText.ToolTip = string.Join("\n", _pendingItems);
        SelectedMediaText.Visibility = Visibility.Visible;
        PlayRow.Visibility = Visibility.Visible;
        var tool = (Style)FindResource("Dm.GhostButton");
        OpenFileButton.Style = OpenFolderButton.Style = OpenUrlButton.Style = tool;
        MediaActions.Margin = new Thickness(0, 22, 0, 0);
        DropZone.Visibility = Visibility.Collapsed;
        DropColumn.Width = new GridLength(0);
        PlaybackChoices.Visibility = PlanSection.Visibility = Visibility.Visible;
        AttentionCard.Visibility = Visibility.Collapsed;
        UpdatePageLayout();
        if (DetailsDrawer.Visibility == Visibility.Visible) RenderDetails();
    }

    /// <summary>Docks the details beside the page when both fit; on a narrow window it
    /// overlays the page from the right instead of crushing the choices.</summary>
    private void UpdatePageLayout()
    {
        bool open = DetailsDrawer.Visibility == Visibility.Visible;
        bool overlay = open && ActualWidth > 0 && ActualWidth < 1040;
        Grid.SetColumn(DetailsDrawer, overlay ? 0 : 1);
        DetailsDrawer.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DetailsDrawer.Effect = overlay ? new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, Opacity = 0.35, BlurRadius = 30, ShadowDepth = 0 } : null;
        double side = open && !overlay ? 40 : 56;
        Page.Margin = new Thickness(side, _pendingItems.Length == 0 ? 40 : 44, side, 72);
    }


    private async Task RefreshPreviewAsync(bool debounce = false)
    {
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        long generation = ++_generation;
        _preparedPlan = null;
        _preparedOptions = null;
        _resumeOffer = null;
        StartBeginningButton.Visibility = Visibility.Collapsed;
        PlayButton.Content = "_Play";
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, "Play");
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
            UpdateResumeOffer(plan);
            _displayChanged = false;
            var (source, planLines) = PlaybackPresentation.SplitSummary(plan.Summary);
            HeroBody.Text = PlaybackPresentation.SourceLine(plan.Source, source);
            PlanText.Text = planLines.Count > 0 ? string.Join("\n", planLines) : plan.Summary;
            StatusText.Text = _resumeOffer is { } offer
                ? "Saved near " + PlaybackRecoveryText.Position(offer.PositionSeconds) + " — choose where to start"
                : "Plan ready — review your choices, then Play";
            PlayButton.IsEnabled = !_playing;
            if (DetailsDrawer.Visibility == Visibility.Visible) RenderDetails();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != _generation || _closed) return;
            HeroBody.Text = "This media could not be prepared.";
            PlanText.Text = "Could not prepare this media. Choose another source or adjust your choices.\n" + ex.Message;
            StatusText.Text = "Plan unavailable";
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void UpdateResumeOffer(PlaybackPlan plan)
    {
        string owned = WatchLaterResume.OwnedDirectory(SettingsStore.DirectoryPath);
        string stableExecutable = plan.Executable;
        if (plan.Renderer.StartsWith("Native Dolby Vision", StringComparison.Ordinal))
            try { stableExecutable = PlaybackService.ResolveMpv(); }
            catch (FileNotFoundException) { }
        _resumeOffer = WatchLaterResume.Find(plan, owned, WatchLaterResume.LegacyDirectory(stableExecutable));
        StartBeginningButton.Visibility = _resumeOffer is null ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.Content = _resumeOffer is null ? "_Play" : "_Resume";
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, _resumeOffer is null ? "Play" : "Resume");
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayPreparedAsync(_resumeOffer is not null);
    private async void StartBeginning_Click(object sender, RoutedEventArgs e) => await PlayPreparedAsync(false);

    private async Task PlayPreparedAsync(bool resume)
    {
        if (_playing || _closed || _preparedPlan is not { } plan) return;
        if (_preparedOptions != CurrentOptions() || _displayChanged || _preparedSourceStamp != SourceStamp(plan)) { await RefreshPreviewAsync(); return; }
        var expectedOffer = _resumeOffer;
        UpdateResumeOffer(plan);
        if (expectedOffer != _resumeOffer) { await RefreshPreviewAsync(); return; }
        plan = WatchLaterResume.ApplyChoice(plan, _resumeOffer, resume,
            WatchLaterResume.OwnedDirectory(SettingsStore.DirectoryPath));
        _playing = true;
        _truthIsCurrent = true;
        _launchedPlan = plan;
        MediaActions.IsEnabled = PlaybackChoices.IsEnabled = SettingsButton.IsEnabled = PlayButton.IsEnabled = StartBeginningButton.IsEnabled = false;
        if (RememberCheck.IsChecked == true) SavePlaybackDefaults();
        try
        {
            // The picture is in the player window; a disabled Play here would only be noise.
            PlayButton.Visibility = Visibility.Collapsed;
            StatusText.Margin = new Thickness(0);
            HeroEyebrow.Text = "Now playing";
            StatusText.Text = "Playing — close the player to return";
            RuntimeText.Text = "Starting the reviewed playback plan…";
            AttentionCard.Visibility = Visibility.Collapsed;
            _truthRefresh.Start();
            RefreshPlaybackState();
            int exitCode = await _backend.Playback.LaunchAsync(plan);
            StatusText.Text = exitCode == 0 ? "Playback ended — ready to play again" : $"Playback ended with an error ({exitCode})";
            RuntimeText.Text = _backend.Playback.LastReport?.Summary is { } report
                ? PlaybackPresentation.ActivityText(report, plan.Summary) : "Playback ended.";
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
                HeroEyebrow.Text = "Ready to play";
                PlayButton.Visibility = Visibility.Visible;
                StatusText.Margin = new Thickness(18, 0, 0, 0);
                RefreshPlaybackState();
                if (_preparedPlan is { } prepared)
                {
                    UpdateResumeOffer(prepared);
                    if (_resumeOffer is { } saved)
                        StatusText.Text = "Saved near " + PlaybackRecoveryText.Position(saved.PositionSeconds) + " — choose where to start";
                }
                MediaActions.IsEnabled = PlaybackChoices.IsEnabled = SettingsButton.IsEnabled = PlayButton.IsEnabled = StartBeginningButton.IsEnabled = true;
                Show();
                WindowState = WindowState.Normal;
                Activate();
            }
        }
    }

    /// <summary>Quiet while healthy: the attention card appears only when the player
    /// reports a condition that persists or fails, and it names that condition.</summary>
    private void RefreshPlaybackState()
    {
        var health = _truthIsCurrent ? _backend.Playback.LastPlaybackHealth : null;
        if (health is not null && PlaybackPresentation.NeedsAttention(health.State))
        {
            string explanation = health.Explanation.Trim();
            AttentionText.Text = HealthText.Describe(health.State) + (explanation.Length == 0 ? "." : ". " + explanation);
            AttentionCard.Visibility = Visibility.Visible;
        }
        else AttentionCard.Visibility = Visibility.Collapsed;
        if (DetailsDrawer.Visibility == Visibility.Visible) RenderDetails();
    }

    // ---- Playback details drawer -------------------------------------------------

    private void DetailsToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool open = DetailsToggle.IsChecked == true;
        DetailsDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageLayout();
        if (!open) return;
        RenderDetails();
        // Feline signature (the second of two): the panel arrives with a brief tail-like
        // settle rather than a flat slide. Skipped when Windows animation effects are off.
        if (!SystemParameters.ClientAreaAnimation) return;
        var slide = new TranslateTransform(28, 0);
        DetailsDrawer.RenderTransform = slide;
        var settle = new System.Windows.Media.Animation.BackEase { Amplitude = 0.35, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        slide.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = settle });
        DetailsDrawer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void InspectDetails_Click(object sender, RoutedEventArgs e) => DetailsToggle.IsChecked = true;

    private void CloseDetails_Click(object sender, RoutedEventArgs e)
    {
        DetailsToggle.IsChecked = false;
        DetailsToggle.Focus();
    }

    /// <summary>Rebuilds the details from the structured truth chain when this media
    /// has played, otherwise from the prepared plan. Nothing is shown as observed
    /// until the player has reported it.</summary>
    private void RenderDetails()
    {
        DetailsHost.Children.Clear();
        if ((_truthIsCurrent ? _backend.Playback.CurrentTruth() : null) is { } truth)
        {
            DetailsSubtitle.Text = _playing
                ? "Live from the player, refreshed every two seconds."
                : "From the last playback of this media.";
            foreach (var section in truth.Sections)
            {
                AddCaps(section.Title.ToUpperInvariant());
                var rows = PlaybackPresentation.Rows(section, truth.Delivery);
                for (int i = 0; i < rows.Count; i++)
                    AddTruthRow(rows[i], first: i == 0, health: ReferenceEquals(section, truth.Health) && i == 0);
            }
            if (truth.History.Count > 0)
            {
                AddCaps("EARLIER ATTEMPTS");
                foreach (var attempt in truth.History)
                {
                    AddTruthRow(new(attempt.Title), first: false, health: false, strong: true);
                    foreach (string line in attempt.Lines) AddTruthRow(new(line), first: true, health: false);
                }
            }
            return;
        }
        if (_preparedPlan is { } plan)
        {
            DetailsSubtitle.Text = "Before playback: the source and the plan. Observed values appear once the player reports them.";
            AddCaps("SOURCE");
            bool first = true;
            foreach (var fact in PlaybackPresentation.SourceFacts(plan.Source)) { AddFact(fact, first); first = false; }
            AddCaps("PLAN");
            var (_, lines) = PlaybackPresentation.SplitSummary(plan.Summary);
            for (int i = 0; i < lines.Count; i++) AddTruthRow(new(lines[i]), first: i == 0, health: false);
            AddCaps("OBSERVED");
            AddTruthRow(new("Nothing observed yet: playback has not started."), first: true, health: false, muted: true);
            return;
        }
        DetailsSubtitle.Text = _pendingItems.Length == 0
            ? "Choose media to see its source and playback plan."
            : "Preparing the plan…";
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void AddCaps(string text) => DetailsHost.Children.Add(new TextBlock
    {
        Text = text, Style = (Style)FindResource("Dm.Caps"), Margin = new Thickness(0, 24, 0, 6),
    });

    private void AddFact(SourceFact fact, bool first)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = fact.Label, FontSize = 12, Foreground = Res("Dm.TextSecondary"), Margin = new Thickness(0, 0, 16, 0) });
        var value = new TextBlock
        {
            Text = fact.Value, FontSize = fact.Machine ? 11.5 : 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right,
            Foreground = Res("Dm.Text"), FontFamily = (FontFamily)FindResource(fact.Machine ? "Dm.FontMono" : "Dm.FontUi"),
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        DetailsHost.Children.Add(Row(grid, first));
    }

    private void AddTruthRow(TruthRow row, bool first, bool health, bool strong = false, bool muted = false)
    {
        var label = new TextBlock
        {
            Text = row.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 18, Foreground = Res(muted ? "Dm.TextMuted" : "Dm.Text"),
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        };
        if (health)
        {
            // Health speaks only when it has something to say: healthy is a quiet
            // semantic green, anything needing attention is warm.
            var state = _backend.Playback.LastPlaybackHealth?.State;
            if (state is SustainedPlaybackHealth.Healthy) label.Foreground = Res("Dm.HealthyText");
            else if (state is { } s && PlaybackPresentation.NeedsAttention(s)) label.Foreground = Res("Dm.AttentionTitle");
        }
        if (row.State is not { } verdict)
        {
            DetailsHost.Children.Add(Row(label, first));
            return;
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(label);
        var (surface, ink) = verdict switch
        {
            DeliveryState.Verified => ("Dm.HealthySurface", "Dm.HealthyText"),
            DeliveryState.FellBack => ("Dm.AttentionSurface", "Dm.AttentionTitle"),
            DeliveryState.Unverified => ("Dm.Pill", "Dm.Text"),
            _ => ("Dm.Pill", "Dm.TextSecondary"),
        };
        var pill = new Border
        {
            Style = (Style)FindResource("Dm.PillBox"), Background = Res(surface), Margin = new Thickness(12, 0, 0, 0),
            Child = new TextBlock { Text = PlaybackPresentation.StateLabel(verdict), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Res(ink) },
        };
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);
        if (row.Evidence.Length > 0)
        {
            var evidence = new TextBlock
            {
                Text = row.Evidence, Style = (Style)FindResource("Dm.Meta"), Margin = new Thickness(0, 4, 0, 0),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 16,
            };
            Grid.SetRow(evidence, 1);
            Grid.SetColumnSpan(evidence, 2);
            grid.Children.Add(evidence);
        }
        DetailsHost.Children.Add(Row(grid, first));
    }

    private Border Row(UIElement content, bool first) => new()
    {
        BorderBrush = Res("Dm.Separator"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
        Padding = new Thickness(0, 8, 0, 8), Child = content,
    };

    // ---- Opening media --------------------------------------------------------------

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

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DetailsDrawer.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseDetails_Click(sender, e);
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control || _playing) return;
        if (e.Key == Key.O) { e.Handled = true; OpenFiles_Click(sender, e); }
        else if (e.Key == Key.U) { e.Handled = true; OpenUrl_Click(sender, e); }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _backend.OpenDiagnostics();
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = System.IO.Path.Combine(DiagnosticsStore.DirectoryPath, "latest.json");
            if (System.IO.File.Exists(path)) { Clipboard.SetText(System.IO.File.ReadAllText(path)); DiagnosticsStatusText.Text = "Diagnostics copied"; }
            else DiagnosticsStatusText.Text = "Play a file first to collect diagnostics";
        }
        catch (Exception ex) { DiagnosticsStatusText.Text = "Could not copy diagnostics: " + ex.Message; }
    }

    // ---- Drag and drop ---------------------------------------------------------------

    private void SetDropActive(bool active)
    {
        DropSurface.Background = Res(active ? "Dm.SurfaceDropActive" : "Dm.SurfaceDrop");
        DropOutline.Stroke = active ? Res("Dm.BorderSelected") : Brushes.Transparent;
        DropTitle.Text = active ? "Release to open" : "Drop a video here";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_playing && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropActive(e.Effects == DragDropEffects.Copy);
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => SetDropActive(false);

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        SetDropActive(false);
        if (!_playing && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            await SelectMediaAsync(paths);
    }
}
