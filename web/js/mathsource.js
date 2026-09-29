// mathsource.js — clicking (or pressing Enter on) a rendered formula offers its LaTeX source
// (§7.3). KaTeX replaces the source with layout markup, so the source is kept here, keyed by the
// element, and handed back on demand.
//
// The source is copied through the host's `copy` message, the same path the code blocks use, so
// there is no clipboard permission involved and the web version's shim handles it too.

import { send } from "./bridge.js";
import { dismissPopover, isStickyPopoverOpen, popoverAnchor, showPopover } from "./popover.js";

const COPY_RESET_DELAY_MS = 2000;

/** How far the pointer may travel between press and release before it counts as a drag, not a click. */
const CLICK_SLOP_PX = 4;

const HINT_ID = "mdr-math-hint";
const HINT_TEXT = "Show the LaTeX source";

/** @type {WeakMap<Element, string>} rendered element → the LaTeX it was rendered from */
const sources = new WeakMap();

let pressedAt = null;

/**
 * Remembers the source of a formula and makes it reachable from the keyboard.
 * @param {Element} element the `span.math` / `div.math` KaTeX rendered into
 * @param {string} source the LaTeX between the delimiters
 */
export function registerMath(element, source) {
  if (!source) return;
  sources.set(element, source);
  element.setAttribute("tabindex", "0");
  element.setAttribute("title", HINT_TEXT);
  // A focusable span with no name is announced as nothing but its own content, so a reader who
  // tabs onto a formula hears the maths and nothing about what Enter would do. One shared
  // *description*, never a label: the accessible name has to stay the formula itself.
  element.setAttribute("aria-describedby", ensureHint().id);
  element.classList.add("mdr-math-source");
}

/** The one visually-hidden node every formula's `aria-describedby` points at. */
function ensureHint() {
  let hint = document.getElementById(HINT_ID);
  if (!hint) {
    hint = document.createElement("span");
    hint.id = HINT_ID;
    hint.className = "mdr-visually-hidden";
    hint.textContent = HINT_TEXT;
    document.body.appendChild(hint);
  }
  return hint;
}

/** Installs the delegated listeners. Called once by main.js. */
export function initMathSource() {
  const content = document.getElementById("mdr-content");
  if (!content) return;

  content.addEventListener("pointerdown", (event) => {
    pressedAt = mathTarget(event.target) ? { x: event.clientX, y: event.clientY } : null;
  });

  content.addEventListener("click", (event) => {
    const element = mathTarget(event.target);
    if (!element) return;

    // A drag across a formula is a selection, not a request for its source.
    const start = pressedAt;
    pressedAt = null;
    if (start && (Math.abs(event.clientX - start.x) > CLICK_SLOP_PX
                  || Math.abs(event.clientY - start.y) > CLICK_SLOP_PX)) {
      return;
    }
    const selection = window.getSelection();
    if (selection && !selection.isCollapsed) return;

    event.preventDefault();
    toggle(element);
  });

  content.addEventListener("keydown", (event) => {
    if (event.key !== "Enter" && event.key !== " ") return;
    const element = mathTarget(event.target);
    if (!element || element !== document.activeElement) return;
    event.preventDefault();
    toggle(element);
  });
}

/** Closes the source popover if one is open. main.js calls this around every render. */
export function resetMathSource() {
  if (isStickyPopoverOpen()) dismissPopover("render");
}

function mathTarget(target) {
  if (!(target instanceof Element)) return null;
  const element = target.closest(".math");
  return element && sources.has(element) ? element : null;
}

function toggle(element) {
  if (isStickyPopoverOpen() && popoverAnchor() === element) {
    dismissPopover("toggle");
    return;
  }

  const source = sources.get(element);
  if (!source) return;
  showPopover({ anchor: element, content: buildPanel(source), mode: "sticky", label: "LaTeX source" });
}

function buildPanel(source) {
  const panel = document.createElement("div");
  panel.className = "mdr-mathsource";

  const button = document.createElement("button");
  button.type = "button";
  button.className = "mdr-mathsource-copy";
  button.textContent = "Copy LaTeX";
  button.addEventListener("click", () => {
    send({ type: "copy", text: source });
    button.textContent = "Copied";
    button.classList.add("is-copied");
    window.clearTimeout(button.__mdrCopyResetTimer);
    button.__mdrCopyResetTimer = window.setTimeout(() => {
      button.textContent = "Copy LaTeX";
      button.classList.remove("is-copied");
    }, COPY_RESET_DELAY_MS);
  });

  const text = document.createElement("pre");
  text.className = "mdr-mathsource-text";
  text.textContent = source;

  panel.appendChild(button);
  panel.appendChild(text);
  return panel;
}
