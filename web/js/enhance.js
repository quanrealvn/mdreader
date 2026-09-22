// enhance.js — shared classic-script/stylesheet loaders, KaTeX math rendering and
// Mermaid diagram rendering. See ARCHITECTURE.md §7.3 for the exact contract.
//
// No implicit globals: `katex`/`mermaid` are read off `globalThis` exactly once, right
// after their script finishes loading, and cached in module-level variables. Nothing
// else in this file (or codeblocks.js) probes `window.katex`/`window.mermaid` directly.

import { log } from "./bridge.js";

const KATEX_JS = "vendor/katex/katex.min.js";
const KATEX_CSS = "vendor/katex/katex.min.css";
const MERMAID_JS = "vendor/mermaid/mermaid.min.js";

const MATH_DELIMITER_LENGTH = 2; // "\(" / "\)" / "\[" / "\]"
const LAZY_ROOT_MARGIN = "1000px";

/** The element content actually scrolls in (index.html/layout.css, WP7) — IntersectionObserver
 *  needs this as `root`, or rootMargin expands the (non-scrolling) window viewport instead of
 *  the scroll container, and lazy elements only render once actually visible (pop-in). */
const mdrMain = document.getElementById("mdr-main");

/**
 * @param {Element} el
 * @param {number} viewportHeight
 */
function isInInitialViewport(el, viewportHeight) {
  const rect = el.getBoundingClientRect();
  return rect.bottom >= 0 && rect.top <= viewportHeight;
}

/** @type {Map<string, Promise<void>>} */
const scriptPromises = new Map();
/** @type {Map<string, Promise<void>>} */
const stylesheetPromises = new Map();

/**
 * Loads a classic (non-module) script exactly once and returns a promise that
 * resolves when it has run. Safe to call repeatedly with the same URL.
 * @param {string} url
 * @returns {Promise<void>}
 */
export function loadClassicScript(url) {
  let promise = scriptPromises.get(url);
  if (!promise) {
    promise = new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = url;
      script.addEventListener("load", () => resolve(), { once: true });
      script.addEventListener(
        "error",
        () => reject(new Error(`Failed to load script: ${url}`)),
        { once: true },
      );
      document.head.appendChild(script);
    });
    scriptPromises.set(url, promise);
  }
  return promise;
}

/**
 * Loads a stylesheet exactly once and returns a promise that resolves once it
 * has been applied. Safe to call repeatedly with the same URL.
 * @param {string} url
 * @returns {Promise<void>}
 */
export function loadStylesheet(url) {
  let promise = stylesheetPromises.get(url);
  if (!promise) {
    promise = new Promise((resolve, reject) => {
      const link = document.createElement("link");
      link.rel = "stylesheet";
      link.href = url;
      link.addEventListener("load", () => resolve(), { once: true });
      link.addEventListener(
        "error",
        () => reject(new Error(`Failed to load stylesheet: ${url}`)),
        { once: true },
      );
      document.head.appendChild(link);
    });
    stylesheetPromises.set(url, promise);
  }
  return promise;
}

// ---------------------------------------------------------------------------
// Math (KaTeX)
// ---------------------------------------------------------------------------

/** @type {any} captured once from globalThis.katex after the script loads */
let katexApi = null;

/** The observer watching math elements queued for lazy (below-the-fold) rendering
 *  by the current document's enhanceMath() call. Reset at the start of every call
 *  so it never keeps watching (and retaining) elements from a replaced document —
 *  an un-disconnected IntersectionObserver keeps its targets alive across a live
 *  reload otherwise. */
let pendingMathObserver = null;

function resetPendingMath() {
  if (pendingMathObserver) {
    pendingMathObserver.disconnect();
    pendingMathObserver = null;
  }
}

async function ensureKatex() {
  if (katexApi) return katexApi;
  try {
    await Promise.all([loadClassicScript(KATEX_JS), loadStylesheet(KATEX_CSS)]);
  } catch (err) {
    log("warn", `enhance: failed to load KaTeX: ${describeError(err)}`);
    throw err;
  }
  katexApi = globalThis.katex ?? null;
  if (!katexApi) {
    const err = new Error("katex global not found after script load");
    log("warn", `enhance: ${err.message}`);
    throw err;
  }
  return katexApi;
}

