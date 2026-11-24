using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AutomatiskSportTV.App.Services;
using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;

namespace AutomatiskSportTV.App;

public partial class MainWindow : Window
{
    private readonly AppStateStore _store;
    private readonly AppConfigSnapshot _config;
    private readonly ChannelOrchestrator _orchestrator;
    private readonly ScanPolicyManager _scanPolicy = new();
    private readonly AppUpdateManager _updateManager = new();
    private readonly Dictionary<StreamingServiceId, List<SportEvent>> _eventsByService = [];
    private readonly DispatcherTimer _scanTimer;
    private readonly DispatcherTimer _cursorWatchTimer;
    private readonly DispatcherTimer _screensaverClockTimer;
    private readonly DispatcherTimer _screensaverDriftTimer;
    private readonly DispatcherTimer _quietHoursTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly Random _rng = new();

    private WebViewHostManager? _webViewManager;
    private ScreensaverAnimator? _screensaverAnimator;
    private TvOverlayWindow? _overlay;
    private CancellationTokenSource? _scanCts;
    private PlaybackTelemetry? _latestTelemetry;
    private DateTimeOffset _lastChannelSwitchUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUserActivityUtc = DateTimeOffset.UtcNow;
    private int _consecutiveEndedTelemetry;
    private int _stallReloadAttempts;
    private int _screensaverTickCount;
    private StreamingServiceId? _activeLoginService;
    private StreamingServiceId? _visualScannerService;
    private string? _watchdogNotice;
    private bool _isFullscreen;
    private bool _isScanning;
    private bool _isScreensaverActive;
    private bool _isVisualScannerOpen;
    private bool _isMuted;
    private bool _isTheaterMode;
    private bool _lastQuietHoursState;
    private (int X, int Y) _lastCursorPos;

