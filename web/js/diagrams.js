// diagrams.js — pan, zoom and export for the diagrams enhance.js renders (§7.3).
//
// A Mermaid diagram of any size is laid out to the page width, which makes anything with more
// than a few dozen nodes unreadable. This wraps the rendered SVG in a viewport the reader can
// drag and zoom, and adds a way to save it as SVG or PNG.
//
// Two rules shape the whole file:
//
//   1. It must never take scrolling away from the page. Zoom is a CSS `transform`, which does not
//      affect layout, so nothing reflows and the document's own scroll position is untouched. A
//      wheel (or trackpad pinch, which arrives as ctrl+wheel) only zooms while the viewport has
//      focus — click it or Tab to it first — so a reader scrolling past a diagram scrolls the
//      page exactly as before, and Ctrl+wheel still works the WebView's own zoom everywhere else.
//      Touch keeps `touch-action: pan-y`, so a vertical swipe always scrolls the document.
//
//   2. Saving goes through the host. §8.4 cancels every WebView download and that stays: the page
//      hands the bytes to the host with `saveFile` and the host shows the save dialog. The web
//      version's bridge shim turns the same message into an ordinary browser download.

import { log, send } from "./bridge.js";
import { createIconButton } from "./icons.js";

const SVG_NS = "http://www.w3.org/2000/svg";
const XLINK_NS = "http://www.w3.org/1999/xlink";

const MIN_SCALE = 0.5;
const MAX_SCALE = 8;
const ZOOM_STEP = 1.25;

/** Wheel zoom sensitivity; `deltaY` is normalized to pixels first. */
const WHEEL_FACTOR = 0.0018;

/** Arrow-key pan distance, in viewport pixels. */
const PAN_STEP_PX = 48;

/** Pointer travel past which a drag is a pan, not a click on something inside the diagram. */
const DRAG_SLOP_PX = 4;

/** The share of the window a zoomed diagram may occupy. At 100 % the viewport keeps its natural height. */
const MAX_VIEWPORT_FRACTION = 0.7;
const MIN_VIEWPORT_PX = 200;

/** Longest side of an exported PNG, and the resolution multiplier below that. */
const PNG_MAX_PX = 4096;
const PNG_SCALE = 2;

/**
 * An export whose name plus content is larger than this is refused rather than posted to the host.
 * `ProtocolConstants.MaxIncomingMessageChars` is 8 M and the JSON envelope costs a few dozen more,
 * so this leaves a wide margin — the host silently drops anything over its own limit, and the page
 * would have no way to tell the reader.
 */
const MAX_EXPORT_MESSAGE_CHARS = 6_000_000;

/** Characters of the nearest heading kept as a file-name suggestion; the host keeps 80 of them. */
const MAX_NAME_CHARS = 80;

const BUTTON_RESET_DELAY_MS = 2000;

/**
 * Viewers attached to diagrams in the document currently on screen, keyed by their container.
 * A plain collection of DOM nodes is a leak waiting to happen, so there are exactly two ways in
 * and two ways out: attachDiagramViewer adds, viewerState removes the container it was asked
 * about (the caller is replacing it), the resize pass drops anything already detached, and
 * resetDiagramViewers clears the lot before a render replaces the document.
 * @type {Map<Element, object>}
 */
const viewers = new Map();

/** @type {WeakMap<Element, {scale: number, tx: number, ty: number}>} container → its view state */
const states = new WeakMap();

/**
 * The pan/zoom state of an already-attached diagram, so a re-render (theme change, print mode)
 * can carry it over instead of snapping back to 100 %. Taking the state also gives up the viewer:
 * the only caller is about to replace this container.
 * @param {Element} container a `.mdr-mermaid` element
 */
export function viewerState(container) {
  viewers.delete(container);
  const state = states.get(container);
  return state ? { ...state } : null;
}

/**
 * Wraps the SVG inside `container` in a pan/zoom viewport and adds the controls.
 * Does nothing when the container holds no SVG (a diagram that failed to parse keeps its error
 * box) or when it has been wrapped already.
 * @param {Element} container the `.mdr-mermaid` element enhance.js built
 * @param {{scale: number, tx: number, ty: number} | null} [initial]
 */
