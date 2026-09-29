// fold.js — folding a section away and bringing it back (§7.3 reading comfort).
//
// The affordance is a small chevron button at the end of every heading that has anything under
// it. Deliberately a button, and deliberately not the whole heading: a heading is also text the
// reader selects and copies, and it may contain a link, so turning the whole line into a
// control would hide a trap in every heading of every document. The button is explicit, it is
// in the tab order where a heading is not, and it carries `aria-expanded`. A click anywhere
// else in the heading — including on a link inside it — is left alone and reaches links.js's
// document-level listener exactly as it did before.
//
// What is folded is a real <details>, built around the section the first time it is folded and
// then kept. That is the whole point rather than an implementation detail: the two pieces of
// this page that have to know what the reader cannot see already understand <details> and
// nothing else. toc.js's `isHiddenFromReader` walks the <details> above a heading, so a folded
// heading stops being named as the section being read without toc.js hearing about folding at
// all; and links.js's `scrollToFragment` opens every collapsed <details> above its target
// before scrolling, so a contents entry inside a folded section still takes the reader there —
// it unfolds on the way. Hiding the section any other way would mean a second kind of invisible
// that both of them would have to learn.
//
// A folded section is not marked in the contents panel, and that is a decision rather than an
// omission. The panel is the shape of the document, not of the reader's view of it, and every
// entry in it still works: picking one inside a fold opens the fold and goes there, so greying
// it out would say "not available" about something that is. The panel does stop *highlighting*
// headings inside a fold, because they are genuinely not on screen — that falls out of the
// <details> above, and it is the one thing folding should change there. Painting anything else
// into the panel from here would also mean chasing it: toc.js rebuilds its list whenever the
// reader sorts or filters, and our marks would quietly vanish the first time they did.
//
// The state that survives is the set of folded headings, held for as long as the same document
// is on screen and dropped when a different one arrives. It is not written down anywhere: there
// is no host message for it, and a fold is something the reader did to read this page now, not
// a preference they want back next week. A live reload — the file changed on disk, a checkbox
// was ticked, the next keystroke in the web version's split view — is the case that matters,
// and there having the appendix you just folded spring open again would be the file change
// undoing your own act.

import { on } from "./bridge.js";
import { scrollToFragment } from "./links.js";

const SVG_NS = "http://www.w3.org/2000/svg";
const HEADINGS = "h1, h2, h3, h4, h5, h6";
const HEADING_TAGS = new Set(["H1", "H2", "H3", "H4", "H5", "H6"]);

let mdrMain = null;
let mdrContent = null;

/** Section keys the reader has folded, in this document. */
let folded = new Set();

/** The document the keys above belong to, from the host's render messages. */
let docId = null;

/** `scrollToId` of the render being applied, if it carried one (see `react`). */
let scrollToId = null;

/** The article's first heading last time folds were applied: a different element means the host
 *  has put a different render in the article, rather than an enhancer rewriting part of one. */
let firstHeading = null;

let wrapperCount = 0;
let observer = null;
let rearranging = false;

/**
 * Wires folding into the article. Safe to call once, from main.js.
 */
export function initFolds() {
  mdrMain = document.getElementById("mdr-main");
  mdrContent = document.getElementById("mdr-content");
  if (!mdrMain || !mdrContent) return;

  // Registered before main.js's own `render` handler, so this runs while the article still
  // holds the previous document — all it needs is what the render is about, not what it says.
  on("render", (message) => {
    const continues = message.continuesDocument ?? !!message.preserveScroll;
    if (message.docId !== docId || !continues) folded.clear();
    docId = message.docId;
    scrollToId = message.scrollToId ?? null;
  });

  // One delegated listener on the article, so it survives every innerHTML replacement.
  mdrContent.addEventListener("click", onClick);

  // `toggle` doesn't bubble, so it is caught on the way down. Every change of fold state passes
  // through here, whichever side asked for it — the button, or links.js opening the way to a
  // fragment it was told to scroll to — so there is one place that decides what is folded.
  mdrContent.addEventListener("toggle", onToggle, true);

  observer = new MutationObserver(onContentMutated);
  observer.observe(mdrContent, { childList: true });

  react();
}

// ---------------------------------------------------------------------------------
// Reacting to what the host put in the article
// ---------------------------------------------------------------------------------

function onContentMutated(records) {
  if (rearranging) return;
  // The enhancers replace a diagram's source with its SVG, and wrap code blocks in toolbars,
  // hundreds of times on a big document. None of that adds or removes a heading, and answering
  // it would mean walking every heading in the article each time.
  if (!touchesHeadings(records)) return;
  react();
}

