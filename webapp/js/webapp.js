// webapp.js — the web version's "host" (ARCHITECTURE §15). It plays the part the desktop app
// plays for the page: after `ready` it posts theme and tocVisibility, renders
// documents through POST /api/render and delivers the returned host messages unchanged
// (except docId/version, which it owns), and answers the page's messages: link, copy,
// tocVisibilityChanged, retry, log, drop, rendered, printModeReady, taskToggle. It also owns
// the web-only chrome: the header toolbar, the tab strip, the Edit/Split/Read workspace
// (editor pane, draggable divider, split ratio), file open / drop, and the toast.
//
// Tabs: each tab is one document {id, title, markdown, scrollTop} (plus in-memory-only
// bookkeeping: docId/version for the protocol, a cached `payload` of host messages so
// switching tabs never re-fetches). The workspace view — Edit, Split or Read — is a single
// global preference (like the theme), not per tab: `mode` decides whether the editor pane,
// the preview pane, or both are shown for whichever tab is active.
//
// Module-scoped state only (content ids can clobber window properties, §7.3).

import { attachHost, deliver } from "./bridge.js";

const API_URL = "api/render";
const MAX_BODY_BYTES = 2 * 1024 * 1024;
const UNTITLED = "document.md"; // the title the server's renderer gives a document without an h1
const APP_NAME = "MdReader";
const TOAST_MS = 3200;
const SAVE_DEBOUNCE_MS = 400; // also used for the split view's live-preview debounce
const PRINT_READY_TIMEOUT_MS = 1500; // fallback if `printModeReady` never arrives
const TEXT_EXTENSIONS = /\.(md|markdown|mdown|mkd|mkdn|mdwn|txt|text)$/i;
const TOAST_TOO_BIG_TO_KEEP = "Some documents are too large to keep after reload.";
const DEFAULT_SPLIT_RATIO = 45;
const MIN_SPLIT_RATIO = 20;
const MAX_SPLIT_RATIO = 80;
// A task checkbox's source line, e.g. "  - [ ] Buy milk" or "1. [x] Done": optional
// indentation, a bullet (-, +, *) or an ordered marker (1. / 1)), at least one space/tab,
// then [ ]/[x]/[X], then a space/tab or end of line. Mirrors Core's TaskListToggle.FindMarker
// (§4.4) so a click flips the same character the desktop would. Group 1 is everything up to
// (not including) the marker char; group 2 is the marker char itself.
const TASK_LINE = /^([ \t]*(?:[-+*]|\d{1,9}[.)])[ \t]+\[)([ xX])(\]($|[ \t]))/;

const SESSION_KEY = "mdreader.session.v1";
// Legacy single-document keys (pre-tabs), migrated once then removed.
const KEY_DOC = "mdr.web.doc";
const KEY_NAME = "mdr.web.name";
const KEY_VIEW = "mdr.web.view";
const KEY_THEME = "mdr.web.theme";
const KEY_TOC = "mdr.web.toc";
const KEY_MODE = "mdr.web.mode";
const KEY_RATIO = "mdr.web.ratio";

const root = document.documentElement;
const input = document.getElementById("mdr-web-input");
const countEl = document.getElementById("mdr-web-count");
const fileInput = document.getElementById("mdr-web-file");
const renderButton = document.getElementById("mdr-web-render");
const tocToggleButton = document.getElementById("mdr-web-toc-toggle");
const printButton = document.getElementById("mdr-web-print");
const progress = document.getElementById("mdr-web-progress");
const toastEl = document.getElementById("mdr-web-toast");
const dropzone = document.getElementById("mdr-web-dropzone");
const content = document.getElementById("mdr-content");
const mdrMain = document.getElementById("mdr-main");
const workspaceEl = document.getElementById("mdr-web-workspace");
const dividerEl = document.getElementById("mdr-web-divider");
const tabListEl = document.getElementById("mdr-web-tablist");
const tabAddButton = document.getElementById("mdr-web-tab-add");
const params = new URLSearchParams(window.location.search);
const systemDark = window.matchMedia("(prefers-color-scheme: dark)");

// ---------------------------------------------------------------------------------
// storage (localStorage can be disabled, full or throw: never let that break the page)
// ---------------------------------------------------------------------------------

function load(key) {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function save(key, value) {
  try {
    if (value === null || value === undefined) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, value);
  } catch {
    // quota exceeded or storage blocked
  }
}

