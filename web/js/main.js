// main.js — entry point. Owns render/renderPart assembly and application, scroll
// preservation, theme, readingStyle, tocVisibility, tocToggle, banner, error and printMode handling, and sends
// `ready` once and `rendered` per phase (§7.1, §7.3).
//
// All state lives in module-scoped variables (never on `window`): content ids from the
// document can shadow window properties (DOM clobbering), so nothing here may rely on
// implicit globals.

import { send, on, PROTOCOL, log } from "./bridge.js";
import { scrollToFragment } from "./links.js";
import "./links.js"; // side effect: installs the document-level click/auxclick/drop listeners
import { renderToc, setTocVisible, toggleToc } from "./toc.js";
import { showError, hideError } from "./errorview.js";
import { enhanceCode } from "./codeblocks.js";
import { enhanceMath, enhanceDiagrams, setDiagramTheme } from "./enhance.js";

const LARGE_DOC_CHARS = 1_000_000;
const SCROLL_REAPPLY_MS = 500;
const DEFAULT_TITLE = "MdReader";

const htmlEl = document.documentElement;
const mdrMain = document.getElementById("mdr-main");
const mdrContent = document.getElementById("mdr-content");
const mdrBanner = document.getElementById("mdr-banner");

let currentTheme = htmlEl.getAttribute("data-theme") === "dark" ? "dark" : "light";
let printModeOn = false;

// The last fully applied render, used to discard stale/duplicate payloads (§7.1 rule 4)
// AND, via `isCurrentRender` below, to detect that a render has been superseded while
// its own content/enhanced phases were still in flight.
let applied = { docId: null, version: -1 };

// The render currently being assembled from `render` + `renderPart` messages, or null.
let assembling = null;

let scrollRestoreState = null;

// ---------------------------------------------------------------------------------
// render / renderPart assembly (§7.1)
// ---------------------------------------------------------------------------------

function handleRender(msg) {
  // Ignore stale or duplicate payloads for the document currently on screen. A render
  // for a *different* docId (the tab switched documents) always proceeds: its version
  // numbering starts over.
  if (msg.docId === applied.docId && msg.version <= applied.version) return;

  const parts = Math.max(1, msg.parts | 0);
  // Overwriting `assembling` here is what "a render with a newer version discards any
  // incomplete older assembly" means in practice: nothing else references the old object.
  assembling = {
    docId: msg.docId,
    version: msg.version,
    startedAt: performance.now(),
    parts,
    received: 1,
    html: new Array(parts),
    toc: msg.toc || [],
    features: msg.features || { mermaid: false, math: false, code: false },
    preserveScroll: !!msg.preserveScroll,
    scrollToId: msg.scrollToId ?? null,
    banner: msg.banner ?? null,
    title: msg.title,
  };
  assembling.html[0] = msg.html;

  if (assembling.received === assembling.parts) applyAssembly();
}

function handleRenderPart(msg) {
  if (!assembling || assembling.docId !== msg.docId || assembling.version !== msg.version) return;
  if (!Number.isInteger(msg.index) || msg.index < 1 || msg.index >= assembling.parts) return;
  if (assembling.html[msg.index] !== undefined) return; // duplicate part, ignore

  assembling.html[msg.index] = msg.html;
  assembling.received += 1;

  if (assembling.received === assembling.parts) applyAssembly();
}

function applyAssembly() {
  const a = assembling;
  assembling = null;
  applied = { docId: a.docId, version: a.version };
  const renderCtx = { docId: a.docId, version: a.version, start: a.startedAt };
  renderContent(a.html.join(""), a, renderCtx);
}

function isCurrentRender(renderCtx) {
  return applied.docId === renderCtx.docId && applied.version === renderCtx.version;
}

// ---------------------------------------------------------------------------------
// Applying a render: DOM replace, empty placeholder, TOC, banner, scroll, phases
// ---------------------------------------------------------------------------------

