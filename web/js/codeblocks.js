// codeblocks.js — lazy syntax highlighting (highlight.js), the per-block toolbar (language label,
// wrap and line-number toggles, copy button), line numbering and diff colouring. See
// ARCHITECTURE.md §7.3.
//
// No implicit globals: `hljs` is read off `globalThis` exactly once, right after
// highlight.min.js finishes loading, and cached in `hljsApi`. Nothing else in this
// file probes `window.hljs` directly.
//
// Everything a block needs beyond its plain text — the toolbar, highlighting, and the split into
// per-line elements that line numbers and diff rows are built on — happens in one lazy pass
// driven by an IntersectionObserver, so a document with two thousand code blocks only pays for
// the ones the reader actually reaches.

import { loadClassicScript } from "./enhance.js";
import { log, send } from "./bridge.js";
import { createIconButton } from "./icons.js";
import { CODE_LINE_NUMBERS, CODE_WRAP, getFlag, onFlagChange, setFlag } from "./prefs.js";

const HLJS_CORE_URL = "vendor/highlight/highlight.min.js";
const HLJS_LANGUAGE_DIR = "vendor/highlight/languages/";
const LAZY_ROOT_MARGIN = "1000px";
const COPY_RESET_DELAY_MS = 2000;

/** Blocks longer than this keep their single text node: line numbers aren't worth the elements. */
const MAX_SPLIT_LINES = 2000;

/** How deep the highlight markup is followed when re-opening spans across a newline. */
const MAX_SPLIT_DEPTH = 32;

/**
 * Aliases for the extra languages vendored outside highlight.js's "common" bundle.
 * A `Map`, not a plain object: `lang` comes from the fenced code info string, which is
 * untrusted document content — a plain-object lookup would resolve values like
 * "constructor" or "toString" off `Object.prototype` instead of returning undefined,
 * turning into a request for ".../[object Function].min.js".
 */
const EXTRA_LANGUAGE_FILES = new Map([
  ["ps1", "powershell"],
  ["pwsh", "powershell"],
  ["powershell", "powershell"],
  ["docker", "dockerfile"],
  ["dockerfile", "dockerfile"],
  ["bat", "dos"],
  ["cmd", "dos"],
  ["dos", "dos"],
  ["fs", "fsharp"],
  ["fsharp", "fsharp"],
  ["nginx", "nginx"],
  ["proto", "protobuf"],
  ["protobuf", "protobuf"],
]);

/** Info strings that mean "unified diff". highlight.js knows `diff`; the rows are ours either way. */
const DIFF_LANGUAGES = new Set(["diff", "patch", "udiff"]);

const LANGUAGE_CLASS_RE = /(?:^|\s)language-(\S+)/;

/** The element content actually scrolls in (index.html/layout.css, WP7) — IntersectionObserver
 *  needs this as `root`, or rootMargin expands the (non-scrolling) window viewport instead of
 *  the scroll container, and lazy blocks only highlight once actually visible (pop-in). */
const mdrMain = document.getElementById("mdr-main");

/** @type {any} captured once from globalThis.hljs after the script loads */
let hljsApi = null;

/**
 * The text a block was rendered from, for blocks whose newlines have become block boundaries
 * rather than characters. The copy button reads this first.
 * @type {WeakMap<Element, string>}
 */
const sources = new WeakMap();

/** The observer watching code blocks queued for lazy (below-the-fold) enhancement
 *  by the current document's enhanceCode() call. Reset at the start of every call
 *  so it never keeps watching (and retaining) elements from a replaced document —
 *  an un-disconnected IntersectionObserver keeps its targets alive across a live
 *  reload otherwise. */
let pendingCodeObserver = null;

function resetPendingCode() {
  if (pendingCodeObserver) {
    pendingCodeObserver.disconnect();
    pendingCodeObserver = null;
  }
}

async function ensureHljs() {
  if (hljsApi) return hljsApi;
  try {
    await loadClassicScript(HLJS_CORE_URL);
  } catch (err) {
    log("warn", `codeblocks: failed to load highlight.js: ${describeError(err)}`);
    throw err;
  }
  hljsApi = globalThis.hljs ?? null;
  if (!hljsApi) {
    const err = new Error("hljs global not found after script load");
    log("warn", `codeblocks: ${err.message}`);
    throw err;
  }
  return hljsApi;
}