function trySave(key, value) {
  try {
    window.localStorage.setItem(key, value);
    return true;
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------------
// state
// ---------------------------------------------------------------------------------

let pageReady = false;
let tabs = [];
let activeId = null;
let tabIdCounter = 0;
let docIdCounter = 0;
let activityCounter = 0;
let lastPayload = null; // messages of the last delivered payload, re-delivered if the page reloads
let pendingScroll = null; // {docId, version, scrollTop} to apply once that render's content phase lands
let inFlight = null; // AbortController of the running render request
let tocEntries = 0;
let toastTimer = 0;
let draftTimer = 0;
let saveTimer = 0;
let liveTimer = 0;
let printReadyTimer = 0;
let dividerDragging = false;

// Theme: a query parameter pins it for this visit (screenshots, links); otherwise the saved
// choice. "system" follows prefers-color-scheme.
const queryTheme = oneOf(params.get("theme"), ["light", "dark"]);
let themeChoice = queryTheme || oneOf(load(KEY_THEME), ["light", "dark"]) || "system";
let tocVisible = load(KEY_TOC) !== "0";

// data-mode was already set (no-flash) by webapp-init.js before this module ran; read it back
// so both scripts agree on the same default without duplicating the heuristic.
let mode = oneOf(root.getAttribute("data-mode"), ["edit", "split", "read"]) || "edit";
let splitRatio = clampRatio(parseFloat(load(KEY_RATIO)));

function oneOf(value, allowed) {
  return allowed.includes(value) ? value : null;
}

function clampRatio(value) {
  if (!Number.isFinite(value)) return DEFAULT_SPLIT_RATIO;
  return Math.min(MAX_SPLIT_RATIO, Math.max(MIN_SPLIT_RATIO, value));
}

// ---------------------------------------------------------------------------------
// tabs
// ---------------------------------------------------------------------------------

function createTab({ markdown = "", title = "Untitled", name = null } = {}) {
  tabIdCounter += 1;
  docIdCounter += 1;
  return {
    id: "t" + tabIdCounter,
    docId: docIdCounter,
    title,
    markdown,
    name,
    scrollTop: 0,
    payload: null,
    version: 0,
    lastActive: 0,
  };
}

function hydrateTab(raw) {
  const markdown = typeof raw.markdown === "string" ? raw.markdown : "";
  const id = typeof raw.id === "string" && raw.id ? raw.id : null;
  tabIdCounter += 1;
  docIdCounter += 1;
  return {
    id: id || "t" + tabIdCounter,
    docId: docIdCounter,
    title: typeof raw.title === "string" && raw.title ? raw.title : "Untitled",
    markdown,
    name: typeof raw.name === "string" && raw.name ? raw.name : null,
    scrollTop: typeof raw.scrollTop === "number" && raw.scrollTop >= 0 ? raw.scrollTop : 0,
    payload: null,
    version: 0,
    lastActive: 0,
  };
}

function getActiveTab() {
  return tabs.find((t) => t.id === activeId) || null;
}

function titleFromRender(renderTitle, tab) {
  if (renderTitle && renderTitle !== UNTITLED) return renderTitle;
  if (tab.name) return tab.name;
  return "Untitled";
}

function webTitle(tab) {
  if (!tab.title || tab.title === "Untitled") return APP_NAME;
  return tab.title + " - " + APP_NAME;
}

function tocEntriesFromPayload(messages) {
  const renderMsg = messages.find((m) => m.type === "render");
  return renderMsg && Array.isArray(renderMsg.toc) ? renderMsg.toc.length : 0;
}

// ---------------------------------------------------------------------------------
// view mode: Edit / Split / Read
// ---------------------------------------------------------------------------------
//
// `mode` is a single global preference, not per tab. The editor pane is visible whenever
// mode !== "read"; the preview pane (the desktop reader page, `.mdr-web-reader`) is visible
// whenever mode !== "edit". Split shows both, with a draggable divider setting the ratio.
// renderTab() is only ever called while mode is "split" or "read" (the preview pane is the
// only place a render is shown), so it never needs to know about `mode` itself.

/** Saves whatever's currently on screen for the active tab back onto it, before the
 * textarea/scroll position it reads from is about to change (tab switch or mode switch).
 * If the editor was visible and its text has since diverged from the cached preview (typed
 * in Edit mode, or the last keystroke hasn't reached the live-preview debounce yet in Split),
 * the cached payload is invalidated so the next reveal re-renders instead of showing stale
 * content. */
function captureActiveTabState() {
  const tab = getActiveTab();
  if (!tab) return;
  if (mode !== "read" && input.value !== tab.markdown) {
    tab.markdown = input.value;
    tab.payload = null;
  }
  if (mode !== "edit") tab.scrollTop = mdrMain.scrollTop;
}

function applyModeAttribute(newMode) {
  mode = newMode;
  root.setAttribute("data-mode", mode);
  updateChoiceButtons();
  save(KEY_MODE, mode);
}

/** Puts the active tab's content on screen for the current mode: fills the editor and/or
 * delivers (or starts) its preview. Call after activeId or mode changes. */
function syncUIToActiveTab() {
  const tab = getActiveTab();
  if (!tab) return;
  if (mode !== "read") {
    input.value = tab.markdown || "";
    updateCount();
  }
  if (mode !== "edit") {
    if (tab.payload) {
      lastPayload = tab.payload;
      tocEntries = tocEntriesFromPayload(tab.payload);
      updateTocButton();
      pendingScroll = { docId: tab.docId, version: tab.version, scrollTop: tab.scrollTop };
      if (pageReady) deliverPayload(tab.payload);
    } else {
      renderTab(tab, tab.markdown);
    }
  }
}

function setMode(newMode) {
  if (newMode === mode || !["edit", "split", "read"].includes(newMode)) return;
  captureActiveTabState();
  cancelInFlight();
  applyModeAttribute(newMode);
  syncUIToActiveTab();
}

// ---------------------------------------------------------------------------------
// split divider (drag to resize, arrow keys for keyboard users)
// ---------------------------------------------------------------------------------

function applyRatio() {
  workspaceEl.style.setProperty("--mdr-split-ratio", splitRatio + "%");
  dividerEl.setAttribute("aria-valuenow", String(Math.round(splitRatio)));
  dividerEl.setAttribute("aria-valuemin", String(MIN_SPLIT_RATIO));
  dividerEl.setAttribute("aria-valuemax", String(MAX_SPLIT_RATIO));
}

applyRatio();

dividerEl.addEventListener("pointerdown", (event) => {
  if (mode !== "split") return;
  dividerDragging = true;
  try {
    dividerEl.setPointerCapture(event.pointerId);
  } catch {
    // ignore: dragging still works from move/up events on the element
  }
  event.preventDefault();
});

dividerEl.addEventListener("pointermove", (event) => {
  if (!dividerDragging) return;
  const rect = workspaceEl.getBoundingClientRect();
  if (rect.width <= 0) return;
  splitRatio = clampRatio(((event.clientX - rect.left) / rect.width) * 100);
  applyRatio();
});

function endDividerDrag(event) {
  if (!dividerDragging) return;
  dividerDragging = false;
  try {
    dividerEl.releasePointerCapture(event.pointerId);
  } catch {
    // already released
  }
  save(KEY_RATIO, String(Math.round(splitRatio)));
}

dividerEl.addEventListener("pointerup", endDividerDrag);
dividerEl.addEventListener("pointercancel", endDividerDrag);

dividerEl.addEventListener("keydown", (event) => {
  if (mode !== "split") return;
  let delta = 0;
  if (event.key === "ArrowLeft") delta = -2;
  else if (event.key === "ArrowRight") delta = 2;
  else return;
  event.preventDefault();
  splitRatio = clampRatio(splitRatio + delta);
  applyRatio();
  save(KEY_RATIO, String(Math.round(splitRatio)));
});

// ---------------------------------------------------------------------------------
// live preview (split mode): re-render ~400ms after the last keystroke, skipping a
// render cycle while one is already in flight (the next keystroke schedules another).
// ---------------------------------------------------------------------------------

function scheduleLivePreview() {
  clearTimeout(liveTimer);
  liveTimer = setTimeout(runLivePreview, SAVE_DEBOUNCE_MS);
}

function runLivePreview() {
  if (mode !== "split" || inFlight) return;
  const tab = getActiveTab();
  if (!tab) return;
  renderTab(tab, input.value);
}

// ---------------------------------------------------------------------------------
// print / PDF
// ---------------------------------------------------------------------------------

function doPrint() {
  clearTimeout(printReadyTimer);
  printReadyTimer = 0;
  window.print();
}

/** Asks the page to switch to print mode (light theme, diagrams re-rendered light) first,
 * so the OS print/PDF dialog opens on output that already looks right; prints anyway after
 * a short timeout if the page never acknowledges. */
function requestPrint() {
  if (mode === "edit") return; // no preview to print
  clearTimeout(printReadyTimer);
  deliver({ type: "printMode", enabled: true });
  printReadyTimer = setTimeout(doPrint, PRINT_READY_TIMEOUT_MS);
}

// ---------------------------------------------------------------------------------
// tab actions
// ---------------------------------------------------------------------------------

function activateTab(id) {
  if (!tabs.some((t) => t.id === id)) return;
  if (id !== activeId) {
    captureActiveTabState();
    cancelInFlight();
    activeId = id;
  }
  const tab = getActiveTab();
  tab.lastActive = ++activityCounter;
  syncUIToActiveTab();
  renderTabStrip();
  scheduleSessionSave();
}

function newTab() {
  const tab = createTab({});
  tabs.push(tab);
  if (mode === "read") setMode("edit"); // a blank tab needs the editor, not an empty preview
  activateTab(tab.id);
  input.focus();
}

function closeTab(id) {
  const idx = tabs.findIndex((t) => t.id === id);
  if (idx === -1) return;

  if (tabs.length === 1) {
    const tab = tabs[0];
    cancelInFlight();
    tab.markdown = "";
    tab.payload = null;
    tab.title = "Untitled";
    tab.name = null;
    tab.scrollTop = 0;
    activeId = tab.id;
    if (mode !== "edit") applyModeAttribute("edit"); // nothing left to preview
    syncUIToActiveTab();
    renderTabStrip();
    scheduleSessionSave();
    return;
  }

  const wasActive = id === activeId;
  tabs.splice(idx, 1);
  if (wasActive) {
    const nextIdx = Math.min(idx, tabs.length - 1);
    activeId = tabs[nextIdx].id;
    getActiveTab().lastActive = ++activityCounter;
    syncUIToActiveTab();
  }
  renderTabStrip();
  scheduleSessionSave();
}

function clearActiveTab() {
  cancelInFlight();
  const tab = getActiveTab();
  if (!tab) return;
  tab.markdown = "";
  tab.payload = null;
  tab.title = "Untitled";
  tab.name = null;
  input.value = "";
  updateCount();
  if (mode === "split") renderTab(tab, "");
  renderTabStrip();
  scheduleSessionSave();
}

// ---------------------------------------------------------------------------------
// tab strip UI
// ---------------------------------------------------------------------------------

function renderTabStrip() {
  tabListEl.textContent = "";
  let activeEl = null;
  for (const tab of tabs) {
    const el = document.createElement("div");
    el.className = "mdr-web-tab" + (tab.id === activeId ? " is-active" : "");
    el.setAttribute("role", "tab");
    el.setAttribute("tabindex", tab.id === activeId ? "0" : "-1");
    el.setAttribute("aria-selected", String(tab.id === activeId));
    el.dataset.tabId = tab.id;
    el.title = tab.title;

    const titleEl = document.createElement("span");
    titleEl.className = "mdr-web-tab-title";
    titleEl.textContent = tab.title;
    el.appendChild(titleEl);

    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "mdr-web-tab-close";
    closeBtn.title = "Close tab";
    closeBtn.setAttribute("aria-label", "Close tab");
    closeBtn.textContent = "×";
    el.appendChild(closeBtn);

    tabListEl.appendChild(el);
    if (tab.id === activeId) activeEl = el;
  }
  if (activeEl) activeEl.scrollIntoView({ block: "nearest", inline: "nearest" });
}

tabListEl.addEventListener("click", (event) => {
  const tabEl = event.target.closest(".mdr-web-tab");
  if (!tabEl) return;
  if (event.target.closest(".mdr-web-tab-close")) {
    closeTab(tabEl.dataset.tabId);
  } else {
    activateTab(tabEl.dataset.tabId);
  }
});

tabListEl.addEventListener("auxclick", (event) => {
  if (event.button !== 1) return; // middle click
  const tabEl = event.target.closest(".mdr-web-tab");
  if (!tabEl) return;
  event.preventDefault();
  closeTab(tabEl.dataset.tabId);
});

tabListEl.addEventListener("keydown", (event) => {
  if (event.key !== "Enter" && event.key !== " ") return;
  if (event.target.closest(".mdr-web-tab-close")) return; // let the close button's own click handle it
  const tabEl = event.target.closest(".mdr-web-tab");
  if (!tabEl) return;
  event.preventDefault();
  activateTab(tabEl.dataset.tabId);
});

tabAddButton.addEventListener("click", () => newTab());

// ---------------------------------------------------------------------------------
// session persistence (localStorage)
// ---------------------------------------------------------------------------------

function serializeTab(tab) {
  return { id: tab.id, title: tab.title, markdown: tab.markdown, scrollTop: tab.scrollTop, name: tab.name || null };
}

function scheduleSessionSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(persistSession, SAVE_DEBOUNCE_MS);
}

function persistSession() {
  clearTimeout(saveTimer);
  const full = JSON.stringify({ tabs: tabs.map(serializeTab), activeId });
  if (trySave(SESSION_KEY, full)) return;

  // The full session doesn't fit. Probe with an empty one first: if that also fails,
  // storage is disabled/blocked rather than merely full, so stay silent (§ save()).
  if (!trySave(SESSION_KEY, JSON.stringify({ tabs: [], activeId: null }))) return;

  const active = getActiveTab();
  const priority = active ? [active] : [];
  for (const t of [...tabs].sort((a, b) => b.lastActive - a.lastActive)) {
    if (t !== active) priority.push(t);
  }

  const kept = [];
  let droppedAny = false;
  for (const tab of priority) {
    const candidate = kept.concat([tab]).map(serializeTab);
    if (trySave(SESSION_KEY, JSON.stringify({ tabs: candidate, activeId }))) {
      kept.push(tab);
    } else {
      droppedAny = true;
    }
  }

  if (kept.length === 0) {
    save(SESSION_KEY, null);
  } else {
    const keptIds = new Set(kept.map((t) => t.id));
    const ordered = tabs.filter((t) => keptIds.has(t.id)).map(serializeTab);
    trySave(SESSION_KEY, JSON.stringify({ tabs: ordered, activeId }));
  }
  if (droppedAny) toast(TOAST_TOO_BIG_TO_KEEP);
}

function loadSession() {
  const raw = load(SESSION_KEY);
  if (!raw) return null;
  try {
    const data = JSON.parse(raw);
    return data && Array.isArray(data.tabs) ? data : null;
  } catch {
    return null;
  }
}

/** Migrates the pre-tabs single-document keys into a one-tab session, then removes them. */
function migrateLegacyIfNeeded() {
  const legacyDoc = load(KEY_DOC);
  if (legacyDoc === null) return null;

  let migrated = null;
  if (!load(SESSION_KEY)) {
    const legacyName = load(KEY_NAME);
    migrated = {
      tabs: [{ id: "t1", title: legacyName || "Untitled", markdown: legacyDoc, scrollTop: 0, name: legacyName || null }],
      activeId: "t1",
    };
  }
  save(KEY_DOC, null);
  save(KEY_NAME, null);
  save(KEY_VIEW, null);
  return migrated;
}

function initSession() {
  const migrated = migrateLegacyIfNeeded();
  const source = migrated || loadSession();
  if (source && Array.isArray(source.tabs) && source.tabs.length > 0) {
    tabs = source.tabs.map(hydrateTab);
    activeId = tabs.some((t) => t.id === source.activeId) ? source.activeId : tabs[0].id;
  } else {
    const tab = createTab({});
    tabs = [tab];
    activeId = tab.id;
  }
  getActiveTab().lastActive = ++activityCounter;
  if (migrated) persistSession(); // don't lose it if nothing else triggers a save before a reload
}

window.addEventListener("pagehide", () => {
  captureActiveTabState();
  persistSession();
});

// ---------------------------------------------------------------------------------
// host -> page messages
// ---------------------------------------------------------------------------------

function resolvedTheme() {
  if (themeChoice === "light" || themeChoice === "dark") return themeChoice;
  return systemDark.matches ? "dark" : "light";
}

function postTheme() {
  if (pageReady) deliver({ type: "theme", theme: resolvedTheme() });
  else root.setAttribute("data-theme", resolvedTheme());
}

function postTocVisibility() {
  if (pageReady) deliver({ type: "tocVisibility", visible: tocVisible });
}

function deliverPayload(messages) {
  for (const message of messages) deliver(message);
}

function onReady() {
  pageReady = true;
  postTheme();
  postTocVisibility();
  if (lastPayload) {
    // A page reload after a render: post the current payload again (§7.1 rule 2).
    deliverPayload(lastPayload);
  }
}

// ---------------------------------------------------------------------------------
// rendering
// ---------------------------------------------------------------------------------

function cancelInFlight() {
  if (inFlight) {
    inFlight.abort();
    inFlight = null;
  }
  setBusy(false);
}

function setBusy(busy) {
  progress.hidden = !busy;
  renderButton.disabled = busy;
  mdrMain.setAttribute("aria-busy", String(busy));
}

/** Renders `text` for `tab` through the API. Caches the result on the tab and, if it's still
 * the active tab, delivers it and schedules the scroll restore once content phase lands.
 * Only ever called while the preview pane is visible (mode "split" or "read"). */
async function renderTab(tab, text) {
  cancelInFlight();
  tab.markdown = text;

  const body = JSON.stringify({ markdown: text });
  if (new Blob([body]).size > MAX_BODY_BYTES) {
    if (tab.id === activeId) showError(413, null);
    return;
  }

  const controller = new AbortController();
  inFlight = controller;
  setBusy(true);

  let response;
  let payload = null;
  try {
    response = await fetch(API_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body,
      signal: controller.signal,
      cache: "no-store",
    });
    payload = await response.json().catch(() => null);
  } catch (err) {
    if (controller.signal.aborted) return; // superseded or cancelled
    if (inFlight === controller) inFlight = null;
    setBusy(false);
    if (tab.id === activeId) showError(0, null);
    console.warn("mdr: render request failed", err);
    return;
  }

  if (inFlight !== controller) return; // superseded while the body was downloading
  inFlight = null;
  setBusy(false);

  if (!response.ok || !payload || !Array.isArray(payload.messages) || payload.messages.length === 0) {
    tab.payload = null;
    if (tab.id === activeId) showError(response.ok ? 500 : response.status, payload);
    return;
  }

  // Each tab owns its own docId/version so switching tabs (a render for a different docId)
  // always applies regardless of version, and re-rendering the same tab always increases (§7.1).
  tab.version += 1;
  const messages = payload.messages.map((message) => {
    if (message.type === "render" || message.type === "renderPart") {
      message.docId = tab.docId;
      message.version = tab.version;
    }
    if (message.type === "render") {
      tab.title = titleFromRender(message.title, tab);
      message.title = webTitle(tab);
    }
    return message;
  });
  tab.payload = messages;

  if (tab.id === activeId) {
    lastPayload = messages;
    tocEntries = tocEntriesFromPayload(messages);
    updateTocButton();
    pendingScroll = { docId: tab.docId, version: tab.version, scrollTop: tab.scrollTop };
    if (pageReady) deliverPayload(messages);
  }
  renderTabStrip();
  scheduleSessionSave();
}

