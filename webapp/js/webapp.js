// webapp.js — the web version's "host" (ARCHITECTURE §15). It plays the part the desktop app
// plays for the page: after `ready` it posts theme, readingStyle and tocVisibility, renders
// documents through POST /api/render and delivers the returned host messages unchanged
// (except docId/version, which it owns), and answers the page's messages: link, copy,
// tocVisibilityChanged, retry, log, drop, rendered. It also owns the web-only chrome: the
// header toolbar, the tab strip, the paste view, file open / drop, and the toast.
//
// Tabs: each tab is one document {id, title, markdown, scrollTop} (plus in-memory-only
// bookkeeping: docId/version for the protocol, a cached `payload` of host messages so
// switching tabs never re-fetches, and `view` for whether it's showing paste or reader).
// Only the active tab is rendered eagerly; others render on first activation.
//
// Module-scoped state only (content ids can clobber window properties, §7.3).

import { attachHost, deliver } from "./bridge.js";

const API_URL = "api/render";
const MAX_BODY_BYTES = 2 * 1024 * 1024;
const UNTITLED = "document.md"; // the title the server's renderer gives a document without an h1
const APP_NAME = "MdReader";
const TOAST_MS = 3200;
const SAVE_DEBOUNCE_MS = 400;
const TEXT_EXTENSIONS = /\.(md|markdown|mdown|mkd|mkdn|mdwn|txt|text)$/i;
const TOAST_TOO_BIG_TO_KEEP = "Some documents are too large to keep after reload.";

const SESSION_KEY = "mdreader.session.v1";
// Legacy single-document keys (pre-tabs), migrated once then removed.
const KEY_DOC = "mdr.web.doc";
const KEY_NAME = "mdr.web.name";
const KEY_VIEW = "mdr.web.view";
const KEY_THEME = "mdr.web.theme";
const KEY_STYLE = "mdr.web.style";
const KEY_TOC = "mdr.web.toc";

const root = document.documentElement;
const input = document.getElementById("mdr-web-input");
const countEl = document.getElementById("mdr-web-count");
const fileInput = document.getElementById("mdr-web-file");
const renderButton = document.getElementById("mdr-web-render");
const tocToggleButton = document.getElementById("mdr-web-toc-toggle");
const progress = document.getElementById("mdr-web-progress");
const toastEl = document.getElementById("mdr-web-toast");
const dropzone = document.getElementById("mdr-web-dropzone");
const content = document.getElementById("mdr-content");
const mdrMain = document.getElementById("mdr-main");
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

// Theme / style: a query parameter pins them for this visit (screenshots, links); otherwise
// the saved choice. theme "system" follows prefers-color-scheme.
const queryTheme = oneOf(params.get("theme"), ["light", "dark"]);
const queryStyle = oneOf(params.get("style"), ["colorful", "classic"]);
let themeChoice = queryTheme || oneOf(load(KEY_THEME), ["light", "dark"]) || "system";
let styleChoice = queryStyle || oneOf(load(KEY_STYLE), ["classic"]) || "colorful";
let tocVisible = load(KEY_TOC) !== "0";

function oneOf(value, allowed) {
  return allowed.includes(value) ? value : null;
}

// ---------------------------------------------------------------------------------
// tabs
// ---------------------------------------------------------------------------------