/**
 * Loads the vendored script for `lang` if it's one of the extra languages and
 * hljs doesn't already know it. Returns whether `lang` is highlightable afterwards.
 * @param {string} lang
 * @returns {Promise<boolean>}
 */
async function ensureLanguageAvailable(lang) {
  if (hljsApi.getLanguage(lang)) return true;
  const file = EXTRA_LANGUAGE_FILES.get(lang);
  if (!file) return false;
  try {
    await loadClassicScript(`${HLJS_LANGUAGE_DIR}${file}.min.js`);
  } catch (err) {
    log("warn", `codeblocks: failed to load language "${file}": ${describeError(err)}`);
    return false;
  }
  return Boolean(hljsApi.getLanguage(lang));
}

/** The info-string language of a `pre > code` block, lower-cased, or null. */
function languageOf(code) {
  const match = LANGUAGE_CLASS_RE.exec(code.className);
  return match ? match[1].toLowerCase() : null;
}

/**
 * @param {Element} code a `pre > code[class*="language-"]` element
 * @param {string} lang
 */
async function highlightBlock(code, lang) {
  if (code.hasAttribute("data-highlighted")) return;

  const available = hljsApi.getLanguage(lang) ? true : await ensureLanguageAvailable(lang);
  if (!available) {
    log("warn", `codeblocks: unknown language "${lang}", left unhighlighted`);
    return;
  }

  try {
    hljsApi.highlightElement(code);
  } catch (err) {
    log("warn", `codeblocks: highlight failed for "${lang}": ${describeError(err)}`);
  }
}

/**
 * @param {Element} code
 * @param {number} viewportHeight
 */
function isInInitialViewport(code, viewportHeight) {
  const target = code.closest("pre") ?? code;
  const rect = target.getBoundingClientRect();
  return rect.bottom >= 0 && rect.top <= viewportHeight;
}

// ---------------------------------------------------------------------------
// Per-block enhancement
// ---------------------------------------------------------------------------

/**
 * Everything one block gets: the toolbar, highlighting, and the split into per-line elements that
 * line numbers and diff rows need. Idempotent, and safe for a block whose language highlight.js
 * doesn't know — or when highlight.js never loaded at all.
 * @param {Element} code
 */
async function decorateBlock(code) {
  const pre = code.closest("pre");
  if (!pre) return;

  const lang = languageOf(code);
  addToolbar(pre, code, lang);

  if (lang && hljsApi) await highlightBlock(code, lang);
  splitLines(code, lang !== null && DIFF_LANGUAGES.has(lang));
}

/**
 * Rewrites the block's contents as one element per source line. Highlight spans that cross a
 * newline are closed and re-opened, so the colours survive; every line's text is unchanged, and
 * the block's original text is kept in `sources` because its newlines are now block boundaries
 * rather than characters.
 * @param {Element} code
 * @param {boolean} isDiff
 */