const ERRORS = {
  0: ["Can't reach " + APP_NAME, "Check your connection, then try again."],
  400: ["This document couldn't be read", "The server couldn't read this document."],
  413: ["This document is too large", "This document is over 2 MB."],
  429: ["Too many documents at once", "Too many requests. Try again in a moment."],
  503: ["The server is busy", "The server is busy. Try again in a moment."],
  500: ["Something went wrong", "This document couldn't be rendered."],
};

function showError(status, payload) {
  const [title, fallback] = ERRORS[status] || (status >= 500 ? ERRORS[500] : ERRORS[400]);
  const message = payload && typeof payload.message === "string" ? payload.message : fallback;
  lastPayload = null;
  tocEntries = 0;
  updateTocButton();
  deliver({
    type: "error",
    kind: status === 413 ? "tooLarge" : "renderFailed",
    title,
    message,
    path: "",
  });
}

function renderFromInput() {
  const text = input.value;
  if (text.trim().length === 0) {
    toast("Paste some Markdown first.");
    input.focus();
    return;
  }
  const tab = getActiveTab();
  if (!tab) return;
  if (mode === "edit") applyModeAttribute("read"); // reveal the preview; always render below
  renderTab(tab, text);
}

// ---------------------------------------------------------------------------------
// task list checkboxes (web -> host `taskToggle`, sent by web/js/tasks.js)
// ---------------------------------------------------------------------------------