function touchesHeadings(records) {
  for (const record of records) {
    for (const node of record.addedNodes) {
      if (isOrHoldsHeading(node)) return true;
    }
    for (const node of record.removedNodes) {
      if (isOrHoldsHeading(node)) return true;
    }
  }
  return false;
}

function isOrHoldsHeading(node) {
  if (node.nodeType !== 1) return false;
  return HEADING_TAGS.has(node.tagName) || node.querySelector(HEADINGS) !== null;
}

/**
 * Gives every foldable heading its button, and — when the article has been replaced rather than
 * merely enhanced — folds again whatever the reader had folded.
 *
 * This runs from the mutation observer, which is a microtask: the browser has not painted since
 * the host replaced the article, so the sections come back already folded and nothing flashes
 * open first.
 */
function react() {
  const headings = Array.from(mdrContent.querySelectorAll(HEADINGS));
  const head = headings.length > 0 ? headings[0] : null;
  const replaced = head !== firstHeading;

  decorate(headings);
  if (!replaced) return;
  firstHeading = head;
  const wanted = scrollToId;
  scrollToId = null;
  if (folded.size === 0) return;

  // main.js has already put the reader back by this point, against an article with nothing
  // folded in it yet. The position it wrote was measured in the FOLDED article it replaced, and
  // folding again gives that article back, so the same number is the right one afterwards —
  // which is why the reader does not move even though the article briefly grew and shrank under
  // them. The one case that does not hold is a render that no longer has every section that was
  // folded, where the article ends up taller than it was; the reader keeps the number they had,
  // which is the closest thing to where they were that is still true.
  const target = mdrMain.scrollTop;
  applyFolds();

  if (wanted !== null) {
    // The render asked for a particular heading. It may be inside something the reader folded,
    // and links.js is what knows how to open the way to it.
    scrollToFragment(wanted);
    return;
  }
  if (mdrMain.scrollTop !== target) mdrMain.scrollTop = target;
}

function decorate(headings) {
  rearrange(() => {
    for (let i = 0; i < headings.length; i++) {
      const heading = headings[i];
      if (toggleOf(heading)) continue;
      if (!hasSection(heading)) continue;   // a heading with nothing under it folds to nothing
      heading.appendChild(createToggle(heading, key(heading, i)));
    }
  });
}

function applyFolds() {
  if (folded.size === 0) return;
  const kept = new Set();
  // Document order, so an outer section is wrapped before the inner ones it contains and each
  // inner wrapper is built inside the outer one rather than across its edge.
  for (const heading of mdrContent.querySelectorAll(HEADINGS)) {
    const button = toggleOf(heading);
    if (!button || !folded.has(button.dataset.mdrSection)) continue;
    const wrapper = wrapperOf(heading) ?? wrapSection(heading);
    if (!wrapper) continue;
    rearrange(() => {
      wrapper.open = false;
    });
    kept.add(button.dataset.mdrSection);
  }
  // Sections the new render no longer has are forgotten rather than carried along for ever.
  folded = kept;
}

// ---------------------------------------------------------------------------------
// The control
// ---------------------------------------------------------------------------------

function onClick(event) {
  const target = event.target;
  if (!target || typeof target.closest !== "function") return;
  const button = target.closest(".mdr-fold-toggle");
  if (!button) return;   // a link inside a heading, a task-list checkbox, ordinary text: not ours

  event.preventDefault();
  const heading = button.parentElement;
  if (!heading || !HEADING_TAGS.has(heading.tagName)) return;

  const wrapper = wrapperOf(heading) ?? wrapSection(heading);
  if (!wrapper) return;
  const open = !wrapper.open;
  rearrange(() => {
    wrapper.open = open;
  });
}

/**
 * A fold changed, whoever asked for it. The button and the remembered set are both brought into
 * line with what the <details> now says, so opening one from links.js on the way to a fragment
 * is not a state this file can end up disagreeing with.
 */
function onToggle(event) {
  const wrapper = event.target;
  if (!wrapper || wrapper.tagName !== "DETAILS") return;
  if (!wrapper.classList.contains("mdr-fold")) return;   // a <details> the document itself wrote

  const heading = wrapper.previousElementSibling;
  if (!heading || !HEADING_TAGS.has(heading.tagName)) return;
  const button = toggleOf(heading);
  if (!button) return;

  const open = wrapper.open;
  button.setAttribute("aria-expanded", open ? "true" : "false");
  button.setAttribute("aria-label", label(open, heading));
  button.title = open ? "Fold this section" : "Unfold this section";
  if (open) folded.delete(button.dataset.mdrSection);
  else folded.add(button.dataset.mdrSection);
}

