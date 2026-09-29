// previews.js — hovering (or focusing) a link or a footnote reference shows what it points at
// (§7.3). Same-document anchors and footnotes show an excerpt of the target; anything that leaves
// the document shows only its path, because the page never touches the file system.
//
// Rules this module exists to keep:
//   * it never fights text selection — nothing is shown while a selection is being made or while
//     a mouse button is down, and the popover itself is selectable;
//   * the keyboard gets the same thing the mouse does, through focusin;
//   * the excerpt is built from already-rendered, already-sanitized DOM, cloned and then stripped
//     of ids, hrefs and app widgets, so a preview can neither steal a duplicate id nor be clicked
//     through into a navigation.

import { resolveFragment } from "./links.js";
import { dismissPopover, isPointerInsidePopover, isStickyPopoverOpen, popoverAnchor, showPopover } from "./popover.js";

const SHOW_DELAY_MS = 350;
const HIDE_DELAY_MS = 120;

/** Characters of target text a preview shows before it is cut off. */
const TEXT_BUDGET = 600;

/** Blocks a heading preview reaches forward over (the heading itself counts as one). */
const MAX_BLOCKS = 3;

/** Past this, the target subtree is summarized as plain text instead of being cloned. */
const CLONE_LIMIT = 4000;

/** Characters of a href shown for a link that leaves the document. */
const PATH_BUDGET = 200;

const ELLIPSIS = "…";

const BLOCK_TAGS = new Set([
  "P", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "PRE", "BLOCKQUOTE", "TABLE", "TR", "TD", "TH",
  "DIV", "DL", "DT", "DD", "FIGURE", "UL", "OL", "DETAILS",
]);

const HEADING_TAGS = new Set(["H1", "H2", "H3", "H4", "H5", "H6"]);

const XLINK_NS = "http://www.w3.org/1999/xlink";

let showTimer = 0;
let hideTimer = 0;
/** The anchor this module currently has a popover open for (never one another module opened). */
let ourAnchor = null;

/** Installs the delegated listeners. Called once by main.js. */
export function initPreviews() {
  const content = document.getElementById("mdr-content");
  if (!content) return;

  content.addEventListener("mouseover", (event) => {
    const anchor = previewAnchor(event.target);
    if (!anchor) return;
    if (anchor === ourAnchor) {
      // Still on the link this preview belongs to; whatever scheduled a hide was a move between
      // the link and something inside it.
      window.clearTimeout(hideTimer);
      hideTimer = 0;
      return;
    }
    // A button held down means a drag (selecting text, panning a diagram): stay out of the way.
    if (event.buttons !== 0) return;
    scheduleShow(anchor);
  });

  content.addEventListener("mouseout", (event) => {
    const anchor = previewAnchor(event.target);
    if (!anchor) return;
    // A link with markup in it (a footnote reference's <sup>, a link around <code>) fires
    // mouseout every time the pointer crosses into one of its own children. That is not leaving.
    if (event.relatedTarget instanceof Node && anchor.contains(event.relatedTarget)) return;
    cancelShow();
    scheduleHide();
  });

  content.addEventListener("focusin", (event) => {
    const anchor = previewAnchor(event.target);
    if (!anchor) return;
    cancelShow();
    show(anchor);
  });

  content.addEventListener("focusout", (event) => {
    if (!previewAnchor(event.target)) return;
    cancelShow();
    scheduleHide();
  });

  // A click goes to links.js; the preview has done its job and would otherwise hang around over
  // the newly scrolled-to target.
  content.addEventListener("click", () => hide());
}

/** Drops any open preview and any pending one. main.js calls this around every render. */
export function resetPreviews() {
  cancelShow();
  window.clearTimeout(hideTimer);
  hideTimer = 0;
  if (ourAnchor) {
    ourAnchor = null;
    dismissPopover("render");
  }
}