/** Character range [start, end) of `markdown`'s 1-based `line`, excluding its line break —
 * counting \n, \r\n and lone \r as one break each, the way Markdig (and Core's
 * TaskListToggle.TryGetLine) does. Returns null if the file has fewer lines. */
function findLineRange(markdown, line) {
  let i = 0;
  let current = 1;
  for (;;) {
    const lineStart = i;
    while (i < markdown.length && markdown[i] !== "\n" && markdown[i] !== "\r") i++;
    if (current === line) return [lineStart, i];
    if (i >= markdown.length) return null;
    i += markdown[i] === "\r" && markdown[i + 1] === "\n" ? 2 : 1;
    current++;
  }
}

/** Sets the marker of the task item on `markdown`'s 1-based `line` to `checked`. Returns the
 * updated text (unchanged if the marker already matched), or null if the line is out of range
 * or isn't a task item any more — mirrors Core's TaskListToggle.TryToggle (§4.4) so a stale
 * line number (the file changed under the render that produced the click) is refused rather
 * than guessed at. Only the one marker character changes; everything else is copied through. */
function toggleTaskLine(markdown, line, checked) {
  const range = findLineRange(markdown, line);
  if (!range) return null;
  const [start, end] = range;
  const match = TASK_LINE.exec(markdown.slice(start, end));
  if (!match) return null;
  const index = start + match[1].length;
  const wanted = checked ? "x" : " ";
  if (markdown[index] === wanted) return markdown;
  return markdown.slice(0, index) + wanted + markdown.slice(index + 1);
}

