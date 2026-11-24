using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AutomatiskSportTV.App.Services;

/// <summary>
/// Manages per-service isolated WebView2 environments, NTFS-restricted persistent cookie profiles,
/// strict HTTPS/domain guards, per-service audio normalization, theater-fill mode, and background scanning.
/// </summary>
public sealed class WebViewHostManager : IAsyncDisposable
{
    private readonly Panel _playerHost;
    private readonly Panel _scannerHost;
    private readonly string _profilesRoot;
    private readonly Dictionary<StreamingServiceId, CoreWebView2Environment> _environments = [];
    private readonly Dictionary<StreamingServiceId, WebView2> _playerViews = [];
    private readonly Dictionary<StreamingServiceId, WebView2> _scannerViews = [];
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private StreamingServiceId? _activePlayerService;
    private StreamingServiceId? _loginViewService;
    private StreamingServiceId? _visibleScannerService;
    private string? _activeWatchUrl;
    private double _activeTargetVolume = 1.0;
    private bool _isMuted;
    private bool _theaterModeEnabled = true;
    private bool _visualScannerMode;

    public WebViewHostManager(Panel playerHost, Panel scannerHost)
    {
        _playerHost = playerHost;
        _scannerHost = scannerHost;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appRoot = Path.Combine(localAppData, "AutomatiskSportTV");
        _profilesRoot = Path.Combine(appRoot, "Profiles");

        DirectorySecurityHelper.RestrictDirectoryToCurrentUser(appRoot);
        DirectorySecurityHelper.RestrictDirectoryToCurrentUser(_profilesRoot);
    }

    public bool IsMuted => _isMuted;
    public bool TheaterModeEnabled => _theaterModeEnabled;
    public bool IsVisualScannerMode => _visualScannerMode;
    public StreamingServiceId? CurrentVisibleScannerService => _visibleScannerService;

    public event Action<PlaybackTelemetry>? TelemetryReceived;
    public event Action<string>? ShortcutKeyForwarded;
    public event Action<StreamingServiceId, string, string>? ScannerProgressChanged;

    /// <summary>
    /// Navigates the specified service's player WebView2 to a validated HTTPS stream URL and brings it to front.
    /// </summary>
    public async Task PlayStreamAsync(StreamingServiceId serviceId, string watchUrl, int volumePercent = 100, bool theaterMode = true)
    {
        if (!UrlSecurityGuard.IsTrustedServiceUrl(serviceId, watchUrl))
        {
            return;
        }

        _loginViewService = null;
        _activeTargetVolume = Math.Clamp(volumePercent, 10, 100) / 100.0;
        _theaterModeEnabled = theaterMode;
        _activeWatchUrl = watchUrl;

        var targetView = await EnsurePlayerViewAsync(serviceId);

        // Unload any previously active player so only one stream plays at a time
        foreach (var (id, view) in _playerViews)
        {
            if (id == serviceId)
            {
                view.Visibility = Visibility.Visible;
                view.IsHitTestVisible = true;
            }
            else
            {
                view.Visibility = Visibility.Collapsed;
                view.IsHitTestVisible = false;
                if (view.CoreWebView2 is not null && view.Source?.AbsoluteUri != "about:blank")
                {
                    view.CoreWebView2.Navigate("about:blank");
                }
            }
        }

        _activePlayerService = serviceId;
        targetView.CoreWebView2.IsMuted = _isMuted;
        targetView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        targetView.CoreWebView2.Navigate(watchUrl);
    }

    /// <summary>
    /// Reloads the active stream (used by the stalled-playback watchdog or network recovery).
    /// </summary>
    public void ReloadActiveStream()
    {
        if (_activePlayerService is { } id &&
            !string.IsNullOrWhiteSpace(_activeWatchUrl) &&
            _playerViews.TryGetValue(id, out var view) &&
            view.CoreWebView2 is not null)
        {
            view.CoreWebView2.Navigate(_activeWatchUrl);
        }
    }

    /// <summary>
    /// Toggles or sets global mute on the active player WebView2 and embedded video element.
    /// </summary>
    public async Task SetMuteAsync(bool muted)
    {
        _isMuted = muted;
        if (_activePlayerService is { } id && _playerViews.TryGetValue(id, out var view) && view.CoreWebView2 is not null)
        {
            view.CoreWebView2.IsMuted = muted;
            var volStr = _activeTargetVolume.ToString("0.00", CultureInfo.InvariantCulture);
            var mutedLiteral = muted ? "true" : "false";
            await view.CoreWebView2.ExecuteScriptAsync($"if (window.__astvSetVolume) window.__astvSetVolume({volStr}, {mutedLiteral});");
        }
    }

