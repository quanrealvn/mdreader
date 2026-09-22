// toc.js — sidebar table of contents: builds the list from `toc` entries, tracks the
// active heading with an IntersectionObserver, and reports visibility changes.
//
// Click-to-scroll is NOT wired here: entries are plain `a[href="#..."]` elements, and
// links.js's single document-level click/auxclick listener (§7.3) already intercepts
// every in-page fragment link, including these — so there is exactly one place that
// resolves fragments and scrolls, and it behaves identically for TOC links and for
// anchors inside the rendered content.

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
let currentEntries = [];
let activeLink = null;
let observer = null;

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

function setActive(id) {
  if (activeLink && activeLink.dataset.tocId === id) return;
  if (activeLink) activeLink.removeAttribute("aria-current");
  const next = tocList ? tocList.querySelector('.mdr-toc-link[data-toc-id="' + cssEscapeId(id) + '"]') : null;
  activeLink = next || null;
  if (next) next.setAttribute("aria-current", "location");
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

/** (Re)builds the sidebar from `toc` entries `{level, id, text}` and auto-hides below 2 entries. */
export function renderToc(entries) {
  currentEntries = Array.isArray(entries) ? entries : [];
  autoHidden = currentEntries.length < 2;

  if (observer) {
    observer.disconnect();
    observer = null;
  }
  activeLink = null;

  const fragment = document.createDocumentFragment();
  for (const entry of currentEntries) {
    const li = document.createElement("li");
    li.className = "mdr-toc-item";
    li.dataset.level = String(entry.level);

    const a = document.createElement("a");
    a.className = "mdr-toc-link";
    a.href = "#" + encodeURIComponent(entry.id);
    a.textContent = entry.text;
    a.title = entry.text;
    a.dataset.tocId = entry.id;

    li.appendChild(a);
    fragment.appendChild(li);
  }

  if (tocList) {
    tocList.textContent = "";
    tocList.appendChild(fragment);
  }

  updateVisibilityClass();
  setupObserver();
}

if (tocClose) {
  tocClose.addEventListener("click", requestClose);
}

if (tocBackdrop) {
  tocBackdrop.addEventListener("click", requestClose);
}