/**
 * Renders every `span.math` (inline, `\(…\)`) and `div.math` (display, `\[…\]`)
 * element under `root` with KaTeX. Loads the library and its stylesheet on first
 * use only (no-op if `root` has no math). Errors are shown inline, never thrown.
 * Elements in the initial viewport are rendered before the returned promise
 * resolves; the rest render lazily via IntersectionObserver (rootMargin 1000px)
 * as the reader scrolls near them, per §7.1 rule 6 ("lazy work below the fold
 * continues afterwards").
 * @param {ParentNode & Element} root
 * @returns {Promise<void>}
 */
export async function enhanceMath(root) {
  // A fresh render pass: drop any leftover pending observer from a previous
  // document (its elements are about to be/already are detached from the DOM).
  resetPendingMath();

  const targets = Array.from(root.querySelectorAll("span.math, div.math"));
  if (targets.length === 0) return;

  try {
    await ensureKatex();
  } catch {
    return; // already logged
  }

  const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
  const initial = [];
  const lazy = [];
  for (const el of targets) {
    (isInInitialViewport(el, viewportHeight) ? initial : lazy).push(el);
  }

  if (lazy.length > 0) {
    pendingMathObserver = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          pendingMathObserver?.unobserve(entry.target);
          renderMathElement(/** @type {Element} */ (entry.target));
        }
      },
      { root: mdrMain, rootMargin: LAZY_ROOT_MARGIN },
    );
    for (const el of lazy) pendingMathObserver.observe(el);
  }

  for (const el of initial) {
    renderMathElement(el);
  }
}

/** @param {Element} el */
function renderMathElement(el) {
  const displayMode = el.tagName === "DIV";
  const source = stripMathDelimiters(el.textContent ?? "");
  try {
    katexApi.render(source, el, {
      displayMode,
      throwOnError: false,
      trust: false,
      strict: "ignore",
      maxSize: 50,
      maxExpand: 1000,
      output: "htmlAndMathml",
    });
  } catch (err) {
    log("warn", `enhance: KaTeX render failed: ${describeError(err)}`);
    showMathError(el, source, describeError(err));
  }
}

/** @param {string} raw */
function stripMathDelimiters(raw) {
  const trimmed = raw.trim();
  if (trimmed.length >= MATH_DELIMITER_LENGTH * 2) {
    return trimmed.slice(MATH_DELIMITER_LENGTH, -MATH_DELIMITER_LENGTH);
  }
  return trimmed;
}

/**
 * @param {Element} el
 * @param {string} source
 * @param {string} message
 */
function showMathError(el, source, message) {
  el.textContent = "";
  const span = document.createElement("span");
  span.className = "mdr-math-error";
  span.title = message;
  span.textContent = source;
  el.appendChild(span);
}

// ---------------------------------------------------------------------------
// Diagrams (Mermaid)
// ---------------------------------------------------------------------------

/** @type {any} captured once from globalThis.mermaid after the script loads */
let mermaidApi = null;
let mermaidInitializedTheme = null;
let mermaidCounter = 0;
/** Theme most recently requested via setDiagramTheme(), or derived from the DOM. */
let trackedTheme = null;

/** Diagrams observed for lazy (below-the-fold) rendering by the current document's
 *  enhanceDiagrams() call, not yet rendered. A plain Set holds strong references to
 *  DOM nodes, so both this and the observer are reset at the start of every
 *  enhanceDiagrams() call — otherwise elements from a replaced document would leak. */
let pendingDiagramObserver = null;
/** @type {Set<Element>} */
const pendingDiagramElements = new Set();

function resetPendingDiagrams() {
  if (pendingDiagramObserver) {
    pendingDiagramObserver.disconnect();
    pendingDiagramObserver = null;
  }
  pendingDiagramElements.clear();
}