export function attachDiagramViewer(container, initial = null) {
  if (container.querySelector(":scope > .mdr-diagram-viewport")) return;
  if (!container.querySelector("svg")) return;

  const viewport = document.createElement("div");
  viewport.className = "mdr-diagram-viewport";
  viewport.tabIndex = 0;
  viewport.setAttribute("role", "group");
  viewport.setAttribute("aria-label", "Diagram");
  // The instructions belong in the tooltip/description, not in the name: a screen reader would
  // otherwise read the whole sentence every time focus lands here.
  viewport.title = "Drag to pan. + and - zoom, 0 resets. Scroll to zoom while this is focused.";

  const canvas = document.createElement("div");
  canvas.className = "mdr-diagram-canvas";
  // Everything Mermaid produced moves across, not just the SVG: nothing it emitted is dropped.
  canvas.replaceChildren(...container.childNodes);
  viewport.appendChild(canvas);

  const viewer = { container, viewport, canvas, scale: 1, tx: 0, ty: 0, zoomLabel: null };
  const tools = buildTools(viewer);

  container.replaceChildren(viewport, tools);
  viewers.set(container, viewer);
  wire(viewer);

  if (initial) {
    viewer.scale = clampScale(initial.scale);
    viewer.tx = Number.isFinite(initial.tx) ? initial.tx : 0;
    viewer.ty = Number.isFinite(initial.ty) ? initial.ty : 0;
  }

  // enhance.js builds the container off-DOM and swaps it in as one step, so the viewport has no
  // size yet. A microtask runs after that swap — still before paint — and measures for real.
  if (container.isConnected) {
    apply(viewer);
  } else {
    queueMicrotask(() => {
      if (container.isConnected) apply(viewer);
    });
  }
}

/** Forgets every viewer. main.js calls this before a render replaces the document's HTML. */
export function resetDiagramViewers() {
  viewers.clear();
}

// ---------------------------------------------------------------------------
// Controls
// ---------------------------------------------------------------------------

function buildTools(viewer) {
  const tools = document.createElement("div");
  tools.className = "mdr-diagram-tools";
  tools.setAttribute("role", "group");
  tools.setAttribute("aria-label", "Diagram controls");

  const zoomOut = createIconButton("zoom-out", "Zoom out", "mdr-diagram-button");
  zoomOut.addEventListener("click", () => zoomBy(viewer, 1 / ZOOM_STEP));

  viewer.zoomLabel = document.createElement("span");
  viewer.zoomLabel.className = "mdr-diagram-zoom";
  viewer.zoomLabel.textContent = "100%";

  const zoomIn = createIconButton("zoom-in", "Zoom in", "mdr-diagram-button");
  zoomIn.addEventListener("click", () => zoomBy(viewer, ZOOM_STEP));

  const reset = createIconButton("reset", "Reset the view", "mdr-diagram-button");
  reset.addEventListener("click", () => resetView(viewer));

  const saveSvg = createIconButton("download", "Save as SVG", "mdr-diagram-button mdr-diagram-button--wide");
  saveSvg.appendChild(labelSpan("SVG"));
  saveSvg.addEventListener("click", () => void exportDiagram(viewer, saveSvg, "svg"));

  const savePng = createIconButton("download", "Save as PNG", "mdr-diagram-button mdr-diagram-button--wide");
  savePng.appendChild(labelSpan("PNG"));
  savePng.addEventListener("click", () => void exportDiagram(viewer, savePng, "png"));

  tools.append(zoomOut, viewer.zoomLabel, zoomIn, reset, saveSvg, savePng);
  return tools;
}

function labelSpan(text) {
  const span = document.createElement("span");
  span.className = "mdr-diagram-button-label";
  span.textContent = text;
  return span;
}

// ---------------------------------------------------------------------------
// Pan / zoom
// ---------------------------------------------------------------------------