/** The `a[href]` a preview would be for, or null. */
function previewAnchor(target) {
  if (!(target instanceof Element)) return null;
  const anchor = target.closest("a[href]");
  return anchor && anchor.getAttribute("href") ? anchor : null;
}

function scheduleShow(anchor) {
  cancelShow();
  showTimer = window.setTimeout(() => {
    showTimer = 0;
    show(anchor);
  }, SHOW_DELAY_MS);
}

function cancelShow() {
  if (showTimer) {
    window.clearTimeout(showTimer);
    showTimer = 0;
  }
}

function scheduleHide() {
  window.clearTimeout(hideTimer);
  hideTimer = window.setTimeout(() => {
    hideTimer = 0;
    // The pointer may have travelled from the link into the popover, which keeps it open.
    if (!isPointerInsidePopover()) hide();
  }, HIDE_DELAY_MS);
}

function hide() {
  if (!ourAnchor) return;
  const anchor = ourAnchor;
  ourAnchor = null;
  if (popoverAnchor() === anchor) dismissPopover("leave");
}

function show(anchor) {
  window.clearTimeout(hideTimer);
  hideTimer = 0;

  // A popover the reader opened on purpose (a formula's source) outranks a hover preview, and a
  // preview must never interrupt a selection the reader is making.
  if (isStickyPopoverOpen() || !anchor.isConnected) return;
  const selection = window.getSelection();
  if (selection && !selection.isCollapsed) return;

  const content = buildPreview(anchor);
  if (!content) return;

  ourAnchor = anchor;
  showPopover({ anchor, content, mode: "hover", label: "Link preview" });
}

/** The popover contents for `anchor`, or null when there is nothing worth showing. */
function buildPreview(anchor) {
  const href = anchor.getAttribute("href") ?? "";
  const wrapper = document.createElement("div");
  wrapper.className = "mdr-preview";

  if (!href.startsWith("#")) {
    // Anything outside this document: the page can't read it, so it shows where the link goes and
    // nothing more. The text is inserted as text, never parsed.
    wrapper.classList.add("mdr-preview--path");
    const path = document.createElement("span");
    path.className = "mdr-preview-path";
    path.textContent = truncate(href, PATH_BUDGET);
    wrapper.appendChild(path);
    return wrapper;
  }

  const target = resolveFragment(href.slice(1));
  if (!target) {
    wrapper.classList.add("mdr-preview--missing");
    wrapper.appendChild(note("This document has no such target."));
    return wrapper;
  }

  const root = excerptRoot(target);
  if (isFootnote(root)) wrapper.classList.add("mdr-preview--footnote");

  for (const node of excerptNodes(root)) wrapper.appendChild(node);
  trimToBudget(wrapper, TEXT_BUDGET);
  return wrapper;
}

/** The block the fragment really points at: an inline target (a footnote back-reference) shows its paragraph. */
function excerptRoot(target) {
  const content = document.getElementById("mdr-content");
  let element = target;
  while (element && element !== content && !BLOCK_TAGS.has(element.tagName)) {
    element = element.parentElement;
  }
  return element && element !== content ? element : target;
}

function isFootnote(element) {
  return element.tagName === "LI" && element.closest(".footnotes") !== null;
}

/** The cleaned clones a preview of `root` is made of. */
function excerptNodes(root) {
  if ((root.textContent ?? "").length > CLONE_LIMIT) {
    // A huge target (a long code block, a whole table) isn't worth cloning: show its text.
    return [note(truncate(collapse(root.textContent ?? ""), TEXT_BUDGET))];
  }

  const nodes = [clean(root.cloneNode(true))];
  if (!HEADING_TAGS.has(root.tagName)) return nodes;

  // A heading on its own says nothing the link text didn't: show what it introduces.
  let sibling = root.nextElementSibling;
  while (sibling && nodes.length < MAX_BLOCKS && !HEADING_TAGS.has(sibling.tagName)) {
    if ((sibling.textContent ?? "").length <= CLONE_LIMIT) {
      nodes.push(clean(sibling.cloneNode(true)));
    }
    sibling = sibling.nextElementSibling;
  }
  return nodes;
}