function onTaskToggle(message) {
  const tab = getActiveTab();
  if (!tab) return;
  // The render on screen when the click happened may already be superseded (a newer render
  // landed, or the tab changed) — drop it rather than editing against text it no longer matches.
  if (Number(message.version) !== tab.version) return;
  // The editor's live text can be ahead of tab.markdown (the draft-save debounce hasn't
  // committed it yet, e.g. a click right after typing): pick it up first, so the toggle below
  // — and the line-number check inside it — run against what's actually on screen, not a
  // stale snapshot that would either edit the wrong line or silently drop the pending keystrokes.
  if (mode !== "read" && input.value !== tab.markdown) tab.markdown = input.value;
  const line = Number(message.line);
  if (Number.isInteger(line) && line >= 1) {
    const updated = toggleTaskLine(tab.markdown, line, !!message.checked);
    if (updated !== null && updated !== tab.markdown) {
      tab.markdown = updated;
      if (mode !== "read") input.value = updated;
      scheduleSessionSave();
    }
    // else: out of range, or the line no longer looks like a task item (stale line number) —
    // leave the text alone and just re-render below, which corrects whatever the click
    // optimistically changed in the DOM.
  }
  renderTab(tab, tab.markdown);
}

// ---------------------------------------------------------------------------------
// files: open + drop
// ---------------------------------------------------------------------------------