    /// <summary>
    /// Updates active video volume immediately when user adjusts a service's volume level.
    /// </summary>
    public async Task SetActiveVolumeAsync(StreamingServiceId serviceId, int volumePercent)
    {
        if (_activePlayerService != serviceId)
        {
            return;
        }

        _activeTargetVolume = Math.Clamp(volumePercent, 10, 100) / 100.0;
        if (_playerViews.TryGetValue(serviceId, out var view) && view.CoreWebView2 is not null)
        {
            var volStr = _activeTargetVolume.ToString("0.00", CultureInfo.InvariantCulture);
            var mutedLiteral = _isMuted ? "true" : "false";
            await view.CoreWebView2.ExecuteScriptAsync($"if (window.__astvSetVolume) window.__astvSetVolume({volStr}, {mutedLiteral});");
        }
    }

    /// <summary>
    /// Toggles edge-to-edge CSS theater fill inside the active stream page.
    /// </summary>
    public async Task SetTheaterModeAsync(bool enabled)
    {
        _theaterModeEnabled = enabled;
        if (_activePlayerService is { } id && _playerViews.TryGetValue(id, out var view) && view.CoreWebView2 is not null)
        {
            var literal = enabled ? "true" : "false";
            await view.CoreWebView2.ExecuteScriptAsync($"if (window.__astvSetTheaterMode) window.__astvSetTheaterMode({literal});");
        }
    }

    /// <summary>
    /// Stops active video playback (e.g. when no live sport is active or quiet hours start).
    /// </summary>
    public void StopActivePlayback()
    {
        foreach (var view in _playerViews.Values)
        {
            view.Visibility = Visibility.Collapsed;
            view.IsHitTestVisible = false;
            if (view.CoreWebView2 is not null && view.Source?.AbsoluteUri != "about:blank")
            {
                view.CoreWebView2.Navigate("about:blank");
            }
        }
        _activePlayerService = null;
        _activeWatchUrl = null;
    }

    /// <summary>
    /// Opens a service interactively inside the main viewport so the user can sign in or select a profile.
    /// </summary>
    public async Task OpenInteractiveLoginAsync(ServiceConfig service)
    {
        var targetUrl = service.AuthState is ServiceAuthState.Authenticated or ServiceAuthState.NotRequired or ServiceAuthState.ProfileSelectionRequired
            ? service.ResolveSportsHubUrl()
            : service.LoginUrl;

        if (!UrlSecurityGuard.IsSafeHttpsUrl(targetUrl))
        {
            targetUrl = service.ResolveSportsHubUrl();
        }
        if (!UrlSecurityGuard.IsSafeHttpsUrl(targetUrl))
        {
            return;
        }

        _loginViewService = service.ServiceId;
        var view = await EnsurePlayerViewAsync(service.ServiceId);

        foreach (var (id, v) in _playerViews)
        {
            if (id == service.ServiceId)
            {
                v.Visibility = Visibility.Visible;
                v.IsHitTestVisible = true;
            }
            else
            {
                v.Visibility = Visibility.Collapsed;
                v.IsHitTestVisible = false;
                if (id != _activePlayerService && v.CoreWebView2 is not null && v.Source?.AbsoluteUri != "about:blank")
                {
                    v.CoreWebView2.Navigate("about:blank");
                }
            }
        }

        view.CoreWebView2.IsMuted = false;
        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        view.CoreWebView2.Navigate(targetUrl);
        view.Focus();
    }

    public void ExitInteractiveLogin()
    {
        _loginViewService = null;
        if (_activePlayerService is { } activeId && _playerViews.TryGetValue(activeId, out var activeView))
        {
            foreach (var (id, v) in _playerViews)
            {
                v.Visibility = id == activeId ? Visibility.Visible : Visibility.Collapsed;
                v.IsHitTestVisible = id == activeId;
            }
            if (activeView.CoreWebView2 is not null)
            {
                activeView.CoreWebView2.IsMuted = _isMuted;
                activeView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            }
        }
        else
        {
            StopActivePlayback();
        }
    }