function wire(viewer) {
  const { viewport } = viewer;

  /** @type {Map<number, {x: number, y: number}>} pointers currently down on this viewport */
  const pointers = new Map();
  let panning = false;
  let pinchDistance = 0;
  let travelled = 0;
  let suppressClick = false;

  viewport.addEventListener("wheel", (event) => {
    // Only a focused diagram takes the wheel; otherwise the page scrolls and Ctrl+wheel still
    // reaches the WebView's own zoom.
    if (!viewport.contains(document.activeElement)) return;
    const delta = normalizeWheel(event);
    // Only claim the event if it actually zoomed. At the scale limits there is nothing left to do,
    // and a focused diagram that silently eats the wheel would trap the reader on the page.
    if (zoomAt(viewer, viewer.scale * Math.exp(-delta * WHEEL_FACTOR), event.clientX, event.clientY)) {
      event.preventDefault();
    }
  }, { passive: false });

  viewport.addEventListener("pointerdown", (event) => {
    // A drag whose click never arrived (the pointer left the window) must not eat the next one.
    suppressClick = false;
    if (event.pointerType === "mouse" && event.button !== 0) return;
    // Mermaid emits links inside its SVG; a click on one must still reach links.js.
    if (event.target instanceof Element && event.target.closest("a[href], a[*|href]")) return;

    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    try {
      viewport.setPointerCapture(event.pointerId);
    } catch {
      // The pointer is already gone; the move/up handlers cope with that.
    }

    if (pointers.size === 1) {
      panning = true;
      travelled = 0;
    } else if (pointers.size === 2) {
      panning = false;
      pinchDistance = spread(pointers);
    }

    viewport.focus({ preventScroll: true });
  });

  viewport.addEventListener("pointermove", (event) => {
    const previous = pointers.get(event.pointerId);
    if (!previous) return;
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });

    if (pointers.size >= 2) {
      // Touch pinch: the two pointers' spread drives the scale, around their midpoint.
      const distance = spread(pointers);
      if (pinchDistance > 0 && distance > 0) {
        const centre = midpoint(pointers);
        zoomAt(viewer, viewer.scale * (distance / pinchDistance), centre.x, centre.y);
      }
      pinchDistance = distance;
      return;
    }

    if (!panning) return;
    const dx = event.clientX - previous.x;
    const dy = event.clientY - previous.y;
    travelled += Math.abs(dx) + Math.abs(dy);
    viewer.tx += dx;
    viewer.ty += dy;
    apply(viewer);
  });

  const release = (event) => {
    if (!pointers.delete(event.pointerId)) return;
    if (pointers.size < 2) pinchDistance = 0;
    if (pointers.size === 0) {
      if (panning && travelled > DRAG_SLOP_PX) suppressClick = true;
      panning = false;
    }
  };
  viewport.addEventListener("pointerup", release);
  viewport.addEventListener("pointercancel", release);

  // A drag that moved must not also activate whatever was under the pointer (a diagram link, or
  // the click handlers previews.js and links.js have on the document).
  viewport.addEventListener("click", (event) => {
    if (!suppressClick) return;
    suppressClick = false;
    event.preventDefault();
    event.stopPropagation();
  }, true);

  // The viewport is a tab stop, so a keyboard reader lands on every diagram in the document. Each
  // key is only claimed when it actually did something: at 100 % there is nothing to pan, and
  // swallowing the arrows there would leave the reader unable to scroll the page at all.
  viewport.addEventListener("keydown", (event) => {
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    let handled = false;
    switch (event.key) {
      case "+":
      case "=":
        handled = zoomBy(viewer, ZOOM_STEP);
        break;
      case "-":
      case "_":
        handled = zoomBy(viewer, 1 / ZOOM_STEP);
        break;
      case "0":
        handled = resetView(viewer);
        break;
      case "Home":
        handled = panTo(viewer, 0, 0);
        break;
      case "End":
        handled = panTo(viewer, Number.NEGATIVE_INFINITY, Number.NEGATIVE_INFINITY);
        break;
      case "ArrowLeft":
        handled = pan(viewer, PAN_STEP_PX, 0);
        break;
      case "ArrowRight":
        handled = pan(viewer, -PAN_STEP_PX, 0);
        break;
      case "ArrowUp":
        handled = pan(viewer, 0, PAN_STEP_PX);
        break;
      case "ArrowDown":
        handled = pan(viewer, 0, -PAN_STEP_PX);
        break;
      default:
        return;
    }
    if (handled) event.preventDefault();
  });
}

