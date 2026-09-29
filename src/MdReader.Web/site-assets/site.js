// site.js — the public pages (about, the tool pages and the cheat sheet). It is loaded by
// those pages only; the reader at "/" does not know this file exists.
//
// Everything this file adds is an improvement on a page that already works: the prose,
// the sample document in the editor and the rendered result beside it are all in the HTML
// the server sent. With scripting off, a tool page is a worked example. With scripting on,
// the example becomes editable.
//
// What runs here, in order of how much it matters:
//   - the theme button, which writes the reader's own localStorage key
//   - re-rendering the editor's document through POST /api/render as it is edited
//   - the shared enhancers (highlight.js, KaTeX, Mermaid), imported only if the page has
//     something for them to do, so a page without a code block never downloads them
//
// The three modules imported from /js/ below are the reader's own: they are read from
// there rather than copied here, because the HTML they act on came out of the same
// renderer the reader uses. Nothing in this file writes to any of them.
//
// Rate limiting is respected by construction rather than by apology: a render is debounced
// and then held to one request per MIN_INTERVAL_MS, which keeps a fast typist under the
// server's per-client limit instead of collecting 429s.

const THEME_KEY = "mdr.web.theme";
const RENDER_URL = "/api/render";
// In step with WebLimitsOptions.DefaultMaxRequestBodyBytes (RenderApiTests checks it).
const MAX_BODY_BYTES = 512 * 1024;
const DEBOUNCE_MS = 900;
const MIN_INTERVAL_MS = 2500;

const root = document.documentElement;
const main = document.getElementById("content");

/** The two shared enhancer modules, imported on demand. Null until a page needs them. */
let enhancers = null;

// ---------------------------------------------------------------------------------
// the enhancers (highlight.js / KaTeX / Mermaid)
// ---------------------------------------------------------------------------------

/** True if `scope` holds anything the enhancers would act on. */
function needsEnhancing(scope) {
  return Boolean(
    scope &&
      scope.querySelector('pre > code[class*="language-"], span.math, div.math, pre.mermaid, div.mermaid'),
  );
}

async function loadEnhancers() {
  if (!enhancers) {
    const [enhance, codeblocks, bridge] = await Promise.all([
      import("/js/enhance.js"),
      import("/js/codeblocks.js"),
      import("/js/bridge.js"),
    ]);
    // codeblocks.js puts a Copy button on every code block and reports the click as a
    // `copy` message, the same way it does inside the desktop app. Nothing else is
    // expected; anything else is dropped rather than queued forever.
    bridge.attachHost((message) => {
      if (message && message.type === "copy") void writeToClipboard(message.text ?? "");
    });
    enhancers = { enhance, codeblocks };
  }
  return enhancers;
}

/**
 * Highlights code, typesets math and draws diagrams under `scope`.
 * @param {Element} scope
 * @param {{ code?: boolean, math?: boolean, mermaid?: boolean }} [features] what to run;
 *        by default, whatever `scope` turns out to contain.
 */
async function enhance(scope, features) {
  if (!scope) return;
  const wanted = features ?? {
    code: Boolean(scope.querySelector('pre > code[class*="language-"]')),
    math: Boolean(scope.querySelector("span.math, div.math")),
    mermaid: Boolean(scope.querySelector("pre.mermaid, div.mermaid")),
  };
  if (!wanted.code && !wanted.math && !wanted.mermaid) return;

  const { enhance: enhanceModule, codeblocks } = await loadEnhancers();
  const tasks = [];
  // Code goes through the whole page: highlighting is idempotent (a block carries
  // data-highlighted once it is done) and the toolbars skip blocks that already have one,
  // so re-running it after a render keeps the prose's own examples working. Math and
  // diagrams are scoped to what changed, because re-running those over already-rendered
  // output would feed KaTeX its own HTML.
  if (wanted.code) tasks.push(codeblocks.enhanceCode(main ?? scope));
  if (wanted.math) tasks.push(enhanceModule.enhanceMath(scope));
  if (wanted.mermaid) tasks.push(enhanceModule.enhanceDiagrams(scope));
  await Promise.allSettled(tasks);
}

