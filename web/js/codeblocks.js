// codeblocks.js — lazy syntax highlighting (highlight.js) plus the language label
// and copy button toolbar added to every code block. See ARCHITECTURE.md §7.3.
//
// No implicit globals: `hljs` is read off `globalThis` exactly once, right after
// highlight.min.js finishes loading, and cached in `hljsApi`. Nothing else in this
// file probes `window.hljs` directly.

import { loadClassicScript } from "./enhance.js";
import { log, send } from "./bridge.js";

const HLJS_CORE_URL = "vendor/highlight/highlight.min.js";
const HLJS_LANGUAGE_DIR = "vendor/highlight/languages/";
const LAZY_ROOT_MARGIN = "1000px";
const COPY_RESET_DELAY_MS = 2000;

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

const LANGUAGE_CLASS_RE = /(?:^|\s)language-(\S+)/;

/** The element content actually scrolls in (index.html/layout.css, WP7) — IntersectionObserver
 *  needs this as `root`, or rootMargin expands the (non-scrolling) window viewport instead of
 *  the scroll container, and lazy blocks only highlight once actually visible (pop-in). */
const mdrMain = document.getElementById("mdr-main");

/** @type {any} captured once from globalThis.hljs after the script loads */
let hljsApi = null;

/** The observer watching code blocks queued for lazy (below-the-fold) highlighting
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

/** @param {Element} code a `pre > code[class*="language-"]` element */
async function highlightBlock(code) {
  if (code.hasAttribute("data-highlighted")) return;

  const match = LANGUAGE_CLASS_RE.exec(code.className);
  if (!match) return;
  const lang = match[1].toLowerCase();

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

/**
 * Adds a language label + copy button toolbar to every `pre > code` block under
 * `root` (regardless of whether the language is recognized). Idempotent.
 * @param {ParentNode} root
 */
function addToolbars(root) {
  const preBlocks = root.querySelectorAll("pre");
  for (const pre of preBlocks) {
    const code = pre.querySelector(":scope > code");
    if (!code) continue; // not a code block (e.g. a mermaid source block)
    if (pre.querySelector(":scope > .mdr-code-toolbar")) continue; // already enhanced
    pre.appendChild(buildToolbar(pre, code));
  }
}

/**
 * @param {Element} pre
 * @param {Element} code
 */
function buildToolbar(pre, code) {
  const toolbar = document.createElement("div");
  toolbar.className = "mdr-code-toolbar";

  const match = LANGUAGE_CLASS_RE.exec(code.className);
  if (match) {
    const label = document.createElement("span");
    label.className = "mdr-code-lang";
    label.textContent = match[1];
    toolbar.appendChild(label);
  }

  toolbar.appendChild(buildCopyButton(code));
  return toolbar;
}

/** @param {Element} code the code element whose text is copied */
function buildCopyButton(code) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "mdr-code-copy";
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
  send({ type: "copy", text: code.textContent ?? "" });

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

/**
 * Highlights every `pre > code[class*="language-"]` under `root` (loading
 * highlight.js, and any extra language file, on first use only), and adds a
 * language label + copy button to every code block. Blocks currently in the
 * viewport are highlighted immediately; the rest are highlighted lazily via
 * IntersectionObserver (rootMargin: "1000px") as the reader scrolls near them.
 * @param {ParentNode & Element} root
 * @returns {Promise<void>} resolves once blocks in the initial viewport are done
 */
export async function enhanceCode(root) {
  // A fresh render pass: drop any leftover pending observer from a previous
  // document (its elements are about to be/already are detached from the DOM).
  resetPendingCode();

  addToolbars(root);

  const codeBlocks = Array.from(root.querySelectorAll('pre > code[class*="language-"]'));
  if (codeBlocks.length === 0) return;

  try {
    await ensureHljs();
  } catch {
    return; // already logged
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
          void highlightBlock(/** @type {Element} */ (entry.target));
        }
      },
      { root: mdrMain, rootMargin: LAZY_ROOT_MARGIN },
    );
    for (const code of lazy) pendingCodeObserver.observe(code);
  }

  await Promise.all(initial.map((code) => highlightBlock(code)));
}

/** @param {unknown} err */
function describeError(err) {
  if (err instanceof Error) return err.message;
  return String(err);
}