function createToggle(heading, section) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "mdr-fold-toggle";
  button.dataset.mdrSection = section;
  button.setAttribute("aria-expanded", "true");
  button.setAttribute("aria-label", label(true, heading));
  button.title = "Fold this section";
  button.appendChild(createChevron());
  return button;
}

/** Says what activating the control will do, and to which section — the label is read out of
 *  context when tabbing, and "Unfold section" on its own would be one of a dozen. The control
 *  itself is drawn, not written, so it adds nothing to the heading's text either way. */
function label(open, heading) {
  return (open ? "Fold section: " : "Unfold section: ") + heading.textContent.trim();
}

/** The contents panel's twisty, at the same size and drawn the same way: an SVG rather than a
 *  text glyph, because a Unicode triangle fell back to a faint dash at 12px in some captures. */
function createChevron() {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("class", "mdr-fold-chevron");
  svg.setAttribute("viewBox", "0 0 16 16");
  svg.setAttribute("width", "12");
  svg.setAttribute("height", "12");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("focusable", "false");
  const path = document.createElementNS(SVG_NS, "path");
  path.setAttribute("d", "M6 3.5l5 4.5-5 4.5");
  path.setAttribute("fill", "none");
  path.setAttribute("stroke", "currentColor");
  path.setAttribute("stroke-width", "2");
  path.setAttribute("stroke-linecap", "round");
  path.setAttribute("stroke-linejoin", "round");
  svg.appendChild(path);
  return svg;
}

// ---------------------------------------------------------------------------------
// The section itself
// ---------------------------------------------------------------------------------

/**
 * Everything under `heading` up to the next heading of the same or a higher level, taken from
 * among its own siblings — a heading inside a blockquote or a document's own <details> owns
 * only what is in there with it.
 */
function sectionNodes(heading) {
  const level = levelOf(heading);
  const nodes = [];
  for (let node = heading.nextSibling; node; node = node.nextSibling) {
    if (node.nodeType === 1 && HEADING_TAGS.has(node.tagName) && levelOf(node) <= level) break;
    nodes.push(node);
  }
  return nodes;
}

function hasSection(heading) {
  return sectionNodes(heading).some((node) => node.nodeType === 1);
}

/** The <details> already built around this heading's section, if there is one. */
function wrapperOf(heading) {
  const next = heading.nextElementSibling;
  return next && next.tagName === "DETAILS" && next.classList.contains("mdr-fold") ? next : null;
}

/**
 * Builds the <details> around the section and moves it inside, whitespace and all so the text
 * the reader would copy is unchanged. It is created open and closed afterwards by the caller,
 * which is what makes the browser fire a real `toggle` — the one signal toc.js and this file
 * both listen for.
 *
 * The summary exists because a <details> without one grows a "Details" line of the browser's
 * own; it is hidden, and the heading's button is the control.
 */
function wrapSection(heading) {
  const nodes = sectionNodes(heading);
  if (!nodes.some((node) => node.nodeType === 1)) return null;

  const wrapper = document.createElement("details");
  wrapper.className = "mdr-fold";
  // "mdr-" is the app's own prefix: the sanitizer renames any content id that starts with it,
  // so this can never collide with something the document brought (links.js, ContentIdPolicy).
  wrapper.id = "mdr-fold-" + ++wrapperCount;
  wrapper.open = true;

  const summary = document.createElement("summary");
  summary.className = "mdr-fold-summary";
  wrapper.appendChild(summary);

  rearrange(() => {
    heading.after(wrapper);
    for (const node of nodes) wrapper.appendChild(node);
  });

  const button = toggleOf(heading);
  if (button) button.setAttribute("aria-controls", wrapper.id);
  return wrapper;
}

// ---------------------------------------------------------------------------------

function toggleOf(heading) {
  return heading.querySelector(":scope > .mdr-fold-toggle");
}

function levelOf(heading) {
  return heading.tagName.charCodeAt(1) - 48;
}

/** A heading's id where it has one — every heading the renderer produced does — and its place
 *  in the document where it doesn't, which is stable for as long as the document is. */
function key(heading, index) {
  return heading.id !== "" ? heading.id : "@" + index;
}

/** Our own rearranging of the article, which the mutation observer must not read as the host
 *  having put a different document there. */
function rearrange(work) {
  rearranging = true;
  try {
    work();
  } finally {
    observer.takeRecords();
    rearranging = false;
  }
}
