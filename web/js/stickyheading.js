// stickyheading.js — the heading of the section being read, kept at the top of the reading pane
// once the real one has scrolled out of sight.
//
// A breadcrumb rather than a single line. A Markdown heading is written to be read underneath
// the one above it: "Options", "Notes", "Example", "Windows" are among the commonest headings
// there are and on their own they say nothing at all. The ancestry is otherwise only visible in
// the contents panel — which is exactly what is missing when the window is narrow enough for
// that panel to become a drawer, or when the reader has closed it. So the strip carries the
// whole chain, innermost last, and cuts the oldest ancestors with an ellipsis once there are
// more than MAX_CRUMBS of them.
//
// What it names is the heading the TOP OF THE PANE belongs to: the last visible heading that
// has scrolled past it. That is the question a strip sitting at the top of the pane is answering
// — it is standing exactly where that heading would be if headings stuck — and it is not quite
// the question the contents panel answers, which is which section the reader is *reading* and
// uses a line 30% down to decide. The two differ only while a heading is in the top third of the
// pane, where it is on the page for the reader to see, and they agree the rest of the time.
//
// "Visible" is toc.js's `isHiddenFromReader`, imported rather than written again: a heading
// inside a collapsed <details> is still laid out, from the <details> box downwards, so its top
// reads as a perfectly ordinary number that overlaps the sections after it. That is what once
// had the contents panel naming headings nobody could see, and a second answer to the same
// question here would be a second chance to get it wrong. A section the reader folded is behind
// a <details> too (fold.js), so it drops out of the strip without this file knowing folding
// exists.
//
// Two things keep the strip out of the document's way. Nothing has scrolled past the top of the
// pane until the document's own title has, so there is no strip over the title, nor anywhere
// above the first heading. And a heading arriving at the top gets the top: while any heading is
// inside the band the strip occupies, the strip stands down for those few pixels rather than
// paint over it, and comes back once the heading it now names has gone by.
//
// Every frame does the same thing: one batch of measurements, then at most one write, and only
// when the chain of crumbs actually changed. Nothing is measured from inside a scroll handler.

import { isHiddenFromReader } from "./toc.js";

const HEADINGS = "h1, h2, h3, h4, h5, h6";

/** Crumbs kept, innermost included. Beyond this the chain is cut at the front and an ellipsis
 *  stands in for what was dropped: six levels of ancestry is a path, not a reminder. */
const MAX_CRUMBS = 4;

/** How long to wait for an animation frame that may never come before answering anyway. */
const FRAME_FALLBACK_MS = 200;

let mdrMain = null;
let mdrContent = null;
let strip = null;
let bar = null;
let trail = null;

/** The headings the reader can actually see, in document order. Rebuilt when the document is
 *  replaced or a <details> opens or closes, so the per-frame work is measurement and nothing
 *  else. */
let headings = [];
let headingsDirty = true;

/** The crumb texts currently written into the strip, so an unchanged chain costs no writes. */
let shown = [];

let queued = false;
let fallbackTimer = 0;

/**
 * Builds the strip and starts tracking. Safe to call once, from main.js.
 */
export function initStickyHeading() {
  mdrMain = document.getElementById("mdr-main");
  mdrContent = document.getElementById("mdr-content");
  if (!mdrMain || !mdrContent) return;

  build();

  mdrMain.addEventListener("scroll", schedule, { passive: true });
  window.addEventListener("resize", schedule, { passive: true });

  // A <details> opening or closing changes which headings the reader can see and where every
  // heading after it sits. `toggle` doesn't bubble, so it has to be caught on the way down.
  mdrContent.addEventListener("toggle", (event) => {
    if (!event.target || event.target.tagName !== "DETAILS") return;
    invalidate();
  }, true);

  // The host replacing the article, and a section being folded away: both rearrange the
  // article's own children. Deliberately not `subtree`, because the enhancers rewrite the
  // inside of a diagram or a formula hundreds of times per document and none of that changes
  // which headings exist — only how tall the things between them are, which the size observer
  // below is what notices.
  new MutationObserver(invalidate).observe(mdrContent, { childList: true });

  // Images, diagrams, math and highlighting land after the first paint and move every heading
  // below them with nothing scrolling. The pane's own box never changes when that happens — it
  // has `overflow-y: auto`, so it stays exactly as tall as it was however tall its contents
  // grow — so the article has to be watched as well as the pane.
  const resize = new ResizeObserver(schedule);
  resize.observe(mdrMain);
  resize.observe(mdrContent);

  invalidate();
}