    public MainWindow()
    {
        InitializeComponent();

        _store = new AppStateStore();
        var configDir = System.IO.Path.GetDirectoryName(_store.ConfigFilePath);
        if (!string.IsNullOrEmpty(configDir))
        {
            DirectorySecurityHelper.RestrictDirectoryToCurrentUser(configDir);
        }
        _config = _store.Load();
        _config.RankingProfile.StartWithWindows = WindowsStartupService.IsEnabled();
        _isTheaterMode = _config.RankingProfile.AutoTheaterMode;
        _lastQuietHoursState = IsQuietHoursNow();

        _orchestrator = new ChannelOrchestrator();
        _orchestrator.ActiveChannelChanged += OnActiveChannelChanged;
        _orchestrator.PendingSwitchOfferChanged += _ => SyncOverlayState();
        _orchestrator.CatalogUpdated += OnCatalogUpdated;

        _scanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ScanPolicyManager.ClampScanInterval(_config.RankingProfile.ScanIntervalSeconds))
        };
        _scanTimer.Tick += async (_, _) => await RunFullScanAsync(isManual: false);

        _cursorWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _cursorWatchTimer.Tick += CursorWatchTimer_Tick;

        _screensaverClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _screensaverClockTimer.Tick += (_, _) => UpdateScreensaverContent();

        _screensaverDriftTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _screensaverDriftTimer.Tick += (_, _) => DriftScreensaverCard();

        _quietHoursTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _quietHoursTimer.Tick += (_, _) => EvaluateQuietHoursTransition();

        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        _updateCheckTimer.Tick += async (_, _) =>
        {
            if (_config.RankingProfile.AutoCheckUpdates)
            {
                await CheckForAppUpdateAsync(allowAutoInstallIfIdle: true);
            }
        };

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        if (_config.RankingProfile.StartFullscreen)
        {
            SetFullscreen(true);
        }

        UpdatePowerMode();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _webViewManager = new WebViewHostManager(PlayerHostGrid, ScannerHostGrid);
        _webViewManager.TelemetryReceived += OnPlayerTelemetryReceived;
        _webViewManager.ShortcutKeyForwarded += OnForwardedShortcutKey;
        _webViewManager.ScannerProgressChanged += OnScannerProgressChanged;

        _screensaverAnimator = new ScreensaverAnimator(ScreensaverEffectsCanvas);
        _screensaverAnimator.BounceStatsChanged += OnScreensaverBounceStatsChanged;

        _overlay = new TvOverlayWindow { Owner = this };
        WireOverlayEvents(_overlay);
        SyncOverlayBounds();
        RenderStandbyDashboard();

        _lastCursorPos = DisplayPowerService.GetCursorPosition();
        _lastUserActivityUtc = DateTimeOffset.UtcNow;
        _cursorWatchTimer.Start();
        _quietHoursTimer.Start();
        _updateCheckTimer.Start();
        _scanTimer.Start();

        _overlay.SyncUpdateStatus(_updateManager.CurrentVersion, _updateManager.StatusSummary, _updateManager.StagedRelease);

        // Non-blocking startup update check after 10 seconds
        if (_config.RankingProfile.AutoCheckUpdates)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                await Dispatcher.InvokeAsync(async () => await CheckForAppUpdateAsync(allowAutoInstallIfIdle: true));
            });
        }

        await RunFullScanAsync(isManual: false);
    }

    private void WireOverlayEvents(TvOverlayWindow overlay)
    {
        overlay.NextChannelRequested += () => { MarkUserActivity(); _orchestrator.SwitchNext(); };
        overlay.PrevChannelRequested += () => { MarkUserActivity(); _orchestrator.SwitchPrevious(); };
        overlay.SkipChannelRequested += () => { MarkUserActivity(); _orchestrator.SkipCurrent(); };
        overlay.PinChannelRequested += () =>
        {
            MarkUserActivity();
            _orchestrator.TogglePinCurrent();
            SyncOverlayState();
            overlay.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile);
        };
        overlay.ToggleMuteRequested += async () => { MarkUserActivity(); await ToggleMuteAsync(); };
        overlay.ToggleTheaterModeRequested += async () => { MarkUserActivity(); await ToggleTheaterModeAsync(); };
        overlay.ToggleFullscreenRequested += () => { MarkUserActivity(); SetFullscreen(!_isFullscreen); };
        overlay.SelectEventRequested += id => { MarkUserActivity(); _orchestrator.SelectEvent(id); };
        overlay.ScanAllRequested += async () => { MarkUserActivity(); await RunFullScanAsync(isManual: true); };
        overlay.ScanSingleServiceRequested += async svcId => { MarkUserActivity(); await RunSingleServiceScanAsync(svcId, showDiagnostics: true); };
        overlay.OpenVisualScannerRequested += async svcId => { MarkUserActivity(); await EnterVisualScannerModeAsync(svcId, autoScan: true); };
        overlay.OpenServiceLoginRequested += async svcId => { MarkUserActivity(); await EnterLoginModeAsync(svcId); };
        overlay.ClearServiceSessionRequested += async svcId => { MarkUserActivity(); await ClearServiceSessionAsync(svcId); };
        overlay.ServiceVolumeChanged += async (svcId, vol) =>
        {
            MarkUserActivity();
            if (_webViewManager is not null)
            {
                await _webViewManager.SetActiveVolumeAsync(svcId, vol);
            }
        };
        overlay.CheckUpdatesRequested += async () => { MarkUserActivity(); await CheckForAppUpdateAsync(allowAutoInstallIfIdle: false); };
        overlay.InstallUpdateRequested += async () => { MarkUserActivity(); await ApplyStagedUpdateAndRestartAsync(); };
        overlay.AcceptPendingSwitchRequested += () => { MarkUserActivity(); _orchestrator.AcceptPendingSwitch(); };
        overlay.DismissPendingSwitchRequested += () => { MarkUserActivity(); _orchestrator.DismissPendingSwitch(); };
        overlay.RankingSaved += async updated =>
        {
            MarkUserActivity();
            updated.ScanIntervalSeconds = ScanPolicyManager.ClampScanInterval(updated.ScanIntervalSeconds);
            _config.RankingProfile = updated;
            _store.Save(_config);
            WindowsStartupService.SetEnabled(updated.StartWithWindows);
            _scanTimer.Interval = TimeSpan.FromSeconds(updated.ScanIntervalSeconds);

            if (_isTheaterMode != updated.AutoTheaterMode)
            {
                _isTheaterMode = updated.AutoTheaterMode;
                if (_webViewManager is not null)
                {
                    await _webViewManager.SetTheaterModeAsync(_isTheaterMode);
                }
            }

            UpdatePowerMode();
            RecomputeCatalog();
        };
        overlay.ServicesChanged += updatedServices =>
        {
            MarkUserActivity();
            _config.Services = updatedServices;
            _store.Save(_config);
            RecomputeCatalog();
        };
        overlay.OverlayClosed += () =>
        {
            MarkUserActivity();
            if (_activeLoginService is null && _orchestrator.CurrentEvent is not null)
            {
                _webViewManager?.FocusActivePlayer();
            }
        };
    }

    private async Task ToggleMuteAsync()
    {
        _isMuted = !_isMuted;
        if (_webViewManager is not null)
        {
            await _webViewManager.SetMuteAsync(_isMuted);
        }
        SyncOverlayState();
        _overlay?.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: true);
    }

    private async Task ToggleTheaterModeAsync()
    {
        _isTheaterMode = !_isTheaterMode;
        if (_webViewManager is not null)
        {
            await _webViewManager.SetTheaterModeAsync(_isTheaterMode);
        }
        SyncOverlayState();
        _overlay?.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: true);
    }

    private bool IsQuietHoursNow() =>
        QuietHoursEvaluator.IsQuietNow(_config.RankingProfile, TimeOnly.FromDateTime(DateTime.Now));

    private void EvaluateQuietHoursTransition()
    {
        var quietNow = IsQuietHoursNow();
        if (quietNow != _lastQuietHoursState)
        {
            _lastQuietHoursState = quietNow;
            RecomputeCatalog();
            if (quietNow && _orchestrator.CurrentEvent is null && _config.RankingProfile.EnableIdleScreensaver)
            {
                EnterScreensaverMode();
            }
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!e.IsAvailable)
            {
                _watchdogNotice = "⚠ Nätverksavbrott — väntar på anslutning...";
                StandbyScanStatusText.Text = _watchdogNotice;
                SyncOverlayState();
                return;
            }

            _watchdogNotice = "⟳ Nätverk återanslutet — återhämtar ström...";
            StandbyScanStatusText.Text = _watchdogNotice;
            SyncOverlayState();

            await Task.Delay(TimeSpan.FromSeconds(3));
            _watchdogNotice = null;
            if (_orchestrator.CurrentEvent is not null && _activeLoginService is null)
            {
                _lastChannelSwitchUtc = DateTimeOffset.UtcNow;
                _webViewManager?.ReloadActiveStream();
            }
            await RunFullScanAsync(isManual: false);
        });
    }

    private async Task RunFullScanAsync(bool isManual = false)
    {
        if (_webViewManager is null || _isScanning || (!isManual && _activeLoginService is not null))
        {
            return;
        }

        _isScanning = true;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var targets = _config.Services
                .Where(s => isManual ? s.Enabled : _scanPolicy.ShouldAutoScan(s, nowUtc))
                .ToList();

            for (var i = 0; i < targets.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var service = targets[i];
                if (isManual)
                {
                    _scanPolicy.Reset(service.ServiceId);
                }

                StandbyScanStatusText.Text = $"Skannar {service.DisplayName}...";
                var result = await _webViewManager.ScanServiceAsync(service, ct);
                ApplyServiceScanResult(service, result);

                // Space out requests politely so streaming services are never hit with rapid bursts
                if (i < targets.Count - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_isVisualScannerOpen ? 2.5 : 4), ct);
                }
            }

            StandbyScanStatusText.Text = $"Senaste skanning {DateTime.Now:HH:mm:ss} • {_orchestrator.LiveEvents.Count} live / {_orchestrator.UpcomingEvents.Count} kommande";
        }
        catch (OperationCanceledException)
        {
            // Scan cancelled by newer request or shutdown
        }
        finally
        {
            _isScanning = false;
            RenderStandbyDashboard();
            if (_isVisualScannerOpen)
            {
                RenderScannerServiceTabs();
            }
            SyncOverlayState();
        }
    }

    private void OnScannerProgressChanged(StreamingServiceId serviceId, string status, string sanitizedUrl)
    {
        Dispatcher.Invoke(() =>
        {
            _visualScannerService = serviceId;
            var svcName = _config.Services.FirstOrDefault(s => s.ServiceId == serviceId)?.DisplayName ?? serviceId.ToString();
            StandbyScanStatusText.Text = $"{svcName}: {status}";
            ScannerActiveServiceText.Text = svcName;
            ScannerLiveStatusText.Text = status;
            if (!string.IsNullOrWhiteSpace(sanitizedUrl))
            {
                ScannerLiveUrlText.Text = sanitizedUrl;
            }
            if (_isVisualScannerOpen)
            {
                RenderScannerServiceTabs();
            }
        });
    }

    private async Task RunSingleServiceScanAsync(StreamingServiceId serviceId, bool showDiagnostics)
    {
        if (_webViewManager is null)
        {
            return;
        }

        var service = _config.Services.FirstOrDefault(s => s.ServiceId == serviceId);
        if (service is null)
        {
            return;
        }

        _scanPolicy.Reset(serviceId);
        StandbyScanStatusText.Text = $"Skannar {service.DisplayName}...";
        var result = await _webViewManager.ScanServiceAsync(service);
        ApplyServiceScanResult(service, result);

        if (showDiagnostics && _overlay is not null)
        {
            _overlay.ShowDiagnosticEvents(service, result);
        }

        StandbyScanStatusText.Text = $"{service.DisplayName} skannad ({result.Events.Count} träffar)";
        RenderStandbyDashboard();
        if (_isVisualScannerOpen)
        {
            RenderScannerServiceTabs();
        }
        SyncOverlayState();
    }

    private async Task ClearServiceSessionAsync(StreamingServiceId serviceId)
    {
        if (_webViewManager is null)
        {
            return;
        }

        var service = _config.Services.FirstOrDefault(s => s.ServiceId == serviceId);
        if (service is null)
        {
            return;
        }

        await _webViewManager.ClearServiceSessionAsync(serviceId);
        _eventsByService.Remove(serviceId);
        _scanPolicy.Reset(serviceId);
        service.AuthState = service.RequiresLogin ? ServiceAuthState.LoginRequired : ServiceAuthState.NotRequired;
        service.LastLiveCount = 0;
        service.LastUpcomingCount = 0;
        service.LastError = null;
        _store.Save(_config);
        RecomputeCatalog();
        RenderStandbyDashboard();
        SyncOverlayState();
    }

    private void ApplyServiceScanResult(ServiceConfig service, ServiceScanResult result)
    {
        service.AuthState = result.AuthState;
        service.LastScanUtc = DateTimeOffset.UtcNow;
        service.LastLiveCount = result.Events.Count(e => e.Status == EventStatus.Live);
        service.LastUpcomingCount = result.Events.Count(e => e.Status == EventStatus.Upcoming);
        service.LastError = result.AuthState is ServiceAuthState.Error or ServiceAuthState.RateLimitedOrChallenge
            ? (service.LastError ?? "Skanning pausad tillfälligt")
            : null;

        _scanPolicy.RecordOutcome(service.ServiceId, result.AuthState, DateTimeOffset.UtcNow);
        _eventsByService[service.ServiceId] = result.Events;
        _store.Save(_config);
        RecomputeCatalog();
    }

    private void RecomputeCatalog()
    {
        var merged = new List<SportEvent>();
        foreach (var service in _config.Services.Where(s => s.Enabled))
        {
            if (_eventsByService.TryGetValue(service.ServiceId, out var list))
            {
                merged.AddRange(list);
            }
        }

        _orchestrator.UpdateCatalog(merged, _config.RankingProfile, quietHoursActive: IsQuietHoursNow());
    }

    private async void OnActiveChannelChanged(RankedSportEvent? target)
    {
        _latestTelemetry = null;
        _consecutiveEndedTelemetry = 0;
        _stallReloadAttempts = 0;
        _watchdogNotice = null;
        _lastChannelSwitchUtc = DateTimeOffset.UtcNow;

        if (_activeLoginService is not null || _isVisualScannerOpen)
        {
            UpdatePowerMode();
            SyncOverlayState();
            return;
        }

        if (target is null)
        {
            _webViewManager?.StopActivePlayback();
            PlayerHostGrid.Visibility = Visibility.Collapsed;
            if (!_isScreensaverActive)
            {
                StandbyView.Visibility = Visibility.Visible;
            }
            MarkUserActivity();
            UpdatePowerMode();
            RenderStandbyDashboard();
            SyncOverlayState();

            if (_updateManager.HasStagedUpdate && _config.RankingProfile.AutoInstallUpdatesWhenIdle && _updateManager.CanSelfReplaceExecutable)
            {
                await ApplyStagedUpdateAndRestartAsync();
            }
            return;
        }

        ExitScreensaverMode();
        StandbyView.Visibility = Visibility.Collapsed;
        PlayerHostGrid.Visibility = Visibility.Visible;
        UpdatePowerMode();

        if (_webViewManager is not null)
        {
            var svc = _config.Services.FirstOrDefault(s => s.ServiceId == target.Event.ServiceId);
            var volumePercent = svc?.VolumePercent ?? 100;
            await _webViewManager.PlayStreamAsync(target.Event.ServiceId, target.Event.WatchUrl, volumePercent, _isTheaterMode);
        }

        SyncOverlayState();
        _overlay?.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: true);
    }

    private void OnCatalogUpdated()
    {
        RenderStandbyDashboard();
        if (_isScreensaverActive)
        {
            UpdateScreensaverContent();
        }
        SyncOverlayState();
    }

    private void OnPlayerTelemetryReceived(PlaybackTelemetry telemetry)
    {
        Dispatcher.Invoke(() =>
        {
            _latestTelemetry = telemetry;

            if (_orchestrator.CurrentEvent is not null &&
                _orchestrator.CurrentEvent.Event.ServiceId == telemetry.ServiceId)
            {
                if (telemetry.PlaybackState == "playing")
                {
                    _consecutiveEndedTelemetry = 0;
                    _stallReloadAttempts = 0;
                    _watchdogNotice = null;
                }
                else if (telemetry.PlaybackState == "ended")
                {
                    if ((DateTimeOffset.UtcNow - _lastChannelSwitchUtc).TotalSeconds >= 10)
                    {
                        _consecutiveEndedTelemetry++;
                        if (_consecutiveEndedTelemetry >= 2)
                        {
                            _orchestrator.ReportCurrentPlaybackEnded();
                            return;
                        }
                    }
                }
                else if (telemetry.PlaybackState == "subscription_required")
                {
                    _watchdogNotice = $"🔒 Kräver tilläggsprenumeration på {_orchestrator.CurrentEvent.Event.ServiceName} — byter match...";
                    SyncOverlayState();
                    _overlay?.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: true);
                    _orchestrator.SkipCurrent();
                    return;
                }
                else if (telemetry.PlaybackState == "upcoming_not_started")
                {
                    _watchdogNotice = $"⏳ Matchen på {_orchestrator.CurrentEvent.Event.ServiceName} har inte börjat än — byter match...";
                    SyncOverlayState();
                    _overlay?.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: true);
                    _orchestrator.SkipCurrent();
                    return;
                }
                else if (telemetry.PlaybackState == "stalled")
                {
                    if ((DateTimeOffset.UtcNow - _lastChannelSwitchUtc).TotalSeconds >= 15)
                    {
                        if (_stallReloadAttempts == 0)
                        {
                            _stallReloadAttempts = 1;
                            _lastChannelSwitchUtc = DateTimeOffset.UtcNow;
                            _watchdogNotice = "⟳ Fryst bild — laddar om ström...";
                            _webViewManager?.ReloadActiveStream();
                            SyncOverlayState();
                            _overlay?.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: true);
                            return;
                        }

                        _watchdogNotice = "⏭ Strömmen svarar inte — byter till nästa match...";
                        _orchestrator.SkipCurrent();
                        return;
                    }
                }
                else
                {
                    _consecutiveEndedTelemetry = 0;
                }

                if (telemetry.PlaybackState is "login_required" or "profile_picker" or "rate_limited_or_challenge")
                {
                    var svc = _config.Services.FirstOrDefault(s => s.ServiceId == telemetry.ServiceId);
                    if (svc is not null)
                    {
                        svc.AuthState = telemetry.PlaybackState switch
                        {
                            "login_required" => ServiceAuthState.LoginRequired,
                            "profile_picker" => ServiceAuthState.ProfileSelectionRequired,
                            _ => ServiceAuthState.RateLimitedOrChallenge
                        };
                        _scanPolicy.RecordOutcome(svc.ServiceId, svc.AuthState, DateTimeOffset.UtcNow);
                    }
                    _overlay?.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: false);
                }
            }

            SyncOverlayState();
        });
    }

    private async Task EnterLoginModeAsync(StreamingServiceId serviceId)
    {
        if (_webViewManager is null)
        {
            return;
        }

        var service = _config.Services.FirstOrDefault(s => s.ServiceId == serviceId);
        if (service is null)
        {
            return;
        }

        ExitScreensaverMode();
        if (_isVisualScannerOpen)
        {
            CloseVisualScannerChromeOnly();
        }
        _activeLoginService = serviceId;
        _overlay?.CloseAllPanels();
        UpdatePowerMode();

        LoginBannerTitle.Text = $"Inloggning / Profil: {service.DisplayName}";
        LoginTopBar.Visibility = Visibility.Visible;
        StandbyView.Visibility = Visibility.Collapsed;
        PlayerHostGrid.Visibility = Visibility.Visible;

        await _webViewManager.OpenInteractiveLoginAsync(service);
    }

    private async Task ExitLoginModeAsync()
    {
        if (_activeLoginService is null)
        {
            return;
        }

        var scannedService = _activeLoginService.Value;
        _activeLoginService = null;
        LoginTopBar.Visibility = Visibility.Collapsed;
        _webViewManager?.ExitInteractiveLogin();
        MarkUserActivity();
        UpdatePowerMode();

        if (_orchestrator.CurrentEvent is null)
        {
            PlayerHostGrid.Visibility = Visibility.Collapsed;
            StandbyView.Visibility = Visibility.Visible;
        }
        else
        {
            StandbyView.Visibility = Visibility.Collapsed;
            PlayerHostGrid.Visibility = Visibility.Visible;
            var svc = _config.Services.FirstOrDefault(s => s.ServiceId == _orchestrator.CurrentEvent.Event.ServiceId);
            await _webViewManager!.PlayStreamAsync(
                _orchestrator.CurrentEvent.Event.ServiceId,
                _orchestrator.CurrentEvent.Event.WatchUrl,
                svc?.VolumePercent ?? 100,
                _isTheaterMode);
        }

        await RunSingleServiceScanAsync(scannedService, showDiagnostics: false);
    }

    private void CloseVisualScannerChromeOnly()
    {
        _isVisualScannerOpen = false;
        ScannerMonitorBar.Visibility = Visibility.Collapsed;
        ScannerHostGrid.Width = 1280;
        ScannerHostGrid.Height = 720;
        ScannerHostGrid.HorizontalAlignment = HorizontalAlignment.Left;
        ScannerHostGrid.VerticalAlignment = VerticalAlignment.Top;
        ScannerHostGrid.Margin = new Thickness(-4000, -4000, 0, 0);
        ScannerHostGrid.IsHitTestVisible = false;
        _webViewManager?.SetVisualScannerMode(false);
    }

    private async Task EnterVisualScannerModeAsync(StreamingServiceId? initialService = null, bool autoScan = true)
    {
        if (_webViewManager is null)
        {
            return;
        }

        ExitScreensaverMode();
        if (_activeLoginService is not null)
        {
            _activeLoginService = null;
            LoginTopBar.Visibility = Visibility.Collapsed;
            _webViewManager.ExitInteractiveLogin();
        }

        _overlay?.CloseAllPanels();
        _isVisualScannerOpen = true;
        _webViewManager.StopActivePlayback();

        StandbyView.Visibility = Visibility.Collapsed;
        PlayerHostGrid.Visibility = Visibility.Collapsed;
        ScannerMonitorBar.Visibility = Visibility.Visible;

        ScannerHostGrid.Width = double.NaN;
        ScannerHostGrid.Height = double.NaN;
        ScannerHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        ScannerHostGrid.VerticalAlignment = VerticalAlignment.Stretch;
        ScannerHostGrid.Margin = new Thickness(0);
        ScannerHostGrid.IsHitTestVisible = true;

        var targetService = initialService
            ?? _webViewManager.CurrentVisibleScannerService
            ?? _visualScannerService
            ?? _config.Services.FirstOrDefault(s => s.Enabled)?.ServiceId
            ?? StreamingServiceId.SvtPlay;

        _visualScannerService = targetService;
        _webViewManager.SetVisualScannerMode(true, targetService);

        var targetCfg = _config.Services.FirstOrDefault(s => s.ServiceId == targetService);
        if (targetCfg is not null)
        {
            ScannerActiveServiceText.Text = targetCfg.DisplayName;
            if (string.IsNullOrWhiteSpace(ScannerLiveStatusText.Text))
            {
                ScannerLiveStatusText.Text = "Redo att skanna";
            }
            ScannerLiveUrlText.Text = UrlSecurityGuard.SanitizeForDisplay(targetCfg.ResolveSportsHubUrl());
        }

        RenderScannerServiceTabs();
        UpdatePowerMode();

        if (!autoScan)
        {
            return;
        }

        if (initialService.HasValue)
        {
            if (_isScanning)
            {
                _scanCts?.Cancel();
            }
            await RunSingleServiceScanAsync(initialService.Value, showDiagnostics: false);
        }
        else if (!_isScanning)
        {
            await RunFullScanAsync(isManual: true);
        }
    }

    private async Task ExitVisualScannerModeAsync()
    {
        if (!_isVisualScannerOpen)
        {
            return;
        }

        CloseVisualScannerChromeOnly();
        MarkUserActivity();
        UpdatePowerMode();

        if (_orchestrator.CurrentEvent is null)
        {
            PlayerHostGrid.Visibility = Visibility.Collapsed;
            StandbyView.Visibility = Visibility.Visible;
            RenderStandbyDashboard();
        }
        else
        {
            StandbyView.Visibility = Visibility.Collapsed;
            PlayerHostGrid.Visibility = Visibility.Visible;
            var svc = _config.Services.FirstOrDefault(s => s.ServiceId == _orchestrator.CurrentEvent.Event.ServiceId);
            await _webViewManager!.PlayStreamAsync(
                _orchestrator.CurrentEvent.Event.ServiceId,
                _orchestrator.CurrentEvent.Event.WatchUrl,
                svc?.VolumePercent ?? 100,
                _isTheaterMode);
        }

        SyncOverlayState();
    }

    private void RenderScannerServiceTabs()
    {
        ScannerServiceTabsPanel.Children.Clear();
        foreach (var svc in _config.Services)
        {
            var isCurrent = _visualScannerService == svc.ServiceId;
            var countTag = svc.LastLiveCount > 0
                ? $" ({svc.LastLiveCount} live)"
                : svc.LastUpcomingCount > 0
                    ? $" ({svc.LastUpcomingCount} komm.)"
                    : svc.AuthState == ServiceAuthState.LoginRequired
                        ? " (Logga in)"
                        : svc.AuthState == ServiceAuthState.ProfileSelectionRequired
                            ? " (Profil)"
                            : string.Empty;

            var btn = new Button
            {
                Content = $"{svc.DisplayName}{countTag}",
                Style = (Style)FindResource(isCurrent ? "TvAccentButtonStyle" : "TvButtonStyle"),
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 4)
            };
            btn.Click += async (_, _) =>
            {
                MarkUserActivity();
                if (_isScanning)
                {
                    _scanCts?.Cancel();
                }
                _visualScannerService = svc.ServiceId;
                _webViewManager?.ShowScannerService(svc.ServiceId);
                RenderScannerServiceTabs();
                await RunSingleServiceScanAsync(svc.ServiceId, showDiagnostics: false);
            };
            ScannerServiceTabsPanel.Children.Add(btn);
        }
    }

    private static readonly string[] SportsLoungeFacts =
    [
        "💡 Visste du? Tryck ← eller → för att byta skärmsläckarstil live utan att väcka TV:n!",
        "⚽ Rekord: Den snabbaste matchen i Allsvenskans historia hade mål efter bara 9 sekunder.",
        "🏒 SHL-fakta: En slagskottspuck i toppklass når ofta över 160 km/h mot mål.",
        "🏎️ Formel 1: Under ett pitstop byts alla fyra däck på under 2 sekunder.",
        "📺 MatchFlow TV skannar dina tjänster i bakgrunden och startar automatiskt så fort en match går live.",
        "⭐ Tips: Tryck R när du är vaken för att ge favoritlag pluspoäng eller studioprogram minuspoäng!",
        "🎯 Väntar du på en hörnträff i Sportstuds-läget? Varje hörnträff firas med fyrverkeri!",
        "👁️ Tips: Tryck V när som helst för att öppna den visuella live-skannern."
    ];

    private void EnterScreensaverMode(bool allowOverPlayback = false)
    {
        if (_isScreensaverActive || (!allowOverPlayback && _orchestrator.CurrentEvent is not null) || _activeLoginService is not null || _isVisualScannerOpen)
        {
            return;
        }

        _isScreensaverActive = true;
        _screensaverTickCount = 0;
        _overlay?.CloseAllPanels();
        StandbyView.Visibility = Visibility.Collapsed;
        PlayerHostGrid.Visibility = Visibility.Collapsed;
        ScreensaverView.Visibility = Visibility.Visible;

        ApplyScreensaverStyleVisuals();
        UpdateScreensaverContent();
        DriftScreensaverCard(immediate: true);

        _screensaverAnimator?.Start(_config.RankingProfile.ScreensaverStyle, _config.RankingProfile.ScreensaverPureBlackMode);
        _screensaverClockTimer.Start();
        _screensaverDriftTimer.Start();
        UpdatePowerMode();
    }

    private void ExitScreensaverMode()
    {
        if (!_isScreensaverActive)
        {
            return;
        }

        _isScreensaverActive = false;
        _screensaverClockTimer.Stop();
        _screensaverDriftTimer.Stop();
        _screensaverAnimator?.Stop();
        ScreensaverView.Visibility = Visibility.Collapsed;

        if (_activeLoginService is null && !_isVisualScannerOpen)
        {
            if (_orchestrator.CurrentEvent is not null)
            {
                PlayerHostGrid.Visibility = Visibility.Visible;
            }
            else
            {
                StandbyView.Visibility = Visibility.Visible;
            }
        }

        MarkUserActivity();
        UpdatePowerMode();
    }

    private void CycleScreensaverStyle(int delta)
    {
        if (!_isScreensaverActive || _config.RankingProfile.ScreensaverPureBlackMode)
        {
            return;
        }

        var styles = Enum.GetValues<ScreensaverStyle>();
        var idx = Array.IndexOf(styles, _config.RankingProfile.ScreensaverStyle);
        var next = styles[(idx + delta + styles.Length) % styles.Length];
        _config.RankingProfile.ScreensaverStyle = next;
        _store.Save(_config);

        ApplyScreensaverStyleVisuals();
        _screensaverAnimator?.SwitchStyle(next, pureBlackMode: false);
        UpdateScreensaverContent();
    }

    private void ApplyScreensaverStyleVisuals()
    {
        var pureBlack = _config.RankingProfile.ScreensaverPureBlackMode;
        var style = _config.RankingProfile.ScreensaverStyle;

        ScreensaverHeaderRow.Visibility = pureBlack ? Visibility.Collapsed : Visibility.Visible;
        ScreensaverSecondsText.Visibility = pureBlack ? Visibility.Collapsed : Visibility.Visible;
        ScreensaverDateText.Visibility = pureBlack ? Visibility.Collapsed : Visibility.Visible;
        ScreensaverInfoGroup.Visibility = pureBlack ? Visibility.Collapsed : Visibility.Visible;

        if (pureBlack)
        {
            ScreensaverFloatingCard.Background = Brushes.Black;
            ScreensaverFloatingCard.BorderBrush = Brushes.Transparent;
            ScreensaverClockText.Foreground = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
            return;
        }

        ScreensaverFloatingCard.Background = new SolidColorBrush(Color.FromArgb(216, 7, 12, 23));
        ScreensaverFloatingCard.BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));
        ScreensaverClockText.Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249));
        ScreensaverSecondsText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));

        ScreensaverStyleLabel.Text = style switch
        {
            ScreensaverStyle.SportBounce => "⚽ SPORTSTUDS",
            ScreensaverStyle.StadiumBoard => "🏟️ ARENATAVLA",
            ScreensaverStyle.Starfield => "✨ STJÄRNPLAN",
            _ => "⚽ SPORTSTUDS"
        };

        ScreensaverBounceBadge.Visibility = style == ScreensaverStyle.SportBounce ? Visibility.Visible : Visibility.Collapsed;
        if (_screensaverAnimator is not null)
        {
            UpdateBounceBadgeText(_screensaverAnimator.BounceCount, _screensaverAnimator.CornerHits);
        }
    }

    private void OnScreensaverBounceStatsChanged(int bounces, int cornerHits, Color accent)
    {
        if (!_isScreensaverActive || _config.RankingProfile.ScreensaverPureBlackMode)
        {
            return;
        }

        UpdateBounceBadgeText(bounces, cornerHits);
        ScreensaverFloatingCard.BorderBrush = new SolidColorBrush(Color.FromArgb(135, accent.R, accent.G, accent.B));
        ScreensaverSecondsText.Foreground = new SolidColorBrush(accent);
    }

    private void UpdateBounceBadgeText(int bounces, int cornerHits)
    {
        ScreensaverBounceStatsText.Text = cornerHits > 0
            ? $"Studsar: {bounces}  •  🎯 Hörnträffar: {cornerHits}"
            : $"Studsar: {bounces}";
        ScreensaverBounceStatsText.Foreground = new SolidColorBrush(
            cornerHits > 0 ? Color.FromRgb(250, 204, 21) : Color.FromRgb(148, 163, 184));
    }

    private void UpdateScreensaverContent()
    {
        _screensaverTickCount++;
        var svCulture = CultureInfo.GetCultureInfo("sv-SE");
        var now = DateTime.Now;
        ScreensaverClockText.Text = now.ToString("HH:mm", svCulture);
        ScreensaverSecondsText.Text = now.ToString(":ss", svCulture);
        ScreensaverDateText.Text = now.ToString("dddd d MMMM", svCulture);
        ScreensaverRadarDot.Opacity = (_screensaverTickCount % 2 == 0) ? 1.0 : 0.35;
        ScreensaverLiveStatusLabel.Text = _isScanning ? "Skannar tjänster nu..." : "Skannar automatiskt";

        if (IsQuietHoursNow())
        {
            ScreensaverServiceBadge.Visibility = Visibility.Collapsed;
            ScreensaverNextLabel.Text = "🌙 NATTLÄGE AKTIVT";
            ScreensaverCountdownTimerText.Text = $"{_config.RankingProfile.QuietHoursStart}–{_config.RankingProfile.QuietHoursEnd}";
            ScreensaverNextMatchText.Text = "Ingen automatisk uppspelning nattetid — tryck G för att välja manuellt.";
            ScreensaverTickerText.Text = SportsLoungeFacts[(_screensaverTickCount / 10) % SportsLoungeFacts.Length];
            return;
        }

        var nextUpcoming = _orchestrator.UpcomingEvents.FirstOrDefault();
        if (nextUpcoming is not null)
        {
            var svc = _config.Services.FirstOrDefault(s => s.ServiceId == nextUpcoming.Event.ServiceId);
            var badgeColor = TryParseAccentColor(svc?.AccentColorHex, Color.FromRgb(56, 189, 248));
            ScreensaverServiceBadge.Background = new SolidColorBrush(badgeColor);
            ScreensaverServiceBadgeText.Text = nextUpcoming.Event.ServiceName.ToUpperInvariant();
            ScreensaverServiceBadge.Visibility = Visibility.Visible;

            var sportLeague = string.IsNullOrWhiteSpace(nextUpcoming.Event.League)
                ? nextUpcoming.Event.Sport
                : $"{nextUpcoming.Event.Sport} • {nextUpcoming.Event.League}";
            ScreensaverNextLabel.Text = $"NÄSTA MATCH  •  {sportLeague.ToUpperInvariant()}";
            ScreensaverNextMatchText.Text = nextUpcoming.Event.Title;

            if (nextUpcoming.Event.StartTime.HasValue)
            {
                var remaining = nextUpcoming.Event.StartTime.Value - DateTimeOffset.Now;
                if (remaining.TotalSeconds <= 60)
                {
                    ScreensaverCountdownTimerText.Text = "⚡ Startar snart!";
                }
                else if (remaining.TotalHours >= 24)
                {
                    ScreensaverCountdownTimerText.Text = $"⏳ {(int)remaining.TotalDays} d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
                }
                else
                {
                    ScreensaverCountdownTimerText.Text = $"⏳ {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
                }
            }
            else
            {
                ScreensaverCountdownTimerText.Text = "Kommande";
            }

            // Alternate every 9 seconds between upcoming schedule preview and fun sports facts
            var showScheduleLine = _orchestrator.UpcomingEvents.Count > 1 && ((_screensaverTickCount / 9) % 2 == 0);
            if (showScheduleLine)
            {
                var more = _orchestrator.UpcomingEvents
                    .Skip(1)
                    .Take(3)
                    .Select(e =>
                    {
                        var t = e.Event.StartTime.HasValue ? $"{e.Event.StartTime.Value.ToLocalTime():HH:mm} " : "";
                        return $"{t}{e.Event.Title}";
                    });
                ScreensaverTickerText.Text = "Senare: " + string.Join("   •   ", more);
            }
            else
            {
                ScreensaverTickerText.Text = SportsLoungeFacts[(_screensaverTickCount / 9) % SportsLoungeFacts.Length];
            }
        }
        else
        {
            ScreensaverServiceBadge.Visibility = Visibility.Collapsed;
            ScreensaverNextLabel.Text = _isScanning ? "SKANNAR TJÄNSTER" : "VÄNTAR PÅ LIVESPORT";
            ScreensaverCountdownTimerText.Text = "";
            ScreensaverNextMatchText.Text = _isScanning
                ? "Letar efter pågående och kommande matcher..."
                : "Inga planerade matcher hittade just nu — skannar automatiskt.";
            ScreensaverTickerText.Text = SportsLoungeFacts[(_screensaverTickCount / 9) % SportsLoungeFacts.Length];
        }
    }

    private static Color TryParseAccentColor(string? hex, Color fallback)
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

    private void DriftScreensaverCard(bool immediate = false)
    {
        var maxX = Math.Max(140, ActualWidth - ScreensaverFloatingCard.Width - 80);
        var maxY = Math.Max(120, ActualHeight - 380);
        var targetX = _rng.Next(60, (int)maxX);
        var targetY = _rng.Next(50, (int)maxY);

        if (immediate)
        {
            ScreensaverTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ScreensaverTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ScreensaverTranslate.X = targetX;
            ScreensaverTranslate.Y = targetY;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromSeconds(4.5);
        ScreensaverTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(targetX, duration) { EasingFunction = ease });
        ScreensaverTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, duration) { EasingFunction = ease });
    }

    private void MarkUserActivity()
    {
        _lastUserActivityUtc = DateTimeOffset.UtcNow;
    }

    private void UpdatePowerMode()
    {
        var isPlayingOrLogin = _orchestrator.CurrentEvent is not null || _activeLoginService is not null || _isVisualScannerOpen;
        var keepDisplayOn = isPlayingOrLogin
            ? _config.RankingProfile.PreventScreenSleep
            : _config.RankingProfile.PreventScreenSleep && !_config.RankingProfile.AllowDisplaySleepWhenNoSport;

        DisplayPowerService.SetPowerMode(keepDisplayOn, keepSystemAwake: true);
    }

    private void SyncOverlayState()
    {
        if (_overlay is null)
        {
            return;
        }

        _overlay.SyncState(
            _orchestrator.CurrentEvent,
            _orchestrator.IsCurrentPinned,
            _isMuted,
            _isTheaterMode,
            _latestTelemetry,
            _orchestrator.LiveEvents,
            _orchestrator.UpcomingEvents,
            _config.RankingProfile,
            _config.Services,
            _orchestrator.PendingOffer,
            _watchdogNotice);
    }

    private void RenderStandbyDashboard()
    {
        if (IsQuietHoursNow())
        {
            StandbyHeadlineText.Text = $"🌙 Nattläge aktivt ({_config.RankingProfile.QuietHoursStart}–{_config.RankingProfile.QuietHoursEnd})";
        }
        else if (_orchestrator.LiveEvents.Count == 0)
        {
            var needsLoginCount = _config.Services.Count(s => s.Enabled && s.AuthState == ServiceAuthState.LoginRequired);
            StandbyHeadlineText.Text = needsLoginCount > 0
                ? $"Ingen live-sport hittad ännu — {needsLoginCount} tjänst(er) kräver inloggning"
                : "Ingen pågående live-sport hittades just nu";
        }
        else
        {
            StandbyHeadlineText.Text = $"{_orchestrator.LiveEvents.Count} pågående sportsändningar redo";
        }

        StandbyServiceChips.Children.Clear();
        foreach (var svc in _config.Services)
        {
            var statusLabel = svc.AuthState switch
            {
                ServiceAuthState.NotRequired => "Redo",
                ServiceAuthState.Authenticated => $"Inloggad ({svc.LastLiveCount} live)",
                ServiceAuthState.LoginRequired => "Kräver inloggning",
                ServiceAuthState.ProfileSelectionRequired => "Välj profil",
                ServiceAuthState.RateLimitedOrChallenge => "Pausad",
                _ => svc.Enabled ? "Ej skannad" : "Avstängd"
            };

            var btn = new Button
            {
                Content = $"{svc.DisplayName}  •  {statusLabel}",
                Style = (Style)FindResource(svc.AuthState is ServiceAuthState.Authenticated or ServiceAuthState.NotRequired ? "TvAccentButtonStyle" : "TvButtonStyle"),
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(16, 10, 16, 10)
            };
            btn.Click += async (_, _) => { MarkUserActivity(); await EnterLoginModeAsync(svc.ServiceId); };
            StandbyServiceChips.Children.Add(btn);
        }

        StandbyUpcomingList.Children.Clear();
        if (_orchestrator.UpcomingEvents.Count > 0)
        {
            var svCulture = CultureInfo.GetCultureInfo("sv-SE");
            StandbyUpcomingHeader.Visibility = Visibility.Visible;
            foreach (var item in _orchestrator.UpcomingEvents.Take(8))
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var timeTag = item.Event.StartTime.HasValue
                    ? $"  ({FormatUpcomingDateTime(item.Event.StartTime, svCulture)})"
                    : string.Empty;
                var text = new TextBlock
                {
                    Text = $"{item.Event.ServiceName}  •  {item.Event.Sport}  •  {item.Event.Title}{timeTag}",
                    Foreground = Brushes.White,
                    FontSize = 14
                };
                card.Child = text;
                StandbyUpcomingList.Children.Add(card);
            }
        }
        else
        {
            StandbyUpcomingHeader.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatUpcomingDateTime(DateTimeOffset? startTime, CultureInfo svCulture)
    {
        if (!startTime.HasValue)
        {
            return "kommande";
        }

        var local = startTime.Value.ToLocalTime();
        if (local.Date == DateTime.Today)
        {
            return $"idag kl. {local:HH:mm}";
        }
        if (local.Date == DateTime.Today.AddDays(1))
        {
            return $"imorgon kl. {local:HH:mm}";
        }
        return local.ToString("ddd d MMM HH:mm", svCulture);
    }

    private void CursorWatchTimer_Tick(object? sender, EventArgs e)
    {
        var pos = DisplayPowerService.GetCursorPosition();
        var delta = Math.Abs(pos.X - _lastCursorPos.X) + Math.Abs(pos.Y - _lastCursorPos.Y);
        _lastCursorPos = pos;

        if (delta > 14 && IsActive)
        {
            MarkUserActivity();

            if (_isScreensaverActive)
            {
                ExitScreensaverMode();
                return;
            }

            if (_activeLoginService is null && !_isVisualScannerOpen && _orchestrator.CurrentEvent is not null &&
                _overlay is not null && _overlay.ActiveSection == TvOverlayWindow.OverlaySection.None)
            {
                _overlay.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile, autoHideOsd: true);
            }
            return;
        }

        if (!_isScreensaverActive &&
            _orchestrator.CurrentEvent is null &&
            _activeLoginService is null &&
            !_isVisualScannerOpen &&
            _config.RankingProfile.EnableIdleScreensaver &&
            _overlay?.HasVisibleContent != true)
        {
            var idleSeconds = (DateTimeOffset.UtcNow - _lastUserActivityUtc).TotalSeconds;
            if (idleSeconds >= Math.Clamp(_config.RankingProfile.ScreensaverDelaySeconds, 10, 3600))
            {
                EnterScreensaverMode();
            }
        }
    }

    private void OnForwardedShortcutKey(string rawKey)
    {
        Dispatcher.Invoke(() =>
        {
            MarkUserActivity();
            if (_activeLoginService is not null)
            {
                if (rawKey.Equals("Escape", StringComparison.OrdinalIgnoreCase))
                {
                    _ = ExitLoginModeAsync();
                }
                return;
            }

            if (_isVisualScannerOpen)
            {
                if (rawKey.Equals("Escape", StringComparison.OrdinalIgnoreCase) || rawKey.Equals("v", StringComparison.OrdinalIgnoreCase))
                {
                    _ = ExitVisualScannerModeAsync();
                }
                return;
            }

            HandleTvKey(rawKey);
        });
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        MarkUserActivity();

        if (_isScreensaverActive)
        {
            if (e.Key == Key.Right)
            {
                CycleScreensaverStyle(+1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Left)
            {
                CycleScreensaverStyle(-1);
                e.Handled = true;
                return;
            }

            ExitScreensaverMode();
            e.Handled = true;
            return;
        }

        if (_activeLoginService is not null)
        {
            if (e.Key == Key.Escape)
            {
                _ = ExitLoginModeAsync();
                e.Handled = true;
            }
            return;
        }

        if (_isVisualScannerOpen)
        {
            if (e.Key is Key.Escape or Key.V)
            {
                _ = ExitVisualScannerModeAsync();
                e.Handled = true;
            }
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        var keyString = e.Key switch
        {
            Key.G or Key.Tab => "g",
            Key.R => "r",
            Key.L => "l",
            Key.V => "v",
            Key.N or Key.PageUp or Key.MediaNextTrack => "n",
            Key.B or Key.PageDown or Key.MediaPreviousTrack => "b",
            Key.S => "s",
            Key.P => "p",
            Key.I => "i",
            Key.M or Key.VolumeMute => "m",
            Key.T => "t",
            Key.Z => "z",
            Key.H or Key.F1 => "F1",
            Key.F11 => "F11",
            Key.Escape => "Escape",
            Key.Space or Key.MediaPlayPause => "Space",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(keyString) && HandleTvKey(keyString))
        {
            e.Handled = true;
        }
    }

    private bool HandleTvKey(string key)
    {
        if (_overlay is null)
        {
            return false;
        }

        switch (key.ToLowerInvariant())
        {
            case "g":
                _overlay.ShowSection(
                    _overlay.ActiveSection == TvOverlayWindow.OverlaySection.Guide
                        ? TvOverlayWindow.OverlaySection.None
                        : TvOverlayWindow.OverlaySection.Guide,
                    _config.RankingProfile);
                return true;

            case "r":
                _overlay.ShowSection(
                    _overlay.ActiveSection == TvOverlayWindow.OverlaySection.Ranking
                        ? TvOverlayWindow.OverlaySection.None
                        : TvOverlayWindow.OverlaySection.Ranking,
                    _config.RankingProfile);
                return true;

            case "l":
                _overlay.ShowSection(
                    _overlay.ActiveSection == TvOverlayWindow.OverlaySection.Services
                        ? TvOverlayWindow.OverlaySection.None
                        : TvOverlayWindow.OverlaySection.Services,
                    _config.RankingProfile);
                return true;

            case "v":
                _ = EnterVisualScannerModeAsync(null, autoScan: true);
                return true;

            case "i":
                _overlay.ShowSection(
                    _overlay.ActiveSection == TvOverlayWindow.OverlaySection.OsdOnly
                        ? TvOverlayWindow.OverlaySection.None
                        : TvOverlayWindow.OverlaySection.OsdOnly,
                    _config.RankingProfile);
                return true;

            case "m":
                _ = ToggleMuteAsync();
                return true;

            case "t":
                _ = ToggleTheaterModeAsync();
                return true;

            case "z":
                if (_activeLoginService is null && !_isVisualScannerOpen)
                {
                    EnterScreensaverMode(allowOverPlayback: true);
                }
                return true;

            case "f1" or "h":
                _overlay.ShowSection(
                    _overlay.ActiveSection == TvOverlayWindow.OverlaySection.Help
                        ? TvOverlayWindow.OverlaySection.None
                        : TvOverlayWindow.OverlaySection.Help,
                    _config.RankingProfile);
                return true;

            case "n" or "pageup":
                _orchestrator.SwitchNext();
                return true;

            case "b" or "pagedown":
                _orchestrator.SwitchPrevious();
                return true;

            case "s":
                _orchestrator.SkipCurrent();
                return true;

            case "p":
                _orchestrator.TogglePinCurrent();
                SyncOverlayState();
                _overlay.ShowSection(TvOverlayWindow.OverlaySection.OsdOnly, _config.RankingProfile);
                return true;

            case "space":
                _ = _webViewManager?.TogglePlayerPlayPauseAsync();
                return true;

            case "f11":
                SetFullscreen(!_isFullscreen);
                return true;

            case "escape":
                if (_overlay.ActiveSection != TvOverlayWindow.OverlaySection.None)
                {
                    _overlay.CloseAllPanels();
                }
                else if (_orchestrator.PendingOffer is not null)
                {
                    _orchestrator.DismissPendingSwitch();
                }
                return true;

            default:
                return false;
        }
    }

    private void SetFullscreen(bool fullscreen)
    {
        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
        SyncOverlayBounds();
    }

    private void SyncOverlayBounds()
    {
        if (_overlay is null || !IsLoaded)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            _overlay.Hide();
            return;
        }

        try
        {
            var topLeftPhysical = PointToScreen(new Point(0, 0));
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
            {
                var transform = source.CompositionTarget.TransformFromDevice;
                var topLeftDip = transform.Transform(topLeftPhysical);
                _overlay.Left = topLeftDip.X;
                _overlay.Top = topLeftDip.Y;
            }
            else
            {
                _overlay.Left = Left;
                _overlay.Top = Top;
            }

            _overlay.Width = ActualWidth;
            _overlay.Height = ActualHeight;
        }
        catch
        {
            // Ignore transient layout measurements during window startup
        }
    }

    private void MainWindow_BoundsChanged(object? sender, EventArgs e) => SyncOverlayBounds();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        SyncOverlayBounds();
        if (_overlay is not null && WindowState != WindowState.Minimized && _overlay.HasVisibleContent)
        {
            _overlay.Show();
        }
    }

    private async Task CheckForAppUpdateAsync(bool allowAutoInstallIfIdle)
    {
        var result = await _updateManager.CheckAndStageAsync(_config.RankingProfile.UpdateRepository);
        SyncUpdateBanner();

        if (_updateManager.HasStagedUpdate &&
            allowAutoInstallIfIdle &&
            _config.RankingProfile.AutoInstallUpdatesWhenIdle &&
            _updateManager.CanSelfReplaceExecutable &&
            _orchestrator.CurrentEvent is null &&
            _activeLoginService is null &&
            !_isVisualScannerOpen &&
            _overlay?.HasVisibleContent != true)
        {
            await ApplyStagedUpdateAndRestartAsync();
        }
        else if (result.Status == UpdateCheckStatus.UpdateAvailable && _updateManager.HasStagedUpdate)
        {
            _watchdogNotice = $"⬆ Uppdatering {_updateManager.StagedRelease!.TagName} redo — installeras vid paus";
            SyncOverlayState();
        }
    }

    private async Task ApplyStagedUpdateAndRestartAsync()
    {
        if (!_updateManager.HasStagedUpdate)
        {
            return;
        }

        if (!_updateManager.CanSelfReplaceExecutable)
        {
            StandbyScanStatusText.Text = $"Ny version {_updateManager.StagedRelease!.TagName} redo (utvecklarläge — ladda ner release-zip för självuppdatering).";
            SyncUpdateBanner();
            return;
        }

        StandbyScanStatusText.Text = $"⬆ Uppdaterar MatchFlow TV till {_updateManager.StagedRelease!.TagName}...";
        _watchdogNotice = $"⬆ Uppdaterar till {_updateManager.StagedRelease!.TagName} och startar om...";
        SyncOverlayState();
        await Task.Delay(TimeSpan.FromSeconds(2));

        if (_updateManager.TryLaunchStagedUpdateInstaller())
        {
            Application.Current.Shutdown();
        }
    }

    private void SyncUpdateBanner()
    {
        _overlay?.SyncUpdateStatus(_updateManager.CurrentVersion, _updateManager.StatusSummary, _updateManager.StagedRelease);

        if (_updateManager.HasStagedUpdate && _updateManager.StagedRelease is not null)
        {
            StandbyUpdateButton.Content = $"⬆ Uppdatera till {_updateManager.StagedRelease.TagName}";
            StandbyUpdateButton.Visibility = Visibility.Visible;
        }
        else
        {
            StandbyUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _scanTimer.Stop();
        _cursorWatchTimer.Stop();
        _screensaverClockTimer.Stop();
        _screensaverDriftTimer.Stop();
        _screensaverAnimator?.Stop();
        _quietHoursTimer.Stop();
        _updateCheckTimer.Stop();
        _scanCts?.Cancel();
        DisplayPowerService.SetPowerMode(keepDisplayOn: false, keepSystemAwake: false);
        _store.Save(_config);

        if (_webViewManager is not null)
        {
            await _webViewManager.DisposeAsync();
        }
    }

    private void StandbyGuide_Click(object sender, RoutedEventArgs e) { MarkUserActivity(); _overlay?.ShowSection(TvOverlayWindow.OverlaySection.Guide, _config.RankingProfile); }
    private void StandbyRanking_Click(object sender, RoutedEventArgs e) { MarkUserActivity(); _overlay?.ShowSection(TvOverlayWindow.OverlaySection.Ranking, _config.RankingProfile); }
    private void StandbyServices_Click(object sender, RoutedEventArgs e) { MarkUserActivity(); _overlay?.ShowSection(TvOverlayWindow.OverlaySection.Services, _config.RankingProfile); }
    private async void StandbyVisualScanner_Click(object sender, RoutedEventArgs e) { MarkUserActivity(); await EnterVisualScannerModeAsync(null, autoScan: true); }
    private async void StandbyScanNow_Click(object sender, RoutedEventArgs e) { MarkUserActivity(); await RunFullScanAsync(isManual: true); }
    private async void StandbyInstallUpdate_Click(object sender, RoutedEventArgs e) { MarkUserActivity(); await ApplyStagedUpdateAndRestartAsync(); }
    private void StandbyScreensaver_Click(object sender, RoutedEventArgs e) => EnterScreensaverMode();
    private void StandbyFullscreen_Click(object sender, RoutedEventArgs e) { MarkUserActivity(); SetFullscreen(!_isFullscreen); }
    private void ScreensaverView_MouseDown(object sender, MouseButtonEventArgs e) => ExitScreensaverMode();
    private async void ExitLoginMode_Click(object sender, RoutedEventArgs e) => await ExitLoginModeAsync();

    private async void LoginOpenVisualScanner_Click(object sender, RoutedEventArgs e)
    {
        if (_activeLoginService is { } svcId)
        {
            await EnterVisualScannerModeAsync(svcId, autoScan: true);
        }
    }

    private async void LoginRescanService_Click(object sender, RoutedEventArgs e)
    {
        if (_activeLoginService is { } svcId)
        {
            await RunSingleServiceScanAsync(svcId, showDiagnostics: false);
        }
    }

    private async void ScannerOpenLogin_Click(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        var target = _visualScannerService ?? _webViewManager?.CurrentVisibleScannerService ?? StreamingServiceId.DisneyPlus;
        await EnterLoginModeAsync(target);
    }

    private async void ScannerScanAll_Click(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        if (_isScanning)
        {
            _scanCts?.Cancel();
            await Task.Delay(250);
        }
        await RunFullScanAsync(isManual: true);
    }

    private async void ExitVisualScanner_Click(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        await ExitVisualScannerModeAsync();
    }
}
