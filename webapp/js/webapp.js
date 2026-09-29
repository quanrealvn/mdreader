// webapp.js — the web version's "host" (ARCHITECTURE §15). It plays the part the desktop app
// plays for the page: after `ready` it posts theme and tocVisibility, renders
// documents through POST /api/render and delivers the returned host messages unchanged
// (except docId/version, which it owns), and answers the page's messages: link, copy,
// tocVisibilityChanged, tocWidthChanged, retry, log, drop, rendered, printModeReady, taskToggle. It also owns
// the web-only chrome: the header toolbar, the tab strip, the Edit/Split/Read workspace
// (editor pane, draggable divider, split ratio), file open / drop, and the toast. The file pane
// beside all of it is folder.js; this module only lends it the tabs.
//
// Tabs: each tab is one document {id, title, markdown, kind, scrollTop} (plus in-memory-only
// bookkeeping: docId/version for the protocol, a cached `payload` of host messages so
// switching tabs never re-fetches). The workspace view — Edit, Split or Read — is a single
// global preference (like the theme), not per tab: `mode` decides whether the editor pane,
// the preview pane, or both are shown for whichever tab is active.
//
// `kind` is which renderer the server should use for a document: "markdown" or "json". It
// belongs to the document rather than to the workspace, because one tab is a README and the next
// is a config file. A file settles it by its name; pasted text has no name, so the reader says
// which with the control in the editor bar. Nothing here reads the text to decide — a document
// can be valid JSON and meaningful Markdown at the same time, and only the person who pasted it
// knows which they meant. Either way the request carries the kind and the server renders it.
//
// Module-scoped state only (content ids can clobber window properties, §7.3).

import { attachHost, deliver } from "./bridge.js";
// One definition of "the reader cannot see this heading", shared with the panel that highlights
// them: a heading inside a collapsed <details> is laid out but invisible, and its box is no use
// to either of us. toc.js is already in the page's module graph, so this costs nothing.
import { isHiddenFromReader } from "./toc.js";
// Folder mode (§15): the file pane owns the tree, the drop of a folder and the reading of a file
// out of it. This module owns the tabs, so opening one comes back here.
import { initFolder, folderDropInProgress } from "./folder.js";

const API_URL = "api/render";
// Kept in step with WebLimitsOptions.DefaultMaxRequestBodyBytes so an oversized document is refused here
// instead of being uploaded and refused there; RenderApiTests checks the two agree.
const MAX_BODY_BYTES = 512 * 1024;
// The two readings the server has (RenderKinds.cs). Anything else is refused there with a 400,
// so these strings are the whole vocabulary the page is allowed.
const KIND_MARKDOWN = "markdown";
const KIND_JSON = "json";
const KINDS = [KIND_MARKDOWN, KIND_JSON];
// The name the server renders each kind under, which is also the title it hands back for a
// document with no title of its own. Neither is ever sent: the client picks a kind, the server
// picks the name.
const UNTITLED = { [KIND_MARKDOWN]: "document.md", [KIND_JSON]: "document.json" };
const APP_NAME = "MdReader";
const TOAST_MS = 3200;
const SAVE_DEBOUNCE_MS = 400; // also used for the split view's live-preview debounce
const PRINT_READY_TIMEOUT_MS = 1500; // fallback if `printModeReady` never arrives
// MdReader.Core's JsonFileTypes.Extensions, exactly: the extension is the whole rule for what a
// file is. .map and .lock are JSON too and are deliberately not here — nobody opens one to read.
const JSON_EXTENSIONS = /\.(json|jsonl|ndjson|geojson)$/i;
const TEXT_EXTENSIONS = /\.(md|markdown|mdown|mkd|mkdn|mdwn|txt|text|json|jsonl|ndjson|geojson)$/i;
const DEFAULT_SPLIT_RATIO = 45;
const MIN_SPLIT_RATIO = 20;
const MAX_SPLIT_RATIO = 80;
const DEFAULT_TOC_WIDTH = 260;
const MIN_TOC_WIDTH = 180;
const MAX_TOC_WIDTH = 560;
// A task checkbox's source line, e.g. "  - [ ] Buy milk", "1. [x] Done", "> - [ ] Quoted" or
// "- > - [ ] Quoted inside a list": any number of block containers (a quote marker, or a list
// marker opening a list that holds one), then the item's own bullet (-, +, *) or ordered
// marker (1. / 1)), at least one space/tab, then [ ]/[x]/[X], then a space/tab or end of line.
// Mirrors Core's TaskListToggle.FindMarker (§4.4) so a click flips the same character the
// desktop would — the container prefix is matched but left untouched. Group 1 is everything up
// to (not including) the marker char; group 2 is the marker char itself.
const TASK_LINE = /^((?:[ \t]*(?:>|(?:[-+*]|\d{1,9}[.)])[ \t]))*[ \t]*(?:[-+*]|\d{1,9}[.)])[ \t]+\[)([ xX])(\]($|[ \t]))/;