/** `deltaY` in pixels, whatever unit the device reports. */
function normalizeWheel(event) {
  if (event.deltaMode === 1) return event.deltaY * 16;   // lines
  if (event.deltaMode === 2) return event.deltaY * 400;  // pages
  return event.deltaY;
}

function spread(pointers) {
  const [a, b] = [...pointers.values()];
  return Math.hypot(a.x - b.x, a.y - b.y);
}

function midpoint(pointers) {
  const [a, b] = [...pointers.values()];
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

function clampScale(scale) {
  if (!Number.isFinite(scale)) return 1;
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
}

/** All four of these return whether the view actually moved, so a caller can decide to claim the input. */
function zoomBy(viewer, factor) {
  const rect = viewer.viewport.getBoundingClientRect();
  return zoomAt(viewer, viewer.scale * factor, rect.left + (rect.width / 2), rect.top + (rect.height / 2));
}

/** Zooms so that the content under (clientX, clientY) stays under it. */
function zoomAt(viewer, target, clientX, clientY) {
  const scale = clampScale(target);
  if (scale === viewer.scale) return false;

  const rect = viewer.viewport.getBoundingClientRect();
  const fx = clientX - rect.left;
  const fy = clientY - rect.top;

  // The canvas maps a point p to `t + scale * p`, so keeping the focal point fixed means
  // t' = f - scale' * (f - t) / scale.
  viewer.tx = fx - ((fx - viewer.tx) * scale / viewer.scale);
  viewer.ty = fy - ((fy - viewer.ty) * scale / viewer.scale);
  viewer.scale = scale;
  apply(viewer);
  return true;
}

function pan(viewer, dx, dy) {
  return panTo(viewer, viewer.tx + dx, viewer.ty + dy);
}

/** Moves to an offset; ±Infinity means "as far as it goes", because apply() clamps. */
function panTo(viewer, tx, ty) {
  return setView(viewer, viewer.scale, tx, ty);
}

function resetView(viewer) {
  return setView(viewer, 1, 0, 0);
}

/** Applies a view and reports whether the clamp left it anywhere new. */
function setView(viewer, scale, tx, ty) {
  const before = `${viewer.scale}|${viewer.tx}|${viewer.ty}`;
  viewer.scale = scale;
  viewer.tx = tx;
  viewer.ty = ty;
  apply(viewer);   // clamps before it writes the transform, so ±Infinity never reaches the DOM
  return before !== `${viewer.scale}|${viewer.tx}|${viewer.ty}`;
}

function apply(viewer) {
  const { viewport, canvas } = viewer;
  const naturalHeight = canvas.offsetHeight;   // layout height: a transform never changes it

  if (viewer.scale === 1) {
    // Back to exactly how the diagram looked before anyone touched it: natural height, no clip.
    viewport.style.removeProperty("height");
  } else {
    const limit = Math.max(MIN_VIEWPORT_PX, Math.round(window.innerHeight * MAX_VIEWPORT_FRACTION));
    viewport.style.height = Math.min(limit, Math.round(naturalHeight * viewer.scale)) + "px";
  }

  clampOffsets(viewer);
  canvas.style.transform = `translate(${viewer.tx.toFixed(2)}px, ${viewer.ty.toFixed(2)}px) scale(${viewer.scale})`;
  viewport.classList.toggle("is-zoomed", viewer.scale !== 1);

  if (viewer.zoomLabel) viewer.zoomLabel.textContent = Math.round(viewer.scale * 100) + "%";
  states.set(viewer.container, { scale: viewer.scale, tx: viewer.tx, ty: viewer.ty });
}

/** Keeps the content inside the viewport: centred when it fits, never dragged past its edges. */
function clampOffsets(viewer) {
  const { viewport, canvas, scale } = viewer;
  const viewWidth = viewport.clientWidth;
  const viewHeight = viewport.clientHeight;
  const contentWidth = canvas.offsetWidth * scale;
  const contentHeight = canvas.offsetHeight * scale;

  viewer.tx = contentWidth <= viewWidth
    ? (viewWidth - contentWidth) / 2
    : Math.min(0, Math.max(viewWidth - contentWidth, viewer.tx));
  viewer.ty = contentHeight <= viewHeight
    ? (viewHeight - contentHeight) / 2
    : Math.min(0, Math.max(viewHeight - contentHeight, viewer.ty));
}

// The zoomed height is a share of the window, and the clamp depends on the viewport's width, so
// both have to be recomputed when the window (or the WebView's zoom) changes. Viewers whose
// container has been replaced in the meantime are dropped here.
window.addEventListener("resize", () => {
  for (const [container, viewer] of [...viewers]) {
    if (!container.isConnected) {
      viewers.delete(container);
      continue;
    }
    apply(viewer);
  }
});

// ---------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------

async function exportDiagram(viewer, button, format) {
  try {
    const bytes = format === "svg" ? svgBytes(viewer) : await pngBytes(viewer);
    const base64 = toBase64(bytes);
    const name = diagramName(viewer.container);

    // The budget is on the whole message, not just the content: the host drops anything over
    // ProtocolConstants.MaxIncomingMessageChars, and the name comes from a heading in an untrusted
    // document. Capping the name (below) keeps it from ever being the reason, and checking the sum
    // here means the reader is told rather than left with a button that did nothing.
    if (name.length + base64.length > MAX_EXPORT_MESSAGE_CHARS) {
      flash(button, "Too big");
      log("warn", `diagrams: the ${format.toUpperCase()} export is too large to save (${bytes.length} bytes).`);
      return;
    }

    send({
      type: "saveFile",
      name,
      mimeType: format === "svg" ? "image/svg+xml" : "image/png",
      base64,
    });
  } catch (err) {
    flash(button, "Failed");
    log("warn", `diagrams: the ${format.toUpperCase()} export failed: ${describeError(err)}`);
  }
}

function svgBytes(viewer) {
  return new TextEncoder().encode(serializeSvg(viewer).text);
}

/**
 * The diagram as a standalone SVG document: its own namespaces, explicit pixel dimensions, an
 * opaque background so it stays readable in a viewer with a different page colour, and the two
 * rules markdown.css applies to Mermaid's edge labels baked in (they live in our stylesheet, not
 * in Mermaid's own `<style>`, so an export without them loses the label chips).
 */
function serializeSvg(viewer) {
  const svg = viewer.canvas.querySelector("svg");
  if (!svg) throw new Error("the diagram has no SVG");

  const clone = svg.cloneNode(true);
  clone.setAttribute("xmlns", SVG_NS);
  clone.setAttribute("xmlns:xlink", XLINK_NS);
  clone.removeAttribute("style");   // Mermaid's inline `max-width`, which a file doesn't want

  const box = exportSize(viewer, clone);
  const { width, height } = box;
  clone.setAttribute("width", String(width));
  clone.setAttribute("height", String(height));
  if (!clone.getAttribute("viewBox")) clone.setAttribute("viewBox", `0 0 ${width} ${height}`);

  const styles = getComputedStyle(document.documentElement);
  const background = styles.getPropertyValue("--mdr-bg").trim() || "#ffffff";
  const chip = styles.getPropertyValue("--mdr-bg-subtle").trim() || background;
  const foreground = styles.getPropertyValue("--mdr-fg").trim() || "#000000";

  const style = document.createElementNS(SVG_NS, "style");
  style.textContent = `.edgeLabel,.edgeLabel p,.labelBkg{background-color:${chip};color:${foreground};}`;

  // In the diagram's own coordinates, not percentages: Mermaid's viewBox often has a non-zero
  // origin, and a rect at 0,0 then leaves a transparent strip down the right and bottom of the
  // saved file.
  const backdrop = document.createElementNS(SVG_NS, "rect");
  backdrop.setAttribute("x", String(box.x));
  backdrop.setAttribute("y", String(box.y));
  backdrop.setAttribute("width", String(box.width));
  backdrop.setAttribute("height", String(box.height));
  backdrop.setAttribute("fill", background);

  clone.prepend(style, backdrop);
  return {
    text: '<?xml version="1.0" encoding="UTF-8"?>\n' + new XMLSerializer().serializeToString(clone),
    width,
    height,
    background,
  };
}

/**
 * The diagram's own coordinate box — its full resolution, not the size it happens to be shown at,
 * and including the origin, which Mermaid does not put at 0,0.
 */
function exportSize(viewer, clone) {
  const viewBox = (clone.getAttribute("viewBox") ?? "").split(/[\s,]+/).map(Number).filter(Number.isFinite);
  if (viewBox.length === 4 && viewBox[2] > 0 && viewBox[3] > 0) {
    return { x: viewBox[0], y: viewBox[1], width: viewBox[2], height: viewBox[3] };
  }
  return {
    x: 0,
    y: 0,
    width: Math.max(1, viewer.canvas.offsetWidth),
    height: Math.max(1, viewer.canvas.offsetHeight),
  };
}

async function pngBytes(viewer) {
  const { text, width, height, background } = serializeSvg(viewer);
  const scale = Math.min(PNG_SCALE, PNG_MAX_PX / width, PNG_MAX_PX / height);

  // A data: URL, which `img-src … data:` already allows — no blob, no object URL, and nothing
  // that would need a CSP change.
  const url = "data:image/svg+xml;base64," + toBase64(new TextEncoder().encode(text));
  const image = new Image();
  image.width = width;
  image.height = height;
  await new Promise((resolve, reject) => {
    image.addEventListener("load", () => resolve(), { once: true });
    image.addEventListener("error", () => reject(new Error("the diagram couldn't be rasterized")), { once: true });
    image.src = url;
  });

  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(width * scale));
  canvas.height = Math.max(1, Math.round(height * scale));
  const context = canvas.getContext("2d");
  if (!context) throw new Error("no 2D canvas context");
  context.fillStyle = background;
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(image, 0, 0, canvas.width, canvas.height);

  const blob = await new Promise((resolve, reject) => {
    try {
      canvas.toBlob((result) => (result ? resolve(result) : reject(new Error("the PNG came out empty"))), "image/png");
    } catch (err) {
      reject(err);   // a tainted canvas throws SecurityError here
    }
  });
  return new Uint8Array(await blob.arrayBuffer());
}