function isTextFile(file) {
  return TEXT_EXTENSIONS.test(file.name) || (file.type && file.type.startsWith("text/"));
}

/** Opens one or more files, each in its own tab (reusing a tab already open with the same
 * name and content). The first opened file's tab becomes active. */
async function openFiles(fileList) {
  const files = Array.from(fileList || []).filter(Boolean);
  if (files.length === 0) return;

  const valid = [];
  let hadBadType = false;
  let hadTooLarge = false;
  for (const file of files) {
    if (!isTextFile(file)) {
      hadBadType = true;
    } else if (file.size > MAX_BODY_BYTES) {
      hadTooLarge = true;
    } else {
      valid.push(file);
    }
  }
  if (valid.length === 0) {
    toast(hadBadType ? "Choose a .md, .markdown or .txt file." : "This file is over 2 MB.");
    return;
  }

  let firstTabId = null;
  for (const file of valid) {
    let text;
    try {
      text = await file.text();
    } catch (err) {
      console.warn("mdr: couldn't read the file", err);
      continue;
    }
    let tab = tabs.find((t) => t.name === file.name && t.markdown === text);
    if (!tab) {
      tab = createTab({ markdown: text, name: file.name, title: file.name });
      tabs.push(tab);
    }
    if (firstTabId === null) firstTabId = tab.id;
  }

  renderTabStrip();
  if (firstTabId !== null) {
    activateTab(firstTabId);
    if (mode === "edit") setMode("read"); // show the opened file, not a blank editor
  }
  scheduleSessionSave();
}