const SESSION_KEY = "mdreader.session.v1";
// Legacy single-document keys (pre-tabs), migrated once then removed.
const KEY_DOC = "mdr.web.doc";
const KEY_NAME = "mdr.web.name";
const KEY_VIEW = "mdr.web.view";
const KEY_THEME = "mdr.web.theme";
const KEY_TOC = "mdr.web.toc";
const KEY_TOC_WIDTH = "mdr.web.tocwidth";
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
const pasteEl = document.getElementById("mdr-web-paste");
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
let shownDocId = null; // docId of the payload the reader pane is showing, or null (nothing/an error)
let pendingScroll = null; // {docId, version, scrollTop, anchor} to put back once that render lands
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
let tocWidth = clampTocWidth(parseFloat(load(KEY_TOC_WIDTH)));

// data-mode was already set (no-flash) by webapp-init.js before this module ran; read it back
// so both scripts agree on the same default without duplicating the heuristic.
let mode = oneOf(root.getAttribute("data-mode"), ["edit", "split", "read"]) || "edit";
let splitRatio = clampRatio(parseFloat(load(KEY_RATIO)));

function oneOf(value, allowed) {
  return allowed.includes(value) ? value : null;
}

function clampTocWidth(value) {
  // Mirrors AppSettings.TocWidth on the desktop side (§4.6); the page caps it at half the
  // window on top of this.
  if (!Number.isFinite(value)) return DEFAULT_TOC_WIDTH;
  return Math.min(MAX_TOC_WIDTH, Math.max(MIN_TOC_WIDTH, Math.round(value)));
}

function clampRatio(value) {
  if (!Number.isFinite(value)) return DEFAULT_SPLIT_RATIO;
  return Math.min(MAX_SPLIT_RATIO, Math.max(MIN_SPLIT_RATIO, value));
}

// ---------------------------------------------------------------------------------
// tabs
// ---------------------------------------------------------------------------------

// A tab id nobody else has. The counter is only a starting point: it is raised past restored
// ids, and a saved session is not something to trust — it can be edited by hand, left behind by
// another version, or corrupted. So the mint checks, every time, rather than assuming.
const MAX_TAB_ID = 1_000_000;

/** A fresh `t<n>` that no current tab and no id in `taken` already carries. */
function mintTabId(taken) {
  for (;;) {
    // Past 2^53, `+ 1` stops changing the value and this would spin: the clamp on restored ids
    // keeps the counter well below that, and this is the backstop if one ever slips through.
    if (!Number.isSafeInteger(tabIdCounter) || tabIdCounter >= MAX_TAB_ID) tabIdCounter = 0;
    tabIdCounter += 1;
    const id = "t" + tabIdCounter;
    const isTaken = taken ? taken.has(id) : tabs.some((tab) => tab.id === id);
    if (!isTaken) return id;
  }
}

/** Which reading a file name asks for. The name is the whole rule, and a name the reader chose
 *  is the strongest thing there is to go on — see the note at the top about not sniffing text. */
function kindForName(name) {
  return name && JSON_EXTENSIONS.test(name) ? KIND_JSON : KIND_MARKDOWN;
}

/** One of the two kinds, whatever came in. Restored sessions and query strings both land here. */
function asKind(value) {
  return value === KIND_JSON ? KIND_JSON : KIND_MARKDOWN;
}

function createTab({ markdown = "", title = "Untitled", name = null, kind = KIND_MARKDOWN } = {}) {
  docIdCounter += 1;
  return {
    id: mintTabId(null),
    docId: docIdCounter,
    title,
    markdown,
    kind: asKind(kind),
    name,
    scrollTop: 0,
    anchor: null,
    payload: null,
    renderedText: markdown,
    version: 0,
    lastActive: 0,
    // Which file of which open folder this tab came from, if any. In memory only: a browser
    // cannot re-read a folder after a reload, so a saved path would name something this page has
    // no way to open (see `serializeTab`).
    folderPath: null,
    folderSession: 0,
  };
}

