using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;

namespace AutomatiskSportTV.App;

public partial class TvOverlayWindow : Window
{
    public enum OverlaySection
    {
        None,
        OsdOnly,
        Guide,
        Ranking,
        Services,
        Help
    }

    private readonly DispatcherTimer _osdHideTimer;
    private readonly DispatcherTimer _clockAndToastTimer;
    private OverlaySection _activeSection = OverlaySection.None;
    private bool _pointerInsideOsd;

    private RankingProfile _editingRanking = RankingProfile.CreateDefault();
    private List<ServiceConfig> _services = [];
    private IReadOnlyList<RankedSportEvent> _liveEvents = [];
    private IReadOnlyList<RankedSportEvent> _upcomingEvents = [];
    private RankedSportEvent? _currentEvent;
    private PendingSwitchOffer? _pendingOffer;
    private string _selectedSportFilter = "Alla";
    private readonly List<Button> _guideFocusButtons = [];
    private int _guideSelectedIndex;
    private bool _isMuted;
    private bool _isTheaterMode = true;

    public TvOverlayWindow()
    {
        InitializeComponent();

        _osdHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _osdHideTimer.Tick += (_, _) =>
        {
            if (!_pointerInsideOsd && _activeSection == OverlaySection.OsdOnly)
            {
                CloseAllPanels();
            }
        };

        _clockAndToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockAndToastTimer.Tick += (_, _) =>
        {
            OsdClockText.Text = DateTime.Now.ToString("HH:mm");
            if (_pendingOffer is not null)
            {
                var remaining = Math.Max(0, (int)Math.Ceiling((_pendingOffer.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
                ToastCountdownText.Text = $"Byter automatiskt om {remaining} s";
                if (remaining <= 0)
                {
                    AcceptPendingSwitchRequested?.Invoke();
                }
            }
        };
        _clockAndToastTimer.Start();
    }

    public OverlaySection ActiveSection => _activeSection;
    public bool HasVisibleContent => _activeSection != OverlaySection.None || _pendingOffer is not null;

    public event Action? NextChannelRequested;
    public event Action? PrevChannelRequested;
    public event Action? SkipChannelRequested;
    public event Action? PinChannelRequested;
    public event Action? ToggleMuteRequested;
    public event Action? ToggleTheaterModeRequested;
    public event Action? ToggleFullscreenRequested;
    public event Action<string>? SelectEventRequested;
    public event Action? ScanAllRequested;
    public event Action<StreamingServiceId>? ScanSingleServiceRequested;
    public event Action<StreamingServiceId>? OpenServiceLoginRequested;
    public event Action<StreamingServiceId>? ClearServiceSessionRequested;
    public event Action<StreamingServiceId, int>? ServiceVolumeChanged;
    public event Action? CheckUpdatesRequested;
    public event Action? InstallUpdateRequested;
    public event Action<RankingProfile>? RankingSaved;
    public event Action<List<ServiceConfig>>? ServicesChanged;
    public event Action? AcceptPendingSwitchRequested;
    public event Action? DismissPendingSwitchRequested;
    public event Action? OverlayClosed;

    public void SyncState(
        RankedSportEvent? currentEvent,
        bool isPinned,
        bool isMuted,
        bool isTheaterMode,
        PlaybackTelemetry? telemetry,
        IReadOnlyList<RankedSportEvent> liveEvents,
        IReadOnlyList<RankedSportEvent> upcomingEvents,
        RankingProfile rankingProfile,
        List<ServiceConfig> services,
        PendingSwitchOffer? pendingOffer,
        string? watchdogNotice = null)
    {
        _currentEvent = currentEvent;
        _isMuted = isMuted;
        _isTheaterMode = isTheaterMode;
        _liveEvents = liveEvents;
        _upcomingEvents = upcomingEvents;
        _services = services;
        _pendingOffer = pendingOffer;

        UpdateOsd(currentEvent, isPinned, telemetry, watchdogNotice);
        UpdateToast(pendingOffer);

        if (_activeSection == OverlaySection.Guide)
        {
            RenderGuideLists(restoreFocus: false);
        }
        else if (_activeSection == OverlaySection.Services)
        {
            RenderServicesPanel();
        }
    }

    public void ShowSection(OverlaySection section, RankingProfile currentRanking, bool autoHideOsd = true)
    {
        _activeSection = section;

        OsdBarPanel.Visibility = Visibility.Collapsed;
        GuidePanel.Visibility = Visibility.Collapsed;
        RankingPanel.Visibility = Visibility.Collapsed;
        ServicesPanel.Visibility = Visibility.Collapsed;
        HelpPanel.Visibility = Visibility.Collapsed;
        ModalBackdrop.Visibility = Visibility.Collapsed;

        switch (section)
        {
            case OverlaySection.None:
                _osdHideTimer.Stop();
                UpdateVisibility();
                OverlayClosed?.Invoke();
                return;

            case OverlaySection.OsdOnly:
                OsdBarPanel.Visibility = Visibility.Visible;
                _osdHideTimer.Stop();
                if (autoHideOsd)
                {
                    _osdHideTimer.Start();
                }
                break;

            case OverlaySection.Guide:
                _osdHideTimer.Stop();
                ModalBackdrop.Visibility = Visibility.Visible;
                GuidePanel.Visibility = Visibility.Visible;
                RenderGuideLists(restoreFocus: true);
                break;

            case OverlaySection.Ranking:
                _osdHideTimer.Stop();
                ModalBackdrop.Visibility = Visibility.Visible;
                RankingPanel.Visibility = Visibility.Visible;
                LoadRankingIntoEditor(currentRanking);
                break;

            case OverlaySection.Services:
                _osdHideTimer.Stop();
                ModalBackdrop.Visibility = Visibility.Visible;
                ServicesPanel.Visibility = Visibility.Visible;
                RenderServicesPanel();
                break;

            case OverlaySection.Help:
                _osdHideTimer.Stop();
                ModalBackdrop.Visibility = Visibility.Visible;
                HelpPanel.Visibility = Visibility.Visible;
                break;
        }

        UpdateVisibility();
        if (section is OverlaySection.Guide or OverlaySection.Ranking or OverlaySection.Services or OverlaySection.Help)
        {
            Activate();
        }
    }

    public void SyncUpdateStatus(Version currentVersion, string statusSummary, AppReleaseInfo? stagedRelease)
    {
        UpdateStatusText.Text = $"Version v{currentVersion} • {statusSummary}";
        if (stagedRelease is not null)
        {
            OsdUpdateButton.Content = $"⬆ Uppdatera till {stagedRelease.TagName}";
            OsdUpdateButton.Visibility = Visibility.Visible;
            RankingInstallUpdateButton.Content = $"⬆ Installera {stagedRelease.TagName} & starta om";
            RankingInstallUpdateButton.Visibility = Visibility.Visible;
        }
        else
        {
            OsdUpdateButton.Visibility = Visibility.Collapsed;
            RankingInstallUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    public void ShowDiagnosticEvents(ServiceConfig service, ServiceScanResult result)
    {
        ServiceDiagnosticTitle.Text = $"{service.DisplayName} — {result.Events.Count} träffar ({result.AuthState})";
        ServiceDiagnosticList.Children.Clear();

        if (result.Events.Count == 0)
        {
            ServiceDiagnosticList.Children.Add(new TextBlock
            {
                Text = $"Inga direktsända eller kommande matcher hittades på {service.SportsHubUrl}.\nStatus: {result.AuthState}\nSidtitel: {result.PageTitle}",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            });
            return;
        }

        foreach (var evt in result.Events)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 34, 53)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"[{evt.Status}] {evt.Title}",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"{evt.Sport} • {evt.League} • {UrlSecurityGuard.SanitizeForDisplay(evt.WatchUrl)}",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
            card.Child = sp;
            ServiceDiagnosticList.Children.Add(card);
        }
    }

    public void CloseAllPanels() => ShowSection(OverlaySection.None, _editingRanking);

    private void UpdateVisibility()
    {
        if (HasVisibleContent)
        {
            if (!IsVisible)
            {
                Show();
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    private void UpdateOsd(RankedSportEvent? current, bool isPinned, PlaybackTelemetry? telemetry, string? watchdogNotice)
    {
        OsdMuteButton.Content = _isMuted ? "🔇 Ljud på (M)" : "🔊 Ljud av (M)";
        OsdTheaterButton.Content = _isTheaterMode ? "🖥 Sajtvy (T)" : "🎬 Helbild (T)";

        if (current is null)
        {
            OsdServiceText.Text = "STANDBY";
            OsdTitleText.Text = "Ingen aktiv sportsändning";
            OsdSportLeagueText.Text = "Öppna Sportguide (G) eller Tjänster (L)";
            OsdScoreText.Text = "";
            OsdPinnedBadge.Visibility = Visibility.Collapsed;
            OsdPinButton.Content = "📌 Lås match (P)";
            OsdTelemetryText.Text = watchdogNotice ?? "Väntar på live-sport";
            return;
        }

        var svc = _services.FirstOrDefault(s => s.ServiceId == current.Event.ServiceId);
        var accentColor = TryParseColor(svc?.AccentColorHex, Color.FromRgb(34, 197, 94));
        OsdServiceBadge.Background = new SolidColorBrush(accentColor);
        OsdServiceText.Text = current.Event.ServiceName.ToUpperInvariant();
        OsdTitleText.Text = current.Event.Title;
        OsdSportLeagueText.Text = string.IsNullOrWhiteSpace(current.Event.League)
            ? current.Event.Sport
            : $"{current.Event.Sport} • {current.Event.League}";
        OsdScoreText.Text = $"Poäng: {current.Score:N0}  |  {current.ScoreBreakdown}";

        OsdPinnedBadge.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
        OsdPinButton.Content = isPinned ? "🔓 Lås upp (P)" : "📌 Lås match (P)";

        if (!string.IsNullOrWhiteSpace(watchdogNotice))
        {
            OsdTelemetryText.Text = watchdogNotice;
        }
        else
        {
            OsdTelemetryText.Text = telemetry?.PlaybackState switch
            {
                "playing" => _isMuted ? "🔇 Spelas (Ljud av)" : "▶ Spelas live",
                "paused" => "⏸ Pausad",
                "stalled" => "⚠ Strömmen frös — återhämtar...",
                "profile_picker" => "👤 Välj profil i bilden",
                "login_required" => "🔒 Kräver inloggning",
                "subscription_required" => "🔒 Kräver tilläggsprenumeration",
                "rate_limited_or_challenge" => "🛡 Verifiering krävs i spelaren",
                "ended" => "⏹ Sändningen slut",
                _ => "⏳ Laddar ström..."
            };
        }
    }

    private void UpdateToast(PendingSwitchOffer? offer)
    {
        if (offer is null)
        {
            SwitchToastPanel.Visibility = Visibility.Collapsed;
            UpdateVisibility();
            return;
        }

        ToastServiceText.Text = offer.Candidate.Event.ServiceName;
        ToastTitleText.Text = offer.Candidate.Event.Title;
        ToastReasonText.Text = offer.Candidate.ScoreBreakdown;
        var remaining = Math.Max(0, (int)Math.Ceiling((offer.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
        ToastCountdownText.Text = $"Byter automatiskt om {remaining} s";
        SwitchToastPanel.Visibility = Visibility.Visible;
        UpdateVisibility();
    }

    private void RenderGuideLists(bool restoreFocus = false)
    {
        GuideSummaryText.Text = $"{_liveEvents.Count} pågående matcher • {_upcomingEvents.Count} kommande  (Bläddra med Upp/Ner + Enter)";
        _guideFocusButtons.Clear();

        // Build sport filter pills
        GuideSportFilters.Children.Clear();
        var sports = new List<string> { "Alla" };
        sports.AddRange(_liveEvents.Select(e => e.Event.Sport).Distinct(StringComparer.OrdinalIgnoreCase));

        foreach (var sport in sports)
        {
            var isSelected = sport.Equals(_selectedSportFilter, StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                Content = sport,
                Style = (Style)FindResource(isSelected ? "TvAccentButtonStyle" : "TvButtonStyle"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 6),
                FontSize = 13
            };
            btn.Click += (_, _) =>
            {
                _selectedSportFilter = sport;
                RenderGuideLists(restoreFocus: true);
            };
            GuideSportFilters.Children.Add(btn);
        }

        var query = (GuideSearchBox.Text ?? string.Empty).Trim();

        GuideLiveList.Children.Clear();
        var filteredLive = _liveEvents.Where(e => MatchesGuideFilter(e, query)).ToList();
        var activeIdx = 0;
        if (filteredLive.Count == 0)
        {
            GuideLiveList.Children.Add(new TextBlock
            {
                Text = "Inga pågående matcher hittades med valt filter.\nKontrollera att du är inloggad under Tjänster (L) eller klicka på 'Skanna alla tjänster nu'.",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4)
            });
        }
        else
        {
            for (var i = 0; i < filteredLive.Count; i++)
            {
                if (_currentEvent?.Event.Id == filteredLive[i].Event.Id)
                {
                    activeIdx = i;
                }
                GuideLiveList.Children.Add(BuildGuideCard(filteredLive[i], i + 1, isLive: true));
            }
        }

        GuideUpcomingList.Children.Clear();
        var filteredUpcoming = _upcomingEvents.Where(e => MatchesGuideFilter(e, query)).ToList();
        if (filteredUpcoming.Count == 0)
        {
            GuideUpcomingList.Children.Add(new TextBlock
            {
                Text = "Inga kommande sändningar upptäckta ännu.",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            for (var i = 0; i < filteredUpcoming.Count; i++)
            {
                GuideUpcomingList.Children.Add(BuildGuideCard(filteredUpcoming[i], i + 1, isLive: false));
            }
        }

        if (restoreFocus && _guideFocusButtons.Count > 0)
        {
            _guideSelectedIndex = Math.Clamp(activeIdx, 0, _guideFocusButtons.Count - 1);
            Dispatcher.BeginInvoke(() => FocusGuideButton(_guideSelectedIndex), DispatcherPriority.Input);
        }
    }

    private void FocusGuideButton(int index)
    {
        if (_guideFocusButtons.Count == 0)
        {
            return;
        }
        _guideSelectedIndex = Math.Clamp(index, 0, _guideFocusButtons.Count - 1);
        var target = _guideFocusButtons[_guideSelectedIndex];
        target.Focus();
        target.BringIntoView();
    }

    private bool MatchesGuideFilter(RankedSportEvent item, string query)
    {
        if (!_selectedSportFilter.Equals("Alla", StringComparison.OrdinalIgnoreCase) &&
            !item.Event.Sport.Equals(_selectedSportFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var haystack = $"{item.Event.Title} {item.Event.Sport} {item.Event.League} {item.Event.ServiceName}";
        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private Border BuildGuideCard(RankedSportEvent item, int rank, bool isLive)
    {
        var isPlaying = _currentEvent?.Event.Id == item.Event.Id;
        var svc = _services.FirstOrDefault(s => s.ServiceId == item.Event.ServiceId);
        var svcColor = TryParseColor(svc?.AccentColorHex, Color.FromRgb(56, 189, 248));

        var card = new Border
        {
            Background = new SolidColorBrush(isPlaying ? Color.FromRgb(20, 83, 45) : Color.FromRgb(24, 34, 53)),
            BorderBrush = new SolidColorBrush(isPlaying ? Color.FromRgb(34, 197, 94) : Color.FromRgb(41, 53, 72)),
            BorderThickness = new Thickness(isPlaying ? 2 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        var topLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        topLine.Children.Add(new TextBlock
        {
            Text = $"#{rank}",
            Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0)
        });
        topLine.Children.Add(new Border
        {
            Background = new SolidColorBrush(svcColor),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = item.Event.ServiceName, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold }
        });
        topLine.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Event.League) ? item.Event.Sport : $"{item.Event.Sport} • {item.Event.League}",
            Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        info.Children.Add(topLine);

        info.Children.Add(new TextBlock
        {
            Text = item.Event.Title,
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        });
        info.Children.Add(new TextBlock
        {
            Text = isLive ? $"Poäng {item.Score:N0} • {item.ScoreBreakdown}" : (item.Event.StartTime?.ToLocalTime().ToString("HH:mm") ?? "Kommande") + $" • {item.ScoreBreakdown}",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (isLive)
        {
            var btn = new Button
            {
                Content = isPlaying ? "▶ Spelas nu" : "Titta",
                Style = (Style)FindResource(isPlaying ? "TvAccentButtonStyle" : "TvButtonStyle"),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Click += (_, _) =>
            {
                SelectEventRequested?.Invoke(item.Event.Id);
                CloseAllPanels();
            };
            var focusIdx = _guideFocusButtons.Count;
            btn.GotFocus += (_, _) => _guideSelectedIndex = focusIdx;
            _guideFocusButtons.Add(btn);
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);
        }

        card.Child = grid;
        return card;
    }

    private void LoadRankingIntoEditor(RankingProfile source)
    {
        _editingRanking = new RankingProfile
        {
            FavoriteTeams = [.. source.FavoriteTeams],
            FavoriteLeagues = [.. source.FavoriteLeagues],
            SportPriorities = [.. source.SportPriorities],
            MutedSports = [.. source.MutedSports],
            ServicePriorities = [.. source.ServicePriorities],
            BlockedKeywords = [.. source.BlockedKeywords],
            AutoSwitchMode = source.AutoSwitchMode,
            AutoSwitchCountdownSeconds = source.AutoSwitchCountdownSeconds,
            ScanIntervalSeconds = source.ScanIntervalSeconds,
            StartFullscreen = source.StartFullscreen,
            PreventScreenSleep = source.PreventScreenSleep,
            AutoTheaterMode = source.AutoTheaterMode,
            DeduplicateSameMatch = source.DeduplicateSameMatch,
            StartWithWindows = source.StartWithWindows,
            EnableIdleScreensaver = source.EnableIdleScreensaver,
            ScreensaverDelaySeconds = source.ScreensaverDelaySeconds,
            ScreensaverPureBlackMode = source.ScreensaverPureBlackMode,
            AllowDisplaySleepWhenNoSport = source.AllowDisplaySleepWhenNoSport,
            EnableQuietHours = source.EnableQuietHours,
            QuietHoursStart = source.QuietHoursStart,
            QuietHoursEnd = source.QuietHoursEnd,
            AutoCheckUpdates = source.AutoCheckUpdates,
            AutoInstallUpdatesWhenIdle = source.AutoInstallUpdatesWhenIdle,
            UpdateRepository = source.UpdateRepository
        };

        ScanIntervalBox.Text = _editingRanking.ScanIntervalSeconds.ToString();
        BlockedKeywordsBox.Text = string.Join(", ", _editingRanking.BlockedKeywords);
        PreventSleepCheck.IsChecked = _editingRanking.PreventScreenSleep;
        StartFullscreenCheck.IsChecked = _editingRanking.StartFullscreen;
        AutoTheaterModeCheck.IsChecked = _editingRanking.AutoTheaterMode;
        DeduplicateCheck.IsChecked = _editingRanking.DeduplicateSameMatch;
        StartWithWindowsCheck.IsChecked = _editingRanking.StartWithWindows;
        EnableQuietHoursCheck.IsChecked = _editingRanking.EnableQuietHours;
        QuietStartBox.Text = _editingRanking.QuietHoursStart;
        QuietEndBox.Text = _editingRanking.QuietHoursEnd;
        EnableScreensaverCheck.IsChecked = _editingRanking.EnableIdleScreensaver;
        ScreensaverPureBlackCheck.IsChecked = _editingRanking.ScreensaverPureBlackMode;
        AllowDisplaySleepIdleCheck.IsChecked = _editingRanking.AllowDisplaySleepWhenNoSport;
        ScreensaverDelayBox.Text = _editingRanking.ScreensaverDelaySeconds.ToString();
        AutoCheckUpdatesCheck.IsChecked = _editingRanking.AutoCheckUpdates;
        AutoInstallUpdatesCheck.IsChecked = _editingRanking.AutoInstallUpdatesWhenIdle;
        UpdateRepoBox.Text = _editingRanking.UpdateRepository;
        RankingStatusText.Text = string.Empty;

        RenderRankingLists();
    }

    private void RenderRankingLists()
    {
        RenderReorderableStrings(TeamsListPanel, _editingRanking.FavoriteTeams);
        RenderReorderableStrings(LeaguesListPanel, _editingRanking.FavoriteLeagues);
        RenderSportsOrder();
        RenderAutoSwitchModes();
        RenderServicePriorities();
    }

    private void RenderReorderableStrings(Panel host, List<string> items)
    {
        host.Children.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var idx = i;
            var row = CreatePriorityRow(
                $"{idx + 1}. {items[idx]}",
                isMuted: false,
                canUp: idx > 0,
                canDown: idx < items.Count - 1,
                onUp: () => { Swap(items, idx, idx - 1); RenderRankingLists(); },
                onDown: () => { Swap(items, idx, idx + 1); RenderRankingLists(); },
                onDelete: () => { items.RemoveAt(idx); RenderRankingLists(); });
            host.Children.Add(row);
        }
    }

    private void RenderSportsOrder()
    {
        SportsListPanel.Children.Clear();
        for (var i = 0; i < _editingRanking.SportPriorities.Count; i++)
        {
            var idx = i;
            var sport = _editingRanking.SportPriorities[idx];
            var isMuted = _editingRanking.MutedSports.Any(s => s.Equals(sport, StringComparison.OrdinalIgnoreCase));

            var row = CreatePriorityRow(
                $"{idx + 1}. {sport}",
                isMuted: isMuted,
                canUp: idx > 0,
                canDown: idx < _editingRanking.SportPriorities.Count - 1,
                onUp: () => { Swap(_editingRanking.SportPriorities, idx, idx - 1); RenderSportsOrder(); },
                onDown: () => { Swap(_editingRanking.SportPriorities, idx, idx + 1); RenderSportsOrder(); },
                onMuteToggle: () =>
                {
                    if (isMuted)
                    {
                        _editingRanking.MutedSports.RemoveAll(s => s.Equals(sport, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        _editingRanking.MutedSports.Add(sport);
                    }
                    RenderSportsOrder();
                });
            SportsListPanel.Children.Add(row);
        }
    }

    private void RenderAutoSwitchModes()
    {
        AutoSwitchModePanel.Children.Clear();
        var modes = new (AutoSwitchMode Mode, string Label)[]
        {
            (AutoSwitchMode.SmartToastWithCountdown, "Smart notis (15s nedräkning innan byte)"),
            (AutoSwitchMode.ImmediateAutoSwitch, "Byt automatiskt direkt när toppmatch startar"),
            (AutoSwitchMode.OnlyWhenCurrentEnds, "Byt aldrig mitt i — först när matchen slutar")
        };

        foreach (var (mode, label) in modes)
        {
            var selected = _editingRanking.AutoSwitchMode == mode;
            var btn = new Button
            {
                Content = (selected ? "● " : "○ ") + label,
                Style = (Style)FindResource(selected ? "TvAccentButtonStyle" : "TvButtonStyle"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 13,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            btn.Click += (_, _) =>
            {
                _editingRanking.AutoSwitchMode = mode;
                RenderAutoSwitchModes();
            };
            AutoSwitchModePanel.Children.Add(btn);
        }
    }

    private void RenderServicePriorities()
    {
        ServicePriorityPanel.Children.Clear();
        for (var i = 0; i < _editingRanking.ServicePriorities.Count; i++)
        {
            var idx = i;
            var svcId = _editingRanking.ServicePriorities[idx];
            var svcName = _services.FirstOrDefault(s => s.ServiceId == svcId)?.DisplayName ?? svcId.ToString();
            var row = CreatePriorityRow(
                $"{idx + 1}. {svcName}",
                isMuted: false,
                canUp: idx > 0,
                canDown: idx < _editingRanking.ServicePriorities.Count - 1,
                onUp: () => { Swap(_editingRanking.ServicePriorities, idx, idx - 1); RenderServicePriorities(); },
                onDown: () => { Swap(_editingRanking.ServicePriorities, idx, idx + 1); RenderServicePriorities(); });
            ServicePriorityPanel.Children.Add(row);
        }
    }

    private Border CreatePriorityRow(
        string label,
        bool isMuted,
        bool canUp,
        bool canDown,
        Action onUp,
        Action onDown,
        Action? onDelete = null,
        Action? onMuteToggle = null)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(isMuted ? Color.FromRgb(30, 27, 36) : Color.FromRgb(24, 34, 53)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var dock = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);

        var upBtn = new Button { Content = "▲", Style = (Style)FindResource("TvButtonStyle"), Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Margin = new Thickness(2, 0, 0, 0), IsEnabled = canUp };
        upBtn.Click += (_, _) => onUp();
        controls.Children.Add(upBtn);

        var downBtn = new Button { Content = "▼", Style = (Style)FindResource("TvButtonStyle"), Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Margin = new Thickness(3, 0, 0, 0), IsEnabled = canDown };
        downBtn.Click += (_, _) => onDown();
        controls.Children.Add(downBtn);

        if (onMuteToggle is not null)
        {
            var muteBtn = new Button
            {
                Content = isMuted ? "Dold" : "Aktiv",
                Style = (Style)FindResource(isMuted ? "TvButtonStyle" : "TvAccentButtonStyle"),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0)
            };
            muteBtn.Click += (_, _) => onMuteToggle();
            controls.Children.Add(muteBtn);
        }

        if (onDelete is not null)
        {
            var delBtn = new Button { Content = "✕", Style = (Style)FindResource("TvButtonStyle"), Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
            delBtn.Click += (_, _) => onDelete();
            controls.Children.Add(delBtn);
        }

        dock.Children.Add(controls);
        dock.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(isMuted ? Color.FromRgb(148, 163, 184) : Colors.White),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        border.Child = dock;
        return border;
    }

    private void RenderServicesPanel()
    {
        ServicesCardsPanel.Children.Clear();
        foreach (var service in _services)
        {
            var accent = TryParseColor(service.AccentColorHex, Color.FromRgb(56, 189, 248));
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);

            var toggleBtn = new Button
            {
                Content = service.Enabled ? "Aktiverad" : "Avstängd",
                Style = (Style)FindResource(service.Enabled ? "TvAccentButtonStyle" : "TvButtonStyle"),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0)
            };
            toggleBtn.Click += (_, _) =>
            {
                service.Enabled = !service.Enabled;
                ServicesChanged?.Invoke(_services);
                RenderServicesPanel();
            };
            actions.Children.Add(toggleBtn);

            var loginBtn = new Button
            {
                Content = "🔑 Logga in / Öppna sajt",
                Style = (Style)FindResource("TvButtonStyle"),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0)
            };
            loginBtn.Click += (_, _) =>
            {
                CloseAllPanels();
                OpenServiceLoginRequested?.Invoke(service.ServiceId);
            };
            actions.Children.Add(loginBtn);

            var scanBtn = new Button
            {
                Content = "Skanna & Testa",
                Style = (Style)FindResource("TvButtonStyle"),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0)
            };
            scanBtn.Click += (_, _) => ScanSingleServiceRequested?.Invoke(service.ServiceId);
            actions.Children.Add(scanBtn);

            var clearBtn = new Button
            {
                Content = "Rensa session",
                Style = (Style)FindResource("TvButtonStyle"),
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 12
            };
            clearBtn.Click += (_, _) => ClearServiceSessionRequested?.Invoke(service.ServiceId);
            actions.Children.Add(clearBtn);

            header.Children.Add(actions);

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new Border
            {
                Width = 12,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(accent),
                Margin = new Thickness(0, 0, 12, 0)
            });
            var textStack = new StackPanel();
            textStack.Children.Add(new TextBlock
            {
                Text = service.DisplayName,
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Bold
            });
            textStack.Children.Add(new TextBlock
            {
                Text = $"{FormatAuthState(service.AuthState)} • Live: {service.LastLiveCount} • Kommande: {service.LastUpcomingCount}" +
                       (service.LastScanUtc.HasValue ? $" • Skannad {service.LastScanUtc.Value.ToLocalTime():HH:mm:ss}" : ""),
                Foreground = new SolidColorBrush(GetAuthColor(service.AuthState)),
                FontSize = 12
            });
            titleStack.Children.Add(textStack);
            header.Children.Add(titleStack);

            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // Sports Hub URL + per-service volume normalization bar
            var urlRow = new DockPanel { Margin = new Thickness(24, 10, 0, 0) };

            var volumeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            DockPanel.SetDock(volumeStack, Dock.Right);
            volumeStack.Children.Add(new TextBlock
            {
                Text = "Volym:",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            var volLabel = new TextBlock
            {
                Text = $"{Math.Clamp(service.VolumePercent, 10, 100)}%",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Width = 38,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var volDownBtn = new Button { Content = "-", Style = (Style)FindResource("TvButtonStyle"), Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
            volDownBtn.Click += (_, _) =>
            {
                service.VolumePercent = Math.Clamp(service.VolumePercent - 10, 10, 100);
                volLabel.Text = $"{service.VolumePercent}%";
                ServiceVolumeChanged?.Invoke(service.ServiceId, service.VolumePercent);
                ServicesChanged?.Invoke(_services);
            };
            var volUpBtn = new Button { Content = "+", Style = (Style)FindResource("TvButtonStyle"), Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
            volUpBtn.Click += (_, _) =>
            {
                service.VolumePercent = Math.Clamp(service.VolumePercent + 10, 10, 100);
                volLabel.Text = $"{service.VolumePercent}%";
                ServiceVolumeChanged?.Invoke(service.ServiceId, service.VolumePercent);
                ServicesChanged?.Invoke(_services);
            };

            volumeStack.Children.Add(volDownBtn);
            volumeStack.Children.Add(volLabel);
            volumeStack.Children.Add(volUpBtn);
            urlRow.Children.Add(volumeStack);

            urlRow.Children.Add(new TextBlock
            {
                Text = "Sport-URL:",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            var urlBox = new TextBox
            {
                Text = service.SportsHubUrl,
                Style = (Style)FindResource("TvTextBoxStyle"),
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4)
            };
            urlBox.LostFocus += (_, _) =>
            {
                var candidate = (urlBox.Text ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && candidate != service.SportsHubUrl)
                {
                    var previewResolved = candidate.Replace("{today}", "2026-01-01", StringComparison.OrdinalIgnoreCase);
                    if (UrlSecurityGuard.IsTrustedServiceUrl(service.ServiceId, previewResolved))
                    {
                        service.SportsHubUrl = candidate;
                        ServicesChanged?.Invoke(_services);
                    }
                    else
                    {
                        urlBox.Text = service.SportsHubUrl;
                    }
                }
            };
            urlRow.Children.Add(urlBox);

            Grid.SetRow(urlRow, 1);
            grid.Children.Add(urlRow);

            card.Child = grid;
            ServicesCardsPanel.Children.Add(card);
        }
    }

    private static string FormatAuthState(ServiceAuthState state) => state switch
    {
        ServiceAuthState.NotRequired => "Ingen inloggning krävs",
        ServiceAuthState.Authenticated => "Inloggad",
        ServiceAuthState.LoginRequired => "Kräver inloggning (skannas ej automatiskt)",
        ServiceAuthState.ProfileSelectionRequired => "Välj profil på sajten",
        ServiceAuthState.RateLimitedOrChallenge => "Pausad (begränsad/verifiering krävs)",
        ServiceAuthState.Error => "Fel vid skanning (pausad tillfälligt)",
        _ => "Ej kontrollerad ännu"
    };

    private static Color GetAuthColor(ServiceAuthState state) => state switch
    {
        ServiceAuthState.Authenticated or ServiceAuthState.NotRequired => Color.FromRgb(74, 222, 128),
        ServiceAuthState.LoginRequired or ServiceAuthState.ProfileSelectionRequired => Color.FromRgb(251, 191, 36),
        ServiceAuthState.RateLimitedOrChallenge or ServiceAuthState.Error => Color.FromRgb(248, 113, 113),
        _ => Color.FromRgb(148, 163, 184)
    };

    private static Color TryParseColor(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
        }
        catch { }
        return fallback;
    }

    private static void Swap<T>(IList<T> list, int a, int b)
    {
        (list[a], list[b]) = (list[b], list[a]);
    }

    private void AddTeamToEditor(string raw)
    {
        var val = raw.Trim();
        if (val.Length >= 2 && !_editingRanking.FavoriteTeams.Any(t => t.Equals(val, StringComparison.OrdinalIgnoreCase)))
        {
            _editingRanking.FavoriteTeams.Insert(0, val);
            RenderRankingLists();
        }
    }

    private void AddLeagueToEditor(string raw)
    {
        var val = raw.Trim();
        if (val.Length >= 2 && !_editingRanking.FavoriteLeagues.Any(l => l.Equals(val, StringComparison.OrdinalIgnoreCase)))
        {
            _editingRanking.FavoriteLeagues.Insert(0, val);
            RenderRankingLists();
        }
    }

    // Event handlers
    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_pendingOffer is not null && _activeSection == OverlaySection.None)
            {
                DismissPendingSwitchRequested?.Invoke();
            }
            else
            {
                CloseAllPanels();
            }
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (_activeSection == OverlaySection.Guide && _guideFocusButtons.Count > 0)
        {
            if (e.Key == Key.Down)
            {
                FocusGuideButton((_guideSelectedIndex + 1) % _guideFocusButtons.Count);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                FocusGuideButton((_guideSelectedIndex - 1 + _guideFocusButtons.Count) % _guideFocusButtons.Count);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.G:
                ShowSection(_activeSection == OverlaySection.Guide ? OverlaySection.None : OverlaySection.Guide, _editingRanking);
                e.Handled = true;
                break;
            case Key.R:
                ShowSection(_activeSection == OverlaySection.Ranking ? OverlaySection.None : OverlaySection.Ranking, _editingRanking);
                e.Handled = true;
                break;
            case Key.L:
                ShowSection(_activeSection == OverlaySection.Services ? OverlaySection.None : OverlaySection.Services, _editingRanking);
                e.Handled = true;
                break;
            case Key.F1 or Key.H:
                ShowSection(_activeSection == OverlaySection.Help ? OverlaySection.None : OverlaySection.Help, _editingRanking);
                e.Handled = true;
                break;
            case Key.N or Key.PageUp:
                NextChannelRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.B or Key.PageDown:
                PrevChannelRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.S:
                SkipChannelRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.P:
                PinChannelRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.M or Key.VolumeMute:
                ToggleMuteRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.T:
                ToggleTheaterModeRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullscreenRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void ModalBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseAllPanels();
    private void OsdBar_MouseEnter(object sender, MouseEventArgs e) => _pointerInsideOsd = true;
    private void OsdBar_MouseLeave(object sender, MouseEventArgs e) => _pointerInsideOsd = false;
    private void AcceptSwitch_Click(object sender, RoutedEventArgs e) => AcceptPendingSwitchRequested?.Invoke();
    private void DismissSwitch_Click(object sender, RoutedEventArgs e) => DismissPendingSwitchRequested?.Invoke();
    private void PrevChannel_Click(object sender, RoutedEventArgs e) => PrevChannelRequested?.Invoke();
    private void NextChannel_Click(object sender, RoutedEventArgs e) => NextChannelRequested?.Invoke();
    private void SkipChannel_Click(object sender, RoutedEventArgs e) => SkipChannelRequested?.Invoke();
    private void PinChannel_Click(object sender, RoutedEventArgs e) => PinChannelRequested?.Invoke();
    private void ToggleMute_Click(object sender, RoutedEventArgs e) => ToggleMuteRequested?.Invoke();
    private void ToggleTheater_Click(object sender, RoutedEventArgs e) => ToggleTheaterModeRequested?.Invoke();
    private void OpenGuide_Click(object sender, RoutedEventArgs e) => ShowSection(OverlaySection.Guide, _editingRanking);
    private void OpenRanking_Click(object sender, RoutedEventArgs e) => ShowSection(OverlaySection.Ranking, _editingRanking);
    private void OpenServices_Click(object sender, RoutedEventArgs e) => ShowSection(OverlaySection.Services, _editingRanking);
    private void OpenHelp_Click(object sender, RoutedEventArgs e) => ShowSection(OverlaySection.Help, _editingRanking);
    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreenRequested?.Invoke();
    private void CloseModals_Click(object sender, RoutedEventArgs e) => CloseAllPanels();
    private void ScanAllNow_Click(object sender, RoutedEventArgs e) => ScanAllRequested?.Invoke();
    private void CheckUpdatesNow_Click(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();
    private void InstallUpdateNow_Click(object sender, RoutedEventArgs e) => InstallUpdateRequested?.Invoke();
    private void GuideFilter_Changed(object sender, TextChangedEventArgs e) => RenderGuideLists();

    private void AddTeam_Click(object sender, RoutedEventArgs e)
    {
        AddTeamToEditor(AddTeamBox.Text);
        AddTeamBox.Clear();
    }

    private void AddTeamBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTeamToEditor(AddTeamBox.Text);
            AddTeamBox.Clear();
            e.Handled = true;
        }
    }

    private void QuickAddTeam_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string team)
        {
            AddTeamToEditor(team);
        }
    }

    private void AddLeague_Click(object sender, RoutedEventArgs e)
    {
        AddLeagueToEditor(AddLeagueBox.Text);
        AddLeagueBox.Clear();
    }

    private void AddLeagueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddLeagueToEditor(AddLeagueBox.Text);
            AddLeagueBox.Clear();
            e.Handled = true;
        }
    }

    private void QuickAddLeague_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string league)
        {
            AddLeagueToEditor(league);
        }
    }

    private void SaveRanking_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(ScanIntervalBox.Text, out var scanSeconds))
        {
            _editingRanking.ScanIntervalSeconds = ScanPolicyManager.ClampScanInterval(scanSeconds);
        }

        _editingRanking.BlockedKeywords = (BlockedKeywordsBox.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _editingRanking.PreventScreenSleep = PreventSleepCheck.IsChecked == true;
        _editingRanking.StartFullscreen = StartFullscreenCheck.IsChecked == true;
        _editingRanking.AutoTheaterMode = AutoTheaterModeCheck.IsChecked == true;
        _editingRanking.DeduplicateSameMatch = DeduplicateCheck.IsChecked == true;
        _editingRanking.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _editingRanking.EnableQuietHours = EnableQuietHoursCheck.IsChecked == true;
        if (QuietHoursEvaluator.TryParseTime(QuietStartBox.Text, out _))
        {
            _editingRanking.QuietHoursStart = QuietStartBox.Text.Trim();
        }
        if (QuietHoursEvaluator.TryParseTime(QuietEndBox.Text, out _))
        {
            _editingRanking.QuietHoursEnd = QuietEndBox.Text.Trim();
        }
        _editingRanking.EnableIdleScreensaver = EnableScreensaverCheck.IsChecked == true;
        _editingRanking.ScreensaverPureBlackMode = ScreensaverPureBlackCheck.IsChecked == true;
        _editingRanking.AllowDisplaySleepWhenNoSport = AllowDisplaySleepIdleCheck.IsChecked == true;

        if (int.TryParse(ScreensaverDelayBox.Text, out var ssDelay))
        {
            _editingRanking.ScreensaverDelaySeconds = Math.Clamp(ssDelay, 10, 3600);
        }

        _editingRanking.AutoCheckUpdates = AutoCheckUpdatesCheck.IsChecked == true;
        _editingRanking.AutoInstallUpdatesWhenIdle = AutoInstallUpdatesCheck.IsChecked == true;
        var repoCandidate = (UpdateRepoBox.Text ?? string.Empty).Trim();
        _editingRanking.UpdateRepository = string.IsNullOrEmpty(repoCandidate) || GitHubUpdateService.IsValidRepositorySlug(repoCandidate)
            ? repoCandidate
            : _editingRanking.UpdateRepository;

        RankingSaved?.Invoke(_editingRanking);
        RankingStatusText.Text = "✓ Rangordning sparad och tillämpad!";
        CloseAllPanels();
    }
}