async function writeToClipboard(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------------
// theme
// ---------------------------------------------------------------------------------

const themeButton = document.getElementById("mdr-site-theme");

function currentTheme() {
  return root.getAttribute("data-theme") === "dark" ? "dark" : "light";
}

function labelThemeButton() {
  const dark = currentTheme() === "dark";
  themeButton.textContent = dark ? "Light" : "Dark";
  themeButton.setAttribute("aria-label", dark ? "Switch to the light theme" : "Switch to the dark theme");
}

if (themeButton) {
  themeButton.hidden = false;
  labelThemeButton();
  themeButton.addEventListener("click", async () => {
    const next = currentTheme() === "dark" ? "light" : "dark";
    root.setAttribute("data-theme", next);
    try {
      window.localStorage.setItem(THEME_KEY, next);
    } catch {
      // Storage blocked: the choice applies to this page and is not remembered.
    }
    labelThemeButton();
    // Diagrams are drawn with baked-in colours, so they have to be redrawn to follow.
    if (enhancers) await enhancers.enhance.setDiagramTheme(next);
  });
}

// ---------------------------------------------------------------------------------
// the editor
// ---------------------------------------------------------------------------------

const input = /** @type {HTMLTextAreaElement | null} */ (document.getElementById("mdr-tool-input"));
const output = document.getElementById("mdr-tool-output");
const statusLine = document.getElementById("mdr-tool-status");
const renderButton = document.getElementById("mdr-tool-render");
const openButton = document.getElementById("mdr-tool-open");
const copyButton = document.getElementById("mdr-tool-copy");
const printButton = document.getElementById("mdr-tool-print");
const fileInput = /** @type {HTMLInputElement | null} */ (document.getElementById("mdr-tool-file"));

/** The text the preview currently shows, so an unchanged document costs no request. */
let rendered = input ? input.value : "";
let timer = null;
let inFlight = null;
let lastRequestAt = 0;

function say(text) {
  if (statusLine) statusLine.textContent = text;
}

function schedule() {
  if (timer !== null) window.clearTimeout(timer);
  const sinceLast = Date.now() - lastRequestAt;
  timer = window.setTimeout(render, Math.max(DEBOUNCE_MS, MIN_INTERVAL_MS - sinceLast));
}

const ERRORS = {
  400: "The server could not read that document.",
  413: "That document is over 512 KB.",
  415: "The server could not read that document.",
  429: "That is a lot of renders. Wait a few seconds and it will catch up.",
  500: "Something went wrong rendering that.",
  503: "The server is busy. Try again in a moment.",
};

async function render() {
  if (timer !== null) {
    window.clearTimeout(timer);
    timer = null;
  }
  if (!input || !output) return;

  const text = input.value;
  if (text === rendered) {
    say("");
    return;
  }

  const body = JSON.stringify({ markdown: text });
  if (new Blob([body]).size > MAX_BODY_BYTES) {
    say(ERRORS[413]);
    return;
  }

  if (inFlight) inFlight.abort();
  const controller = new AbortController();
  inFlight = controller;
  lastRequestAt = Date.now();
  say("Rendering.");

  let response;
  let payload = null;
  try {
    response = await fetch(RENDER_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body,
      signal: controller.signal,
      cache: "no-store",
    });
    payload = await response.json().catch(() => null);
  } catch {
    if (controller.signal.aborted) return; // superseded by a newer edit
    inFlight = null;
    say("Could not reach the server. Check the connection and try again.");
    return;
  }

  if (inFlight !== controller) return; // superseded while the body was downloading
  inFlight = null;

  if (!response.ok || !payload || !Array.isArray(payload.messages) || payload.messages.length === 0) {
    const fallback = ERRORS[response.status] ?? ERRORS[response.status >= 500 ? 500 : 400];
    say(typeof payload?.message === "string" ? payload.message : fallback);
    return;
  }

  rendered = text;
  const parts = payload.messages.filter((m) => m.type === "render" || m.type === "renderPart");
  const html = parts.map((m) => m.html).join("");
  const features = payload.messages.find((m) => m.type === "render")?.features ?? {};

  // Server-sanitized HTML: this is the same string the reader assigns, from the same
  // endpoint, after the same allow-list pass.
  output.innerHTML = html;
  if (html.trim().length === 0) {
    output.innerHTML = '<p class="mdr-tool-empty">Nothing to show yet.</p>';
  }
  say("");
  await enhance(output, features);
}

if (input && output) {
  if (renderButton) {
    renderButton.hidden = false;
    renderButton.addEventListener("click", () => void render());
  }

  input.addEventListener("input", schedule);
  input.addEventListener("keydown", (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
      event.preventDefault();
      void render();
    }
  });

  if (openButton && fileInput) {
    openButton.hidden = false;
    openButton.addEventListener("click", () => fileInput.click());
    fileInput.addEventListener("change", async () => {
      const file = fileInput.files && fileInput.files[0];
      if (!file) return;
      try {
        input.value = await file.text();
      } catch {
        say("That file could not be read.");
        return;
      } finally {
        fileInput.value = "";
      }
      input.dispatchEvent(new Event("input"));
      await render();
    });
  }

  if (copyButton) {
    copyButton.hidden = false;
    copyButton.addEventListener("click", async () => {
      say((await writeToClipboard(input.value))
        ? "Copied."
        : "The browser would not allow the copy. Select the text and copy it yourself.");
    });
  }

  if (printButton) {
    printButton.hidden = false;
    printButton.addEventListener("click", async () => {
      // print.css paints the light palette under [data-print] as well as in @media print,
      // and site.css hides everything but the preview. Diagrams carry their colours in
      // their own markup, so they are redrawn light before the dialog opens rather than
      // after it.
      root.setAttribute("data-print", "");
      if (enhancers) await enhancers.enhance.setDiagramTheme("light");
      if (document.fonts) await document.fonts.ready.then(() => undefined, () => undefined);
      window.print();
    });

    window.addEventListener("afterprint", () => {
      root.removeAttribute("data-print");
      if (enhancers) void enhancers.enhance.setDiagramTheme(currentTheme());
    });
  }
}

// ---------------------------------------------------------------------------------
// the table grid, and the first pass over what the server already rendered
// ---------------------------------------------------------------------------------

const builder = document.getElementById("mdr-tool-builder");
if (builder && input) {
  const { initTableBuilder } = await import("/site/table-tool.js");
  initTableBuilder(builder, input, () => {
    input.dispatchEvent(new Event("input"));
  });
}

if (needsEnhancing(main)) {
  await enhance(main);
}