/** Restores one saved tab. `taken` collects the ids handed out so far in this restore. */
function hydrateTab(raw, taken) {
  const markdown = typeof raw.markdown === "string" ? raw.markdown : "";
  const saved = typeof raw.id === "string" && raw.id && !taken.has(raw.id) ? raw.id : null;
  // A saved id keeps its name, because the saved `activeId` points at it. But the counter
  // only knows how many tabs came back, not what they are called: restoring t1 and t3 (the
  // middle tab was closed before the reload) left it at 2, so the next "New tab" minted t3
  // again. Two tabs with one identity: the new tab showed the old one's document, both were
  // drawn as active, and typing overwrote a document the reader still had open. Carry the
  // counter past restored ids — but only as far as a real session could have got, because a
  // saved "t9007199254740991" would otherwise push it to where `+ 1` no longer counts.
  const suffix = saved && /^t(\d+)$/.exec(saved);
  if (suffix) {
    const restored = Number(suffix[1]);
    if (Number.isSafeInteger(restored)) tabIdCounter = Math.max(tabIdCounter, Math.min(restored, MAX_TAB_ID));
  }
  const id = saved || mintTabId(taken);
  taken.add(id);
  docIdCounter += 1;
  return {
    id,
    docId: docIdCounter,
    title: typeof raw.title === "string" && raw.title ? raw.title : "Untitled",
    markdown,
    // A session written before JSON existed has no kind, and Markdown is what it meant.
    kind: asKind(raw.kind),
    name: typeof raw.name === "string" && raw.name ? raw.name : null,
    scrollTop: typeof raw.scrollTop === "number" && raw.scrollTop >= 0 ? raw.scrollTop : 0,
    anchor:
      raw.anchor && typeof raw.anchor.id === "string" && Number.isFinite(raw.anchor.delta)
        ? { id: raw.anchor.id, delta: raw.anchor.delta }
        : null,
    payload: null,
    // What was on screen last time is exactly what this tab will render first, so the
    // position it saved still describes the document it is about to get back.
    renderedText: markdown,
    version: 0,
    lastActive: 0,
    folderPath: null,
    folderSession: 0,
  };
}

function getActiveTab() {
  return tabs.find((t) => t.id === activeId) || null;
}

function titleFromRender(renderTitle, tab) {
  // "document.md" / "document.json" is the server saying the document had no title of its own,
  // not a title. A JSON document never has one, so this is the usual answer for a pasted tree.
  if (renderTitle && renderTitle !== UNTITLED[KIND_MARKDOWN] && renderTitle !== UNTITLED[KIND_JSON]) {
    return renderTitle;
  }
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
  if (mode !== "edit") {
    tab.scrollTop = mdrMain.scrollTop;
    tab.anchor = captureScrollAnchor();
  }
}

// ---------------------------------------------------------------------------------
// where the reader is
// ---------------------------------------------------------------------------------
//
// A pixel offset does not describe a place in a document that is about to be laid out again.
// Coming back to a tab re-renders it from scratch, and at the moment the content lands its
// diagrams, math and highlighting have not: the document is shorter than the one the offset
// was measured in, so the offset lands in a different section, or past the end and gets
// clamped. The heading the reader was under, plus how far above the view's top it sat, means
// the same thing in both documents.

const HEADING_SELECTOR = "h1[id], h2[id], h3[id], h4[id], h5[id], h6[id]";

/**
 * `{id, delta}` for the heading nearest the top of the view and how far from it that heading
 * sits, or null at the very top of the document (and when it has no headings). Nearest, rather
 * than the one the reader is under, because only the layout between the anchor and the view can
 * still move — the less of it there is, the less the restore can drift.
 *
 * Headings the reader cannot see are skipped. An earlier version considered them, and settled on
 * one 3450 px above the view while the heading 16 px away was three entries further on, so the
 * restore drifted by the whole distance between them — the exact fault this anchor exists to
 * remove.
 *
 * It also stopped at the first heading further from the view than the one before it, which is
 * only sound if heading tops increase down the document. Skipping the hidden ones makes that
 * true again, so the early exit would be correct now and no probe fails with it restored; the
 * full walk is here for the same reason as the scan in toc.js, which says it at length.
 */
function captureScrollAnchor() {
  if (mdrMain.scrollTop <= 0) return null;
  const top = mdrMain.getBoundingClientRect().top;
  let found = null;
  let best = Infinity;
  for (const heading of content.querySelectorAll(HEADING_SELECTOR)) {
    if (isHiddenFromReader(heading)) continue; // inside a collapsed <details>: its box is a lie
    const delta = heading.getBoundingClientRect().top - top;
    const distance = Math.abs(delta);
    if (distance >= best) continue;
    best = distance;
    found = { id: heading.id, delta: Math.round(delta) };
  }
  return found;
}

