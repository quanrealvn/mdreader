// webapp.js — the web version's "host" (ARCHITECTURE §15). It plays the part the desktop app
// plays for the page: after `ready` it posts theme, readingStyle and tocVisibility, renders
// documents through POST /api/render and delivers the returned host messages unchanged
// (except docId/version, which it owns), and answers the page's messages: link, copy,
// tocVisibilityChanged, retry, log, drop, rendered. It also owns the web-only chrome: the
// header toolbar, the paste view, file open / drop, and the toast.
//
// Module-scoped state only (content ids can clobber window properties, §7.3).

import { attachHost, deliver } from "./bridge.js";

const API_URL = "api/render";
const MAX_BODY_BYTES = 2 * 1024 * 1024;
const DOC_ID = 1;
const UNTITLED = "document.md"; // the title the server's renderer gives a document without an h1
const APP_NAME = "MdReader";
const TOAST_MS = 3200;
const DRAFT_SAVE_MS = 400;
const TEXT_EXTENSIONS = /\.(md|markdown|mdown|mkd|mkdn|mdwn|txt|text)$/i;

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
    // quota exceeded or storage blocked: the document just isn't remembered
  }
}

// ---------------------------------------------------------------------------------
// state
// ---------------------------------------------------------------------------------

let pageReady = false;
let version = 0;
let currentText = null; // the document on screen (or being rendered)
let currentName = null; // file name it came from, if any
let lastPayload = null; // messages of the last applied render, re-delivered if the page reloads
let inFlight = null; // AbortController of the running request
let tocEntries = 0;
let toastTimer = 0;
let draftTimer = 0;

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
// views
// ---------------------------------------------------------------------------------

function setView(view) {
  root.setAttribute("data-view", view);
  save(KEY_VIEW, view);
  if (view === "paste") {
    document.title = APP_NAME;
  }
}

function showPaste({ focus = true } = {}) {
  cancelInFlight();
  setView("paste");
  updateCount();
  if (focus) input.focus();
}

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
  document.getElementById("mdr-main").setAttribute("aria-busy", String(busy));
}

function webTitle(title) {
  if (!title || title === UNTITLED) return currentName ? currentName + " - " + APP_NAME : APP_NAME;
  return title + " - " + APP_NAME;
}

/** Renders `text` through the API and shows it in the reader view. */
async function renderText(text, name) {
  cancelInFlight();
  currentText = text;
  currentName = name || null;
  save(KEY_DOC, text);
  save(KEY_NAME, currentName);

  const body = JSON.stringify({ markdown: text });
  if (new Blob([body]).size > MAX_BODY_BYTES) {
    setView("reader");
    showError(413, null);
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
    setView("reader");
    showError(0, null);
    console.warn("mdr: render request failed", err);
    return;
  }

  if (inFlight !== controller) return; // superseded while the body was downloading
  inFlight = null;
  setBusy(false);
  setView("reader");

  if (!response.ok || !payload || !Array.isArray(payload.messages) || payload.messages.length === 0) {
    showError(response.ok ? 500 : response.status, payload);
    return;
  }

  // The page owns nothing about versions across requests (the server may restart): number
  // every render here so each one is newer than the last (§7.1 rule 4).
  version += 1;
  const messages = payload.messages.map((message) => {
    if (message.type === "render" || message.type === "renderPart") {
      message.docId = DOC_ID;
      message.version = version;
    }
    if (message.type === "render") {
      message.title = webTitle(message.title);
      tocEntries = Array.isArray(message.toc) ? message.toc.length : 0;
    }
    return message;
  });
  updateTocButton();
  lastPayload = messages;
  if (pageReady) deliverPayload(messages);
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
  renderText(text, text === currentText ? currentName : null);
}

// ---------------------------------------------------------------------------------
// files: open + drop
// ---------------------------------------------------------------------------------

function isTextFile(file) {
  return TEXT_EXTENSIONS.test(file.name) || (file.type && file.type.startsWith("text/"));
}

async function openFile(file) {
  if (!file) return;
  if (!isTextFile(file)) {
    toast("Choose a .md, .markdown or .txt file.");
    return;
  }
  if (file.size > MAX_BODY_BYTES) {
    toast("This file is over 2 MB.");
    return;
  }
  try {
    const text = await file.text();
    input.value = text;
    updateCount();
    await renderText(text, file.name);
  } catch (err) {
    console.warn("mdr: couldn't read the file", err);
    toast("Couldn't read this file.");
  }
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
  if (message.version !== version) return;
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
    case "retry":
      if (currentText !== null) renderText(currentText, currentName);
      else showPaste();
      break;
    case "log":
      logToConsole(message.level, message.message);
      break;
    case "drop":
      if (files && files.length > 0) openFile(files[0]);
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

document.getElementById("mdr-web-edit").addEventListener("click", () => {
  if (currentText !== null && input.value.trim().length === 0) input.value = currentText;
  showPaste();
});

document.getElementById("mdr-web-new").addEventListener("click", () => {
  clearDocument();
  showPaste();
});

document.getElementById("mdr-web-home").addEventListener("click", () => showPaste());

// ---------------------------------------------------------------------------------
// paste view
// ---------------------------------------------------------------------------------

function updateCount() {
  const length = input.value.length;
  countEl.textContent = length === 0 ? "" : length.toLocaleString() + (length === 1 ? " character" : " characters");
}

function clearDocument() {
  cancelInFlight();
  input.value = "";
  currentText = null;
  currentName = null;
  lastPayload = null;
  save(KEY_DOC, null);
  save(KEY_NAME, null);
  updateCount();
}

renderButton.addEventListener("click", renderFromInput);
document.getElementById("mdr-web-open").addEventListener("click", () => fileInput.click());
document.getElementById("mdr-web-clear").addEventListener("click", () => {
  clearDocument();
  input.focus();
});

fileInput.addEventListener("change", () => {
  const file = fileInput.files && fileInput.files[0];
  fileInput.value = ""; // picking the same file again must fire `change` again
  openFile(file);
});

input.addEventListener("input", () => {
  updateCount();
  clearTimeout(draftTimer);
  // Keep the draft too, so a reload doesn't lose what was typed or pasted.
  draftTimer = setTimeout(() => save(KEY_DOC, input.value || null), DRAFT_SAVE_MS);
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

const savedText = load(KEY_DOC);
if (savedText) {
  input.value = savedText;
  currentText = savedText;
  currentName = load(KEY_NAME);
}
updateCount();

if (root.getAttribute("data-view") === "reader" && savedText) {
  renderText(savedText, currentName);
} else {
  setView("paste");
}
