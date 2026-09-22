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
  if (el) el.scrollIntoView({ block: "start" });
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

function hasFiles(dataTransfer) {
  return !!dataTransfer && !!dataTransfer.types && Array.from(dataTransfer.types).includes("Files");
}

document.addEventListener("dragover", (event) => {
  if (!hasFiles(event.dataTransfer)) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = "copy";
});

document.addEventListener("drop", (event) => {
  const files = event.dataTransfer && event.dataTransfer.files;
  if (!files || files.length === 0) return;
  event.preventDefault();
  sendWithFiles({ type: "drop" }, files);
});