function createTab({ markdown = "", title = "Untitled", name = null, view } = {}) {
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
    view: view || (markdown.trim().length > 0 ? "reader" : "paste"),
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
    view: markdown.trim().length > 0 ? "reader" : "paste",
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
// views
// ---------------------------------------------------------------------------------

function setView(view) {
  root.setAttribute("data-view", view);
  if (view === "paste") document.title = APP_NAME;
}

function leaveActiveTab() {
  const tab = getActiveTab();
  if (!tab) return;
  if (root.getAttribute("data-view") === "paste") {
    tab.markdown = input.value;
  } else {
    tab.scrollTop = mdrMain.scrollTop;
  }
}

/** Shows the active tab: paste view for an empty/draft tab, reader view otherwise (rendering it first if needed). */
function showActiveTab() {
  cancelInFlight();
  const tab = getActiveTab();
  if (!tab) return;
  if (tab.view !== "reader") {
    setView("paste");
    input.value = tab.markdown || "";
    updateCount();
    return;
  }
  if (tab.payload) {
    setView("reader");
    lastPayload = tab.payload;
    tocEntries = tocEntriesFromPayload(tab.payload);
    updateTocButton();
    pendingScroll = { docId: tab.docId, version: tab.version, scrollTop: tab.scrollTop };
    if (pageReady) deliverPayload(tab.payload);
  } else {
    renderTab(tab, tab.markdown, { immediateReaderView: true });
  }
}

function showPasteForActiveTab({ focus = true } = {}) {
  cancelInFlight();
  const tab = getActiveTab();
  if (tab) {
    tab.view = "paste";
    input.value = tab.markdown || "";
  }
  setView("paste");
  updateCount();
  if (focus) input.focus();
}

// ---------------------------------------------------------------------------------
// tab actions
// ---------------------------------------------------------------------------------

function activateTab(id) {
  if (!tabs.some((t) => t.id === id)) return;
  if (id !== activeId) {
    leaveActiveTab();
    activeId = id;
  }
  const tab = getActiveTab();
  tab.lastActive = ++activityCounter;
  showActiveTab();
  renderTabStrip();
  scheduleSessionSave();
}

function newTab() {
  const tab = createTab({});
  tabs.push(tab);
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
    tab.view = "paste";
    tab.scrollTop = 0;
    activeId = tab.id;
    showActiveTab();
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
    showActiveTab();
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
  tab.view = "paste";
  input.value = "";
  updateCount();
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
  leaveActiveTab();
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

function postReadingStyle() {
  // Set directly as well: the page applies `readingStyle` itself where supported (§14).
  root.setAttribute("data-style", styleChoice);
  if (pageReady) deliver({ type: "readingStyle", style: styleChoice });
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
  postReadingStyle();
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
 * the active tab, delivers it and schedules the scroll restore once content phase lands. */
async function renderTab(tab, text, { immediateReaderView = false } = {}) {
  cancelInFlight();
  tab.markdown = text;

  const body = JSON.stringify({ markdown: text });
  if (new Blob([body]).size > MAX_BODY_BYTES) {
    if (tab.id === activeId) {
      setView("reader");
      showError(413, null);
    }
    return;
  }

  const controller = new AbortController();
  inFlight = controller;
  setBusy(true);
  if (immediateReaderView && tab.id === activeId) setView("reader");

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
    if (tab.id === activeId) {
      setView("reader");
      showError(0, null);
    }
    console.warn("mdr: render request failed", err);
    return;
  }

  if (inFlight !== controller) return; // superseded while the body was downloading
  inFlight = null;
  setBusy(false);

  if (!response.ok || !payload || !Array.isArray(payload.messages) || payload.messages.length === 0) {
    tab.payload = null;
    if (tab.id === activeId) {
      setView("reader");
      showError(response.ok ? 500 : response.status, payload);
    }
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
  tab.view = "reader";

  if (tab.id === activeId) {
    setView("reader");
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
  renderTab(tab, text, { immediateReaderView: false });
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
      tab = createTab({ markdown: text, name: file.name, title: file.name, view: "reader" });
      tabs.push(tab);
    }
    if (firstTabId === null) firstTabId = tab.id;
  }

  renderTabStrip();
  if (firstTabId !== null) activateTab(firstTabId);
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
      else showPasteForActiveTab({ focus: false });
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
    default:
      break; // printModeReady etc.: nothing to do on the web
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
  for (const button of document.querySelectorAll("[data-style-choice]")) {
    button.setAttribute("aria-pressed", String(button.dataset.styleChoice === styleChoice));
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

for (const button of document.querySelectorAll("[data-style-choice]")) {
  button.addEventListener("click", () => {
    styleChoice = button.dataset.styleChoice;
    save(KEY_STYLE, styleChoice);
    updateChoiceButtons();
    postReadingStyle();
  });
}

systemDark.addEventListener("change", () => {
  if (themeChoice === "system") postTheme();
});

tocToggleButton.addEventListener("click", () => {
  if (pageReady) deliver({ type: "tocToggle" });
});

document.getElementById("mdr-web-edit").addEventListener("click", () => showPasteForActiveTab());
document.getElementById("mdr-web-new").addEventListener("click", () => newTab());
document.getElementById("mdr-web-home").addEventListener("click", () => showPasteForActiveTab());

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
    if (tab && root.getAttribute("data-view") === "paste") {
      tab.markdown = input.value;
      scheduleSessionSave();
    }
  }, SAVE_DEBOUNCE_MS);
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && root.getAttribute("data-view") === "paste") {
    event.preventDefault();
    renderFromInput();
  }
});

// ---------------------------------------------------------------------------------
// start
// ---------------------------------------------------------------------------------

updateChoiceButtons();
updateTocButton();
postReadingStyle();

initSession();
renderTabStrip();
showActiveTab();
