// links.js — the single document-level click/auxclick listener for `a[href]` (§7.3 web
// rules), in-page fragment scrolling, and file drag/drop.
//
// Exports `resolveFragment`/`scrollToFragment` so other modules (main.js for the
// `scrollTo` message and the render's `scrollToId`; toc.js relies on the same `a[href]`
// delegation rather than calling these directly) share one fragment-resolution
// implementation instead of duplicating the decode/prefix/lowercase fallback chain.

import { send, sendWithFiles } from "./bridge.js";

const USER_CONTENT_PREFIX = "user-content-";
const MDR_PREFIX_RE = /^mdr-/i;

/**
 * Resolves a (possibly percent-encoded) fragment id to its element.
 *
 * Ids starting with "mdr-" (any case) mirror `ContentIdPolicy` on the host: the
 * sanitizer renames any such content id to "user-content-" + id specifically so it
 * can never collide with our own app element ids (`mdr-toc`, `mdr-main`, ...). So for
 * those, we look up ONLY "user-content-" + id — never the bare id, which would only
 * ever resolve to an app chrome element, never a legitimate content target.
 *
 * For everything else: the decoded id, the decoded id with the "user-content-" prefix,
 * then lowercase variants of both.
 *
 * Either way, only elements inside `#mdr-content` are valid targets — app chrome must
 * never be a scroll destination.
 */
export function resolveFragment(id) {
  if (id == null || id === "") return null;

  const contentRoot = document.getElementById("mdr-content");
  if (!contentRoot) return null;

  let decoded = id;
  try {
    decoded = decodeURIComponent(id);
  } catch {
    // Malformed percent-escapes: fall back to the raw id.
  }

  const candidates = MDR_PREFIX_RE.test(decoded)
    ? [USER_CONTENT_PREFIX + decoded]
    : [decoded, USER_CONTENT_PREFIX + decoded, decoded.toLowerCase(), USER_CONTENT_PREFIX + decoded.toLowerCase()];

  for (const candidate of candidates) {
    const el = document.getElementById(candidate);
    if (el && contentRoot.contains(el)) return el;
  }
  return null;
}

/**
 * Scrolls to the given (decoded) fragment id, or to the top of the content when `id` is
 * empty/nullish. Never touches `location.hash`.
 */
export function scrollToFragment(id) {
  const mdrMain = document.getElementById("mdr-main");
  if (id == null || id === "") {
    if (mdrMain) mdrMain.scrollTo({ top: 0, left: 0, behavior: "auto" });
    return;
  }
  const el = resolveFragment(id);
  if (!el) return;
  revealAncestors(el);
  el.scrollIntoView({ block: "start" });
}

/**
 * Opens every collapsed <details> the target sits inside, so it can actually be scrolled to.
 *
 * A heading inside a closed block still reaches the contents panel, and clicking its entry did
 * nothing at all: a browser will not scroll to something it is not showing, so the reader
 * clicked an appendix and stayed where they were, with no sign anything had happened. Opening
 * the block is what they asked for by clicking, and it is what the browser does by itself when
 * a fragment in the address bar points inside one.
 *
 * Outermost first: opening a block lays out its contents and moves everything after it, so the
 * caller's scroll lands against a settled layout rather than the one before the last expand.
 */
function revealAncestors(el) {
  const closed = [];
  for (let node = el.parentElement; node; node = node.parentElement) {
    if (node.tagName === "DETAILS" && !node.open) closed.push(node);
  }

  for (let i = closed.length - 1; i >= 0; i--) {
    closed[i].open = true;
  }
}

const XLINK_NS = "http://www.w3.org/1999/xlink";

// Mermaid emits `<a xlink:href>` inside its SVG output even under `securityLevel:
// "strict"` — an SVG <a> generally carries no plain `href` attribute, only the
// namespaced `xlink:href` one, so both the selector and the attribute read below have
// to account for it or every Mermaid diagram link is silently inert.
function findAnchor(target) {
  return target && typeof target.closest === "function" ? target.closest("a[href], a[*|href]") : null;
}

function getHref(anchor) {
  const plain = anchor.getAttribute("href");
  if (plain != null) return plain;
  return anchor.getAttributeNS(XLINK_NS, "href");
}

function handleActivate(event) {
  // auxclick fires for the middle button and the right/back/forward buttons; we only
  // care about middle-click (button 1) — right-click opens the context menu instead.
  if (event.type === "auxclick" && event.button !== 1) return;

  const anchor = findAnchor(event.target);
  if (!anchor) return;

  const href = getHref(anchor);
  if (href == null) return;

  if (href.startsWith("#")) {
    event.preventDefault();
    scrollToFragment(href.slice(1));
    return;
  }

  event.preventDefault();
  send({
    type: "link",
    href,
    newTab: event.ctrlKey || event.metaKey || event.button === 1,
  });
}

document.addEventListener("click", handleActivate);
document.addEventListener("auxclick", handleActivate);

const URI_LIST = "text/uri-list";
const PLAIN_TEXT = "text/plain";

// A dropped address is a string, not a document. The host caps it again; this only keeps a
// stray drop of a whole article out of the message.
const MAX_DROP_TEXT = 4096;

function hasFiles(dataTransfer) {
  return !!dataTransfer && !!dataTransfer.types && Array.from(dataTransfer.types).includes("Files");
}

// A link dragged out of a browser carries no file, only text: `text/uri-list`, and usually
// `text/plain` as well. The page reads it and hands it over as it found it — it never decides
// what counts as an address; the host does that (`DocumentUrl`).
function hasText(dataTransfer) {
  if (!dataTransfer || !dataTransfer.types) return false;
  const types = Array.from(dataTransfer.types);
  return types.includes(URI_LIST) || types.includes(PLAIN_TEXT);
}

// Dropping text into a text box is the text box's business. The desktop page has none (its editor
// is a native control outside the web view), but the web version's editor is a <textarea> right
// here, and taking its drops away would be a regression.
function isEditable(target) {
  if (!target || !target.tagName) return false;
  return target.tagName === "TEXTAREA" || target.tagName === "INPUT" || target.isContentEditable === true;
}

function droppedText(dataTransfer) {
  const list = dataTransfer.getData(URI_LIST);
  if (list) {
    // A uri-list is line based, and "#" starts a comment line.
    for (const line of list.split(/\r?\n/)) {
      const trimmed = line.trim();
      if (trimmed && trimmed.charAt(0) !== "#") return trimmed.slice(0, MAX_DROP_TEXT);
    }
  }
  return (dataTransfer.getData(PLAIN_TEXT) || "").trim().slice(0, MAX_DROP_TEXT);
}

document.addEventListener("dragover", (event) => {
  const files = hasFiles(event.dataTransfer);
  if (!files && (!hasText(event.dataTransfer) || isEditable(event.target))) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = "copy";
});

document.addEventListener("drop", (event) => {
  const files = event.dataTransfer && event.dataTransfer.files;
  if (files && files.length > 0) {
    event.preventDefault();
    sendWithFiles({ type: "drop" }, files);
    return;
  }

  if (!event.dataTransfer || isEditable(event.target)) return;
  const text = droppedText(event.dataTransfer);
  if (!text) return;
  event.preventDefault();
  send({ type: "dropText", text });
});