function splitLines(code, isDiff) {
  if (code.hasAttribute("data-mdr-lines")) return;

  const source = code.textContent ?? "";
  let newlines = 0;
  for (let i = 0; i < source.length; i++) {
    if (source.charCodeAt(i) === 10) newlines++;
  }
  if (newlines + 1 > MAX_SPLIT_LINES) {
    // A block this long would cost more elements than line numbers are worth; leave it as text.
    code.setAttribute("data-mdr-lines", "0");
    return;
  }

  const lines = [];
  /** @type {{source: Element, clone: Element}[]} the highlight spans open at the cut point */
  const open = [];
  let line = newLineElement();
  let cursor = line;

  function endLine() {
    lines.push(line);
    line = newLineElement();
    cursor = line;
    for (const frame of open) {
      const clone = frame.source.cloneNode(false);
      cursor.appendChild(clone);
      frame.clone = clone;
      cursor = clone;
    }
  }

  function addText(text) {
    const parts = text.split("\n");
    for (let i = 0; i < parts.length; i++) {
      if (i > 0) endLine();
      if (parts[i].length > 0) cursor.appendChild(document.createTextNode(parts[i]));
    }
  }

  function walk(node, depth) {
    for (const child of node.childNodes) {
      if (child.nodeType === Node.TEXT_NODE) {
        addText(child.nodeValue ?? "");
        continue;
      }
      if (child.nodeType !== Node.ELEMENT_NODE) continue;
      if (depth >= MAX_SPLIT_DEPTH) {
        addText(child.textContent ?? "");
        continue;
      }

      const clone = child.cloneNode(false);
      cursor.appendChild(clone);
      open.push({ source: child, clone });
      cursor = clone;
      walk(child, depth + 1);
      open.pop();
      cursor = open.length > 0 ? open[open.length - 1].clone : line;
    }
  }

  walk(code, 0);
  lines.push(line);

  // Fenced code always ends with a newline, which would otherwise leave a numbered empty line.
  // The test is on text, not on children: endLine() re-opens every highlight span that was still
  // open, so a block ending inside an unterminated comment or string leaves a final line that has
  // children but no text at all.
  if (lines.length > 1 && (lines[lines.length - 1].textContent ?? "").length === 0) lines.pop();

  // A blank source line is a block with no text in it, and Chromium leaves those out when it
  // serializes a selection — so the browser's own Copy and Select All (which §8.4 deliberately
  // keeps) would silently drop every blank line. A `<br>` gives the line a line box to occupy and
  // a newline in the clipboard, without putting any character into the code itself.
  for (const element of lines) {
    if ((element.textContent ?? "").length === 0) element.appendChild(document.createElement("br"));
  }

  if (isDiff) {
    for (const element of lines) markDiffLine(element);
  }

  sources.set(code, source);
  code.replaceChildren(...lines);
  code.style.setProperty("--mdr-code-gutter", `${Math.max(2, String(lines.length).length)}ch`);
  code.setAttribute("data-mdr-lines", String(lines.length));
}

function newLineElement() {
  const element = document.createElement("span");
  element.className = "mdr-code-line";
  return element;
}

/**
 * Tints a whole unified-diff row. highlight.js marks the changed text, but only the text: the row
 * itself has to be classed for the background to reach across the block.
 * @param {HTMLElement} element
 */
function markDiffLine(element) {
  const text = element.textContent ?? "";
  if (isFileHeader(text, "---") || isFileHeader(text, "+++") || text.startsWith("diff ")
      || text.startsWith("index ") || text.startsWith("new file") || text.startsWith("deleted file")) {
    element.classList.add("is-meta");
  } else if (text.startsWith("@@")) {
    element.classList.add("is-hunk");
  } else if (text.startsWith("+")) {
    element.classList.add("is-added");
  } else if (text.startsWith("-")) {
    element.classList.add("is-removed");
  }
}

/**
 * A unified-diff file header is `--- a/path` or a bare `---`. "----foo" is not one: it is a
 * removed line whose content happens to start with dashes, and it has to stay red.
 */
function isFileHeader(text, marker) {
  return text === marker || text.startsWith(marker + " ") || text.startsWith(marker + "\t");
}

/** The text a block was rendered from, whether or not its lines have been split out. */
function codeText(code) {
  return sources.get(code) ?? code.textContent ?? "";
}

// ---------------------------------------------------------------------------
// Toolbar
// ---------------------------------------------------------------------------

/**
 * @param {Element} pre
 * @param {Element} code
 * @param {string | null} lang
 */
function addToolbar(pre, code, lang) {
  if (pre.querySelector(":scope > .mdr-code-toolbar")) return;

  const toolbar = document.createElement("div");
  toolbar.className = "mdr-code-toolbar";

  if (lang) {
    const label = document.createElement("span");
    label.className = "mdr-code-lang";
    label.textContent = lang;
    toolbar.appendChild(label);
  }

  toolbar.appendChild(buildPrefToggle("wrap", CODE_WRAP, "Wrap long lines"));
  toolbar.appendChild(buildPrefToggle("line-numbers", CODE_LINE_NUMBERS, "Show line numbers"));
  toolbar.appendChild(buildCopyButton(code));
  pre.appendChild(toolbar);
}

/**
 * A toggle for one of the reader's code preferences. It sits in every block's toolbar because
 * that is where the reader wants it, but it sets one preference for the whole page and for the
 * next document they open (prefs.js) — not for this block and not for this document.
 */