async function ensureMermaid() {
  if (mermaidApi) return mermaidApi;
  try {
    await loadClassicScript(MERMAID_JS);
  } catch (err) {
    log("warn", `enhance: failed to load Mermaid: ${describeError(err)}`);
    throw err;
  }
  mermaidApi = globalThis.mermaid ?? null;
  if (!mermaidApi) {
    const err = new Error("mermaid global not found after script load");
    log("warn", `enhance: ${err.message}`);
    throw err;
  }
  return mermaidApi;
}

function resolveTheme() {
  if (trackedTheme) return trackedTheme;
  const attr = document.documentElement.getAttribute("data-theme");
  return attr === "dark" ? "dark" : "light";
}

/**
 * Mermaid's built-in "dark" theme derives its 12-color categorical scale
 * (`cScale0`…`cScale11`, `THEME_COLOR_LIMIT` = 12) from hard-coded hex values that
 * assume a lighter canvas than our `--mdr-bg` (#0D1117) — most strikingly
 * `cScale1 = "#0b0000"`, which at the ~50% fill-opacity several diagram types use
 * (radar curves, and others that alias their own palette from cScale, such as pie
 * slices) reads as nearly invisible against our near-black page. Pin the whole
 * scale to a readable categorical palette (GitHub Primer's dark accents) instead.
 * Diagram types that use their own separately-seeded palette (e.g. git graph's
 * git0…git7) are unaffected and keep Mermaid's own colors.
 * @type {Record<string, string>}
 */
const DARK_THEME_VARIABLES = {
  cScale0: "#4493F8",
  cScale1: "#3FB950",
  cScale2: "#AB7DF8",
  cScale3: "#D29922",
  cScale4: "#F85149",
  cScale5: "#39C5CF",
  cScale6: "#DB61A2",
  cScale7: "#E3B341",
  cScale8: "#6CB6FF",
  cScale9: "#56D364",
  cScale10: "#BC8CFF",
  cScale11: "#FF7B72",
};

/** @param {"light"|"dark"} theme */
function initializeMermaid(theme) {
  const mermaidTheme = theme === "dark" ? "dark" : "default";
  if (mermaidInitializedTheme === mermaidTheme) return;
  const options = { startOnLoad: false, securityLevel: "strict", theme: mermaidTheme };
  if (mermaidTheme === "dark") {
    options.themeVariables = DARK_THEME_VARIABLES;
  }
  mermaidApi.initialize(options);
  mermaidInitializedTheme = mermaidTheme;
}

/**
 * Renders every `pre.mermaid`/`div.mermaid` element under `root`. Loads Mermaid on
 * first use only (no-op if `root` has no diagrams). The current theme is whatever
 * setDiagramTheme() last set, falling back to `<html data-theme>`. Diagrams in the
 * initial viewport are rendered before the returned promise resolves; the rest
 * render lazily via IntersectionObserver (rootMargin 1000px) as the reader scrolls
 * near them, per §7.1 rule 6 ("lazy work below the fold continues afterwards").
 * Elements that are still pending when the theme changes (or print mode starts)
 * are completed by setDiagramTheme() instead of waiting for the reader to scroll
 * to them — see there.
 * @param {ParentNode & Element} root
 * @returns {Promise<void>}
 */
export async function enhanceDiagrams(root) {
  // A fresh render pass: drop any leftover pending state from a previous document
  // (its elements are about to be/already are detached from the DOM).
  resetPendingDiagrams();

  const targets = Array.from(root.querySelectorAll("pre.mermaid, div.mermaid"));
  if (targets.length === 0) return;

  try {
    await ensureMermaid();
  } catch {
    return; // already logged
  }

  trackedTheme = resolveTheme();
  initializeMermaid(trackedTheme);

  const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
  const initial = [];
  const lazy = [];
  for (const el of targets) {
    (isInInitialViewport(el, viewportHeight) ? initial : lazy).push(el);
  }

  if (lazy.length > 0) {
    pendingDiagramObserver = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          const el = /** @type {Element} */ (entry.target);
          pendingDiagramObserver?.unobserve(el);
          pendingDiagramElements.delete(el);
          void renderDiagramFromSource(el.textContent ?? "", el);
        }
      },
      { root: mdrMain, rootMargin: LAZY_ROOT_MARGIN },
    );
    for (const el of lazy) {
      pendingDiagramElements.add(el);
      pendingDiagramObserver.observe(el);
    }
  }

  await Promise.all(initial.map((el) => renderDiagramFromSource(el.textContent ?? "", el)));
}