function renderContent(html, meta, renderCtx) {
  const isEmpty = html.trim().length === 0;
  const savedScrollTop = meta.preserveScroll ? mdrMain.scrollTop : 0;

  hideError();
  mdrContent.classList.remove("mdr-empty-state");

  if (isEmpty) {
    mdrContent.innerHTML = "";
    mdrContent.classList.add("mdr-empty-state");
    const p = document.createElement("p");
    p.className = "mdr-empty-state__text";
    p.textContent = "This document is empty";
    mdrContent.appendChild(p);
  } else {
    mdrContent.innerHTML = html;
  }

  htmlEl.classList.toggle("mdr-large", html.length > LARGE_DOC_CHARS);

  if (meta.title) document.title = meta.title;

  renderToc(meta.toc);
  setBanner(meta.banner);

  if (meta.preserveScroll) {
    restoreScroll(savedScrollTop);
  } else if (meta.scrollToId) {
    scrollToFragment(meta.scrollToId);
  } else {
    mdrMain.scrollTop = 0;
  }

  scheduleContentPhase(renderCtx, meta.features);
}

function scheduleContentPhase(renderCtx, features) {
  const fire = () => {
    setTimeout(() => {
      sendRendered(renderCtx, "content");
      runEnhancers(renderCtx, features);
    }, 0);
  };
  // Hidden WebViews (a background tab, e.g. a Ctrl+click open) pause
  // requestAnimationFrame entirely, so a render applied while backgrounded would
  // otherwise never reach the "content" phase — and the tab would stay in "Loading"
  // — until the user switches to it. setTimeout keeps firing regardless of visibility.
  if (document.hidden) {
    setTimeout(fire, 0);
  } else {
    requestAnimationFrame(fire);
  }
}

function restoreScroll(target) {
  if (scrollRestoreState) {
    mdrMain.removeEventListener("scroll", scrollRestoreState.listener);
    clearTimeout(scrollRestoreState.timer);
    scrollRestoreState = null;
  }

  const state = { userScrolled: false, restoring: false, listener: null, timer: null };
  state.listener = () => {
    if (state.restoring) {
      state.restoring = false;
      return;
    }
    state.userScrolled = true;
  };
  mdrMain.addEventListener("scroll", state.listener, { passive: true });

  setScrollTop(target, state);

  state.timer = setTimeout(() => {
    if (!state.userScrolled) setScrollTop(target, state);
    mdrMain.removeEventListener("scroll", state.listener);
    if (scrollRestoreState === state) scrollRestoreState = null;
  }, SCROLL_REAPPLY_MS);

  scrollRestoreState = state;
}

function setScrollTop(target, state) {
  const max = Math.max(0, mdrMain.scrollHeight - mdrMain.clientHeight);
  const clamped = Math.min(Math.max(0, target), max);
  if (mdrMain.scrollTop === clamped) {
    // A same-value write fires no `scroll` event at all, so there is no echo for the
    // listener below to consume. Don't arm `restoring` in that case — otherwise it
    // stays stuck `true` forever and the user's next genuine scroll gets misread as
    // our own echo and silently swallowed (finding: scroll restore).
    return;
  }
  state.restoring = true;
  mdrMain.scrollTop = clamped;
}

function sendRendered(renderCtx, phase) {
  // The render this timing/phase belongs to may have been superseded by a newer one
  // while content/enhanced work was still in flight (finding: `rendered` reporting the
  // wrong version). Only report for the render that's actually currently applied.
  if (!isCurrentRender(renderCtx)) return;
  const ms = performance.now() - renderCtx.start;
  send({ type: "rendered", docId: renderCtx.docId, version: renderCtx.version, ms, phase });
}

function runEnhancers(renderCtx, features) {
  if (!isCurrentRender(renderCtx)) return;

  const tasks = [];
  if (features.code) tasks.push(enhanceCode(mdrContent));
  if (features.math) tasks.push(enhanceMath(mdrContent));
  if (features.mermaid) tasks.push(enhanceDiagrams(mdrContent));

  Promise.allSettled(tasks).then((results) => {
    if (!isCurrentRender(renderCtx)) return; // superseded while enhancers were running
    for (const result of results) {
      if (result.status === "rejected") {
        log("warn", "enhancer failed: " + describeError(result.reason));
      }
    }
    sendRendered(renderCtx, "enhanced");
  });
}

function describeError(err) {
  if (err instanceof Error) return err.message;
  try {
    return String(err);
  } catch {
    return "unknown error";
  }
}