function buildPrefToggle(iconName, key, label) {
  const button = createIconButton(iconName, label, "mdr-code-button mdr-code-toggle");
  button.dataset.mdrPref = key;
  button.setAttribute("aria-pressed", String(getFlag(key)));
  button.addEventListener("click", () => setFlag(key, !getFlag(key)));
  return button;
}

/** @param {Element} code the code element whose text is copied */
function buildCopyButton(code) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "mdr-code-button mdr-code-copy";
  button.setAttribute("aria-label", "Copy code");
  button.title = "Copy";

  const icon = document.createElement("span");
  icon.className = "mdr-code-copy-icon";
  icon.setAttribute("aria-hidden", "true");

  const label = document.createElement("span");
  label.className = "mdr-code-copy-label";
  label.textContent = "Copy";

  button.appendChild(icon);
  button.appendChild(label);
  button.addEventListener("click", () => handleCopyClick(button, label, code));
  return button;
}

/**
 * @param {HTMLButtonElement} button
 * @param {HTMLElement} label
 * @param {Element} code
 */
function handleCopyClick(button, label, code) {
  send({ type: "copy", text: codeText(code) });

  button.classList.add("is-copied");
  button.setAttribute("aria-label", "Copied!");
  button.title = "Copied!";
  label.textContent = "Copied";

  window.clearTimeout(/** @type {any} */ (button).__mdrCopyResetTimer);
  /** @type {any} */ (button).__mdrCopyResetTimer = window.setTimeout(() => {
    button.classList.remove("is-copied");
    button.setAttribute("aria-label", "Copy code");
    button.title = "Copy";
    label.textContent = "Copy";
  }, COPY_RESET_DELAY_MS);
}

// ---------------------------------------------------------------------------
// Preferences
// ---------------------------------------------------------------------------

/**
 * Both code preferences are a class on `<html>`, so turning one on or off costs one attribute
 * write no matter how many blocks the document has — and a block enhanced later is already in the
 * right state. Called once at load and again whenever a preference changes, here or in another
 * view of the same profile.
 */
function applyCodePrefs() {
  const root = document.documentElement;
  root.classList.toggle("mdr-code-wrap", getFlag(CODE_WRAP));
  root.classList.toggle("mdr-code-numbers", getFlag(CODE_LINE_NUMBERS));

  for (const button of document.querySelectorAll(".mdr-code-toggle")) {
    const key = /** @type {HTMLElement} */ (button).dataset.mdrPref;
    if (key) button.setAttribute("aria-pressed", String(getFlag(key)));
  }
}

onFlagChange(applyCodePrefs);
applyCodePrefs();

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

/**
 * Enhances every `pre > code` block under `root`: toolbar, highlighting (for blocks with a
 * `language-` class, loading highlight.js and any extra language file on first use only), the
 * split into lines, and diff rows. Blocks currently in the viewport are done before the returned
 * promise resolves; the rest follow lazily via IntersectionObserver (rootMargin: "1000px") as the
 * reader scrolls near them.
 * @param {ParentNode & Element} root
 * @returns {Promise<void>} resolves once blocks in the initial viewport are done
 */
export async function enhanceCode(root) {
  // A fresh render pass: drop any leftover pending observer from a previous
  // document (its elements are about to be/already are detached from the DOM).
  resetPendingCode();

  const codeBlocks = Array.from(root.querySelectorAll("pre > code"));
  if (codeBlocks.length === 0) return;

  if (codeBlocks.some((code) => languageOf(code) !== null)) {
    try {
      await ensureHljs();
    } catch {
      // Already logged. Toolbars, line numbers and diff rows don't need highlight.js, so the pass
      // below still runs; the code is simply not coloured.
    }
  }

  const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
  const initial = [];
  const lazy = [];
  for (const code of codeBlocks) {
    (isInInitialViewport(code, viewportHeight) ? initial : lazy).push(code);
  }

  if (lazy.length > 0) {
    pendingCodeObserver = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          pendingCodeObserver?.unobserve(entry.target);
          void decorateBlock(/** @type {Element} */ (entry.target));
        }
      },
      { root: mdrMain, rootMargin: LAZY_ROOT_MARGIN },
    );
    for (const code of lazy) pendingCodeObserver.observe(code);
  }

  await Promise.all(initial.map((code) => decorateBlock(code)));
}

/** @param {unknown} err */
function describeError(err) {
  if (err instanceof Error) return err.message;
  return String(err);
}