/** Puts the reader back at `{scrollTop, anchor}`, preferring the anchor when its heading is there. */
function applyScrollPosition(target) {
  const anchor = target.anchor;
  const heading = anchor && anchor.id ? document.getElementById(anchor.id) : null;
  if (heading && content.contains(heading) && !isHiddenFromReader(heading)) {
    mdrMain.scrollTop += heading.getBoundingClientRect().top - mdrMain.getBoundingClientRect().top - anchor.delta;
  } else {
    mdrMain.scrollTop = target.scrollTop;
  }
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
  updateKindButtons();
  if (mode !== "read") {
    input.value = tab.markdown || "";
    updateCount();
  }
  if (mode !== "edit") {
    if (tab.payload) {
      lastPayload = tab.payload;
      tocEntries = tocEntriesFromPayload(tab.payload);
      updateTocButton();
      // A cached payload is being shown again after a tab or mode switch, and the page must
      // apply it, not recognise it. Coming back from Edit it is the payload the page already
      // has: `render` with a version it has seen is dropped as a duplicate (§7.1 rule 4), so
      // nothing re-rendered, no `rendered` came back, and the restore below never ran — while
      // Edit had hidden the reader pane with display:none, which resets its scrollTop. The
      // reader came back two sections from where they left. Give it a version the page has
      // not seen, and it renders and reports as usual.
      tab.version += 1;
      for (const message of tab.payload) {
        if (message.type === "render" || message.type === "renderPart") message.version = tab.version;
        if (message.type === "render") {
          // The position on screen now belongs to some other document (or to nothing, after
          // the pane was hidden), so this render must not hold on to it: restore this tab's
          // own, below. It is still the same document arriving again, though, so the contents
          // panel keeps what the reader was doing with it.
          message.preserveScroll = false;
          message.continuesDocument = true;
        }
      }
      pendingScroll = { docId: tab.docId, version: tab.version, scrollTop: tab.scrollTop, anchor: tab.anchor };
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
  // The capture above may have taken text straight out of the editor (the draft debounce
  // hadn't fired yet); without this it would only reach storage on pagehide.
  scheduleSessionSave();
}

// ---------------------------------------------------------------------------------
// Markdown / JSON: which renderer this document asks for
// ---------------------------------------------------------------------------------

/** Points the control, the placeholder and the label at the active tab's kind. */
function updateKindButtons() {
  const tab = getActiveTab();
  const kind = tab ? tab.kind : KIND_MARKDOWN;
  for (const button of document.querySelectorAll("[data-kind-choice]")) {
    button.setAttribute("aria-pressed", String(button.dataset.kindChoice === kind));
  }
  const what = kind === KIND_JSON ? "JSON" : "Markdown";
  input.placeholder = "Paste " + what + " here";
  input.setAttribute("aria-label", what + " to read");
}

/** Reads the active document the other way. The text is untouched; only what is made of it changes. */
function setKind(newKind) {
  const tab = getActiveTab();
  if (!tab || !KINDS.includes(newKind) || tab.kind === newKind) return;
  captureActiveTabState();
  cancelInFlight();
  tab.kind = newKind;
  // The cached render is this text read the other way, so it is not this tab's render any more.
  // Clearing `renderedText` with it is what stops the re-render below putting the reader back at
  // an offset measured in a document that no longer exists: the same characters as a tree and as
  // prose are different lengths, different headings, different everything.
  tab.payload = null;
  tab.renderedText = null;
  tab.scrollTop = 0;
  tab.anchor = null;
  updateKindButtons();
  if (mode !== "edit") renderTab(tab, tab.markdown);
  renderTabStrip();
  scheduleSessionSave();
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
  // Measured from the editor's own left edge, not the workspace's. They are the same thing until
  // the file pane is open, and then they are 260 px apart: taking the workspace's edge would make
  // the divider jump that far the moment the pointer moved, and hand the editor a share of the
  // row it is not in.
  const editorLeft = pasteEl.getBoundingClientRect().left;
  splitRatio = clampRatio(((event.clientX - editorLeft) / rect.width) * 100);
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
// live preview (split mode): re-render ~400ms after the last keystroke, waiting rather than
// giving up when a render is already in flight.
// ---------------------------------------------------------------------------------

function scheduleLivePreview() {
  clearTimeout(liveTimer);
  liveTimer = setTimeout(runLivePreview, SAVE_DEBOUNCE_MS);
}

function runLivePreview() {
  if (mode !== "split") return;
  // A render is still running. This timer is the only thing that starts a live preview, so
  // dropping this turn stranded everything typed since that render began: on any connection
  // slower than localhost, a burst of typing that ended while a request was in flight simply
  // never reached the preview, until the reader happened to type again. Come back instead.
  if (inFlight) {
    scheduleLivePreview();
    return;
  }
  const tab = getActiveTab();
  if (!tab) return;
  renderTab(tab, input.value, { continues: true });
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

  // Asking for the tab you are already on is not a switch, and must not be treated as one.
  // syncUIToActiveTab() below re-delivers the tab's cached payload, and the page now applies
  // that rather than recognising it: the whole document would be replaced — throwing away the
  // diagrams and highlighting that had landed on it — and the reader put back at
  // `tab.scrollTop`, which for a tab nobody has switched away from is still its initial 0.
  // Clicking the tab you were reading dropped you at the top of it. Reachable from the tab
  // strip by mouse and by Enter or Space, and from "Open file" or a drop of a file that is
  // already open, because openFiles() ends by activating the first one.
  if (id === activeId) {
    const current = getActiveTab();
    if (current) current.lastActive = ++activityCounter;
    return;
  }

  captureActiveTabState();
  cancelInFlight();
  activeId = id;
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
    tab.anchor = null;
    tab.folderPath = null; // an emptied tab is nobody's file any more
    shownDocId = null; // whatever the pane still shows is not this tab's document any more
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
  tab.scrollTop = 0;
  tab.anchor = null;
  tab.folderPath = null;
  shownDocId = null;
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

// `folderPath`/`folderSession` are deliberately not here. A document's text is the visitor's and
// comes back; the folder it came out of is a live handle the browser will not hand back after a
// reload, and localStorage cannot hold one. Saving the path would put a name in storage that
// nothing on the next visit can open, and a tab that claimed to belong to a folder that is not
// there. So the documents come back and the tree does not, and the Files button says "Open a
// folder" — one gesture, which is the price the browser charges for not letting a page read your
// disk behind your back.
function serializeTab(tab) {
  return { id: tab.id, title: tab.title, markdown: tab.markdown, kind: tab.kind, scrollTop: tab.scrollTop, anchor: tab.anchor, name: tab.name || null };
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

  // Keep as much as storage takes, most useful first: the active tab, then the others by how
  // recently they were looked at. One document too big to store on its own is skipped, not
  // fatal — the smaller ones behind it are still kept. Each attempt writes the whole kept set
  // in tab order, so the last one that succeeded is already what's in storage.
  const active = getActiveTab();
  const priority = active ? [active] : [];
  for (const t of [...tabs].sort((a, b) => b.lastActive - a.lastActive)) {
    if (t !== active) priority.push(t);
  }

  const keptIds = new Set();
  for (const tab of priority) {
    const candidate = tabs.filter((t) => t === tab || keptIds.has(t.id));
    // Point the restore at a tab it will actually have; the active one unless it didn't fit.
    const restoreId = candidate.some((t) => t.id === activeId) ? activeId : candidate[0].id;
    if (trySave(SESSION_KEY, JSON.stringify({ tabs: candidate.map(serializeTab), activeId: restoreId }))) {
      keptIds.add(tab.id);
    }
  }

  // The tabs are still open and still editable — they just won't come back, so say so plainly.
  const dropped = tabs.length - keptIds.size;
  if (dropped > 0) {
    toast(
      `Not enough browser storage: ${dropped} ${dropped === 1 ? "document" : "documents"} won't be here after a reload.`,
    );
  }
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
    const taken = new Set();
    tabs = source.tabs.map((raw) => hydrateTab(raw, taken));
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
  if (pageReady) deliver({ type: "tocVisibility", visible: tocVisible, width: tocWidth });
}

function deliverPayload(messages) {
  const render = messages.find((m) => m.type === "render");
  shownDocId = render ? render.docId : null;
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
 * the active tab, delivers it and puts the reader back where they were once it lands.
 * Only ever called while the preview pane is visible (mode "split" or "read").
 *
 * `continues` means this render carries on the document already on screen — the next
 * keystroke in Split, a ticked checkbox — rather than putting a different one there: a
 * continuation leaves the reader exactly where they are, anything else starts from where
 * the tab left off, or at the top when the text is not the one the tab last showed. */
async function renderTab(tab, text, { continues = false } = {}) {
  cancelInFlight();

  // Whether the page should hold its scroll position through the swap. Decided before
  // anything else can change it (see `preserveScroll` below).
  const inPlace = continues && tab.id === activeId && shownDocId === tab.docId;
  // Whether the position this tab remembers was measured in a render of this same text. It
  // was not, if the reader replaced the document and asked to read it: its pixel offset means
  // nothing in the new one (the anchor below still applies when its heading survived the edit).
  const sameText = tab.renderedText === text;
  tab.markdown = text;

  // The cached render belongs to the text that produced it. Drop it before anything can fail,
  // so a tab whose render never landed re-renders when it is looked at again instead of
  // showing the last good version of text it no longer holds.
  tab.payload = null;

  // `kind` is how the server is told which renderer to use; it picks the document name itself
  // from it (RenderKinds), so nothing the document or its file name contains reaches that
  // decision. An unknown kind is a 400 there, which is why only the two constants are ever sent.
  const body = JSON.stringify({ markdown: text, kind: tab.kind });
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
      // The server renders every document as if it were new, so every re-render would drop
      // the reader back at the top: typing one more character in Split, or ticking a task
      // checkbox halfway down a long document in Read. When this render replaces the document
      // already on screen, say so and let the page keep the position it is at (§7.1 rule 6,
      // §7.3 "scroll preservation") — the desktop host does exactly this for a live reload.
      message.preserveScroll = inPlace;
      message.continuesDocument = inPlace;
    }
    return message;
  });
  tab.payload = messages;
  tab.renderedText = text;

  if (tab.id === activeId) {
    lastPayload = messages;
    tocEntries = tocEntriesFromPayload(messages);
    updateTocButton();
    // A document the reader has just put in front of themselves starts at the top. The anchor
    // has to go with the offset when that happens: it was measured in the text the tab used to
    // hold, and applyScrollPosition() prefers it whenever the heading id still exists — which a
    // second version of the same file is full of, so the offset's 0 never got a chance.
    pendingScroll = inPlace
      ? null
      : {
          docId: tab.docId,
          version: tab.version,
          scrollTop: sameText ? tab.scrollTop : 0,
          anchor: sameText ? tab.anchor : null,
        };
    if (pageReady) deliverPayload(messages);
  }
  renderTabStrip();
  scheduleSessionSave();
}

const ERRORS = {
  0: ["Can't reach " + APP_NAME, "Check your connection, then try again."],
  // Status 0 while the browser knows it is offline: the shell and the documents came back from this
  // machine, but rendering happens on the server, so it can't.
  offline: ["You're offline", "Your documents are still here, but " + APP_NAME + " renders them on the server."],
  400: ["This document couldn't be read", "The server couldn't read this document."],
  408: ["That took too long", "The upload took too long. Check your connection, then try again."],
  413: ["This document is too large", "This document is over 512 KB."],
  422: ["This document is too complex", "It takes too long to render. Try a smaller one."],
  429: ["Too many documents at once", "Too many requests. Try again in a moment."],
  503: ["The server is busy", "The server is busy. Try again in a moment."],
  500: ["Something went wrong", "This document couldn't be rendered."],
};

function showError(status, payload) {
  const key = status === 0 && navigator.onLine === false ? "offline" : status;
  const [title, fallback] = ERRORS[key] || (status >= 500 ? ERRORS[500] : ERRORS[400]);
  const message = payload && typeof payload.message === "string" ? payload.message : fallback;
  lastPayload = null;
  shownDocId = null; // the error view replaced the document; nothing is "already on screen"
  tocEntries = 0;
  updateTocButton();
  deliver({
    type: "error",
    // The protocol's DocumentErrorKind (§7.2). 413 and 422 both mean "this document", not "this moment", which
    // is what tooLarge says; everything else maps to renderFailed.
    kind: status === 413 || status === 422 ? "tooLarge" : "renderFailed",
    title,
    message,
    path: "",
  });
}

function renderFromInput() {
  const text = input.value;
  const tab = getActiveTab();
  if (!tab) return;
  if (text.trim().length === 0) {
    toast(tab.kind === KIND_JSON ? "Paste some JSON first." : "Paste some Markdown first.");
    input.focus();
    return;
  }
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
  renderTab(tab, tab.markdown, { continues: true });
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
    toast(hadBadType ? "Choose a .md, .markdown, .txt or .json file." : "This file is over 512 KB.");
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
      // The name decides how it is read: a .json opened or dropped is a JSON document, with no
      // question asked and nothing to turn on first.
      tab = createTab({ markdown: text, name: file.name, title: file.name, kind: kindForName(file.name) });
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

// ---------------------------------------------------------------------------------
// files: the folder pane
// ---------------------------------------------------------------------------------
//
// What a folder costs the server: nothing, until a file is opened, and then exactly one
// `POST /api/render` carrying that file's characters — the same request the Read button makes
// for pasted text. The tree, the names, the sizes and every file the reader never opens stay in
// the browser. That is why a two-thousand-file folder does not go anywhere near the sixty
// renders a minute a visitor is allowed.

/** Puts an already-open tab for `path` back on screen. True when there was one. */
function activateFolderTab(path, session) {
  const tab = tabs.find((t) => t.folderPath === path && t.folderSession === session);
  if (!tab) return false;
  activateTab(tab.id);
  if (mode === "edit") setMode("read");
  return true;
}

/** Opens a file read out of the open folder, in a tab, exactly as "Open file" would. */
function openFolderFile({ path, name, session, text }) {
  // A file whose text is already open under its own name is that document, whichever way it was
  // opened: the same rule `openFiles` uses, and what keeps re-opening a folder after a reload
  // from doubling every tab the reader still has.
  let tab = tabs.find((t) => t.name === name && t.markdown === text);
  if (!tab) {
    tab = createTab({ markdown: text, name, title: name, kind: kindForName(name) });
    tabs.push(tab);
  }
  tab.folderPath = path;
  tab.folderSession = session;
  renderTabStrip();
  activateTab(tab.id);
  if (mode === "edit") setMode("read"); // show the opened file, not a blank editor
  scheduleSessionSave();
}

initFolder({
  activate: activateFolderTab,
  open: openFolderFile,
  toast: (text) => toast(text),
  maxBytes: MAX_BODY_BYTES,
});

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
  if (pendingScroll && message.docId === pendingScroll.docId && message.version === pendingScroll.version) {
    // Twice: at "content", so the reader is roughly in place as soon as there is something to
    // see, and again at "enhanced", because the diagrams, math and highlighting that landed in
    // between moved everything below them. The second pass is skipped if the reader has
    // scrolled since the first — they have taken over, and yanking them back is worse than
    // being a screen off.
    if (message.phase === "content" || Math.round(mdrMain.scrollTop) === pendingScroll.placed) {
      applyScrollPosition(pendingScroll);
      pendingScroll.placed = Math.round(mdrMain.scrollTop);
    }
    if (message.phase === "enhanced") pendingScroll = null;
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
    case "tocWidthChanged":
      tocWidth = clampTocWidth(Number(message.width));
      save(KEY_TOC_WIDTH, String(tocWidth));
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
      // A dropped FOLDER is in `dataTransfer.files` too — a zero-byte File with no type — so
      // links.js, which is the desktop's code and must go on behaving as it does there, sends
      // it here like any other drop. folder.js has already taken it; without this the reader
      // would get "Choose a .md, .markdown or .txt file." on top of the folder they just opened.
      if (folderDropInProgress()) break;
      if (files && files.length > 0) openFiles(files);
      break;
    case "dropText":
      // The desktop app fetches a dropped address. A browser tab can't (its own CSP allows same-origin
      // requests only, and most servers send no CORS headers), so it says what a drop can be instead.
      toast("Drop a Markdown or JSON file to open it.");
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
    case "saveFile":
      saveFile(message);
      break;
    default:
      break;
  }
});

/**
 * The page asked for a file to be saved (a diagram export, §7.2 `saveFile`). The desktop host
 * shows a save dialog because §8.4 cancels WebView downloads; in a browser tab an ordinary
 * download is exactly the right thing, so the shim turns the message back into one.
 */
function saveFile(message) {
  const extension = message.mimeType === "image/svg+xml" ? "svg"
    : message.mimeType === "image/png" ? "png"
      : null;
  if (!extension) return;

  let url = null;
  try {
    const binary = atob(String(message.base64 ?? ""));
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

    url = URL.createObjectURL(new Blob([bytes], { type: message.mimeType }));
    const link = document.createElement("a");
    link.href = url;
    link.download = `${downloadStem(message.name, extension)}.${extension}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
  } catch {
    toast("Couldn't save the file.");
  } finally {
    // Revoked on the next turn: the URL has to still resolve while the click is being handled.
    if (url) setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

/** Windows reserves these stems on every extension; the same list MdReader.Core keeps. */
const RESERVED_NAMES = /^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i;

/** Characters of the stem kept, counted in UTF-16 units — the same unit MdReader.Core counts in. */
const MAX_STEM_LENGTH = 80;

/**
 * There is no host to sanitize this on the web, so this is the sanitizer: the rules of
 * MdReader.Core's `ExportFileTypes.SuggestFileName`, transliterated. Letters, decimal digits and
 * combining marks are kept; every run of anything else collapses to the first separator in it, or
 * to a dash; the stem is cut to 80 units, never mid-character; leading and trailing separators go;
 * and a reserved device name is defused. No path, no drive, no dot and therefore no second
 * extension can survive it.
 *
 * One documented difference from the host: iterating by code point keeps letters outside the Basic
 * Multilingual Plane, which `char.IsLetterOrDigit` turns into a dash. The web version is the more
 * generous of the two, and neither can produce anything unsafe.
 */
function downloadStem(name, extension) {
  let stem = "";
  let lastWasSeparator = false;

  for (const character of String(name ?? "")) {   // by code point, so a pair is never split
    if (stem.length >= MAX_STEM_LENGTH) break;
    if (/[\p{L}\p{Nd}\p{M}]/u.test(character)) {
      stem += character;
      lastWasSeparator = false;
      continue;
    }
    if (lastWasSeparator || stem.length === 0) continue;
    stem += "_- ".includes(character) ? character : "-";
    lastWasSeparator = true;
  }

  stem = stem.replace(/[-_ ]+$/u, "");
  if (stem.length === 0) return "diagram";
  return RESERVED_NAMES.test(stem) ? `${stem}-${extension}` : stem;
}

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
// installable app: the service worker, the install offer and the update offer (§15)
// ---------------------------------------------------------------------------------

const KEY_INSTALL_DISMISSED = "mdr.web.install-dismissed";
const INSTALL_SNOOZE_MS = 30 * 24 * 60 * 60 * 1000; // "not now" means "not now", for a month

const installButton = document.getElementById("mdr-web-install");
let installPrompt = null;

function installDismissedRecently() {
  const at = Number(load(KEY_INSTALL_DISMISSED));
  return Number.isFinite(at) && at > 0 && Date.now() - at < INSTALL_SNOOZE_MS;
}

window.addEventListener("beforeinstallprompt", (event) => {
  // Keep the browser's own bar out of the way; the offer goes where the other actions are, and only
  // if the visitor hasn't already said no.
  event.preventDefault();
  installPrompt = event;
  if (installButton && !installDismissedRecently()) installButton.hidden = false;
});

window.addEventListener("appinstalled", () => {
  installPrompt = null;
  if (installButton) installButton.hidden = true;
  toast("MdReader is installed.");
});

if (installButton) {
  installButton.addEventListener("click", async () => {
    const prompt = installPrompt;
    installPrompt = null; // a prompt can only be used once
    installButton.hidden = true;
    if (!prompt) return;
    try {
      await prompt.prompt();
      const choice = await prompt.userChoice;
      if (choice && choice.outcome === "dismissed") save(KEY_INSTALL_DISMISSED, String(Date.now()));
    } catch (err) {
      console.warn("mdr: the install prompt failed", err);
    }
  });
}

/**
 * A new version has finished installing and is waiting. Take it, without asking.
 *
 * There used to be an Update button here, which is a fair thing for an installed app to offer
 * and a strange thing to meet on a website: a website is whatever the server is serving, and
 * nobody expects to press a button to get it. So the new version is applied as soon as it is
 * ready, and the page reloads into it.
 *
 * Nothing is lost in that reload. It fires `pagehide`, which writes down the open tabs, their
 * text and where the reader had got to, and the page restores all of it on the way back up
 * (a browser probe drives exactly this and checks the document and the scroll position come back).
 *
 * The worker still does not call skipWaiting in its own install handler. It waits to be told,
 * here, once the new shell is completely cached - so a page always has index.html, the CSS and
 * every app module from a single version, never a mix of two.
 */
function applyUpdate(worker) {
  if (!worker) return;
  worker.postMessage({ type: "mdr-skip-waiting" });
}

if ("serviceWorker" in navigator) {
  // True only when this page is already controlled: the very first install claims the page too, and
  // that must not reload anything.
  const hadController = !!navigator.serviceWorker.controller;
  let reloading = false;

  navigator.serviceWorker.addEventListener("controllerchange", () => {
    if (!hadController || reloading) return;
    reloading = true;
    // The reload below fires `pagehide`, and that handler takes down where the reader is and
    // saves. This save is belt and braces for the draft debounce, and measurably changes
    // nothing on its own: a build without it keeps the reader's place across an update just
    // the same, and a build with neither is what actually loses it.
    persistSession();
    window.location.reload();
  });

  window.addEventListener("load", () => {
    navigator.serviceWorker.register("sw.js").then((registration) => {
      if (registration.waiting) applyUpdate(registration.waiting);
      registration.addEventListener("updatefound", () => {
        const installing = registration.installing;
        if (!installing) return;
        installing.addEventListener("statechange", () => {
          // "installed" while something is already in charge = a waiting update, not a first install.
          if (installing.state === "installed" && navigator.serviceWorker.controller) applyUpdate(installing);
        });
      });
    }, (err) => {
      // Not fatal: the app works exactly as before, it just won't open offline.
      console.warn("mdr: the service worker didn't register", err);
    });
  });
}

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

for (const button of document.querySelectorAll("[data-kind-choice]")) {
  button.addEventListener("click", () => setKind(button.dataset.kindChoice));
}

systemDark.addEventListener("change", () => {
  if (themeChoice === "system") postTheme();
});

tocToggleButton.addEventListener("click", () => {
  if (pageReady) deliver({ type: "tocToggle" });
});

printButton.addEventListener("click", requestPrint);

// The toolbar's JSON link. links.js is the desktop's code and must go on behaving as it does
// there, and what it does is take every `a[href]` in the page for document content: it cancels the
// click and hands the address to the host, which opens http(s) addresses in a new tab and says
// "Only web links can be opened here." to everything else. That is exactly right for a link inside
// a document, and wrong for one that is part of the app's own chrome — the button simply did
// nothing, which is how a browser probe found it. Stopping the event at the element, in the target
// phase, means the document-level listener never sees it and the browser's own default is left
// alone: the link navigates, middle click still opens it in a tab, and no rendered document can
// reach this because no rendered document is this element.
const jsonLink = document.getElementById("mdr-web-json");
if (jsonLink) {
  const keepFromLinksJs = (event) => event.stopPropagation();
  jsonLink.addEventListener("click", keepFromLinksJs);
  jsonLink.addEventListener("auxclick", keepFromLinksJs);
}

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
  // `fileInput.files` is the input's own live list, not a copy: clearing the input — which is
  // what makes picking the same file twice in a row fire `change` the second time — empties
  // the very list this handler is holding. Take the files out of it first, or every pick
  // reaches openFiles() as an empty list and is dropped without a word.
  const files = Array.from(fileInput.files);
  fileInput.value = "";
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

/**
 * `?kind=json` — how /json hands someone into the reader with JSON already chosen.
 *
 * It applies to the document in front of them when that document is empty, and otherwise opens a
 * new tab, because arriving from a page about JSON is not a reason to re-read the README somebody
 * already had open. Then it is taken out of the address: a reload is a reload, not a second tab.
 */
function applyKindFromAddress() {
  if (params.get("kind") !== KIND_JSON) return;

  try {
    const url = new URL(window.location.href);
    url.searchParams.delete("kind");
    window.history.replaceState(null, "", url.pathname + url.search + url.hash);
  } catch {
    // History is unavailable or the address is not one we can rewrite: the parameter stays in it,
    // which costs a spare tab on the next reload and nothing else.
  }

  const current = getActiveTab();
  if (current && current.markdown.trim().length === 0) {
    current.kind = KIND_JSON;
  } else {
    const tab = createTab({ kind: KIND_JSON });
    tabs.push(tab);
    activeId = tab.id;
    tab.lastActive = ++activityCounter;
  }
  // Either way the reader is now in front of an empty document, which needs the editor and the
  // control beside it rather than a blank preview.
  if (mode === "read") applyModeAttribute("edit");
  scheduleSessionSave();
}

updateChoiceButtons();
updateTocButton();

initSession();
applyKindFromAddress();
renderTabStrip();
syncUIToActiveTab();