/** Base64 without a data: round trip; chunked so a megabyte doesn't blow the argument limit. */
function toBase64(bytes) {
  const CHUNK = 0x8000;
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += CHUNK) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + CHUNK));
  }
  return btoa(binary);
}

/**
 * A file-name suggestion from the nearest heading above the diagram, capped here rather than only
 * at the host: a document can make its headings any length it likes, and an uncapped name would
 * push the message past the protocol's size limit and take the export down with it — silently,
 * because the page's send() would still have succeeded.
 */
function diagramName(container) {
  const content = document.getElementById("mdr-content");
  let node = container;
  while (node && node !== content) {
    for (let sibling = node.previousElementSibling; sibling; sibling = sibling.previousElementSibling) {
      if (/^H[1-6]$/.test(sibling.tagName)) {
        const text = (sibling.textContent ?? "").trim();
        if (text) return capName(text);
      }
    }
    node = node.parentElement;
  }
  return capName(document.title) || "diagram";
}

/** The first MAX_NAME_CHARS code units, never splitting a surrogate pair. */
function capName(text) {
  const value = (text ?? "").trim();
  if (value.length <= MAX_NAME_CHARS) return value;
  const code = value.charCodeAt(MAX_NAME_CHARS - 1);
  const end = code >= 0xd800 && code <= 0xdbff ? MAX_NAME_CHARS - 1 : MAX_NAME_CHARS;
  return value.slice(0, end);
}

/** The label a flashing button goes back to. Captured once, so a second flash can't overwrite it. */
const restingLabels = new WeakMap();

function flash(button, text) {
  const label = button.querySelector(".mdr-diagram-button-label");
  if (!label) return;

  // Without this, a second failure while the first is still showing records "Failed" as the text
  // to restore, and clearing the pending timer strands the button on it for good.
  if (!restingLabels.has(button)) restingLabels.set(button, label.textContent);

  label.textContent = text;
  button.classList.add("is-failed");
  window.clearTimeout(button.__mdrFlashTimer);
  button.__mdrFlashTimer = window.setTimeout(() => {
    label.textContent = restingLabels.get(button) ?? label.textContent;
    restingLabels.delete(button);
    button.classList.remove("is-failed");
  }, BUTTON_RESET_DELAY_MS);
}

function describeError(err) {
  if (err instanceof Error) return err.message;
  return String(err);
}