/**
 * Renders `source` and swaps it in for `elementToReplace` in one step: the SVG (or
 * error box) is built up completely off-DOM first, so the element being replaced —
 * whether a still-raw `pre.mermaid`/`div.mermaid`, or a previously-rendered diagram
 * being re-themed — stays on screen unchanged until the new content is ready. No
 * intermediate placeholder, so there is nothing to flash or jump.
 * @param {string} source
 * @param {Element} elementToReplace
 */
async function renderDiagramFromSource(source, elementToReplace) {
  const id = `mdr-mermaid-${++mermaidCounter}`;
  try {
    const { svg, bindFunctions } = await mermaidApi.render(id, source);
    const container = document.createElement("div");
    container.className = "mdr-mermaid";
    container.setAttribute("data-mdr-source", source);
    container.innerHTML = svg;
    if (typeof bindFunctions === "function") bindFunctions(container);
    elementToReplace.replaceWith(container);
  } catch (err) {
    log("warn", `enhance: Mermaid render failed: ${describeError(err)}`);
    cleanupStrayMermaidNodes(id);
    elementToReplace.replaceWith(buildMermaidError(source, describeError(err)));
  }
}

/** Mermaid can leave a detached render target behind when it throws mid-render. */
function cleanupStrayMermaidNodes(id) {
  for (const strayId of [id, `d${id}`]) {
    document.getElementById(strayId)?.remove();
  }
}

/**
 * @param {string} source
 * @param {string} message
 */
function buildMermaidError(source, message) {
  const pre = document.createElement("pre");
  pre.className = "mdr-mermaid-error";
  pre.setAttribute("data-mdr-source", source);

  const messageEl = document.createElement("span");
  messageEl.className = "mdr-mermaid-error-message";
  messageEl.textContent = `Diagram failed to render: ${message}`;

  const sourceEl = document.createElement("span");
  sourceEl.className = "mdr-mermaid-error-source";
  sourceEl.textContent = source;

  pre.appendChild(messageEl);
  pre.appendChild(sourceEl);
  return pre;
}

/**
 * Sets the Mermaid theme and re-renders every diagram in the document with it:
 * both already-rendered (or errored) ones, swapped in atomically (old SVG stays
 * until the new one is ready — no placeholder, no layout jump), and any still
 * awaiting their first (lazy) render, rendered now instead of waiting for the
 * reader to scroll to them. That completeness matters because this same function
 * is what main.js calls to enter print mode with `"light"`: printing needs the
 * whole document, not just what happened to be on screen. A no-op (beyond
 * recording the theme) if Mermaid hasn't loaded yet — the next enhanceDiagrams()
 * call picks it up.
 * @param {"light"|"dark"} theme
 * @returns {Promise<void>}
 */
export async function setDiagramTheme(theme) {
  trackedTheme = theme === "dark" ? "dark" : "light";
  if (!mermaidApi) return;

  initializeMermaid(trackedTheme);

  const rendered = Array.from(
    document.querySelectorAll(
      ".markdown-body .mdr-mermaid[data-mdr-source], .markdown-body pre.mdr-mermaid-error[data-mdr-source]",
    ),
  );
  const tasks = rendered.map((el) => {
    const source = el.getAttribute("data-mdr-source") ?? "";
    return renderDiagramFromSource(source, el);
  });

  if (pendingDiagramElements.size > 0) {
    const stillPending = Array.from(pendingDiagramElements);
    pendingDiagramElements.clear();
    for (const el of stillPending) {
      pendingDiagramObserver?.unobserve(el);
      tasks.push(renderDiagramFromSource(el.textContent ?? "", el));
    }
  }

  await Promise.all(tasks);
}

// ---------------------------------------------------------------------------

/** @param {unknown} err */
function describeError(err) {
  if (err instanceof Error) return err.message;
  return String(err);
}