let dropzoneTimer = 0;

function hasFiles(event) {
  return !!event.dataTransfer && Array.from(event.dataTransfer.types || []).includes("Files");
}

document.addEventListener("dragover", (event) => {
  if (!hasFiles(event)) return;
  dropzone.hidden = false;
  clearTimeout(dropzoneTimer);
  dropzoneTimer = setTimeout(() => (dropzone.hidden = true), 200);
});

document.addEventListener("drop", () => {
  clearTimeout(dropzoneTimer);
  dropzone.hidden = true;
});

// ---------------------------------------------------------------------------------
// page -> host messages
// ---------------------------------------------------------------------------------

function openLink(href) {
  let url = null;
  if (/^https?:\/\//i.test(href)) {
    try {
      url = new URL(href);
    } catch {
      url = null;
    }
  }
  if (url && (url.protocol === "http:" || url.protocol === "https:")) {
    window.open(url.href, "_blank", "noopener,noreferrer");
  } else {
    toast("Only web links can be opened here.");
  }
}

function copyText(text) {
  if (!navigator.clipboard || typeof navigator.clipboard.writeText !== "function") {
    toast("Copying isn't available in this browser.");
    return;
  }
  navigator.clipboard.writeText(text).catch(() => toast("Couldn't copy."));
}

function logToConsole(level, message) {
  const method = level === "error" ? "error" : level === "warn" ? "warn" : level === "debug" ? "debug" : "info";
  console[method]("mdr:", message);
}

function onRendered(message) {
  const active = getActiveTab();
  if (!active || message.docId !== active.docId) return; // a background/superseded render
  if (
    message.phase === "content" &&
    pendingScroll &&
    message.docId === pendingScroll.docId &&
    message.version === pendingScroll.version
  ) {
    mdrMain.scrollTop = pendingScroll.scrollTop;
    pendingScroll = null;
  }
  replaceMissingImages(content);
}

attachHost((message, files) => {
  switch (message.type) {
    case "ready":
      onReady();
      break;
    case "link":
      openLink(String(message.href || ""));
      break;
    case "copy":
      copyText(String(message.text ?? ""));
      break;
    case "tocVisibilityChanged":
      tocVisible = !!message.visible;
      save(KEY_TOC, tocVisible ? "1" : "0");
      break;
    case "retry": {
      const tab = getActiveTab();
      if (tab && tab.markdown.trim().length > 0) renderTab(tab, tab.markdown);
      else if (mode !== "edit") setMode("edit");
      break;
    }
    case "log":
      logToConsole(message.level, message.message);
      break;
    case "drop":
      if (files && files.length > 0) openFiles(files);
      break;
    case "rendered":
      onRendered(message);
      break;
    case "printModeReady":
      if (printReadyTimer) doPrint();
      break;
    case "taskToggle":
      onTaskToggle(message);
      break;
    default:
      break;
  }
});

// ---------------------------------------------------------------------------------
// images the web version can't show
// ---------------------------------------------------------------------------------

const SVG_NS = "http://www.w3.org/2000/svg";

function imageIcon() {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("class", "mdr-web-icon");
  const rect = document.createElementNS(SVG_NS, "rect");
  rect.setAttribute("x", "3");
  rect.setAttribute("y", "3");
  rect.setAttribute("width", "18");
  rect.setAttribute("height", "18");
  rect.setAttribute("rx", "2");
  const circle = document.createElementNS(SVG_NS, "circle");
  circle.setAttribute("cx", "9");
  circle.setAttribute("cy", "9");
  circle.setAttribute("r", "2");
  const path = document.createElementNS(SVG_NS, "path");
  path.setAttribute("d", "m21 15-3.1-3.1a2 2 0 0 0-2.8 0L6 21");
  svg.append(rect, circle, path);
  return svg;
}

