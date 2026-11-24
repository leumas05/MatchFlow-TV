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
    extractorUsed: "",
    linksInspected: document.querySelectorAll('a[href]').length,
    pageSnippet: "",
    events: []
  };

  const bodyText = (document.body?.innerText || "").slice(0, 8000);
  const lowerUrl = location.href.toLowerCase();
  result.pageSnippet = bodyText
    .replace(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi, "[redacted]")
    .replace(/\s+/g, " ")
    .trim()
    .slice(0, 260);

  // Detect auth / profile / rate-limit gate cleanly without interacting with credentials or challenges
  const hasPasswordField = !!document.querySelector('input[type="password"]');
  const loginUrlHit = /\/login|\/signin|\/logga-in|\/auth|accounts\.|sign-in|identity\/login/.test(lowerUrl);
  const challengeHit =
    /verify you are human|too many requests|access denied|checking your browser|captcha|rate limit/i.test(bodyText) ||
    !!document.querySelector('iframe[src*="captcha" i], iframe[src*="turnstile" i]');
  const profilePickerHit =
    /select-profile|who's watching|vem tittar|välj profil|choose profile|select profile/i.test(lowerUrl + " " + bodyText) &&
    document.querySelectorAll('[data-uia*="profile"], [class*="profile-icon"], [data-testid*="profile"], [data-gv2elementkey*="profile"]').length > 0;

  if (challengeHit) {
    result.authState = "rate_limited_or_challenge";
    return JSON.stringify(result);
  } else if (profilePickerHit) {
    result.authState = "profile_picker";
  } else if (hasPasswordField || (loginUrlHit && !document.querySelector('a[href*="/sport"], a[href*="/video"], a[href*="/entity-"]'))) {
    result.authState = "login_required";
  } else if (SERVICE === "SvtPlay") {
    result.authState = "not_required";
  } else {
    const signInCta = Array.from(document.querySelectorAll('a, button')).some(el => {
      const txt = (el.innerText || "").trim().toLowerCase();
      return /^(logga in|sign in|log in|skaffa disney\+)$/.test(txt);
    });
    const hasLoggedInCue = !!document.querySelector('[aria-label*="profil" i], [data-testid*="avatar" i], [data-testid*="profile-avatar" i], [data-uia*="account" i], [data-testid="nav-profile"]');
    result.authState = signInCta && !hasLoggedInCue
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

  // =========================================================================
  // Dedicated per-site schedule extractors (verified against real site schemas)
  // =========================================================================

  // 1. SVT Play (?tab=schedule&scheduleCategory=all) — window.URQL_DATA schedule JSON
  const scanSvtPlay = () => {
    let found = 0;
    try {
      const urql = window.URQL_DATA;
      if (urql && typeof urql === "object") {
        for (const entry of Object.values(urql)) {
          if (!entry || typeof entry.data !== "string" || !entry.data.includes("categoryPage")) continue;
          const parsed = JSON.parse(entry.data);
          const tabs = parsed?.categoryPage?.lazyLoadedTabs || [];
          const schedTab = tabs.find(t => t?.slug === "schedule") || tabs.find(t => t?.schedule?.schedule?.sections);
          const sections = schedTab?.schedule?.schedule?.sections || [];
          for (const sec of sections) {
            // Skip past days and distant weeks (keep all liveNow + upcoming within ~48 hours)
            const listId = sec?.analytics?.json?.listId || "";
            const dateMatch = listId.match(/(\d{4}-\d{2}-\d{2})/);
            const dateStr = dateMatch ? dateMatch[1] : "";
            const isPastSection = Boolean(sec?.daysAgoLabel) && !/idag|imorgon/i.test(sec?.heading || "");
            const listIndex = Number(sec?.analytics?.json?.listIndex || 0);

            for (const mod of (sec?.modules || [])) {
              const items = mod?.subsection?.scheduleItems || [];
              for (const item of items) {
                const isLive = item?.liveNow === true;
                if (isPastSection && !isLive) continue;
                if (!isLive && listIndex > 3) continue;
                const path = item?.urls?.svtplay || "";
                if (!path) continue;
                const heading = (item.heading || "").trim();
                const decoText = (item.decorations?.icon?.text || item.decorations?.text || "").trim();
                const sub = (item.subHeading || "").trim();
                const detail = decoText || sub;
                const detailIsJustTime = /^(ikväll|imorgon|idag|[a-zåäö]{3}\s+\d{1,2}\s+[a-zåäö]{3})\s+\d{1,2}[:.]\d{2}$/i.test(detail);
                const title = (detail && !detailIsJustTime) ? `${heading} — ${detail}` : heading;
                const timeLabel = (item.timeLabel || "").trim();
                const startTimeIso = (dateStr && /^\d{1,2}:\d{2}$/.test(timeLabel))
                  ? `${dateStr}T${timeLabel.padStart(5, "0")}:00`
                  : "";
                addEvent({
                  title,
                  subtitle: [sub, item.durationFormatted].filter(Boolean).join(" • "),
                  status: isLive ? "Live" : "Upcoming",
                  startTimeIso,
                  watchUrl: path
                });
                found++;
              }
            }
          }
        }
      }
    } catch {}
    return found > 0;
  };

  // 2. Viaplay (/sport#sport-schedule-top) — __NEXT_DATA__ viaplay:blocks -> viaplay:products
  const scanViaplay = () => {
    let found = 0;
    try {
      const nextEl = document.getElementById("__NEXT_DATA__");
      if (!nextEl?.textContent) return false;
      const nd = JSON.parse(nextEl.textContent);
      const store = nd?.props?.pageProps?.storeState;
      if (store?.user && typeof store.user.isLoggedIn === "boolean") {
        result.authState = store.user.isLoggedIn ? "authenticated" : "login_required";
      }

      const blocks =
        store?.page?.page?.raw?._embedded?.["viaplay:blocks"] ||
        store?.page?.raw?._embedded?.["viaplay:blocks"] ||
        [];

      const sportSlugMap = {
        fotboll: "Fotboll",
        ishockey: "Ishockey",
        motorsport: "Motorsport",
        vintersport: "Vintersport",
        handboll: "Handboll",
        tennis: "Tennis",
        basket: "Basket",
        golf: "Golf",
        kampsport: "Kampsport",
        "amerikansk-fotboll": "Amerikansk fotboll",
        cykling: "Cykling",
        friidrott: "Friidrott",
        snooker: "Snooker"
      };

      const nowMs = Date.now();
      for (const block of blocks) {
        const products = block?._embedded?.["viaplay:products"] || [];
        for (const p of products) {
          if (!p || p.type !== "sport" || !p.publicPath) continue;
          const plan = p.system?.availability?.planInfo;
          if (plan?.isRental === true || plan?.isPurchase === true) continue;

          const flags = Array.isArray(p.system?.flags) ? p.system.flags : [];
          const startIso = p.epg?.streamStart || p.epg?.start || "";
          const endIso = p.epg?.streamEnd || p.epg?.end || "";
          const startMs = startIso ? Date.parse(startIso) : NaN;
          const endMs = endIso ? Date.parse(endIso) : NaN;

          const isLive = flags.includes("isLive") || (!Number.isNaN(startMs) && !Number.isNaN(endMs) && nowMs >= startMs && nowMs <= endMs);
          const isUpcoming = !isLive && !Number.isNaN(startMs) && startMs > nowMs;
          if (!isLive && !isUpcoming) continue;

          const title = (p.content?.title || "").trim();
          if (!title) continue;

          const slugParts = String(p.publicPath).replace(/^\/+/, "").split("/");
          const mappedSport = sportSlugMap[(slugParts[0] || "").toLowerCase()] || "";
          const league = (p.content?.format?.title || "").trim();
          const subtitle = [league, p.content?.originalTitle].filter(Boolean).join(" • ");

          addEvent({
            title,
            subtitle,
            sport: mappedSport,
            league,
            status: isLive ? "Live" : "Upcoming",
            startTimeIso: startIso,
            watchUrl: "https://viaplay.se/sport/" + String(p.publicPath).replace(/^\/+/, "")
          });
          found++;
        }
      }
    } catch {}
    return found > 0;
  };

  // 3. Disney+ Sport Hub (/sv-se/browse/page-1eaf1f02-deb4-48d0-b54f-cbf7a843e8f3) — stitchDocument + Authenticated SPA DOM
  const scanDisneyPlus = () => {
    let found = 0;
    const extractText = (node) => {
      if (!node) return "";
      if (typeof node === "string") return node;
      if (Array.isArray(node)) return node.map(extractText).join(" ");
      if (typeof node === "object") return extractText(node.children || node.text || node.label || node.value || "");
      return "";
    };

    const pad2 = (n) => String(n).padStart(2, "0");
    const formatDateOffset = (daysOffset) => {
      const d = new Date();
      d.setDate(d.getDate() + daysOffset);
      return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
    };

    const nonLiveShelfRegex = /repris|dokumentär|berättelser|serier|filmer|feel-good|highlights|höjdpunkter|replay|movies|series|originals|extras|klipp|sammandrag|arkiv|recap/i;
    const liveShelfRegex = /live och kommande|live & upcoming|live nu|live now|live-sport|live på|pågår|kommande|live-evenemang|live events|direkt|sport|espn|uefa|europa league|conference league|champions league|laliga|nhl/i;
    const disneyFutureOnlyRegex = /\b(kommer|kommande|upcoming|imorgon|i morgon|börjar om|startar om|nästa vecka)\b|\b\d{1,2}\/\d{1,2}\s*\|/i;
    const disneyUpcomingTimeRegex = /\b(kommer|kommande|upcoming|idag|i dag|imorgon|i morgon|mån|tis|ons|tor|fre|lör|sön|\d{1,2}\/\d{1,2}|\d{1,2}[:.]\d{2})\b/i;
    const disneyLiveBadgeRegex = /\b(live nu|live now|direkt|pågår|sänds nu|började för \d+|started \d+ min)/i;
    const disneyStandaloneLiveRegex = /(^|[\s•|,\-–])live($|[\s•|,\-–])/i;

    const swedishMonthMap = {
      jan: 1, januari: 1, feb: 2, februari: 2, mar: 3, mars: 3, apr: 4, april: 4,
      maj: 5, may: 5, jun: 6, juni: 6, jul: 7, juli: 7, aug: 8, augusti: 8,
      sep: 9, sept: 9, september: 9, okt: 10, oct: 10, oktober: 10,
      nov: 11, november: 11, dec: 12, december: 12
    };

    const stripShelfPhrase = (str) =>
      String(str || "").replace(/live\s+(och|&)\s+(kommande|upcoming)|live[- ]?evenemang|live[- ]?sport/gi, " ");

    const parseStartIso = (str) => {
      const s = String(str || "");
      const timeMatch = s.match(/\b(\d{1,2})[:.](\d{2})\b/);
      if (!timeMatch) return "";
      const hh = pad2(Number(timeMatch[1]));
      const mm = timeMatch[2];
      const now = new Date();
      let datePart = "";

      // 1) Explicit Swedish/English long date: "25 november 2026" or "26 nov"
      const longDateMatch = s.match(/\b(\d{1,2})\s+(januari|februari|mars|april|maj|juni|juli|augusti|september|oktober|november|december|jan|feb|mar|apr|may|jun|jul|aug|sept?|okt|oct|nov|dec)\b(?:\s+(\d{4}))?/i);
      if (longDateMatch) {
        const day = Number(longDateMatch[1]);
        const month = swedishMonthMap[longDateMatch[2].toLowerCase()];
        const year = longDateMatch[3] ? Number(longDateMatch[3]) : now.getFullYear();
        if (month && day >= 1 && day <= 31) {
          datePart = `${year}-${pad2(month)}-${pad2(day)}`;
        }
      }

      // 2) Explicit slash date: "25/11" or "26/11"
      if (!datePart) {
        const slashDateMatch = s.match(/\b(\d{1,2})\/(\d{1,2})\b/);
        if (slashDateMatch) {
          const day = Number(slashDateMatch[1]);
          const month = Number(slashDateMatch[2]);
          if (month >= 1 && month <= 12 && day >= 1 && day <= 31) {
            let year = now.getFullYear();
            // Handle Dec -> Jan year rollover
            if (now.getMonth() + 1 === 12 && month === 1) year++;
            datePart = `${year}-${pad2(month)}-${pad2(day)}`;
          }
        }
      }

      // 3) Relative date ("imorgon" vs today)
      if (!datePart) {
        datePart = /\b(imorgon|i morgon)\b/i.test(s) ? formatDateOffset(1) : formatDateOffset(0);
      }

      return `${datePart}T${hh}:${mm}:00`;
    };

    const cleanDisneyTitleAndContext = (rawTitle) => {
      let t = String(rawTitle || "").replace(/\s+/g, " ").trim();
      const extraContext = [];

      // Strip leading status/date/time accessibility text from Disney+ aria-labels
      t = t
        .replace(/^(började för \d+\s+(minut|minuter|timme|timmar)\s+sedan|started \d+\s+(minute|minutes|hour|hours)\s+ago)\s*[.,:\-–|•]?\s*/i, "")
        .replace(/^\d{1,2}\/\d{1,2}\s*\|\s*\d{1,2}[:.]\d{2}\s*/i, "")
        .replace(/^\d{1,2}\s+(januari|februari|mars|april|maj|juni|juli|augusti|september|oktober|november|december|jan|feb|mar|apr|jun|jul|aug|sep|okt|nov|dec)\.?\s*(\d{4})?\s*[•·,\-–|]?\s*/i, "")
        .replace(/^(\d{1,2}[:.]\d{2}\s*[-–—]\s*\d{1,2}[:.]\d{2}\s*)+/i, "")
        .replace(/^\d{1,2}[:.]\d{2}\s*(\d{1,2}[:.]\d{2}\s*[-–—]\s*\d{1,2}[:.]\d{2})?\s*[•·,\-–|]?\s*/i, "")
        .replace(/^(live now|live nu|live|kommer|kommande|upcoming|idag|i dag|imorgon|i morgon)\s*[.,:\-–|•]?\s*/i, "");

      // Strip trailing screen-reader boilerplate ("Premiär 2026. Välj för mer information om denna titel.", "Choose Feed Entry")
      t = t
        .replace(/\s*(välj för mer information om denna titel\.?|choose feed entry\.?|select for more information.*|premiär\s*\d{4}\.?|new\s+in\s+\d{4}\.?)\s*/gi, " ")
        .replace(/\s*[.,:\-–|•]?\s*(live now|live nu|live|kommer|kommande|upcoming)$/i, "")
        .replace(/\s+/g, " ")
        .trim();

      // Move trailing "ESPN <league/sport>" suffix into subtitle so match title stays crisp ("Kairat Almaty - Mjällby")
      const espnIdx = t.search(/\s+ESPN\b/i);
      if (espnIdx > 4) {
        extraContext.push(t.slice(espnIdx).trim());
        t = t.slice(0, espnIdx).trim();
      }

      return { title: t, extraContext: extraContext.join(" • ") };
    };

    // Pass A: Public / Next.js SSR stitchDocument ("Live och kommande" slider)
    try {
      const nextEl = document.getElementById("__NEXT_DATA__");
      if (nextEl?.textContent) {
        const nd = JSON.parse(nextEl.textContent);
        const stitch = nd?.props?.pageProps?.stitchDocument?.mainContent;
        const walkSliders = (node, depth) => {
          if (!node || typeof node !== "object" || depth > 12) return;
          if (Array.isArray(node)) {
            for (const x of node) walkSliders(x, depth + 1);
            return;
          }
          if (node._type === "Slider") {
            const sliderTitle = extractText(node.title).trim();
            const isLiveOrUpcomingSlider = /live|kommande|upcoming|pågår/i.test(sliderTitle) && !nonLiveShelfRegex.test(sliderTitle);
            if (isLiveOrUpcomingSlider) {
              const walkCards = (c, d2) => {
                if (!c || typeof c !== "object" || d2 > 6) return;
                if (Array.isArray(c)) { for (const x of c) walkCards(x, d2 + 1); return; }
                if (c._type === "ImageCard" && c.title && c.url) {
                  const badge = extractText(c.badgeText).trim();
                  const cleanBadge = stripShelfPhrase(badge);
                  const isLive = (disneyLiveBadgeRegex.test(cleanBadge) || disneyStandaloneLiveRegex.test(cleanBadge)) && !disneyFutureOnlyRegex.test(cleanBadge);
                  const cleaned = cleanDisneyTitleAndContext(c.title);
                  addEvent({
                    title: cleaned.title || c.title,
                    subtitle: [cleaned.extraContext, badge, sliderTitle].filter(Boolean).join(" • "),
                    status: isLive ? "Live" : "Upcoming",
                    startTimeIso: isLive ? "" : parseStartIso(badge + " " + c.title),
                    watchUrl: c.url
                  });
                  found++;
                  return;
                }
                for (const v of Object.values(c)) walkCards(v, d2 + 1);
              };
              walkCards(node.children, 0);
            }
            return;
          }
          for (const v of Object.values(node)) walkSliders(v, depth + 1);
        };
        walkSliders(stitch, 0);
        if (found > 0) result.extractorUsed = "DisneyPlus:stitchDocument";
      }
    } catch {}

    // Pass B: Authenticated Disney+ Web SPA DOM (both <a href> and non-anchor React tiles + Hero banners)
    if (found === 0) {
      const cardSelector = [
        'a[href*="/entity-"]',
        'a[href*="/browse/entity-"]',
        'a[href*="/video/"]',
        'a[href*="/play/"]',
        '[data-testid="set-item"]',
        '[data-testid*="content-tile" i]',
        '[data-testid*="collection-tile" i]',
        '[data-gv2elementkey*="tile" i]',
        '[data-gv2elementkey*="card" i]'
      ].join(", ");

      const resolveDisneyUrl = (el) => {
        const anchor = el.matches?.('a[href]') ? el : (el.querySelector?.('a[href]') || el.closest?.('a[href]'));
        const href = anchor?.getAttribute("href") || "";
        if (href && !/\/browse\/page-/.test(href)) return href;

        const directId =
          el.getAttribute?.("data-entity-id") ||
          el.getAttribute?.("data-item-id") ||
          el.getAttribute?.("data-content-id") ||
          "";
        if (directId) {
          const cleanId = directId.replace(/^entity-/, "");
          return `/sv-se/browse/entity-${cleanId}`;
        }

        const gv2 = el.getAttribute?.("data-gv2interactiondata") || el.querySelector?.("[data-gv2interactiondata]")?.getAttribute("data-gv2interactiondata") || "";
        const entityMatch = gv2.match(/entity-[0-9a-f-]{8,}/i);
        if (entityMatch) return `/sv-se/browse/${entityMatch[0]}`;

        return href && !/\/browse\/page-/.test(href) ? href : "";
      };

      const parseDisneyCard = (el, shelfTitle, requireLiveOrUpcomingSignal) => {
        const card = el.closest?.('[data-testid="set-item"], [data-testid*="content-tile" i], [data-testid*="collection-tile" i], li, article') || el;
        const href = resolveDisneyUrl(card) || resolveDisneyUrl(el);

        const anchor = el.matches?.('a[href]') ? el : card.querySelector?.('a[href]');
        const aria = (el.getAttribute?.("aria-label") || anchor?.getAttribute("aria-label") || card.getAttribute?.("aria-label") || "").trim();
        const imgAlt = (card.querySelector?.("img")?.getAttribute("alt") || "").trim();
        const rawText = (card.innerText || "").replace(/\s+/g, " ").trim();
        const rawLines = (card.innerText || "").split(/\r?\n/).map(l => l.trim()).filter(Boolean);
        const combined = `${aria} ${rawText}`;
        const cleanedForLive = stripShelfPhrase(combined);

        const hasFutureOnly = disneyFutureOnlyRegex.test(cleanedForLive);
        const hasUpcomingCue = disneyUpcomingTimeRegex.test(combined);
        const hasLiveDomBadge = !!card.querySelector?.('[data-testid*="live" i], [class*="live-badge" i], [class*="liveBadge" i], [aria-label*="live" i]');
        const hasStandaloneLiveLine = rawLines.some(l => /^(●\s*)?(live|live nu|live now|direkt|pågår|sänds nu)$/i.test(l));
        const hasStartedAgoLive = /började för \d+\s+(minut|minuter|timme|timmar)\s+sedan|started \d+\s+(min|hour)/i.test(combined);
        const startIsoCandidate = parseStartIso(combined);
        const parsedStartMs = startIsoCandidate ? Date.parse(startIsoCandidate) : NaN;
        const isFutureDateOrTime = !Number.isNaN(parsedStartMs) && parsedStartMs > Date.now() + 10 * 60 * 1000;
        const hasLiveBadge = (hasStartedAgoLive || hasStandaloneLiveLine || hasLiveDomBadge || disneyLiveBadgeRegex.test(cleanedForLive) || disneyStandaloneLiveRegex.test(cleanedForLive)) && !hasFutureOnly && !isFutureDateOrTime;

        const isLiveOrUpcomingShelf = /live och kommande|live & upcoming|live nu|live now|live-sport|live på|pågår|kommande|live-evenemang|live events/i.test(shelfTitle || "");
        if (requireLiveOrUpcomingSignal && !hasLiveBadge && !hasUpcomingCue) return false;
        if (!hasLiveBadge && !hasUpcomingCue && !isLiveOrUpcomingShelf) return false;

        // Require either a specific entity/video URL OR a real live/upcoming card with clickable role
        const watchUrl = href || (hasLiveBadge ? (location.origin + location.pathname) : "");
        if (!watchUrl) return false;

        const titleLine = rawLines.find(l => l.length >= 3 && !/^(live|live nu|live now|kommer|kommande|upcoming|idag|i dag|imorgon|i morgon|\d{1,2}\/\d{1,2}\s*\|\s*\d{1,2}[:.]\d{2}|\d{1,2}[:.]\d{2}|ny|nytt|espn|sport)$/i.test(l)) || "";
        const rawCandidate = aria || imgAlt || titleLine || rawText;
        const cleaned = cleanDisneyTitleAndContext(rawCandidate);
        const title = cleaned.title;
        if (!title || title.length < 3) return false;

        const startIso = hasLiveBadge ? "" : startIsoCandidate;
        addEvent({
          title,
          subtitle: [cleaned.extraContext, rawText !== title && rawText.length < 120 ? rawText : "", shelfTitle].filter(Boolean).slice(0, 2).join(" • "),
          status: hasLiveBadge ? "Live" : "Upcoming",
          startTimeIso: startIso,
          watchUrl
        });
        return true;
      };

      // Step B1: Check featured Hero banners at top of Disney+ Sport page (often hosts the headline LIVE game!)
      const heroes = Array.from(document.querySelectorAll('[data-testid*="hero" i], [data-testid*="featured" i], [data-gv2elementkey*="hero" i], section:first-of-type'));
      for (const hero of heroes.slice(0, 3)) {
        const heroText = (hero.innerText || "").replace(/\s+/g, " ").trim();
        if (!heroText || heroText.length > 900) continue;
        const heroClean = stripShelfPhrase(heroText);
        const heroIsLive = disneyLiveBadgeRegex.test(heroClean) && !disneyFutureOnlyRegex.test(heroClean);
        const heroHasWatchLive = !!hero.querySelector('[data-testid="watch-live-button"], [data-testid="play-button"], a[href*="/play/"], a[href*="/entity-"]');
        if (heroIsLive && heroHasWatchLive) {
          const heroLink = hero.querySelector('a[href*="/play/"], a[href*="/video/"], a[href*="/entity-"]')?.getAttribute("href") || (location.origin + location.pathname);
          const heroTitle =
            (hero.querySelector('h1, h2, [data-testid*="title" i]')?.innerText || "").trim() ||
            (hero.querySelector('img[alt]')?.getAttribute("alt") || "").trim();
          if (heroTitle && heroTitle.length >= 3) {
            addEvent({
              title: heroTitle,
              subtitle: "Live på Disney+ (Utvald)",
              status: "Live",
              watchUrl: heroLink
            });
            found++;
          }
        }
      }

      // Step B2: Walk sections/shelves by heading so documentaries/replays are cleanly excluded
      const headings = Array.from(document.querySelectorAll('h1, h2, h3, h4, [role="heading"], [data-testid*="set-title" i]'));
      for (const h of headings) {
        const hText = (h.innerText || "").replace(/\s+/g, " ").trim();
        if (!hText || nonLiveShelfRegex.test(hText) || !liveShelfRegex.test(hText)) continue;

        let container = h.parentElement;
        for (let step = 0; step < 5 && container && container.parentElement; step++) {
          if (container.querySelectorAll(cardSelector).length > 0) break;
          container = container.parentElement;
        }
        if (!container) continue;

        const cards = Array.from(container.querySelectorAll(cardSelector));
        for (const el of cards) {
          if (parseDisneyCard(el, hText, false)) found++;
        }
      }

      // Step B3: Always scan cards with explicit LIVE / Kommer signals outside documentary/replay shelves
      const allCards = Array.from(document.querySelectorAll(cardSelector));
      for (const el of allCards) {
        const sectionHeading = (el.closest?.("section, [data-testid*='set' i], [role='region']")?.querySelector?.("h2, h3, [role='heading'], [data-testid*='set-title' i]")?.innerText || "").trim();
        if (nonLiveShelfRegex.test(sectionHeading)) continue;
        if (parseDisneyCard(el, sectionHeading, true)) found++;
      }

      if (found > 0) result.extractorUsed = "DisneyPlus:DOM";
    }

    return found > 0;
  };

  // 4. TV4 Play Sport Tablå (/sport/tabla?d=YYYY-MM-DD) — dynamic schedule row parser
  const scanTv4Play = () => {
    let found = 0;
    const params = new URLSearchParams(location.search);
    const todayLocal = new Date();
    const pad2 = (n) => String(n).padStart(2, "0");
    const dateStr = params.get("d") || `${todayLocal.getFullYear()}-${pad2(todayLocal.getMonth() + 1)}-${pad2(todayLocal.getDate())}`;
    const nowMs = Date.now();

    const tv4NoiseRegex = /^(sport total|tv4 play\+?(\s*sport(\s*total)?)?|tv4 play|sport|plus|gratis|ingår|reklamfri|paket\s+.*|live|live nu|direkt|pågår|pågår nu|sänds nu|kommande|idag|i dag|imorgon|i morgon|ny|nytt|min lista|lägg till.*|sändningsstart.*|matchstart.*|nedsläpp.*|avspark.*|tv4(\s+sport(\s+live\s*\d+)?)?|tv4\s+(hockey|fotboll|motor|tennis|play)|tv12|sjuan|sportkanalen|\d{1,2}[:.]\d{2}(\s*[-–—]\s*\d{1,2}[:.]\d{2})?)$/i;
    const tv4BareSportMap = {
      ishockey: "Ishockey",
      hockey: "Ishockey",
      fotboll: "Fotboll",
      handboll: "Handboll",
      bandy: "Bandy",
      innebandy: "Innebandy",
      tennis: "Tennis",
      basket: "Basket",
      golf: "Golf",
      motorsport: "Motorsport",
      vintersport: "Vintersport",
      skidskytte: "Vintersport",
      alpint: "Vintersport",
      "längdskidor": "Vintersport",
      friidrott: "Friidrott",
      cykling: "Cykling",
      kampsport: "Kampsport",
      snooker: "Snooker",
      "amerikansk fotboll": "Amerikansk fotboll",
      padel: "Övrigt",
      trav: "Övrigt",
      galopp: "Övrigt",
      esport: "Övrigt",
      "e-sport": "Övrigt"
    };
    const tv4BareLeagueRegex = /^(shl|sdhl|hockeyallsvenskan|hockeyettan|allsvenskan|superettan|damallsvenskan|la liga|laliga|serie a|ligue 1|champions league|uefa champions league|europa league|conference league|nfl|nba|nhl|atp|wta|wrc|formel 1|f1|ssl|sbl|handbollsligan|svenska cupen|v75|v86)$/i;
    const matchupRegex = /\s[-–—]\s|\svs\.?\s|\smot\s/i;

    const humanizeTv4Slug = (href) => {
      try {
        const path = new URL(href, location.origin).pathname;
        const segments = path.split("/").filter(Boolean);
        // TV4 video URLs look like /video/<hex-id>/<match-slug>
        const slug = segments.length >= 3 && segments[0] === "video" ? segments[2] : (segments[segments.length - 1] || "");
        if (!slug || /^[0-9a-f]{8,}$/i.test(slug) || /^(sport|tabla|video|program|kanal)$/i.test(slug)) return "";
        const words = decodeURIComponent(slug)
          .replace(/[-_]+/g, " ")
          .replace(/\s+/g, " ")
          .trim();
        if (words.length < 4 || tv4NoiseRegex.test(words) || Boolean(tv4BareSportMap[words.toLowerCase()])) return "";
        return words.charAt(0).toUpperCase() + words.slice(1);
      } catch {
        return "";
      }
    };

    const cleanAriaCandidate = (raw) => {
      if (!raw) return "";
      const cleaned = raw
        .replace(/\b(paket\s+sport\s+total|paket\s+sport|sport\s+total|tv4\s+play\+?|tv4\s+sport\s+live\s*\d*|live\s+nu|direktsändning|kommande|idag|imorgon)\b/gi, " ")
        .replace(/\b\d{1,2}[:.]\d{2}(\s*[-–—]\s*\d{1,2}[:.]\d{2})?\b/g, " ")
        .replace(/^[\s•,|:\-–]+|[\s•,|:\-–]+$/g, "")
        .replace(/\s+/g, " ")
        .trim();
      if (!cleaned || cleaned.length < 3 || tv4NoiseRegex.test(cleaned) || Boolean(tv4BareSportMap[cleaned.toLowerCase()])) return "";
      return cleaned;
    };

    const scope = document.querySelector("main") || document.body;
    const links = Array.from(scope.querySelectorAll('a[href]'));
    for (const a of links) {
      const href = a.getAttribute("href") || "";
      if (!href || /^\/?(sport(\/tabla)?|kanaler|alla-program|faq)?(\?.*)?$/i.test(href)) continue;
      if (!/\/(video|program|sport|kanal)\//i.test(href)) continue;

      let row = a.closest?.('li, article, [role="listitem"], [data-testid*="schedule" i], [data-testid*="card" i], [data-testid*="event" i]') || a;
      if (row !== a) {
        const rLinks = row.querySelectorAll('a[href]').length;
        const rText = (row.innerText || "").trim();
        if (rLinks > 4 || rText.length > 600) row = a;
      }
      if (row === a) {
        for (let step = 0; step < 4 && row.parentElement; step++) {
          const pLinks = row.parentElement.querySelectorAll('a[href]').length;
          const pText = (row.parentElement.innerText || "").trim();
          if (pLinks <= 3 && pText.length > 0 && pText.length < 500) {
            row = row.parentElement;
          } else {
            break;
          }
        }
      }

      // If this row contains a direct /video/ link, skip secondary /program/ or /sport/ links inside the same row
      if (!/\/video\//i.test(href) && row.querySelector('a[href*="/video/"]')) continue;
      if (isLockedOrAddOnCard(row) || isLockedOrAddOnCard(a)) continue;

      const text = (row.innerText || a.getAttribute("aria-label") || "").trim();
      if (text.length < 4) continue;

      const rawLines = text.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
      const timeMatch = text.match(/\b(\d{1,2})[:.](\d{2})\b/);
      let startTimeIso = "";
      let isUpcoming = false;
      let isFutureStart = false;

      if (timeMatch) {
        const hh = pad2(Number(timeMatch[1]));
        const mm = timeMatch[2];
        startTimeIso = `${dateStr}T${hh}:${mm}:00`;
        const parsedMs = Date.parse(startTimeIso);
        if (!Number.isNaN(parsedMs)) {
          isFutureStart = parsedMs > nowMs + 10 * 60 * 1000;
          if (parsedMs >= nowMs - 15 * 60 * 1000) isUpcoming = true;
        }
      }

      // Strip channel names like "TV4 Sport Live 1" before testing for an actual LIVE status badge
      const textWithoutChannels = text.replace(/tv4\s+sport\s+live(\s*\d+)?/gi, " ");
      const hasLiveBadgeLine = rawLines.some(l => /^(●\s*)?(live|live nu|direkt|pågår|pågår nu|sänds nu)$/i.test(l));
      const hasLiveDomCue = !!row.querySelector('[data-testid*="live-badge" i], [class*="liveBadge" i], [class*="live-badge" i], [aria-label="Live" i]');
      const hasLive = !isFutureStart && (hasLiveBadgeLine || hasLiveDomCue || /\b(live nu|direkt|pågår|sänds nu)\b/i.test(textWithoutChannels));

      if (!hasLive && startTimeIso) {
        const parsedMs = Date.parse(startTimeIso);
        if (!Number.isNaN(parsedMs) && parsedMs < nowMs - 3.5 * 3600 * 1000) continue;
      }
      if (!hasLive && !isUpcoming && !/\bkommande\b|\bbörjar\b/i.test(text)) continue;

      let detectedSport = "";
      let detectedLeague = "";
      const contentLines = [];
      for (const line of rawLines) {
        if (line.length < 2 || tv4NoiseRegex.test(line)) continue;
        const lower = line.toLowerCase();
        if (tv4BareSportMap[lower]) {
          if (!detectedSport) detectedSport = tv4BareSportMap[lower];
          continue;
        }
        if (tv4BareLeagueRegex.test(line) && !detectedLeague) {
          detectedLeague = line;
        }
        contentLines.push(line);
      }

      const headingCandidates = Array.from(row.querySelectorAll('h1, h2, h3, h4, [data-testid*="title" i], [class*="title" i]'))
        .map(el => (el.innerText || "").replace(/\s+/g, " ").trim())
        .filter(t => t.length >= 2 && !tv4NoiseRegex.test(t) && !tv4BareSportMap[t.toLowerCase()]);

      const specificLines = contentLines.filter(l => !tv4BareLeagueRegex.test(l));
      const specificHeadings = headingCandidates.filter(h => !tv4BareLeagueRegex.test(h));

      const ariaCandidate = cleanAriaCandidate(a.getAttribute("aria-label") || a.getAttribute("title") || row.querySelector("img")?.getAttribute("alt") || "");
      const slugCandidate = humanizeTv4Slug(href);

      let title =
        specificLines.find(l => matchupRegex.test(l)) ||
        specificHeadings.find(h => matchupRegex.test(h)) ||
        specificHeadings[0] ||
        specificLines[0] ||
        (ariaCandidate && !tv4BareLeagueRegex.test(ariaCandidate) ? ariaCandidate : "") ||
        slugCandidate ||
        (detectedLeague && detectedSport ? `${detectedLeague} (${detectedSport})` : (detectedLeague || detectedSport));

      if (!title || title.length < 2 || tv4NoiseRegex.test(title)) continue;

      // If title is a generic single word (like "Studio") and we know the league, prefix with league for clarity
      if (detectedLeague && !title.toLowerCase().includes(detectedLeague.toLowerCase()) && /^(studio|försnack|uppsnack|magasin|sändning|direkt)$/i.test(title)) {
        title = `${detectedLeague}: ${title}`;
      }

      const subtitleParts = [];
      if (detectedLeague && !title.toLowerCase().includes(detectedLeague.toLowerCase())) {
        subtitleParts.push(detectedLeague);
      }
      for (const l of contentLines) {
        if (l !== title && l !== detectedLeague && !subtitleParts.includes(l)) {
          subtitleParts.push(l);
        }
      }

      addEvent({
        title,
        subtitle: subtitleParts.slice(0, 3).join(" • "),
        sport: detectedSport,
        league: detectedLeague,
        status: hasLive ? "Live" : "Upcoming",
        startTimeIso,
        watchUrl: href
      });
      found++;
    }
    return found > 0;
  };

  // 5. Expressen Livesport (livesport.expressen.se/sv/live — StayLive React SPA)
  const scanExpressen = () => {
    let found = 0;
    const appRoot = document.getElementById("app") || document.body;
    const navExclusions = /^\/?(sv\/?)?(live|sports?|login|logga-in|search|packages?|account|konto|help|faq|about|clubs?|om|schedule|tabla)?\/?$/i;

    const pad2 = (n) => String(n).padStart(2, "0");
    const formatDateOffset = (daysOffset) => {
      const d = new Date();
      d.setDate(d.getDate() + daysOffset);
      return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
    };

    const upcomingDateTokenRegex = /\b(imorgon|i morgon|idag|i dag|kommande|om \d+ (dag|dagar|tim|timmar|min)|mån|tis|ons|tor|fre|lör|sön|\d{1,2}\s+(jan|feb|mar|apr|maj|jun|jul|aug|sep|okt|nov|dec))\b/i;
    const noiseLineRegex = /^(live|live nu|direkt|pågår|pågår nu|sänds nu|kommande|idag|i dag|imorgon|i morgon|ingår|\+allt|\d{1,2}[:.]\d{2}|(mån|tis|ons|tor|fre|lör|sön)\.?\s+\d{1,2}\s+\w+|\d{1,2}\s+(jan|feb|mar|apr|maj|jun|jul|aug|sep|okt|nov|dec)\w*)$/i;

    const getSectionHint = (el) => {
      let cur = el;
      for (let step = 0; step < 5 && cur && cur !== appRoot; step++) {
        // Only check an immediate sibling/section heading, NOT the global page navbar ("Lokal livesport")
        const heading = cur.querySelector(":scope > h1, :scope > h2, :scope > h3, :scope > div > h2, :scope > div > h3");
        if (heading && !el.contains(heading)) {
          const hText = (heading.innerText || "").trim();
          if (/repris|avslutad|tidigare|arkiv|senaste/i.test(hText)) return "ended";
          if (/^(live nu|pågår nu|sänds nu|pågår live)$/i.test(hText)) return "live";
          if (/kommande|schemalag|idag|imorgon|snart|nästa/i.test(hText)) return "upcoming";
        }
        cur = cur.parentElement;
      }
      return "";
    };

    const links = Array.from(appRoot.querySelectorAll('a[href]'));
    for (const a of links) {
      const href = a.getAttribute("href") || "";
      if (!href || href === "#" || href.startsWith("javascript:")) continue;
      const absHref = href.startsWith("http") ? href : (location.origin + (href.startsWith("/") ? href : "/" + href));
      if (!/livesport\.expressen\.se|expressen\.se\/(tv|sport)/i.test(absHref)) continue;

      try {
        const pathOnly = new URL(absHref).pathname;
        if (navExclusions.test(pathOnly)) continue;
      } catch { continue; }

      let card = a;
      for (let step = 0; step < 3 && card.parentElement; step++) {
        const pLinks = card.parentElement.querySelectorAll('a[href]').length;
        const pText = (card.parentElement.innerText || "").trim();
        if (pLinks <= 2 && pText.length > 0 && pText.length < 360) {
          card = card.parentElement;
        } else {
          break;
        }
      }
      if (isLockedOrAddOnCard(card) || isLockedOrAddOnCard(a)) continue;

      const sectionHint = getSectionHint(card);
      if (sectionHint === "ended") continue;

      const rawText = (card.innerText || a.getAttribute("aria-label") || "").trim();
      if (rawText.length < 4) continue;

      const rawLines = rawText.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
      // Explicit upcoming indicators ALWAYS prevent falsely tagging an event as Live
      const hasExplicitUpcoming = upcomingDateTokenRegex.test(rawText) || sectionHint === "upcoming";
      // StayLive uses "live" in generic CSS classes (staylive-*), so require a real standalone LIVE badge line
      const hasStandaloneLiveBadge = rawLines.some(l => /^(●\s*)?(live|live nu|pågår|pågår nu|sänds nu|direkt)$/i.test(l));

      let startTimeIso = "";
      const timeMatch = rawText.match(/\b(\d{1,2})[:.](\d{2})\b/);
      if (timeMatch) {
        const hh = pad2(Number(timeMatch[1]));
        const mm = timeMatch[2];
        const datePart = /\b(imorgon|i morgon)\b/i.test(rawText) ? formatDateOffset(1) : formatDateOffset(0);
        startTimeIso = `${datePart}T${hh}:${mm}:00`;
      }

      const startMs = startTimeIso ? Date.parse(startTimeIso) : NaN;
      const isFutureTime = !Number.isNaN(startMs) && startMs > Date.now() + 5 * 60 * 1000;

      const isLive = !hasExplicitUpcoming && !isFutureTime && (hasStandaloneLiveBadge || sectionHint === "live");
      const isUpcoming = !isLive && (hasExplicitUpcoming || isFutureTime || Boolean(timeMatch));

      if (!isLive && !isUpcoming) continue;

      const contentLines = rawLines.filter(l => l.length >= 3 && !noiseLineRegex.test(l));
      if (contentLines.length === 0) continue;

      // Prefer a matchup line ("Team A - Team B") if present, otherwise first non-badge content line
      const title = contentLines.find(l => /\s[-–—]\s|\svs\.?\s|\smot\s/i.test(l)) || contentLines[0];
      const subtitle = contentLines.filter(l => l !== title).slice(0, 3).join(" • ");

      addEvent({
        title,
        subtitle,
        status: isLive ? "Live" : "Upcoming",
        startTimeIso,
        watchUrl: absHref
      });
      found++;
    }
    return found > 0;
  };

  let dedicatedSucceeded = false;
  if (SERVICE === "SvtPlay") {
    dedicatedSucceeded = scanSvtPlay();
    if (dedicatedSucceeded) result.extractorUsed = "SvtPlay:URQL";
  } else if (SERVICE === "Viaplay") {
    dedicatedSucceeded = scanViaplay();
    if (dedicatedSucceeded) result.extractorUsed = "Viaplay:NEXT_DATA";
  } else if (SERVICE === "DisneyPlus") {
    dedicatedSucceeded = scanDisneyPlus();
  } else if (SERVICE === "Tv4Play") {
    dedicatedSucceeded = scanTv4Play();
    if (dedicatedSucceeded) result.extractorUsed = "Tv4Play:TablåDOM";
  } else if (SERVICE === "Expressen") {
    dedicatedSucceeded = scanExpressen();
    if (dedicatedSucceeded) result.extractorUsed = "Expressen:StayLiveDOM";
  }

  if (dedicatedSucceeded) {
    return JSON.stringify(result);
  }

  result.extractorUsed = "GenericFallback";

  // Strategy 1 (Fallback for Prime Video, Netflix, HBO Max, or if site schema changes): Structured JSON-LD SportsEvent / BroadcastEvent
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
      .filter(l => l.length >= 2 && !/^(live|direkt|pågår|ny|nytt|gratis|ingår|sport total|tv4 play\+?(\s*sport(\s*total)?)?|i dag|idag|i morgon|imorgon)$/i.test(l));

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
        var clampedVol = Math.Clamp(targetVolume, 0.0, 1.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
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
      const desiredVol = Math.max(0.0, Math.min(1.0, Number(window.__astvTargetVolume ?? 1.0)));
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
    if (/select-profile|who's watching|vem tittar|välj profil|choose profile|select profile/i.test(lowerUrl + " " + text) &&
        document.querySelectorAll('[data-uia*="profile"], [class*="profile" i], [data-testid*="profile"], [data-gv2elementkey*="profile"]').length > 0) {
      return "profile_picker";
    }
    if (document.querySelector('input[type="password"]') || /\/login|\/signin|\/logga-in|identity\/login/.test(lowerUrl)) {
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

      // Detect upcoming countdown pages (e.g. Expressen "01 Dagar 03 Timmar 23 Minuter 05 Sekunder / i morgon")
      const hasCountdown =
        (/\bdagar\b[\s\S]{0,60}\btimmar\b[\s\S]{0,60}\bminuter\b/i.test(text) && /\bsekunder\b/i.test(text)) ||
        (/\bdays\b[\s\S]{0,60}\bhours\b[\s\S]{0,60}\bminutes\b/i.test(text) && /\bseconds\b/i.test(text)) ||
        /sändningen startar om|börjar om \d|starts in \d/i.test(text);
      if (hasCountdown && !hasFreeWatchCta) {
        return "upcoming_not_started";
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
      const safePlayRegex = /^(spela|spela upp|titta live|titta nu|titta|börja titta|se live|se direkt|se nu|starta sändning|gå till live|till sändningen|play|watch live|watch now|join live|resume|fortsätt titta|fortsätt spela)\b/i;
      const playSelectors = [
        '[data-testid="play-button"]',
        '[data-testid="watch-live-button"]',
        '[data-gv2elementkey*="play" i]',
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

    /// <summary>
    /// Builds a lightweight DOM hydration probe used during scanning so slow client-side SPAs
    /// (such as Disney+, TV4 Play, and Expressen) finish rendering tiles before extraction runs.
    /// </summary>
    public static string BuildHydrationProbeScript(StreamingServiceId serviceId)
    {
        var serviceKey = serviceId.ToString();
        return $$"""
(() => {
  const SERVICE = "{{serviceKey}}";
  const bodyText = (document.body?.innerText || "").slice(0, 4000);
  const lowerUrl = location.href.toLowerCase();

  let gate = "none";
  if (/verify you are human|too many requests|access denied|checking your browser|captcha|rate limit/i.test(bodyText) ||
      document.querySelector('iframe[src*="captcha" i], iframe[src*="turnstile" i]')) {
    gate = "challenge";
  } else if (/select-profile|who's watching|vem tittar|välj profil|choose profile|select profile/i.test(lowerUrl + " " + bodyText) &&
             document.querySelectorAll('[data-uia*="profile"], [class*="profile-icon"], [data-testid*="profile"], [data-gv2elementkey*="profile"]').length > 0) {
    gate = "profile";
  } else if (document.querySelector('input[type="password"]') ||
             (/\/login|\/signin|\/logga-in|\/auth|sign-in|identity\/login/.test(lowerUrl) &&
              !document.querySelector('a[href*="/sport"], a[href*="/video"], a[href*="/entity-"]'))) {
    gate = "login";
  }

  const links = document.querySelectorAll('a[href]').length;
  let cards = 0;
  let ready = false;

  if (SERVICE === "SvtPlay") {
    const hasUrql = Boolean(window.URQL_DATA && Object.values(window.URQL_DATA).some(v => typeof v?.data === "string" && v.data.includes("categoryPage")));
    cards = document.querySelectorAll('a[href*="/video/"]').length;
    ready = hasUrql || cards >= 2;
  } else if (SERVICE === "Viaplay") {
    const nextText = document.getElementById("__NEXT_DATA__")?.textContent || "";
    cards = document.querySelectorAll('a[href*="/sport/"]').length;
    ready = nextText.includes("viaplay:products") || cards >= 4;
  } else if (SERVICE === "DisneyPlus") {
    const nextText = document.getElementById("__NEXT_DATA__")?.textContent || "";
    cards = document.querySelectorAll('a[href*="/entity-"], a[href*="/browse/entity-"], a[href*="/video/"], a[href*="/play/"], [data-testid="set-item"], [data-testid*="content-tile" i], [data-testid*="collection-tile" i], [data-gv2elementkey*="tile" i]').length;
    ready = nextText.includes("stitchDocument") || cards >= 2;
  } else if (SERVICE === "Tv4Play") {
    cards = document.querySelectorAll('a[href*="/video/"], a[href*="/program/"], a[href*="/sport/"]').length;
    ready = cards >= 3;
  } else if (SERVICE === "Expressen") {
    cards = document.querySelectorAll('#app a[href], main a[href]').length;
    ready = cards >= 4 && bodyText.trim().length > 80;
  } else {
    cards = links;
    ready = links >= 8 && bodyText.trim().length > 150;
  }

  return JSON.stringify({
    ready,
    gate,
    links,
    cards,
    url: location.origin + location.pathname
  });
})();
""";
    }

    /// <summary>
    /// Selects the first unlocked (non-PIN) Disney+ profile when the background scanner WebView2
    /// lands on /select-profile due to per-tab sessionStorage isolation. Never interacts with PINs or credentials.
    /// </summary>
    public static string BuildDisneyProfileAutoSelectScript() => """
(() => {
  const bodyText = (document.body?.innerText || "").slice(0, 3000);
  if (document.querySelector('input[type="password"], input[inputmode="numeric"], [data-testid*="pin-input" i]') ||
      /ange.*pin|enter.*pin/i.test(bodyText)) {
    return "pin_required";
  }

  const rawCandidates = Array.from(document.querySelectorAll(
    '[data-testid="profile-avatar"], [data-testid*="profile-item" i], [data-gv2elementkey*="profile" i], [class*="profile-avatar" i]'
  ));

  let sawLocked = false;
  for (const el of rawCandidates) {
    const rect = el.getBoundingClientRect();
    if (rect.width < 32 || rect.height < 32) continue;

    const card = el.closest('li, [role="listitem"], [data-testid*="profile" i]') || el;
    const label = ((card.innerText || "") + " " + (el.getAttribute("aria-label") || "") + " " + (card.getAttribute("aria-label") || "")).toLowerCase();
    if (/lägg till|add profile|redigera|edit profile|hantera|manage/i.test(label)) continue;

    const hasLock = Boolean(card.querySelector('[data-testid*="lock" i], [aria-label*="pin" i], [aria-label*="lås" i], [aria-label*="lock" i], [class*="lock" i]'));
    if (hasLock) {
      sawLocked = true;
      continue;
    }

    el.click();
    return "clicked";
  }

  return sawLocked ? "pin_required" : "none";
})();
""";
}
