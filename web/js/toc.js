// toc.js — sidebar table of contents: builds a collapsible, filterable, sortable tree
// from `toc` entries, tracks the active heading with an IntersectionObserver, and
// reports visibility changes.
//
// Click-to-scroll is NOT wired here: entries are plain `a[href="#..."]` elements, and
// links.js's single document-level click/auxclick listener (§7.3) already intercepts
// every in-page fragment link, including these — so there is exactly one place that
// resolves fragments and scrolls, and it behaves identically for TOC links and for
// anchors inside the rendered content. The collapse twisty and sort button are plain
// `<button>`s (not links), so they never reach that listener.

import { send } from "./bridge.js";

const tocNav = document.getElementById("mdr-toc");
const tocList = document.getElementById("mdr-toc-list");
const tocClose = document.getElementById("mdr-toc-close");
const tocBackdrop = document.getElementById("mdr-toc-backdrop");

// Below 900 CSS px the docked sidebar becomes an overlay drawer (§7.3). Two separate
// pieces of state, two separate host messages (§7.2, final-review S1):
//
//  - `hostVisible` is the persisted *docked* preference. The host sets it with
//    `tocVisibility` (after `ready`, and whenever the setting changes in any tab); that
//    message never opens or closes the drawer.
//  - `drawerOpen` is purely local UI state, always starts `false` (the drawer never opens
//    over the content just because a document loads in a narrow window), and is only
//    meaningful while in drawer mode. It never reaches the host.
//  - `tocToggle` (Ctrl+B / the toolbar button) lets the page decide what "toggle" means:
//    in drawer mode it flips `drawerOpen` locally; docked, it flips `hostVisible` and
//    reports `tocVisibilityChanged` so the host persists it and tells the other tabs.
//  - The in-page close button and backdrop: in drawer mode they close the drawer locally;
//    docked, they set `hostVisible = false` and report `tocVisibilityChanged`.
const narrowQuery = window.matchMedia("(max-width: 899px)");

let hostVisible = true;
let drawerOpen = false;
let autoHidden = true; // no entries yet
let currentEntries = []; // flat, always in DOCUMENT order — the IntersectionObserver
                          // active-heading logic depends on that order regardless of
                          // the sidebar's own sort mode.
let treeRoots = [];
let nodesById = new Map();
let activeId = null;
let observer = null;

// Sort mode is a sidebar display preference, not per-document state: it intentionally
// survives across `renderToc` calls (switching documents) but is never persisted to the
// host. Collapse state IS per-document: it lives on the tree nodes rebuilt by every
// `renderToc` call, so it always starts fully expanded on a new document (per spec).
let sortMode = "doc"; // "doc" | "alpha"

function isNarrow() {
  return narrowQuery.matches;
}

function updateVisibilityClass() {
  const closed = autoHidden || (isNarrow() ? !drawerOpen : !hostVisible);
  if (tocNav) {
    tocNav.classList.toggle("mdr-toc--closed", closed);
    // A closed drawer sits off-screen (transform) but stays in the DOM so the slide
    // transition can play; keep it out of the tab order and the accessibility tree
    // while it isn't visible instead of leaving it silently reachable.
    tocNav.inert = isNarrow() && closed;
  }
  if (tocBackdrop) tocBackdrop.hidden = !isNarrow() || closed;
}

narrowQuery.addEventListener("change", updateVisibilityClass);

/**
 * Applies the host's `tocVisibility` message: the persisted docked preference only. It
 * never opens or closes the narrow-mode drawer. Combined with the auto-hide rule below.
 */
export function setTocVisible(visible) {
  hostVisible = !!visible;
  updateVisibilityClass();
}

/**
 * Applies the host's `tocToggle` message (Ctrl+B / the toolbar button): toggles the drawer
 * locally in narrow mode; otherwise flips the docked sidebar and reports the new
 * preference to the host.
 */
export function toggleToc() {
  if (isNarrow()) {
    drawerOpen = !drawerOpen;
    updateVisibilityClass();
    return;
  }
  hostVisible = !hostVisible;
  updateVisibilityClass();
  send({ type: "tocVisibilityChanged", visible: hostVisible });
}

/** Closes the TOC in whichever mode is active — drawer (local only) or docked (host-persisted). */
function requestClose() {
  if (isNarrow()) {
    drawerOpen = false;
    updateVisibilityClass();
    return;
  }
  hostVisible = false;
  updateVisibilityClass();
  send({ type: "tocVisibilityChanged", visible: false });
}

