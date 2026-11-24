using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Generates resilient JavaScript scanners and 3-stage player automation hooks tailored to each streaming site.
/// </summary>
public static class SiteAdapters
{
    /// <summary>
    /// Builds a self-contained JavaScript snippet that inspects the currently loaded sports hub page
    /// inside an authenticated WebView2 session, filters out unsubscribed add-on/paywall items,
    /// avoids generic league hub links when direct match cards exist, and returns normalized events as JSON.
    /// </summary>
    public static string BuildScannerScript(StreamingServiceId serviceId)
    {
        var serviceKey = serviceId.ToString();
        return $$"""
(() => {
  const SERVICE = "{{serviceKey}}";
  const result = {
    service: SERVICE,
    url: location.origin + location.pathname,
    pageTitle: document.title || "",
    authState: "unknown",
    events: []
  };

  const bodyText = (document.body?.innerText || "").slice(0, 8000);
  const lowerUrl = location.href.toLowerCase();

  // Detect auth / profile / rate-limit gate cleanly without interacting with credentials or challenges
  const hasPasswordField = !!document.querySelector('input[type="password"]');
  const loginUrlHit = /\/login|\/signin|\/logga-in|\/auth|accounts\.|sign-in/.test(lowerUrl);
  const challengeHit =
    /verify you are human|too many requests|access denied|checking your browser|captcha|rate limit/i.test(bodyText) ||
    !!document.querySelector('iframe[src*="captcha" i], iframe[src*="turnstile" i]');
  const profilePickerHit =
    /who's watching|vem tittar|välj profil|choose profile|select profile/i.test(bodyText) &&
    document.querySelectorAll('[data-uia*="profile"], [class*="profile-icon"], [data-testid*="profile"]').length > 0;

  if (challengeHit) {
    result.authState = "rate_limited_or_challenge";
    return JSON.stringify(result);
  } else if (profilePickerHit) {
    result.authState = "profile_picker";
  } else if (hasPasswordField || (loginUrlHit && !document.querySelector('a[href*="/sport"], a[href*="/video"]'))) {
    result.authState = "login_required";
  } else if (SERVICE === "SvtPlay") {
    result.authState = "not_required";
  } else {
    const signInCta = Array.from(document.querySelectorAll('a, button')).some(el => {
      const txt = (el.innerText || "").trim().toLowerCase();
      return /^(logga in|sign in|log in)$/.test(txt);
    });
    result.authState = signInCta && !document.querySelector('[aria-label*="profil" i], [data-testid*="avatar" i], [data-uia*="account" i]')
      ? "login_required"
      : "authenticated";
  }

  // Detect unsubscribed add-on / paywall / upsell cards (e.g. Prime Video HBO Max Sport / Viaplay add-on channels)
  const paywallTextRegex = /prenumerera på|starta en .*prenumeration|tilläggsprenumeration|kostnadsfri.*provperiod|kr\/mån|köp eller hyr|uppgradera.*paket|skaffa.*paket|ingår inte i|subscribe to|start your.*free trial|add-on subscription|requires.*subscription|buy or rent|upgrade to watch/i;

  const isLockedOrAddOnCard = (el) => {
    if (!el) return false;
    const text = (el.innerText || "") + " " + (el.getAttribute("aria-label") || "");
    if (paywallTextRegex.test(text)) return true;

    // Prime Video yellow shopping-bag / entitlement lock indicators for add-on channels
    if (SERVICE === "PrimeVideo") {
      if (el.querySelector('[data-testid*="entitlement-icon" i], [data-testid*="store-bag" i], [class*="entitlement-bag" i], [class*="unentitled" i], [aria-label*="prenumerera" i], [aria-label*="köp" i], [aria-label*="subscribe" i], [aria-label*="rent" i]')) {
        return true;
      }
      // Prime Video marks paid add-on tiles with a yellow bag SVG (often using fill="#F5C518" or "#FFCC00" or "lock")
      const svgs = Array.from(el.querySelectorAll('svg, img'));
      const hasStoreBadge = svgs.some(icon => {
        const meta = ((icon.getAttribute("aria-label") || "") + " " + (icon.getAttribute("alt") || "") + " " + (icon.getAttribute("class") || "") + " " + (icon.getAttribute("data-testid") || "")).toLowerCase();
        return /bag|lock|store|purchase|premium|paywall|addon|channel-upsell/.test(meta);
      });
      if (hasStoreBadge) return true;
    }

    // Generic lock / upgrade icon check across services
    if (el.querySelector('[data-testid*="lock" i], [aria-label*="låst" i], [aria-label*="locked" i], [class*="padlock" i]')) {
      return true;
    }

    return false;
  };

  const seen = new Set();
  const addEvent = (evt) => {
    if (!evt || !evt.title) return;
    const title = evt.title.replace(/\s+/g, " ").trim();
    if (title.length < 3) return;
    if (/^(logga in|sign in|hem|home|serier|filmer|sport|sök|search|meny|menu|inställningar|följ|tablå|upptäck)$/i.test(title)) return;
    if (paywallTextRegex.test(title + " " + (evt.subtitle || ""))) return;

    let url = evt.watchUrl || "";
    if (url && url.startsWith("/")) {
      url = location.origin + url;
    }
    const key = (url || title).toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);

    result.events.push({
      title,
      subtitle: (evt.subtitle || "").replace(/\s+/g, " ").trim(),
      sport: evt.sport || "",
      league: evt.league || "",
      status: evt.status || "Live",
      startTimeIso: evt.startTimeIso || "",
      watchUrl: url || location.href,
      thumbnailUrl: evt.thumbnailUrl || ""
    });
  };

  // Strategy 1: Structured JSON-LD SportsEvent / BroadcastEvent
  try {
    document.querySelectorAll('script[type="application/ld+json"]').forEach(script => {
      const parsed = JSON.parse(script.textContent || "null");
      const items = Array.isArray(parsed) ? parsed : (parsed?.itemListElement || [parsed]);
      for (const raw of items) {
        const node = raw?.item || raw;
        if (!node || typeof node !== "object") continue;
        const typeStr = String(node["@type"] || "").toLowerCase();
        if (typeStr.includes("sport") || typeStr.includes("broadcast") || typeStr.includes("video")) {
          const isLive = Boolean(node.isLiveBroadcast) || /live/i.test(String(node.publication?.isLiveBroadcast || ""));
          addEvent({
            title: node.name || node.headline,
            subtitle: node.description || "",
            status: isLive ? "Live" : "Upcoming",
            startTimeIso: node.startDate || "",
            watchUrl: node.url || "",
            thumbnailUrl: typeof node.thumbnailUrl === "string" ? node.thumbnailUrl : ""
          });
        }
      }
    });
  } catch {}

  // Strategy 2: Next.js __NEXT_DATA__ payload inspection (SVT Play, TV4 Play, etc.)
  try {
    const nextDataEl = document.getElementById("__NEXT_DATA__");
    if (nextDataEl?.textContent) {
      const nextJson = JSON.parse(nextDataEl.textContent);
      const walk = (obj, depth) => {
        if (!obj || typeof obj !== "object" || depth > 9) return;
        if (Array.isArray(obj)) {
          for (const item of obj) walk(item, depth + 1);
          return;
        }
        const title = obj.title || obj.name || obj.heading || obj.programTitle;
        const slugOrUrl = obj.urls?.svtplay || obj.url || obj.slug || obj.href || obj.path;
        const liveFlag = obj.live === true || obj.isLive === true || obj.broadcastStatus === "LIVE" || obj.status === "LIVE" ||
                         /live|pågår|direkt/i.test(String(obj.badge || obj.liveLabel || obj.label || ""));
        const upcomingFlag = obj.broadcastStatus === "UPCOMING" || /kommande|snart/i.test(String(obj.badge || ""));
        const entitled = obj.isEntitled !== false && obj.entitled !== false && obj.requiresSubscription !== true;
        if (entitled && typeof title === "string" && typeof slugOrUrl === "string" && (liveFlag || upcomingFlag)) {
          if (/\/video|\/sport|\/program|\/kanal|\/watch|\/detail|\/event|\/livestream|\/match|\/channel/i.test(slugOrUrl)) {
            addEvent({
              title,
              subtitle: obj.description || obj.shortDescription || obj.category || obj.league || "",
              sport: obj.sport || obj.categoryName || "",
              league: obj.league || obj.tournament || "",
              status: liveFlag ? "Live" : "Upcoming",
              startTimeIso: obj.startTime || obj.validFrom || obj.startDate || "",
              watchUrl: slugOrUrl,
              thumbnailUrl: obj.image?.url || obj.images?.landscape || ""
            });
          }
        }
        for (const key of Object.keys(obj)) {
          if (key === "parent") continue;
          walk(obj[key], depth + 1);
        }
      };
      walk(nextJson, 0);
    }
  } catch {}

  // Strategy 3: DOM card & link scanning with LIVE / DIREKT / PÅGÅR badges + service-aware URL filters
  const isLikelyLeagueHubUrl = (u) => {
    try {
      const parsed = new URL(u);
      const segments = parsed.pathname.split("/").filter(Boolean);
      // Viaplay league pages look like /sport/fotboll/uefa-youth-league (3 segments) whereas actual events have >=4 segments or /player/ or /watch/
      if (SERVICE === "Viaplay" && segments[0] === "sport" && segments.length <= 3 && !/\/player\/|\/watch\//i.test(parsed.pathname)) {
        return true;
      }
      return false;
    } catch {
      return false;
    }
  };

  const serviceUrlMatchers = {
    SvtPlay: (u) => /svtplay\.se\/(video|kanaler|sport)/i.test(u) || /^\/video\//.test(u),
    Tv4Play: (u) => /tv4play\.se\/(video|program|kanal|sport)/i.test(u) || /^\/(video|program|kanal)\//.test(u),
    Viaplay: (u) => /viaplay\./i.test(u) && /\/sport|\/player|\/watch/i.test(u) && !/\/sport\/?$/i.test(u),
    DisneyPlus: (u) => /disneyplus\.com\/.*(video|play|entity|browse|live)/i.test(u),
    PrimeVideo: (u) => /primevideo\.com\/.*(detail|video|watch|dp|live)/i.test(u),
    Netflix: (u) => /netflix\.com\/(watch|title)/i.test(u),
    Expressen: (u) => /livesport\.expressen\.se|expressen\.se\/(tv|sport)/i.test(u) && !/\/(logga-in|login)\/?$/i.test(u),
    HboMax: (u) => /(hbomax|max)\.com\/.*(sport|video|watch|event|channel|live|show)/i.test(u) && !/\/(login|signin)\/?$/i.test(u)
  };
  const isCandidateUrl = serviceUrlMatchers[SERVICE] || (() => true);

  const liveBadgeRegex = /\b(live|direkt|pågår|live nu|watching now|sänds nu)\b/i;
  const upcomingRegex = /\b(kommande|börjar|startar|kl\.?\s*\d{1,2}[:.]\d{2}|i kväll|idag\s+\d{1,2}[:.]\d{2}|imorgon)\b/i;

  const anchorCandidates = Array.from(document.querySelectorAll('a[href]'));
  for (const a of anchorCandidates) {
    const href = a.getAttribute("href") || "";
    if (!href || href === "#" || href.startsWith("javascript:")) continue;
    const absHref = href.startsWith("http") ? href : (location.origin + (href.startsWith("/") ? href : "/" + href));
    if (!isCandidateUrl(absHref)) continue;

    // Walk up up to 3 levels to capture surrounding card text & badges
    let card = a;
    for (let step = 0; step < 3 && card.parentElement; step++) {
      const parentText = (card.parentElement.innerText || "").trim();
      if (parentText.length > 0 && parentText.length < 360 && card.parentElement.querySelectorAll('a[href]').length <= 3) {
        card = card.parentElement;
      } else {
        break;
      }
    }

    if (isLockedOrAddOnCard(card) || isLockedOrAddOnCard(a)) continue;

    const rawText = (card.innerText || a.getAttribute("aria-label") || "").trim();
    if (!rawText || rawText.length < 4) continue;

    // Avoid picking bare Viaplay league hub links when they don't carry a match matchup or time range
    if (isLikelyLeagueHubUrl(absHref) && !/\b\d{1,2}[:.]\d{2}\b|\s-\s|\svs\.?\s/i.test(rawText)) {
      continue;
    }

    const badgeText = Array.from(card.querySelectorAll('[class*="live" i], [data-testid*="live" i], [aria-label*="live" i], span, div'))
      .slice(0, 12)
      .map(el => (el.textContent || "").trim())
      .join(" ");

    const hasLiveSignal = liveBadgeRegex.test(rawText) || liveBadgeRegex.test(badgeText) ||
                          !!card.querySelector('[class*="live" i], [data-testid*="live" i]');
    const hasUpcomingSignal = !hasLiveSignal && upcomingRegex.test(rawText);

    if (!hasLiveSignal && !hasUpcomingSignal) continue;

    const lines = rawText
      .split(/\r?\n/)
      .map(l => l.trim())
      .filter(l => l.length >= 2 && !/^(live|direkt|pågår|ny|nytt|gratis|ingår|i dag|idag|i morgon|imorgon)$/i.test(l));

    if (lines.length === 0) continue;

    const ariaLabel = a.getAttribute("aria-label") || "";
    const title = (ariaLabel.length > 5 && !/^(spela|play|titta)/i.test(ariaLabel))
      ? ariaLabel
      : (lines.find(l => !/^\d{1,2}[:.]\d{2}\s*[-–]\s*\d{1,2}[:.]\d{2}$/.test(l)) || lines[0]);
    const subtitle = lines.filter(l => l !== title).slice(0, 3).join(" • ");
    const img = card.querySelector("img");

    addEvent({
      title,
      subtitle,
      status: hasLiveSignal ? "Live" : "Upcoming",
      watchUrl: absHref,
      thumbnailUrl: img?.currentSrc || img?.src || ""
    });
  }

  return JSON.stringify(result);
})();
""";
    }