// ---------------------------------------------------------------------------------
// banner
// ---------------------------------------------------------------------------------

function setBanner(banner) {
  if (!banner) {
    mdrBanner.hidden = true;
    mdrBanner.textContent = "";
    mdrBanner.className = "mdr-banner";
    return;
  }
  mdrBanner.hidden = false;
  mdrBanner.className = "mdr-banner mdr-banner--" + banner.kind;
  mdrBanner.textContent = banner.text;
}

// ---------------------------------------------------------------------------------
// theme
// ---------------------------------------------------------------------------------

function applyTheme(theme) {
  currentTheme = theme;
  htmlEl.setAttribute("data-theme", theme);
  htmlEl.style.setProperty("color-scheme", theme);
  if (!printModeOn) setDiagramTheme(theme);
}

// Reading style (§14): CSS keys the structure colors off `data-style`; anything but
// "classic" means colorful (the default, also when the attribute is missing).
function applyReadingStyle(style) {
  htmlEl.setAttribute("data-style", style === "classic" ? "classic" : "colorful");
}

// ---------------------------------------------------------------------------------
// print mode
// ---------------------------------------------------------------------------------

function nextFrame() {
  return new Promise((resolve) => requestAnimationFrame(() => resolve()));
}

async function enterPrintMode() {
  printModeOn = true;
  htmlEl.setAttribute("data-print", "");
  await setDiagramTheme("light");
  await nextFrame();
  send({ type: "printModeReady", enabled: true });
}

function exitPrintMode() {
  if (!printModeOn) return;
  printModeOn = false;
  htmlEl.removeAttribute("data-print");
  setDiagramTheme(currentTheme);
}

window.addEventListener("afterprint", () => {
  if (printModeOn) exitPrintMode();
});

// ---------------------------------------------------------------------------------
// CSP violation reporting
// ---------------------------------------------------------------------------------

// A sanitizer miss or a vendor script tripping the CSP (blocked `eval`, a stray remote
// fetch, ...) should be visible in the host's logs, but a chatty/misbehaving page could
// otherwise flood them — throttle to a handful per second and fold the rest into one
// "N more suppressed" line per window.
const CSP_LOG_WINDOW_MS = 1000;
const CSP_LOG_MAX_PER_WINDOW = 10;
let cspWindowStart = 0;
let cspWindowCount = 0;
let cspSuppressedInWindow = 0;

window.addEventListener("securitypolicyviolation", (event) => {
  const now = performance.now();
  if (now - cspWindowStart >= CSP_LOG_WINDOW_MS) {
    if (cspSuppressedInWindow > 0) {
      log("warn", "CSP: " + cspSuppressedInWindow + " more violation(s) suppressed");
    }
    cspWindowStart = now;
    cspWindowCount = 0;
    cspSuppressedInWindow = 0;
  }

  if (cspWindowCount >= CSP_LOG_MAX_PER_WINDOW) {
    cspSuppressedInWindow += 1;
    return;
  }
  cspWindowCount += 1;

  log(
    "warn",
    "CSP: " +
      event.violatedDirective +
      " blocked " +
      event.blockedURI +
      " (" +
      event.sourceFile +
      ":" +
      event.lineNumber +
      ")"
  );
});

// ---------------------------------------------------------------------------------
// wiring + ready
// ---------------------------------------------------------------------------------

on("render", handleRender);
on("renderPart", handleRenderPart);
on("theme", (msg) => applyTheme(msg.theme === "dark" ? "dark" : "light"));
on("readingStyle", (msg) => applyReadingStyle(msg.style));
on("scrollTo", (msg) => scrollToFragment(msg.id));
on("tocVisibility", (msg) => setTocVisible(!!msg.visible));
on("tocToggle", () => toggleToc());
on("banner", (msg) => setBanner(msg.banner ?? null));
on("error", (msg) => {
  assembling = null;
  renderToc([]);
  setBanner(null);
  document.title = DEFAULT_TITLE;
  showError(msg);
});
on("printMode", (msg) => {
  if (msg.enabled) {
    enterPrintMode();
  } else {
    exitPrintMode();
  }
});

send({ type: "ready", protocol: PROTOCOL });