    /// <summary>
    /// Wipes cookies, local storage, cache, and cached auth state for one service's isolated profile.
    /// </summary>
    public async Task ClearServiceSessionAsync(StreamingServiceId serviceId)
    {
        if (_activePlayerService == serviceId)
        {
            StopActivePlayback();
        }

        var view = await EnsurePlayerViewAsync(serviceId);
        if (view.CoreWebView2 is not null)
        {
            view.CoreWebView2.Navigate("about:blank");
            await view.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }

        if (_scannerViews.TryGetValue(serviceId, out var scanner) && scanner.CoreWebView2 is not null)
        {
            scanner.CoreWebView2.Navigate("about:blank");
        }
    }

    /// <summary>
    /// Toggles Visual Scanner Mode so the user can directly watch (and interact with) the live WebView2 scanner.
    /// </summary>
    public void SetVisualScannerMode(bool enabled, StreamingServiceId? focusService = null)
    {
        _visualScannerMode = enabled;
        if (focusService.HasValue)
        {
            _visibleScannerService = focusService.Value;
        }

        if (enabled)
        {
            if (!_visibleScannerService.HasValue && _scannerViews.Count > 0)
            {
                _visibleScannerService = _scannerViews.Keys.First();
            }

            foreach (var (id, view) in _scannerViews)
            {
                var isTarget = id == _visibleScannerService;
                view.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
                view.Width = isTarget ? double.NaN : 1280;
                view.Height = isTarget ? double.NaN : 720;
                view.IsHitTestVisible = isTarget;
                if (view.CoreWebView2 is not null)
                {
                    view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = isTarget;
                }
            }
        }
        else
        {
            foreach (var view in _scannerViews.Values)
            {
                view.Visibility = Visibility.Visible;
                view.Width = 1280;
                view.Height = 720;
                view.IsHitTestVisible = false;
                if (view.CoreWebView2 is not null)
                {
                    view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    if (_scanLock.CurrentCount > 0 && view.Source?.AbsoluteUri != "about:blank")
                    {
                        view.CoreWebView2.Navigate("about:blank");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Brings a specific service's scanner WebView2 to the foreground when Visual Scanner Mode is open.
    /// </summary>
    public void ShowScannerService(StreamingServiceId serviceId)
    {
        _visibleScannerService = serviceId;
        if (!_visualScannerMode)
        {
            return;
        }

        foreach (var (id, view) in _scannerViews)
        {
            var isTarget = id == serviceId;
            view.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
            view.Width = isTarget ? double.NaN : 1280;
            view.Height = isTarget ? double.NaN : 720;
            view.IsHitTestVisible = isTarget;
            if (view.CoreWebView2 is not null)
            {
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = isTarget;
            }
        }
    }

    /// <summary>
    /// Navigates the scanner WebView2 for one service, polls until SPA content hydrates, and reads live sports.
    /// </summary>
    public async Task<ServiceScanResult> ScanServiceAsync(ServiceConfig service, CancellationToken ct = default)
    {
        var scanUrl = service.ResolveSportsHubUrl();
        if (!UrlSecurityGuard.IsTrustedServiceUrl(service.ServiceId, scanUrl))
        {
            service.LastError = "Ogiltig eller osäker HTTPS-adress för tjänsten.";
            ScannerProgressChanged?.Invoke(service.ServiceId, "Ogiltig HTTPS-adress", string.Empty);
            return new ServiceScanResult
            {
                ServiceId = service.ServiceId,
                AuthState = ServiceAuthState.Error
            };
        }

        await _scanLock.WaitAsync(ct);
        try
        {
            var scanner = await EnsureScannerViewAsync(service.ServiceId);
            ShowScannerService(service.ServiceId);

            var sanitizedTarget = UrlSecurityGuard.SanitizeForDisplay(scanUrl);
            ScannerProgressChanged?.Invoke(service.ServiceId, $"Laddar {service.DisplayName}...", sanitizedTarget);

            await NavigateScannerAndWaitAsync(scanner, scanUrl, ct);

            // Smart SPA hydration polling so slow client-rendered apps (especially Disney+, TV4 Play, Expressen) finish loading tiles
            var maxHydrationSeconds = service.ServiceId switch
            {
                StreamingServiceId.DisneyPlus => 11.0,
                StreamingServiceId.Tv4Play or StreamingServiceId.Expressen or StreamingServiceId.HboMax or StreamingServiceId.PrimeVideo or StreamingServiceId.Netflix => 7.5,
                _ => 4.0
            };

            ScannerProgressChanged?.Invoke(
                service.ServiceId,
                $"Väntar på att {service.DisplayName} laddar matcher...",
                UrlSecurityGuard.SanitizeForDisplay(scanner.Source?.AbsoluteUri ?? scanUrl));

            var probe = await WaitForSpaHydrationAsync(scanner, service.ServiceId, maxHydrationSeconds, ct);

            // Disney+ isolates profile selection in sessionStorage per tab; if an unlocked profile picker appears, select it once and reopen sports hub
            if (service.ServiceId == StreamingServiceId.DisneyPlus && probe.Gate == "profile")
            {
                ScannerProgressChanged?.Invoke(
                    service.ServiceId,
                    "Disney+ visar profilväljare — försöker välja upplåst profil...",
                    UrlSecurityGuard.SanitizeForDisplay(probe.Url));

                var rawPick = await scanner.CoreWebView2.ExecuteScriptAsync(SiteAdapters.BuildDisneyProfileAutoSelectScript());
                var pickStatus = UnwrapJsonString(rawPick);
                if (pickStatus == "clicked")
                {
                    await Task.Delay(TimeSpan.FromSeconds(3.2), ct);
                    ScannerProgressChanged?.Invoke(service.ServiceId, "Profil vald — öppnar Disney+ Sport igen...", sanitizedTarget);
                    await NavigateScannerAndWaitAsync(scanner, scanUrl, ct);
                    probe = await WaitForSpaHydrationAsync(scanner, service.ServiceId, 10.0, ct);
                }
            }

            if (probe.Gate == "none")
            {
                ScannerProgressChanged?.Invoke(
                    service.ServiceId,
                    $"Scrollar sidan för att ladda fler rader ({probe.Cards} kort / {probe.Links} länkar)...",
                    UrlSecurityGuard.SanitizeForDisplay(probe.Url));

                await scanner.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 650);");
                await Task.Delay(TimeSpan.FromMilliseconds(750), ct);

                if (service.ServiceId is StreamingServiceId.DisneyPlus or StreamingServiceId.Tv4Play or StreamingServiceId.Expressen or StreamingServiceId.HboMax or StreamingServiceId.PrimeVideo)
                {
                    await scanner.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 650);");
                    await Task.Delay(TimeSpan.FromMilliseconds(650), ct);
                }

                if (_visualScannerMode)
                {
                    await scanner.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0, 0);");
                }
            }

            ScannerProgressChanged?.Invoke(
                service.ServiceId,
                $"Läser ut matcher från {service.DisplayName}...",
                UrlSecurityGuard.SanitizeForDisplay(scanner.Source?.AbsoluteUri ?? scanUrl));

            var script = SiteAdapters.BuildScannerScript(service.ServiceId);
            var rawJson = await scanner.CoreWebView2.ExecuteScriptAsync(script);
            var parsed = ScanResultParser.Parse(service.ServiceId, service.DisplayName, rawJson);

            var liveCount = parsed.Events.Count(e => e.Status == EventStatus.Live);
            var upcomingCount = parsed.Events.Count(e => e.Status == EventStatus.Upcoming);
            var extractorLabel = string.IsNullOrWhiteSpace(parsed.ExtractorUsed) ? "Standard" : parsed.ExtractorUsed;
            ScannerProgressChanged?.Invoke(
                service.ServiceId,
                $"Klar: {liveCount} live • {upcomingCount} kommande • {parsed.AuthState} ({extractorLabel}, {parsed.LinksInspected} länkar)",
                string.IsNullOrWhiteSpace(parsed.Url) ? sanitizedTarget : parsed.Url);

            // Keep the scanned page visible when Visual Scanner Mode is open so the user can inspect it directly
            if (!_visualScannerMode)
            {
                scanner.CoreWebView2.Navigate("about:blank");
            }

            return parsed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            service.LastError = ex.Message;
            ScannerProgressChanged?.Invoke(service.ServiceId, $"Fel: {ex.Message}", UrlSecurityGuard.SanitizeForDisplay(scanUrl));
            return new ServiceScanResult
            {
                ServiceId = service.ServiceId,
                AuthState = ServiceAuthState.Error
            };
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private sealed record HydrationProbeResult(bool Ready, string Gate, int Links, int Cards, string Url);

    private static async Task NavigateScannerAndWaitAsync(WebView2 scanner, string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            tcs.TrySetResult(args.IsSuccess);
        }

        scanner.CoreWebView2.NavigationCompleted += OnNavCompleted;
        try
        {
            scanner.CoreWebView2.Navigate(url);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(18));

            try
            {
                await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Proceed with partial DOM extraction if page scripts took longer than 18s
            }
        }
        finally
        {
            scanner.CoreWebView2.NavigationCompleted -= OnNavCompleted;
        }
    }

    private async Task<HydrationProbeResult> WaitForSpaHydrationAsync(
        WebView2 scanner,
        StreamingServiceId serviceId,
        double maxSeconds,
        CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1.0, maxSeconds - 1.2));
        var probeScript = SiteAdapters.BuildHydrationProbeScript(serviceId);
        var latest = new HydrationProbeResult(false, "none", 0, 0, scanner.Source?.AbsoluteUri ?? string.Empty);

        while (true)
        {
            try
            {
                var raw = await scanner.CoreWebView2.ExecuteScriptAsync(probeScript);
                var json = UnwrapJsonString(raw);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    latest = new HydrationProbeResult(
                        root.TryGetProperty("ready", out var r) && r.GetBoolean(),
                        root.TryGetProperty("gate", out var g) ? (g.GetString() ?? "none") : "none",
                        root.TryGetProperty("links", out var l) ? l.GetInt32() : 0,
                        root.TryGetProperty("cards", out var c) ? c.GetInt32() : 0,
                        root.TryGetProperty("url", out var u) ? (u.GetString() ?? string.Empty) : string.Empty);

                    if (latest.Ready || latest.Gate != "none")
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Keep polling if page is mid-redirect
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
        }

        // Brief settle pause after first cards render so horizontal sliders finish mounting
        if (latest.Ready && latest.Gate == "none")
        {
            await Task.Delay(TimeSpan.FromMilliseconds(serviceId == StreamingServiceId.DisneyPlus ? 1100 : 600), ct);
        }

        return latest;
    }

    private static string UnwrapJsonString(string? rawScriptOutput)
    {
        if (string.IsNullOrWhiteSpace(rawScriptOutput) || rawScriptOutput == "null")
        {
            return string.Empty;
        }

        var trimmed = rawScriptOutput.Trim();
        if (trimmed.StartsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed) ?? string.Empty;
            }
            catch
            {
                return trimmed.Trim('"');
            }
        }