    /// <summary>
    /// Builds a 3-stage script injected into the active player WebView2:
    /// 1) Detects login, profile picker, CAPTCHA/rate-limit, or add-on paywall/subscription gates (never clicks purchase CTAs).
    /// 2) Automatically launches the stream: starts existing video, clicks Watch Live / Play CTAs (and "Titta live" choice modals),
    ///    or drills down from a league/competition hub page directly into the active LIVE match card.
    /// 3) Applies edge-to-edge theater fill, per-service volume/mute, and monitors ended/stalled telemetry.
    /// </summary>
    public static string BuildPlayerAutomationScript(
        StreamingServiceId serviceId,
        double targetVolume = 1.0,
        bool startMuted = false,
        bool enableTheaterFill = true)
    {
        var serviceKey = serviceId.ToString();
        var clampedVol = Math.Clamp(targetVolume, 0.05, 1.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var mutedLiteral = startMuted ? "true" : "false";
        var theaterLiteral = enableTheaterFill ? "true" : "false";

        return $$"""
(() => {
  window.__astvTargetVolume = {{clampedVol}};
  window.__astvMuted = {{mutedLiteral}};
  window.__astvTheaterEnabled = {{theaterLiteral}};

  if (window.__autoSportTvHookInstalled) {
    if (typeof window.__astvApplyAudioAndTheater === "function") {
      window.__astvApplyAudioAndTheater();
    }
    return;
  }
  window.__autoSportTvHookInstalled = true;

  const SERVICE = "{{serviceKey}}";
  let lastPost = 0;
  let playAttemptedCount = 0;
  let drillDownAttempted = 0;
  let stalledCounter = 0;
  let loadingTicksWithoutVideo = 0;
  let lastVideoTime = -1;
  let lastClickMs = 0;
  let lastTrackedHref = location.href;

  const style = document.createElement("style");
  style.id = "__astv-player-style";
  style.textContent = `
    html.__astv-hide-cursor, html.__astv-hide-cursor * { cursor: none !important; }
    html.__astv-theater-active, html.__astv-theater-active body {
      overflow: hidden !important;
      background: #000 !important;
    }
    .__astv-theater-target {
      position: fixed !important;
      inset: 0 !important;
      top: 0 !important;
      left: 0 !important;
      width: 100vw !important;
      height: 100vh !important;
      max-width: 100vw !important;
      max-height: 100vh !important;
      margin: 0 !important;
      padding: 0 !important;
      z-index: 2147483640 !important;
      background: #000 !important;
      object-fit: contain !important;
      transform: none !important;
    }
    .__astv-theater-target video {
      width: 100% !important;
      height: 100% !important;
      max-width: 100vw !important;
      max-height: 100vh !important;
      object-fit: contain !important;
    }
  `;
  (document.head || document.documentElement).appendChild(style);

  const isVisible = (el) => {
    if (!el) return false;
    const rect = el.getBoundingClientRect();
    if (rect.width < 20 || rect.height < 20) return false;
    const cs = window.getComputedStyle(el);
    return cs.display !== "none" && cs.visibility !== "hidden" && parseFloat(cs.opacity || "1") > 0.05;
  };

  const findPrimaryVideo = () => {
    const videos = Array.from(document.querySelectorAll("video")).filter(v => {
      const r = v.getBoundingClientRect();
      return r.width > 120 && r.height > 80;
    });
    if (videos.length === 0) return null;
    return videos.sort((a, b) => (b.clientWidth * b.clientHeight) - (a.clientWidth * a.clientHeight))[0];
  };

  const applyTheaterFill = (video) => {
    document.querySelectorAll(".__astv-theater-target").forEach(el => el.classList.remove("__astv-theater-target"));
    if (!window.__astvTheaterEnabled || !video) {
      document.documentElement.classList.remove("__astv-theater-active");
      return;
    }

    let target = video;
    let parent = video.parentElement;
    for (let depth = 0; depth < 4 && parent && parent !== document.body; depth++) {
      const rect = parent.getBoundingClientRect();
      const vRect = video.getBoundingClientRect();
      const hasNav = parent.querySelector("header, nav, footer");
      if (!hasNav && vRect.width > 120 && rect.width <= vRect.width * 1.35 && rect.height <= vRect.height * 1.45) {
        target = parent;
        parent = parent.parentElement;
      } else {
        break;
      }
    }

    document.documentElement.classList.add("__astv-theater-active");
    target.classList.add("__astv-theater-target");
  };

  const applyAudio = (video) => {
    if (!video) return;
    try {
      const desiredVol = Math.max(0.05, Math.min(1.0, Number(window.__astvTargetVolume ?? 1.0)));
      if (Math.abs(video.volume - desiredVol) > 0.02) {
        video.volume = desiredVol;
      }
      if (video.muted !== Boolean(window.__astvMuted)) {
        video.muted = Boolean(window.__astvMuted);
      }
    } catch {}
  };

  window.__astvApplyAudioAndTheater = () => {
    const video = findPrimaryVideo();
    applyAudio(video);
    applyTheaterFill(video);
  };

  window.__astvSetVolume = (vol, muted) => {
    window.__astvTargetVolume = vol;
    window.__astvMuted = Boolean(muted);
    applyAudio(findPrimaryVideo());
  };

  window.__astvSetTheaterMode = (enabled) => {
    window.__astvTheaterEnabled = Boolean(enabled);
    applyTheaterFill(findPrimaryVideo());
  };

  const paywallCtaRegex = /prenumerera|starta en .*prenumeration|tilläggsprenumeration|provperiod|kr\/mån|köp|hyr|uppgradera|skaffa .*paket|ingår inte i|subscribe|free trial|add-on|buy|rent|upgrade|get access|sign up for/i;

  const detectGate = () => {
    const text = (document.body?.innerText || "").slice(0, 5000);
    const lowerUrl = location.href.toLowerCase();

    if (/verify you are human|too many requests|access denied|checking your browser|captcha|rate limit/i.test(text) ||
        document.querySelector('iframe[src*="captcha" i], iframe[src*="turnstile" i]')) {
      return "rate_limited_or_challenge";
    }
    if (/who's watching|vem tittar|välj profil|choose profile/i.test(text) &&
        document.querySelectorAll('[data-uia*="profile"], [class*="profile" i]').length > 0) {
      return "profile_picker";
    }
    if (document.querySelector('input[type="password"]') || /\/login|\/signin|\/logga-in/.test(lowerUrl)) {
      return "login_required";
    }

    // Detect add-on / extra subscription paywall pages (e.g. Prime Video HBO Max Sport upsell, Viaplay upgrade gate)
    if (!findPrimaryVideo()) {
      const hasPaywallCopy = /starta en .*prenumeration|prenumerera på .* för \d|tilläggsprenumeration|starta din kostnadsfria provperiod|uppgradera ditt paket|ingår inte i ditt paket|subscribe to .* for|start your .*free trial|requires an add-on|add-on subscription/i.test(text);
      const primaryButtons = Array.from(document.querySelectorAll('a, button')).filter(isVisible);
      const hasFreeWatchCta = primaryButtons.some(el => {
        const lbl = ((el.innerText || "") + " " + (el.getAttribute("aria-label") || "")).replace(/\s+/g, " ").trim().toLowerCase();
        return /^(spela|spela upp|titta live|titta nu|se live|play|watch live|watch now|join live|resume|fortsätt titta)$/.test(lbl) && !paywallCtaRegex.test(lbl);
      });
      if (hasPaywallCopy && !hasFreeWatchCta) {
        return "subscription_required";
      }
    }

    return null;
  };

  const tryStartPlayback = () => {
    const primaryVideo = findPrimaryVideo();

    // Stage 1: Video is already present in DOM
    if (primaryVideo) {
      loadingTicksWithoutVideo = 0;
      applyAudio(primaryVideo);
      applyTheaterFill(primaryVideo);
      if (primaryVideo.paused && !primaryVideo.ended && playAttemptedCount < 8) {
        playAttemptedCount++;
        primaryVideo.play().catch(() => {
          // Some players require clicking their internal play overlay button
          const overlayPlay = Array.from(document.querySelectorAll('button, [role="button"]')).find(el => {
            if (!isVisible(el)) return false;
            const lbl = ((el.innerText || "") + " " + (el.getAttribute("aria-label") || "")).trim().toLowerCase();
            return !paywallCtaRegex.test(lbl) && /^(spela|play|titta live|watch live)$/.test(lbl);
          });
          if (overlayPlay) overlayPlay.click();
        });
      }
      return primaryVideo;
    }

    const now = Date.now();
    if (now - lastClickMs < 2200) return null;

    // Stage 2: Event detail page or "Watch Live vs Start Over" modal -> click safe Play / Watch Live CTA
    if (playAttemptedCount < 6) {
      const safePlayRegex = /^(spela|spela upp|titta live|titta nu|se live|se direkt|starta sändning|gå till live|till sändningen|play|watch live|watch now|join live|resume|fortsätt titta|fortsätt spela)\b/i;
      const playSelectors = [
        '[data-automation-id*="play" i]',
        '[data-test-id*="play" i]',
        '[data-testid*="play" i]',
        '[data-uia*="play" i]',
        'a[data-rt="play-button"]',
        'button[aria-label*="titta live" i]',
        'button[aria-label*="watch live" i]',
        'button[aria-label*="spela" i]',
        'a[aria-label*="spela" i]',
        'button',
        'a[href]'
      ];

      for (const sel of playSelectors) {
        const candidates = Array.from(document.querySelectorAll(sel));
        const match = candidates.find(el => {
          if (!isVisible(el)) return false;
          const lbl = ((el.innerText || "") + " " + (el.getAttribute("aria-label") || "")).replace(/\s+/g, " ").trim();
          if (!lbl || lbl.length > 70) return false;
          if (paywallCtaRegex.test(lbl) || /trailer|förhandsvisning|följ|follow/i.test(lbl)) return false;
          return safePlayRegex.test(lbl);
        });
        if (match) {
          playAttemptedCount++;
          lastClickMs = now;
          match.click();
          return null;
        }
      }
    }

    // Stage 3: Landed on a League / Competition hub page (e.g. Viaplay "UEFA Youth League" showing Live match cards)
    // Automatically click into the first unlocked LIVE match card so the user enters the actual stream!
    if (drillDownAttempted < 2 && loadingTicksWithoutVideo >= 1) {
      const liveCardLinks = Array.from(document.querySelectorAll('a[href], [role="link"], [role="button"]')).filter(el => {
        if (!isVisible(el)) return false;
        const rect = el.getBoundingClientRect();
        // Match cards are medium/large visual tiles (not small nav links)
        if (rect.width < 140 || rect.height < 80) return false;
        const txt = (el.innerText || "").replace(/\s+/g, " ").trim();
        if (!txt || paywallCtaRegex.test(txt)) return false;
        const hasLive = /\b(live|direkt|pågår)\b/i.test(txt) ||
                        !!el.querySelector('[class*="live" i], [data-testid*="live" i]');
        if (!hasLive) return false;
        // Avoid clicking a link back to the exact same URL we're already on
        const href = el.getAttribute("href");
        if (href && (href === location.pathname || href === location.href)) return false;
        return true;
      });

      if (liveCardLinks.length > 0) {
        // Prefer the first visible Live match card with a play icon or matchup text
        const preferred = liveCardLinks.find(el =>
          el.querySelector('svg, [class*="play" i], img') && /\s-\s|\svs\.?\s|\d{1,2}[:.]\d{2}/i.test(el.innerText || "")
        ) || liveCardLinks[0];

        drillDownAttempted++;
        lastClickMs = now;
        preferred.click();
      }
    }

    return null;
  };

  const detectEndedText = () => {
    const text = (document.body?.innerText || "").toLowerCase();
    return /sändningen har avslutats|sändningen är slut|programmet är slut|live-sändningen har avslutats|stream has ended|this live event has ended|broadcast has ended|tack för att du tittade/.test(text);
  };

  const report = () => {
    const now = Date.now();
    if (now - lastPost < 1500) return;
    lastPost = now;

    // Reset counters whenever an SPA route transition happens (e.g. after drilling down from a league hub into a match)
    if (location.href !== lastTrackedHref) {
      lastTrackedHref = location.href;
      playAttemptedCount = 0;
      loadingTicksWithoutVideo = 0;
      stalledCounter = 0;
      lastVideoTime = -1;
    }

    const gate = detectGate();
    const video = gate ? null : tryStartPlayback();

    let state = "loading";
    let currentTime = 0;
    let isMuted = Boolean(window.__astvMuted);
    let detail = "";

    if (gate) {
      state = gate;
      stalledCounter = 0;
    } else if (detectEndedText()) {
      state = "ended";
      stalledCounter = 0;
    } else if (video) {
      currentTime = Number(video.currentTime || 0);
      isMuted = Boolean(video.muted);
      if (video.ended) {
        state = "ended";
        stalledCounter = 0;
      } else if (!video.paused) {
        if (Math.abs(currentTime - lastVideoTime) < 0.05 || video.readyState < 2) {
          stalledCounter++;
        } else {
          stalledCounter = 0;
        }
        lastVideoTime = currentTime;
        if (stalledCounter >= 12) {
          state = "stalled";
          detail = "Ingen bildrörelse på ~24 sekunder";
        } else {
          state = video.readyState >= 2 ? "playing" : "loading";
        }
      } else {
        state = "paused";
        stalledCounter = 0;
      }
    } else {
      loadingTicksWithoutVideo++;
      if (loadingTicksWithoutVideo >= 14) {
        state = "stalled";
        detail = "Videospelaren laddade inte inom ~28 sekunder";
      }
    }

    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(JSON.stringify({
        serviceId: SERVICE,
        playbackState: state,
        currentTimeSeconds: currentTime,
        hasVideoElement: Boolean(video),
        isMuted,
        pageTitle: document.title || "",
        currentUrl: location.origin + location.pathname,
        detail
      }));
    }
  };

  setInterval(report, 2000);
  setTimeout(report, 800);
})();
""";
    }
}
