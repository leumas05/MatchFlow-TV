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
    private string? _activeWatchUrl;
    private double _activeTargetVolume = 1.0;
    private bool _isMuted;
    private bool _theaterModeEnabled = true;

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

    public event Action<PlaybackTelemetry>? TelemetryReceived;
    public event Action<string>? ShortcutKeyForwarded;

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
    /// Navigates the background scanner WebView2 for one service, waits for SPA hydration, and reads live sports.
    /// </summary>
    public async Task<ServiceScanResult> ScanServiceAsync(ServiceConfig service, CancellationToken ct = default)
    {
        var scanUrl = service.ResolveSportsHubUrl();
        if (!UrlSecurityGuard.IsTrustedServiceUrl(service.ServiceId, scanUrl))
        {
            service.LastError = "Ogiltig eller osäker HTTPS-adress för tjänsten.";
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
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                tcs.TrySetResult(args.IsSuccess);
            }

            scanner.CoreWebView2.NavigationCompleted += OnNavCompleted;
            try
            {
                scanner.CoreWebView2.Navigate(scanUrl);
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

            // Client-only SPAs (TV4 Tablå, Expressen/StayLive, Disney+) need a slightly longer hydration wait
            var initialWaitSec = service.ServiceId is StreamingServiceId.Tv4Play or StreamingServiceId.Expressen or StreamingServiceId.DisneyPlus
                ? 4.2
                : 3.0;
            await Task.Delay(TimeSpan.FromSeconds(initialWaitSec), ct);
            await scanner.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 600);");
            await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);

            var script = SiteAdapters.BuildScannerScript(service.ServiceId);
            var rawJson = await scanner.CoreWebView2.ExecuteScriptAsync(script);

            scanner.CoreWebView2.Navigate("about:blank");

            return ScanResultParser.Parse(service.ServiceId, service.DisplayName, rawJson);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            service.LastError = ex.Message;
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

        var view = new WebView2
        {
            Width = 1280,
            Height = 720,
            DefaultBackgroundColor = System.Drawing.Color.Black
        };
        _scannerHost.Children.Add(view);
        _scannerViews[serviceId] = view;

        var env = await GetEnvironmentAsync(serviceId);
        await view.EnsureCoreWebView2Async(env);

        ConfigureHardenedSettings(view.CoreWebView2, serviceId, muteAudio: true, isScanner: true);
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
    const hotkeys = ['g', 'r', 'l', 'n', 'b', 'p', 's', 'i', 'm', 't', 'z', 'F11', 'Escape', 'PageUp', 'PageDown'];
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
