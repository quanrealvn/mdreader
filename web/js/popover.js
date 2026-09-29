// popover.js — the one floating layer the page uses for link/footnote previews and for the
// LaTeX source of a formula (§7.3). One layer, one open popover at a time: a hover preview can
// never appear on top of a source popover the reader opened on purpose.
//
// Two modes:
//   "hover"  — follows the pointer/focus, never takes focus, closes as soon as the reader moves
//              away. Used by previews.js.
//   "sticky" — opened by a deliberate click or Enter, takes focus so its buttons are reachable,
//              and closes on Escape, on a click outside, or on the next render. Used by
//              mathsource.js.
//
// The layer lives directly under <body>, outside #mdr-content, so replacing the document's HTML
// never detaches it — main.js calls dismissPopover() around every render instead.

const MARGIN = 8;
const MAX_WIDTH = 420;

/** App-owned id: the sanitizer renames any content id starting with `mdr-`, so it can't collide. */
const POPOVER_ID = "mdr-popover";

let layer = null;
let body = null;
let state = null; // { anchor, mode, restoreFocus, describedBy }

function ensureLayer() {
  if (layer) return layer;

  layer = document.createElement("div");
  layer.id = POPOVER_ID;
  layer.className = "mdr-popover";
  layer.hidden = true;

  body = document.createElement("div");
  body.className = "mdr-popover-body";
  layer.appendChild(body);
  document.body.appendChild(layer);

  // A pointer that lands inside a hover popover keeps it open (so the reader can read a long
  // preview, or select text in it); leaving it closes it again.
  layer.addEventListener("mouseenter", () => {
    if (state?.mode === "hover") state.pointerInside = true;
  });
  layer.addEventListener("mouseleave", () => {
    if (state?.mode !== "hover") return;
    state.pointerInside = false;
    dismissPopover("pointer-left");
  });

  return layer;
}

/** The element the open popover belongs to, or null. */
export function popoverAnchor() {
  return state?.anchor ?? null;
}

/** True while a popover opened with `mode: "sticky"` is showing. */
export function isStickyPopoverOpen() {
  return state?.mode === "sticky";
}

/**
 * Shows `content` next to `anchor`. Replaces whatever was open before.
 * @param {{anchor: Element, content: Node, mode?: "hover"|"sticky", label?: string}} options
 */
export function showPopover({ anchor, content, mode = "hover", label }) {
  if (!anchor?.isConnected) return;

  const element = ensureLayer();
  const previousFocus = mode === "sticky" ? document.activeElement : null;

  body.replaceChildren(content);
  element.classList.toggle("mdr-popover--sticky", mode === "sticky");
  // A tooltip may not contain interactive content, and the sticky mode puts a Copy button inside
  // and moves focus to it — that is a (non-modal) dialog, not a tooltip.
  element.setAttribute("role", mode === "sticky" ? "dialog" : "tooltip");
  if (mode === "sticky") element.setAttribute("aria-modal", "false");
  else element.removeAttribute("aria-modal");
  element.setAttribute("aria-label", label ?? "Preview");
  element.hidden = false;

  state = { anchor, mode, restoreFocus: previousFocus, pointerInside: false, describedBy: null };
  position(element, anchor);

  if (mode === "sticky") {
    // A click or Enter on a formula is a deliberate action, so the popover's Copy button has to
    // be reachable from the keyboard right away. Escape puts focus back where it came from.
    const focusable = element.querySelector("button, [href], [tabindex]");
    if (focusable instanceof HTMLElement) focusable.focus();
    return;
  }

  // A hover popover never takes focus, so nothing would announce it: point the anchor at it for
  // the time it is open. Focusing a link is one of the two ways to open one, and a preview a
  // screen reader never reads is no preview at all. Any value the document itself set is put back.
  state.describedBy = anchor.getAttribute("aria-describedby");
  anchor.setAttribute("aria-describedby", POPOVER_ID);
}

/**
 * Hides the popover, if any. `reason` is only used to decide whether focus goes back to the
 * element the sticky popover was opened from: it does for an explicit dismissal, not for a
 * render or a scroll that happened to close it.
 * @param {string} [reason]
 */
export function dismissPopover(reason) {
  if (!state || !layer) return;

  const { mode, restoreFocus, anchor, describedBy } = state;
  const wasInside = layer.contains(document.activeElement);
  state = null;

  layer.hidden = true;
  body.replaceChildren();
  layer.removeAttribute("aria-label");

  // Put the anchor's own aria-describedby back, whatever it was — including "not set at all".
  if (mode === "hover" && anchor.isConnected && anchor.getAttribute("aria-describedby") === POPOVER_ID) {
    if (describedBy === null) anchor.removeAttribute("aria-describedby");
    else anchor.setAttribute("aria-describedby", describedBy);
  }

  if (mode === "sticky" && (reason === "escape" || (wasInside && reason !== "render"))
      && restoreFocus instanceof HTMLElement && restoreFocus.isConnected) {
    restoreFocus.focus();
  }
}

/** True while the pointer is inside an open hover popover (previews.js must not close it then). */
export function isPointerInsidePopover() {
  return state?.mode === "hover" && state.pointerInside === true;
}

function position(element, anchor) {
  // Measure with the layer laid out but before it is placed, so the first frame is already right.
  element.style.left = "0px";
  element.style.top = "0px";
  element.style.maxWidth = Math.min(MAX_WIDTH, window.innerWidth - (2 * MARGIN)) + "px";

  const target = anchor.getBoundingClientRect();
  const size = element.getBoundingClientRect();

  let left = target.left + (target.width / 2) - (size.width / 2);
  left = Math.max(MARGIN, Math.min(left, window.innerWidth - size.width - MARGIN));

  // Below the anchor when it fits, otherwise above it; if neither fits, whichever side has more
  // room, clamped into the viewport so the popover is never partly off-screen.
  const below = target.bottom + MARGIN;
  const above = target.top - size.height - MARGIN;
  let top;
  if (below + size.height <= window.innerHeight - MARGIN) {
    top = below;
    element.classList.remove("mdr-popover--above");
  } else if (above >= MARGIN) {
    top = above;
    element.classList.add("mdr-popover--above");
  } else {
    top = Math.max(MARGIN, window.innerHeight - size.height - MARGIN);
    element.classList.remove("mdr-popover--above");
  }

  element.style.left = Math.round(left) + "px";
  element.style.top = Math.round(top) + "px";
}

/** Wires the dismissal rules that apply to every popover. Called once by main.js. */
export function initPopover() {
  ensureLayer();

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && state) {
      event.stopPropagation();
      dismissPopover("escape");
    }
  });

  // A click outside closes a sticky popover. Hover popovers are left alone here: they close on
  // mouseout, and closing them on pointerdown would cancel a drag that started on the link.
  document.addEventListener("pointerdown", (event) => {
    if (state?.mode !== "sticky") return;
    const target = event.target;
    if (layer.contains(target) || state.anchor === target
        || (target instanceof Node && state.anchor.contains(target))) {
      return;
    }
    dismissPopover("outside");
  }, true);

  // The popover is positioned in viewport coordinates, so anything that moves the document under
  // it has to close it rather than leave it pointing at the wrong place.
  const main = document.getElementById("mdr-main");
  main?.addEventListener("scroll", () => dismissPopover("scroll"), { passive: true });
  window.addEventListener("resize", () => dismissPopover("resize"));
  window.addEventListener("beforeprint", () => dismissPopover("print"));
}