function cssEscapeId(value) {
  if (window.CSS && typeof CSS.escape === "function") return CSS.escape(value);
  return String(value).replace(/["\\]/g, "\\$&");
}

// ---------------------------------------------------------------------------------
// Active heading (IntersectionObserver) — unaffected by sort/filter/collapse: it always
// walks `currentEntries` in true document order and just flags the current link.
// ---------------------------------------------------------------------------------

function applyActiveAttribute() {
  if (!tocList) return;
  const current = tocList.querySelectorAll(".mdr-toc-link[aria-current]");
  for (const link of current) link.removeAttribute("aria-current");
  if (!activeId) return;
  const next = tocList.querySelector('.mdr-toc-link[data-toc-id="' + cssEscapeId(activeId) + '"]');
  if (next) next.setAttribute("aria-current", "location");
}

function setActive(id) {
  if (activeId === id) return;
  activeId = id;
  applyActiveAttribute();
}

function setupObserver() {
  const mdrMain = document.getElementById("mdr-main");
  if (!mdrMain || currentEntries.length === 0) return;

  const intersecting = new Set();

  observer = new IntersectionObserver(
    (records) => {
      for (const record of records) {
        const id = record.target.id;
        if (record.isIntersecting) intersecting.add(id);
        else intersecting.delete(id);
      }
      for (const entry of currentEntries) {
        if (intersecting.has(entry.id)) {
          setActive(entry.id);
          return;
        }
      }
    },
    { root: mdrMain, rootMargin: "0px 0px -70% 0px", threshold: 0 }
  );

  for (const entry of currentEntries) {
    const heading = document.getElementById(entry.id);
    if (heading) observer.observe(heading);
  }

  setActive(currentEntries[0].id);
}

// ---------------------------------------------------------------------------------
// Tree: headings only carry a `level` (1-6); nesting is inferred the usual way — an
// entry's parent is the nearest earlier entry with a smaller level (so a skipped level,
// e.g. h1 -> h3, still nests one step, not three).
// ---------------------------------------------------------------------------------

function buildTree(entries) {
  const roots = [];
  const stack = []; // {node, level}
  const byId = new Map();
  for (const entry of entries) {
    const node = { entry, children: [], collapsed: false, li: null, twistyBtn: null };
    while (stack.length && stack[stack.length - 1].level >= entry.level) stack.pop();
    if (stack.length === 0) roots.push(node);
    else stack[stack.length - 1].node.children.push(node);
    stack.push({ node, level: entry.level });
    byId.set(entry.id, node);
  }
  return { roots, byId };
}

function compareText(a, b) {
  return a.localeCompare(b, undefined, { sensitivity: "base" });
}

function toggleCollapse(node) {
  node.collapsed = !node.collapsed;
  if (node.li) node.li.setAttribute("data-collapsed", node.collapsed ? "true" : "false");
  if (node.twistyBtn) {
    node.twistyBtn.setAttribute("aria-expanded", node.collapsed ? "false" : "true");
    node.twistyBtn.setAttribute("aria-label", (node.collapsed ? "Expand " : "Collapse ") + node.entry.text);
  }
}

const SVG_NS = "http://www.w3.org/2000/svg";

/** A small inline chevron-right (rotated to point down when expanded, via CSS). Drawn as
 *  an SVG rather than a text glyph so it stays crisp and unmistakable at 12px regardless
 *  of font/fallback — a Unicode triangle glyph read as a faint dash in some captures. */
function createTwistyIcon() {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("class", "mdr-toc-twisty-icon");
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

/** Builds `<li>`s for `nodes` into `container` (either the root `<ol>` or a freshly
 *  created `.mdr-toc-children` `<ol>`), honoring the current sort mode. Non-destructive:
 *  it never reorders `node.children` itself, only the DOM, so switching sort mode back
 *  to document order needs no re-parse. */
function renderNodesInto(container, nodes) {
  const ordered = sortMode === "alpha" ? [...nodes].sort((a, b) => compareText(a.entry.text, b.entry.text)) : nodes;

  for (const node of ordered) {
    const li = document.createElement("li");
    li.className = "mdr-toc-item";
    li.dataset.level = String(node.entry.level);
    node.li = li;

    // The twisty is absolutely positioned to vertically center within its own row without
    // affecting row height. Its containing block must be THIS row only, not the whole
    // `<li>` — a parent `<li>` also contains its (possibly tall) expanded `.mdr-toc-children`
    // subtree, and `top: 50%` against that combined box would center the twisty somewhere
    // in the middle of the whole branch instead of against its own row (it read as a stray
    // mark floating between rows). `.mdr-toc-row` wraps only the twisty + link.
    const row = document.createElement("div");
    row.className = "mdr-toc-row";

    const hasChildren = node.children.length > 0;
    if (hasChildren) {
      li.setAttribute("data-collapsed", node.collapsed ? "true" : "false");

      const twisty = document.createElement("button");
      twisty.type = "button";
      twisty.className = "mdr-toc-twisty";
      twisty.dataset.tocId = node.entry.id;
      twisty.setAttribute("aria-expanded", node.collapsed ? "false" : "true");
      twisty.setAttribute("aria-label", (node.collapsed ? "Expand " : "Collapse ") + node.entry.text);
      twisty.appendChild(createTwistyIcon());
      node.twistyBtn = twisty;
      row.appendChild(twisty);
    } else {
      node.twistyBtn = null;
    }

    const a = document.createElement("a");
    a.className = "mdr-toc-link";
    a.href = "#" + encodeURIComponent(node.entry.id);
    a.dataset.tocId = node.entry.id;
    a.title = node.entry.text;
    const text = document.createElement("span");
    text.className = "mdr-toc-link-text";
    text.textContent = node.entry.text;
    a.appendChild(text);
    row.appendChild(a);

    li.appendChild(row);

    if (hasChildren) {
      const childOl = document.createElement("ol");
      childOl.className = "mdr-toc-children";
      renderNodesInto(childOl, node.children);
      li.appendChild(childOl);
    }

    container.appendChild(li);
  }
}

function rebuildListDom() {
  if (!tocList) return;
  tocList.textContent = "";
  renderNodesInto(tocList, treeRoots);
}

// ---------------------------------------------------------------------------------
// Filter — case- and accent-insensitive substring match, anywhere in the heading text.
// A node is visible if it matches itself or any descendant matches (so ancestors of a
// match stay visible even when they themselves don't match); collapse state is ignored
// while a filter is active so matches buried in a collapsed section aren't hidden twice.
// ---------------------------------------------------------------------------------

function normalizeForSearch(value) {
  return String(value)
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase();
}

function applyFilter() {
  const raw = tocFilterInput ? tocFilterInput.value : "";
  const query = normalizeForSearch(raw.trim());
  const filtering = query.length > 0;
  if (tocNav) tocNav.classList.toggle("mdr-toc--filtering", filtering);

  let matchCount = 0;

  function walk(node) {
    const selfMatch = !filtering || normalizeForSearch(node.entry.text).includes(query);
    let descendantMatch = false;
    for (const child of node.children) {
      if (walk(child)) descendantMatch = true;
    }
    const visible = !filtering || selfMatch || descendantMatch;
    if (node.li) node.li.hidden = !visible;
    if (filtering && selfMatch) matchCount++;
    return visible;
  }

  for (const root of treeRoots) walk(root);

  if (tocStatus) {
    if (!filtering) {
      tocStatus.hidden = true;
      tocStatus.textContent = "";
    } else {
      tocStatus.hidden = false;
      tocStatus.textContent =
        matchCount === 0
          ? "No matching headings"
          : matchCount + " of " + currentEntries.length + (matchCount === 1 ? " heading" : " headings");
    }
  }
}

// ---------------------------------------------------------------------------------
// Toolbar: filter box + sort toggle. Built once; `renderToc` only rebuilds the list.
// ---------------------------------------------------------------------------------

let tocFilterInput = null;
let tocSortButton = null;
let tocStatus = null;

function updateSortButton() {
  if (!tocSortButton) return;
  const alpha = sortMode === "alpha";
  tocSortButton.setAttribute("aria-pressed", alpha ? "true" : "false");
  tocSortButton.title = alpha ? "Sorted A\u2192Z" : "Document order";
  tocSortButton.setAttribute(
    "aria-label",
    alpha ? "Sort order: alphabetical. Activate for document order." : "Sort order: document. Activate for alphabetical."
  );
}

function nodeFromFocusTarget(el) {
  if (!el || !el.classList || !el.dataset || !el.dataset.tocId) return null;
  if (!el.classList.contains("mdr-toc-twisty") && !el.classList.contains("mdr-toc-link")) return null;
  return nodesById.get(el.dataset.tocId) || null;
}

function buildToolbar() {
  if (!tocNav || !tocList) return;

  const toolbar = document.createElement("div");
  toolbar.className = "mdr-toc-toolbar";

  const searchWrap = document.createElement("div");
  searchWrap.className = "mdr-toc-search";

  const input = document.createElement("input");
  input.type = "search";
  input.className = "mdr-toc-search-input";
  input.placeholder = "Filter headings";
  input.setAttribute("aria-label", "Filter headings");
  input.autocomplete = "off";
  input.spellcheck = false;
  searchWrap.appendChild(input);
  tocFilterInput = input;

  const sortButton = document.createElement("button");
  sortButton.type = "button";
  sortButton.className = "mdr-icon-button mdr-toc-sort";
  tocSortButton = sortButton;
  updateSortButton();

  toolbar.appendChild(searchWrap);
  toolbar.appendChild(sortButton);

  const status = document.createElement("div");
  status.className = "mdr-toc-status";
  status.setAttribute("aria-live", "polite");
  status.hidden = true;
  tocStatus = status;

  tocNav.insertBefore(toolbar, tocList);
  tocNav.insertBefore(status, tocList);

  input.addEventListener("input", applyFilter);
  input.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && input.value !== "") {
      e.preventDefault();
      e.stopPropagation();
      input.value = "";
      applyFilter();
    }
  });

  sortButton.addEventListener("click", () => {
    sortMode = sortMode === "alpha" ? "doc" : "alpha";
    updateSortButton();
    rebuildListDom();
    applyFilter();
    applyActiveAttribute();
  });

  // Delegated so it keeps working across `rebuildListDom()` calls without re-binding.
  tocList.addEventListener("click", (e) => {
    const twisty = e.target.closest(".mdr-toc-twisty");
    if (!twisty) return;
    e.preventDefault();
    const node = nodesById.get(twisty.dataset.tocId);
    if (node) toggleCollapse(node);
  });

  tocList.addEventListener("keydown", (e) => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      const focusable = Array.from(tocList.querySelectorAll(".mdr-toc-link")).filter((a) => a.offsetParent !== null);
      if (focusable.length === 0) return;
      let index = focusable.indexOf(document.activeElement);
      if (index < 0 && document.activeElement && document.activeElement.classList.contains("mdr-toc-twisty")) {
        const li = document.activeElement.closest(".mdr-toc-item");
        const sibling = li ? li.querySelector(":scope > .mdr-toc-row > .mdr-toc-link") : null;
        index = sibling ? focusable.indexOf(sibling) : -1;
      }
      const next = e.key === "ArrowDown" ? Math.min(index < 0 ? 0 : index + 1, focusable.length - 1) : Math.max(index < 0 ? 0 : index - 1, 0);
      e.preventDefault();
      focusable[next].focus();
      return;
    }

    if (e.key === "ArrowLeft" || e.key === "ArrowRight") {
      const node = nodeFromFocusTarget(document.activeElement);
      if (!node || node.children.length === 0) return;
      const wantCollapsed = e.key === "ArrowLeft";
      if (node.collapsed !== wantCollapsed) {
        e.preventDefault();
        toggleCollapse(node);
      }
    }
  });
}

/** (Re)builds the sidebar from `toc` entries `{level, id, text}` and auto-hides below 2 entries. */
export function renderToc(entries) {
  currentEntries = Array.isArray(entries) ? entries : [];
  autoHidden = currentEntries.length < 2;

  if (observer) {
    observer.disconnect();
    observer = null;
  }
  activeId = null;

  const built = buildTree(currentEntries);
  treeRoots = built.roots;
  nodesById = built.byId;

  if (tocFilterInput) tocFilterInput.value = "";

  rebuildListDom();
  applyFilter();
  applyActiveAttribute();

  updateVisibilityClass();
  setupObserver();
}

buildToolbar();

if (tocClose) {
  tocClose.addEventListener("click", requestClose);
}

if (tocBackdrop) {
  tocBackdrop.addEventListener("click", requestClose);
}