/**
 * Makes a cloned subtree safe to drop into the popover: no duplicate ids for resolveFragment to
 * trip over, no working links, no focus stops, and none of the toolbars the enhancers added.
 * @param {Node} node
 */
function clean(node) {
  if (!(node instanceof Element)) return node;

  for (const junk of node.querySelectorAll(".mdr-code-toolbar, .mdr-diagram-tools, .footnote-back-ref")) {
    junk.remove();
  }

  // A rendered diagram is a whole SVG document; a thumbnail of it would be unreadable anyway.
  for (const diagram of node.querySelectorAll("svg")) {
    diagram.replaceWith(note("Diagram"));
  }

  const elements = [node, ...node.querySelectorAll("*")];
  for (const element of elements) {
    element.removeAttribute("id");
    element.removeAttribute("tabindex");
    element.removeAttribute("data-line");
    element.removeAttribute("data-mdr-source");
    if (element.tagName === "A") {
      element.removeAttribute("href");
      element.removeAttributeNS(XLINK_NS, "href");
    }
    if (element.tagName === "INPUT") {
      element.setAttribute("disabled", "");
    }
  }
  return node;
}

/**
 * Cuts the preview down to its character budget by taking characters off the end, not nodes.
 *
 * Removing whole nodes is the obvious implementation and it is wrong: a preview is normally one
 * block — a paragraph, a footnote's `<li>`, a `<details>` — so "drop the container's last child"
 * drops that block's entire text and leaves nothing but the ellipsis. Trimming the deepest
 * trailing text node keeps the beginning of the target, which is the whole point of a preview.
 */
function trimToBudget(container, budget) {
  let overflow = (container.textContent ?? "").length - budget;
  if (overflow <= 0) return;

  // Bounded: every pass either shortens one text node (and stops) or removes one, and the excerpt
  // was already capped at CLONE_LIMIT characters before it got here.
  let passes = 4000;
  while (overflow > 0 && passes-- > 0) {
    const text = lastTextNode(container);
    if (!text) break;

    const value = text.nodeValue ?? "";
    if (value.length > overflow) {
      text.nodeValue = clip(value, value.length - overflow);
      break;
    }

    overflow -= value.length;
    text.remove();
  }

  container.appendChild(note(ELLIPSIS));
}

/**
 * The last text node inside `root`, dropping empty elements found on the way so the next call
 * doesn't walk past them again. Null when there is no text left at all.
 */
function lastTextNode(root) {
  for (let node = root; ;) {
    while (node.lastChild) node = node.lastChild;
    if (node === root) return null;
    if (node.nodeType === Node.TEXT_NODE) return node;

    // An element with no children contributes no text: drop it and descend again from its parent.
    const parent = node.parentNode;
    node.remove();
    node = parent ?? root;
  }
}

function note(text) {
  const span = document.createElement("span");
  span.className = "mdr-preview-note";
  span.textContent = text;
  return span;
}

function collapse(text) {
  return text.replace(/\s+/g, " ").trim();
}

/**
 * The first `length` characters of `text`, never splitting a surrogate pair — half an emoji is a
 * lone code unit, and it would travel from here into the clipboard and the protocol JSON.
 */
function clip(text, length) {
  const end = length > 0 && isHighSurrogate(text.charCodeAt(length - 1)) ? length - 1 : length;
  return text.slice(0, end);
}

function isHighSurrogate(code) {
  return code >= 0xd800 && code <= 0xdbff;
}

/** Cuts to `budget` characters *including* the ellipsis, so the result is never over budget. */
function truncate(text, budget) {
  return text.length <= budget ? text : clip(text, budget - 1) + ELLIPSIS;
}