/** Replaces an image with a small box showing its alt text. */
function toPlaceholder(img, reason) {
  if (!img.isConnected) return;
  const box = document.createElement("span");
  box.className = "mdr-web-missing-image";
  box.setAttribute("role", "img");
  const alt = (img.getAttribute("alt") || "").trim();
  box.setAttribute("aria-label", alt || "Image");
  box.title =
    reason === "local" ? "Images stored next to a file can't be shown in the web version" : "This image couldn't be loaded";

  const text = document.createElement("span");
  text.className = "mdr-web-missing-image__text";
  text.textContent = alt || "Image";
  const note = document.createElement("span");
  note.className = "mdr-web-missing-image__note";
  note.textContent = reason === "local" ? "local image" : "couldn't load";

  box.append(imageIcon(), text, note);
  img.replaceWith(box);
}

function replaceMissingImages(rootEl) {
  // The server drops every local image reference (§15), leaving <img> without a source.
  for (const img of rootEl.querySelectorAll("img")) {
    if (!img.getAttribute("src") && !img.getAttribute("srcset")) toPlaceholder(img, "local");
  }
}

// Remote images that fail to load (error events don't bubble: capture them).
document.addEventListener(
  "error",
  (event) => {
    const target = event.target;
    if (target instanceof HTMLImageElement && content.contains(target)) toPlaceholder(target, "broken");
  },
  true
);

// ---------------------------------------------------------------------------------
// toast
// ---------------------------------------------------------------------------------

function toast(text) {
  if (toastEl.classList.contains("is-visible") && toastEl.textContent === text) {
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove("is-visible"), TOAST_MS);
    return;
  }
  toastEl.textContent = text;
  toastEl.classList.add("is-visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toastEl.classList.remove("is-visible"), TOAST_MS);
}

// ---------------------------------------------------------------------------------
// toolbar
// ---------------------------------------------------------------------------------

function updateChoiceButtons() {
  for (const button of document.querySelectorAll("[data-theme-choice]")) {
    button.setAttribute("aria-pressed", String(button.dataset.themeChoice === themeChoice));
  }
  for (const button of document.querySelectorAll("[data-mode-choice]")) {
    button.setAttribute("aria-pressed", String(button.dataset.modeChoice === mode));
  }
}

function updateTocButton() {
  tocToggleButton.disabled = tocEntries < 2;
}

for (const button of document.querySelectorAll("[data-theme-choice]")) {
  button.addEventListener("click", () => {
    themeChoice = button.dataset.themeChoice;
    save(KEY_THEME, themeChoice === "system" ? null : themeChoice);
    updateChoiceButtons();
    postTheme();
  });
}

for (const button of document.querySelectorAll("[data-mode-choice]")) {
  button.addEventListener("click", () => setMode(button.dataset.modeChoice));
}

systemDark.addEventListener("change", () => {
  if (themeChoice === "system") postTheme();
});

tocToggleButton.addEventListener("click", () => {
  if (pageReady) deliver({ type: "tocToggle" });
});

printButton.addEventListener("click", requestPrint);

document.getElementById("mdr-web-new").addEventListener("click", () => newTab());
document.getElementById("mdr-web-home").addEventListener("click", () => setMode("edit"));

// ---------------------------------------------------------------------------------
// paste view
// ---------------------------------------------------------------------------------

function updateCount() {
  const length = input.value.length;
  countEl.textContent = length === 0 ? "" : length.toLocaleString() + (length === 1 ? " character" : " characters");
}

renderButton.addEventListener("click", renderFromInput);
document.getElementById("mdr-web-open").addEventListener("click", () => fileInput.click());
document.getElementById("mdr-web-clear").addEventListener("click", () => {
  clearActiveTab();
  input.focus();
});

fileInput.addEventListener("change", () => {
  const files = fileInput.files;
  fileInput.value = ""; // picking the same file(s) again must fire `change` again
  openFiles(files);
});

input.addEventListener("input", () => {
  updateCount();
  clearTimeout(draftTimer);
  draftTimer = setTimeout(() => {
    const tab = getActiveTab();
    if (tab && mode !== "read") {
      tab.markdown = input.value;
      // Split mode's live preview (below) keeps the payload fresh on its own; Edit mode has
      // no preview running, so the cached one is now stale until the next render.
      if (mode === "edit") tab.payload = null;
      scheduleSessionSave();
    }
  }, SAVE_DEBOUNCE_MS);
  if (mode === "split") scheduleLivePreview();
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && mode !== "read") {
    event.preventDefault();
    renderFromInput();
  }
});

// ---------------------------------------------------------------------------------
// start
// ---------------------------------------------------------------------------------

updateChoiceButtons();
updateTocButton();

initSession();
renderTabStrip();
syncUIToActiveTab();