function build() {
  strip = document.createElement("div");
  // Away rather than `hidden`, because the strip's own height is part of deciding whether it is
  // standing over a heading, and `display: none` is a box with no height to measure. It keeps
  // its (zero-height) place and stops being painted instead.
  strip.className = "mdr-sticky mdr-sticky--away";
  // Every word in here is already a heading in the document, one screenful up. A screen reader
  // that announced it again would read the same heading twice on every section, so the strip is
  // a picture of where you are and nothing more.
  strip.setAttribute("aria-hidden", "true");

  bar = document.createElement("div");
  bar.className = "mdr-sticky-bar";

  trail = document.createElement("div");
  trail.className = "mdr-sticky-trail";

  bar.appendChild(trail);
  strip.appendChild(bar);

  // First child of the pane, so `position: sticky` has the whole scrollable height to stick
  // through. The strip itself is zero-height and the bar inside it overflows, so the article
  // below starts exactly where it did before.
  mdrMain.insertBefore(strip, mdrMain.firstChild);
}

function invalidate() {
  headingsDirty = true;
  schedule();
}

function schedule() {
  if (queued) return;
  queued = true;
  requestAnimationFrame(update);
  // `queued` cleared only inside the frame callback would stay stuck true for good the first
  // time a frame never ran, and the strip would freeze on the section the reader had left. A
  // hidden WebView pauses requestAnimationFrame outright, a busy one merely drops frames;
  // either way, answer anyway.
  fallbackTimer = setTimeout(update, FRAME_FALLBACK_MS);
}

function update() {
  queued = false;
  clearTimeout(fallbackTimer);
  fallbackTimer = 0;
  if (!mdrMain || !mdrContent) return;

  if (headingsDirty) {
    refreshHeadings();
    headingsDirty = false;
  }

  apply(measure());
}

function refreshHeadings() {
  headings = [];
  for (const el of mdrContent.querySelectorAll(HEADINGS)) {
    if (isHiddenFromReader(el)) continue;
    headings.push({ el, level: el.tagName.charCodeAt(1) - 48, text: el.textContent.trim() });
  }
}

/**
 * The whole of this frame's layout reading, in one pass: the pane, the strip's own box and every
 * visible heading. Returns the crumb texts the strip should be showing, which may be none.
 */
function measure() {
  if (headings.length === 0) return [];

  const paneTop = mdrMain.getBoundingClientRect().top;
  const barBottom = paneTop + bar.getBoundingClientRect().height;

  const tops = new Array(headings.length);
  const bottoms = new Array(headings.length);
  for (let i = 0; i < headings.length; i++) {
    const rect = headings[i].el.getBoundingClientRect();
    tops[i] = rect.top;
    bottoms[i] = rect.bottom;
  }

  // The heading the top of the pane belongs to. Nothing has passed the top yet on the way down
  // to the first one — nor while the document's own title is still on screen — and there is then
  // nothing for the strip to stand in for.
  let current = -1;
  for (let i = 0; i < headings.length; i++) {
    if (bottoms[i] <= paneTop) current = i;
  }
  if (current < 0) return [];

  // The handover: a real heading anywhere in the strip's band gets the top of the pane to
  // itself, and the strip waits the few pixels out.
  for (let i = 0; i < headings.length; i++) {
    if (tops[i] < barBottom && bottoms[i] > paneTop) return [];
  }

  const chain = [current];
  let level = headings[current].level;
  for (let i = current - 1; i >= 0 && level > 1; i--) {
    if (headings[i].level < level) {
      chain.unshift(i);
      level = headings[i].level;
    }
  }

  const texts = chain.map((i) => headings[i].text);
  if (texts.length > MAX_CRUMBS) return ["…", ...texts.slice(texts.length - MAX_CRUMBS + 1)];
  return texts;
}

/** The only writes in the frame, and only when the chain of crumbs has actually changed. */
function apply(texts) {
  if (texts.length === shown.length && texts.every((text, i) => text === shown[i])) return;
  shown = texts;

  if (texts.length === 0) {
    strip.classList.add("mdr-sticky--away");
    return;
  }

  trail.textContent = "";
  for (let i = 0; i < texts.length; i++) {
    if (i > 0) {
      const separator = document.createElement("span");
      separator.className = "mdr-sticky-sep";
      separator.textContent = "›";
      trail.appendChild(separator);
    }
    const crumb = document.createElement("span");
    crumb.className = "mdr-sticky-crumb";
    crumb.textContent = texts[i];
    trail.appendChild(crumb);
  }

  strip.classList.remove("mdr-sticky--away");
}