        return trimmed;
    }

    public async Task TogglePlayerPlayPauseAsync()
    {
        if (_activePlayerService is { } id && _playerViews.TryGetValue(id, out var view) && view.CoreWebView2 is not null)
        {
            await view.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                  const v = document.querySelector('video');
                  if (!v) return;
                  if (v.paused) v.play().catch(()=>{}); else v.pause();
                })();
                """);
        }
    }

    public void FocusActivePlayer()
    {
        if (_activePlayerService is { } id && _playerViews.TryGetValue(id, out var view))
        {
            view.Focus();
        }
    }

    private async Task<CoreWebView2Environment> GetEnvironmentAsync(StreamingServiceId serviceId)
    {
        if (_environments.TryGetValue(serviceId, out var existing))
        {
            return existing;
        }

        var profileFolder = Path.Combine(_profilesRoot, serviceId.ToString());
        DirectorySecurityHelper.RestrictDirectoryToCurrentUser(profileFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            Language = "sv-SE"
        };

        var env = await CoreWebView2Environment.CreateAsync(null, profileFolder, options);
        _environments[serviceId] = env;
        return env;
    }

    private async Task<WebView2> EnsurePlayerViewAsync(StreamingServiceId serviceId)
    {
        if (_playerViews.TryGetValue(serviceId, out var existing))
        {
            return existing;
        }

        var view = new WebView2
        {
            Visibility = Visibility.Collapsed,
            DefaultBackgroundColor = System.Drawing.Color.Black
        };
        _playerHost.Children.Add(view);
        _playerViews[serviceId] = view;

        var env = await GetEnvironmentAsync(serviceId);
        await view.EnsureCoreWebView2Async(env);

        ConfigureHardenedSettings(view.CoreWebView2, serviceId, muteAudio: false, isScanner: false);

        view.CoreWebView2.NavigationCompleted += async (_, args) =>
        {
            if (_loginViewService == serviceId || !args.IsSuccess)
            {
                return;
            }
            if (view.Source?.AbsoluteUri == "about:blank")
            {
                return;
            }
            if (!UrlSecurityGuard.IsTrustedServiceUrl(serviceId, view.Source?.AbsoluteUri))
            {
                return;
            }
            await view.CoreWebView2.ExecuteScriptAsync(
                SiteAdapters.BuildPlayerAutomationScript(serviceId, _activeTargetVolume, _isMuted, _theaterModeEnabled));
            await InjectKeyboardForwardingAsync(view.CoreWebView2);
        };

        view.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            if (_loginViewService == serviceId)
            {
                return;
            }
            if (!UrlSecurityGuard.IsTrustedServiceUrl(serviceId, args.Source))
            {
                return;
            }
            HandleWebMessage(serviceId, args.TryGetWebMessageAsString());
        };

        return view;
    }

    private async Task<WebView2> EnsureScannerViewAsync(StreamingServiceId serviceId)
    {
        if (_scannerViews.TryGetValue(serviceId, out var existing))
        {
            return existing;
        }

        var isVisualTarget = _visualScannerMode && _visibleScannerService == serviceId;
        var view = new WebView2
        {
            Width = isVisualTarget ? double.NaN : 1280,
            Height = isVisualTarget ? double.NaN : 720,
            Visibility = !_visualScannerMode || isVisualTarget ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = isVisualTarget,
            DefaultBackgroundColor = System.Drawing.Color.Black
        };
        _scannerHost.Children.Add(view);
        _scannerViews[serviceId] = view;

        var env = await GetEnvironmentAsync(serviceId);
        await view.EnsureCoreWebView2Async(env);

        ConfigureHardenedSettings(view.CoreWebView2, serviceId, muteAudio: true, isScanner: true);
        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = isVisualTarget;
        return view;
    }

    private void ConfigureHardenedSettings(CoreWebView2 core, StreamingServiceId serviceId, bool muteAudio, bool isScanner)
    {
        core.IsMuted = muteAudio;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;

        core.NavigationStarting += (_, e) =>
        {
            var isInteractiveLogin = !isScanner && _loginViewService == serviceId;
            if (!UrlSecurityGuard.IsAllowedNavigation(serviceId, e.Uri, isInteractiveLogin))
            {
                e.Cancel = true;
            }
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var isInteractiveLogin = !isScanner && _loginViewService == serviceId;
            if (!string.IsNullOrWhiteSpace(e.Uri) && UrlSecurityGuard.IsAllowedNavigation(serviceId, e.Uri, isInteractiveLogin))
            {
                core.Navigate(e.Uri);
            }
        };

        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind is CoreWebView2PermissionKind.Notifications
                or CoreWebView2PermissionKind.Geolocation
                or CoreWebView2PermissionKind.Camera
                or CoreWebView2PermissionKind.Microphone
                or CoreWebView2PermissionKind.ClipboardRead)
            {
                e.State = CoreWebView2PermissionState.Deny;
            }
        };
    }

    private static async Task InjectKeyboardForwardingAsync(CoreWebView2 core)
    {
        const string keyForwardScript = """
(() => {
  if (window.__astvKeyForwardInstalled) return;
  window.__astvKeyForwardInstalled = true;
  window.addEventListener('keydown', (e) => {
    const tag = (document.activeElement?.tagName || '').toLowerCase();
    if (tag === 'input' || tag === 'textarea' || document.activeElement?.isContentEditable) return;
    const hotkeys = ['g', 'r', 'l', 'v', 'n', 'b', 'p', 's', 'i', 'm', 't', 'z', 'F11', 'Escape', 'PageUp', 'PageDown'];
    if (hotkeys.includes(e.key) || hotkeys.includes(e.key.toLowerCase())) {
      window.chrome?.webview?.postMessage(JSON.stringify({ __shortcutKey: e.key }));
    }
  }, true);
})();
""";
        await core.ExecuteScriptAsync(keyForwardScript);
    }

    private void HandleWebMessage(StreamingServiceId serviceId, string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawMessage);
            if (doc.RootElement.TryGetProperty("__shortcutKey", out var keyEl))
            {
                var key = keyEl.GetString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    ShortcutKeyForwarded?.Invoke(key);
                }
                return;
            }

            var telemetry = JsonSerializer.Deserialize<PlaybackTelemetry>(rawMessage, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (telemetry is not null)
            {
                telemetry.ServiceId = serviceId;
                telemetry.CurrentUrl = UrlSecurityGuard.SanitizeForDisplay(telemetry.CurrentUrl);
                telemetry.ReceivedAtUtc = DateTimeOffset.UtcNow;
                TelemetryReceived?.Invoke(telemetry);
            }
        }
        catch
        {
            // Ignore malformed messages from third-party site scripts
        }
    }

    public ValueTask DisposeAsync()
    {
        _scanLock.Dispose();
        foreach (var view in _playerViews.Values)
        {
            view.Dispose();
        }
        foreach (var view in _scannerViews.Values)
        {
            view.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
