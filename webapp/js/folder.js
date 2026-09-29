// folder.js — folder mode for the web version (ARCHITECTURE §15; the desktop's own is §17).
//
// Drop a folder on the page, or pick one, and read the Markdown in it. The whole of it happens
// in the browser: the tree comes from `DataTransferItem.webkitGetAsEntry()` and
// `FileSystemDirectoryReader` (a drop) or from the `webkitRelativePath` of a `webkitdirectory`
// input (a pick), and a file's text is read with `File.text()`. **Nothing here is uploaded.**
// The server sees a file only when the reader opens it, and then it sees exactly what pasted
// text would be: one `POST /api/render` with that document's characters in it. A folder of
// two thousand files costs the server nothing until somebody clicks one.
//
// What this is not: the desktop's folder mode. There is no watcher (a browser is not told when
// a folder changes), no quick open and no search across files — those read every file in the
// folder, which here would mean holding thousands of file handles and reading megabytes the
// visitor never asked for. What is shared is the shape of the answer: Markdown only until you
// ask for the rest, node_modules/.git/bin/obj left out, folders before files, a bounded walk
// that says when it stopped.
//
// Module-scoped state only (content ids can clobber window properties, §7.3).

// ---------------------------------------------------------------------------------
// limits (and why these numbers)
// ---------------------------------------------------------------------------------
//
// MAX_ENTRIES / MAX_DEPTH are the desktop's own defaults (FolderScanOptions), so a folder that
// reads whole in the app reads whole here. They are not arbitrary: 20 000 names is more than
// any notes folder and still a list the page can hold and sort without being felt; 32 levels is
// past anything a person nests by hand and short of a link loop's idea of deep. EXCLUDED is the
// desktop's list, and it is applied in the walk rather than in the view, so `node_modules` is
// never read at all — that is the difference between a scan that takes a moment and one that
// takes a minute, and it is why "Show all files" cannot reveal them.
//
// ROW_BATCH is a separate, smaller bound, on the DOM rather than on the scan: a folder may
// legitimately hold twenty thousand names, and twenty thousand rows is a page that scrolls like
// treacle. The pane draws a thousand and offers the rest by the thousand, saying how many are
// left rather than pretending that is all there is.
const MAX_ENTRIES = 20_000;
const MAX_DEPTH = 32;
const EXCLUDED_DIRECTORIES = ["node_modules", ".git", "bin", "obj"];
const ROW_BATCH = 1000;
// The walk gives the page a turn this often. `readEntries` already yields once per batch of a
// hundred, which is enough for a dropped folder; a picked one arrives as one flat FileList and
// would otherwise be built in a single uninterruptible go.
const YIELD_EVERY = 1500;

// MarkdownFileTypes.Extensions (§4.3), exactly: the desktop's file pane shows these and nothing
// else until "show all files". The web version's Open button also takes .txt and .mdwn, because
// a file somebody hands you is a file somebody meant; a folder is a haystack, and widening the
// default here would put every changelog, licence and note in the tree.
const MARKDOWN_RE = /\.(md|markdown|mdown|mkd|mkdn)$/i;

const KEY_SHOW_ALL = "mdr.web.folder.all";

// ---------------------------------------------------------------------------------
// elements
// ---------------------------------------------------------------------------------

const pane = document.getElementById("mdr-web-folder");
const nameEl = document.getElementById("mdr-web-folder-name");
const treeEl = document.getElementById("mdr-web-folder-tree");
const statusEl = document.getElementById("mdr-web-folder-status");
const emptyEl = document.getElementById("mdr-web-folder-empty");
const emptyTextEl = document.getElementById("mdr-web-folder-empty-text");
const allButton = document.getElementById("mdr-web-folder-all");
const closeButton = document.getElementById("mdr-web-folder-close");
const toggleButton = document.getElementById("mdr-web-folder-toggle");
const folderInput = document.getElementById("mdr-web-folder-input");

// ---------------------------------------------------------------------------------
// state
// ---------------------------------------------------------------------------------

let host = null; // { activate(path, session) -> bool, open({...}), toast(text), maxBytes }
let root = null; // the scanned tree, or null when no folder is open
let scan = null; // { skipped, unreadable, truncatedEntries, truncatedDepth, entries }
let scanGeneration = 0; // a newer folder abandons an older walk's results
let sessionCounter = 0; // which opening of a folder a tab belongs to
let session = 0;
let showAll = false;
let expanded = new Set(); // relative paths of the folders the reader has opened
let shownRows = ROW_BATCH;
let rows = []; // the flattened, filtered view currently in the DOM
let focusPath = null; // roving tabindex: the row that would take focus
let paneVisible = true; // the toggle's state, only meaningful while a folder is open
// True from the moment a folder drop is recognised until the end of that task. links.js still
// sees the folder in `dataTransfer.files` (a zero-byte File with no type) and still hands it to
// the host as a plain file drop; this is what tells the host to let that one go by.
let swallowingDrop = false;

function read(key) {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key, value) {
  try {
    if (value === null) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, value);
  } catch {
    // disabled, blocked or full: a preference is not worth breaking the page for
  }
}

showAll = read(KEY_SHOW_ALL) === "1";

// ---------------------------------------------------------------------------------
// names
// ---------------------------------------------------------------------------------

function isMarkdownName(name) {
  return MARKDOWN_RE.test(name);
}

function isHiddenName(name) {
  return name.charAt(0) === ".";
}

function isExcludedName(name) {
  const lower = name.toLowerCase();
  return EXCLUDED_DIRECTORIES.some((excluded) => excluded === lower);
}

/**
 * Folders before files, then case-insensitively and then ordinally — the desktop scanner's
 * ordering rule, and for the same reason: the same folder has to give the same tree on every
 * machine, which a locale-aware comparison does not promise.
 */
function compareNodes(a, b) {
  if (a.dir !== b.dir) return a.dir ? -1 : 1;
  const la = a.name.toLowerCase();
  const lb = b.name.toLowerCase();
  if (la < lb) return -1;
  if (la > lb) return 1;
  if (a.name < b.name) return -1;
  if (a.name > b.name) return 1;
  return 0;
}

function makeNode(name, path, dir) {
  return { name, path, dir, children: dir ? [] : null, file: null, entry: null, unread: false, visible: false };
}

// ---------------------------------------------------------------------------------
// the walk — a dropped folder
// ---------------------------------------------------------------------------------

const yieldToPage = () => new Promise((resolve) => setTimeout(resolve, 0));

/**
 * Every entry in one directory.
 *
 * `readEntries` answers a BATCH at a time — a hundred in Chromium — and signals the end with an
 * empty array. A reader that calls it once gets the first hundred names of every folder and
 * loses the rest without a word, which is the single easiest way to get this wrong.
 *
 * Returns null when the directory cannot be listed at all; that is counted and said out loud
 * rather than being silently treated as empty (the desktop's `IgnoreInaccessible = false` rule,
 * §17.1 — a folder you may not read and an empty one must not look the same).
 */
async function readDirectory(entry) {
  let reader;
  try {
    reader = entry.createReader();
  } catch {
    return null;
  }

  const all = [];
  for (;;) {
    let batch;
    try {
      batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
    } catch {
      return all.length > 0 ? all : null;
    }
    if (!batch || batch.length === 0) return all;
    for (const item of batch) all.push(item);
    // A directory that keeps answering past the cap is not worth reading to the end: the walk
    // is about to stop anyway.
    if (all.length > MAX_ENTRIES) return all;
  }
}

/**
 * Walks a dropped directory into a tree. Iterative, never recursive: a link farm or a checked-out
 * monorepo can nest deeper than a stack survives, and the depth cap is a policy about what to
 * show, not a guard against crashing.
 *
 * `generation` is the drop this walk belongs to. Every await is a chance for a second folder to
 * be dropped, and the loser of that race must abandon its result rather than paint it over the
 * winner's.
 */
async function walkEntry(rootEntry, generation) {
  const tree = makeNode(rootEntry.name, "", true);
  const info = { skipped: 0, unreadable: 0, truncatedEntries: false, truncatedDepth: false, entries: 0 };
  const stack = [{ node: tree, entry: rootEntry, depth: 0 }];
  let sinceYield = 0;

  while (stack.length > 0) {
    const frame = stack.pop();
    const listing = await readDirectory(frame.entry);
    if (generation !== scanGeneration) return null;

    if (listing === null) {
      info.unreadable += 1;
      continue;
    }

    // A folder sitting at the depth cap is listed before the flag is set, so an empty one at the
    // boundary does not claim there is more below it (§17.1). Its own row stays, and it is left
    // un-prunable: we know it holds something, we just decided not to look.
    if (frame.depth >= MAX_DEPTH) {
      if (listing.length > 0) {
        info.truncatedDepth = true;
        frame.node.unread = true;
      }
      continue;
    }

    const children = [];
    for (const child of listing) {
      if (info.entries >= MAX_ENTRIES) {
        info.truncatedEntries = true;
        break;
      }
      if (child.isDirectory) {
        if (isExcludedName(child.name)) {
          info.skipped += 1;
          continue;
        }
        const node = makeNode(child.name, frame.node.path ? frame.node.path + "/" + child.name : child.name, true);
        children.push(node);
        stack.push({ node, entry: child, depth: frame.depth + 1 });
        info.entries += 1;
      } else {
        const node = makeNode(child.name, frame.node.path ? frame.node.path + "/" + child.name : child.name, false);
        node.entry = child;
        children.push(node);
        info.entries += 1;
      }
    }

    children.sort(compareNodes);
    frame.node.children = children;

    sinceYield += listing.length;
    if (sinceYield >= YIELD_EVERY) {
      sinceYield = 0;
      await yieldToPage();
      if (generation !== scanGeneration) return null;
    }
    if (info.truncatedEntries) break;
  }

  return { tree, info };
}

// ---------------------------------------------------------------------------------
// the walk — a picked folder
// ---------------------------------------------------------------------------------

/**
 * Builds the same tree from the flat FileList a `webkitdirectory` input hands over. Each file
 * carries `webkitRelativePath` — "notes/guides/install.md" — whose first segment is the folder's
 * own name; the rest is the path the pane shows.
 *
 * A picked folder is already fully enumerated by the browser, so the caps here can only trim
 * what is shown, not what was read. They are applied all the same, because the pane's job below
 * that point is identical and a 200 000-file pick must not become 200 000 nodes.
 */
async function walkFiles(fileList, generation) {
  const files = Array.from(fileList);
  if (files.length === 0) return null;

  const first = files[0].webkitRelativePath || files[0].name;
  const rootName = first.split("/")[0] || "folder";
  const tree = makeNode(rootName, "", true);
  const info = { skipped: 0, unreadable: 0, truncatedEntries: false, truncatedDepth: false, entries: 0 };
  const byPath = new Map([["", tree]]);
  const skippedRoots = new Set();
  let sinceYield = 0;

  for (const file of files) {
    const relative = file.webkitRelativePath || file.name;
    const segments = relative.split("/").slice(1).filter((segment) => segment.length > 0);
    if (segments.length === 0) continue;

    // The picker hands over everything, node_modules included. Refuse the whole branch at its
    // top-most excluded folder and count that folder once, so "4 folders were skipped" means
    // four folders and not four thousand files.
    const excludedAt = segments.slice(0, -1).findIndex((segment) => isExcludedName(segment));
    if (excludedAt !== -1) {
      skippedRoots.add(segments.slice(0, excludedAt + 1).join("/"));
      continue;
    }
    if (segments.length > MAX_DEPTH) {
      info.truncatedDepth = true;
      continue;
    }

    let parent = tree;
    let path = "";
    let dropped = false;
    for (let i = 0; i < segments.length - 1; i++) {
      path = path ? path + "/" + segments[i] : segments[i];
      let node = byPath.get(path);
      if (!node) {
        if (info.entries >= MAX_ENTRIES) {
          info.truncatedEntries = true;
          dropped = true;
          break;
        }
        node = makeNode(segments[i], path, true);
        byPath.set(path, node);
        parent.children.push(node);
        info.entries += 1;
      }
      parent = node;
    }
    if (dropped) continue;

    const name = segments[segments.length - 1];
    const filePath = path ? path + "/" + name : name;
    if (byPath.has(filePath)) continue;
    if (info.entries >= MAX_ENTRIES) {
      info.truncatedEntries = true;
      continue;
    }
    const node = makeNode(name, filePath, false);
    node.file = file;
    byPath.set(filePath, node);
    parent.children.push(node);
    info.entries += 1;

    sinceYield += 1;
    if (sinceYield >= YIELD_EVERY) {
      sinceYield = 0;
      await yieldToPage();
      if (generation !== scanGeneration) return null;
    }
  }

  info.skipped = skippedRoots.size;
  sortTree(tree);
  return { tree, info };
}

function sortTree(node) {
  const stack = [node];
  while (stack.length > 0) {
    const current = stack.pop();
    if (!current.children) continue;
    current.children.sort(compareNodes);
    for (const child of current.children) if (child.dir) stack.push(child);
  }
}

// ---------------------------------------------------------------------------------
// what the pane shows
// ---------------------------------------------------------------------------------

/**
 * Marks every node visible or not for the current "show all files" setting, and returns whether
 * the subtree has anything in it.
 *
 * Default: Markdown files only, hidden names left out, and a folder that then holds nothing is
 * pruned — a repository otherwise shows a forest of empty folders (§17.1). A folder at the depth
 * cap is never pruned: it is known to hold something, and hiding it would turn "there is more
 * down there" into silence.
 *
 * "Show all files" reveals everything that was walked, hidden names included, and stops pruning.
 * It cannot reveal node_modules, .git, bin or obj, because those were never read.
 */
function markVisible(node) {
  if (!node.dir) {
    node.visible = showAll ? true : isMarkdownName(node.name) && !isHiddenName(node.name);
    return node.visible;
  }

  let any = false;
  for (const child of node.children) {
    if (markVisible(child)) any = true;
  }

  if (node === root) {
    node.visible = true;
    return any;
  }
  node.visible = showAll ? true : !isHiddenName(node.name) && (any || node.unread);
  return node.visible;
}

/** The rows the pane would draw, in order, honouring which folders the reader has opened. */
function flatten() {
  const out = [];
  const walk = (node, depth) => {
    for (const child of node.children) {
      if (!child.visible) continue;
      out.push({ node: child, depth });
      if (child.dir && expanded.has(child.path)) walk(child, depth + 1);
    }
  };
  walk(root, 0);
  return out;
}

function statusText() {
  if (!scan) return "";
  const parts = [];
  if (scan.skipped === 1) {
    parts.push("1 folder was skipped (node_modules, .git, bin and obj are left out).");
  } else if (scan.skipped > 1) {
    parts.push(`${scan.skipped} folders were skipped (node_modules, .git, bin and obj are left out).`);
  }
  if (scan.unreadable === 1) parts.push("1 folder couldn't be read.");
  else if (scan.unreadable > 1) parts.push(`${scan.unreadable} folders couldn't be read.`);
  if (scan.truncatedEntries) parts.push(`Showing the first ${MAX_ENTRIES.toLocaleString()} entries of this folder.`);
  else if (scan.truncatedDepth) parts.push(`Showing the first ${MAX_DEPTH} levels of this folder.`);
  return parts.join(" ");
}

function icon(paths) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("class", "mdr-web-file-icon");
  for (const d of paths) {
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", d);
    svg.appendChild(path);
  }
  return svg;
}

const CHEVRON = ["M9 6l6 6-6 6"];
const FILE_ICON = ["M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z", "M14 3v5h5"];

function renderTree() {
  treeEl.textContent = "";
  if (!root) {
    rows = [];
    emptyEl.hidden = true;
    statusEl.textContent = "";
    return;
  }

  markVisible(root);
  rows = flatten();

  const drawn = rows.slice(0, shownRows);
  if (focusPath !== null && !drawn.some((row) => row.node.path === focusPath)) focusPath = null;
  if (focusPath === null && drawn.length > 0) focusPath = drawn[0].node.path;

  const fragment = document.createDocumentFragment();
  for (const row of drawn) {
    const el = document.createElement("div");
    el.className = "mdr-web-file" + (row.node.dir ? " mdr-web-file--dir" : "");
    el.dataset.path = row.node.path;
    el.setAttribute("role", "treeitem");
    el.setAttribute("aria-level", String(row.depth + 1));
    el.setAttribute("tabindex", row.node.path === focusPath ? "0" : "-1");
    el.style.setProperty("--mdr-file-depth", String(row.depth));
    if (row.node.dir && !row.node.unread) el.setAttribute("aria-expanded", String(expanded.has(row.node.path)));
    if (row.node.unread) el.title = "This folder is past the depth this scan covers.";

    const glyph = icon(row.node.dir ? CHEVRON : FILE_ICON);
    if (row.node.dir) glyph.classList.add("mdr-web-file-chevron");
    el.appendChild(glyph);

    const label = document.createElement("span");
    label.className = "mdr-web-file-name";
    label.textContent = row.node.name;
    el.appendChild(label);

    fragment.appendChild(el);
  }

  if (rows.length > drawn.length) {
    const left = rows.length - drawn.length;
    const more = document.createElement("button");
    more.type = "button";
    more.className = "mdr-web-file-more";
    more.textContent = `Show ${Math.min(ROW_BATCH, left).toLocaleString()} more (${left.toLocaleString()} left)`;
    fragment.appendChild(more);
  }

  treeEl.appendChild(fragment);

  const status = statusText();
  statusEl.textContent = status;
  if (rows.length > 0) {
    emptyEl.hidden = true;
  } else {
    emptyEl.hidden = false;
    emptyTextEl.textContent =
      scan && scan.entries === 0
        ? "This folder is empty."
        : "No Markdown files here. Turn on Show all files to see what is.";
  }
}

// ---------------------------------------------------------------------------------
// opening a folder
// ---------------------------------------------------------------------------------

function showPane() {
  paneVisible = true;
  pane.hidden = false;
  updateToggle();
}

function updateToggle() {
  if (!toggleButton) return;
  if (root) {
    toggleButton.title = paneVisible ? "Hide the file list" : "Show the file list";
    toggleButton.setAttribute("aria-pressed", String(paneVisible));
  } else {
    toggleButton.title = "Open a folder";
    toggleButton.removeAttribute("aria-pressed");
  }
}

/** Opens the pane on `name` and says it is working, before the walk has anything to show. */
function beginScan(name) {
  session = ++sessionCounter;
  nameEl.textContent = name;
  nameEl.title = name;
  statusEl.textContent = "Reading…";
  emptyEl.hidden = true;
  treeEl.textContent = "";
  rows = [];
  focusPath = null;
  shownRows = ROW_BATCH;
  showPane();
}

/**
 * Puts a finished walk on screen.
 *
 * The folders the reader had opened are kept, by path, if the new tree still has them. That is
 * what makes dropping the same folder twice a no-op rather than a reset — a browser cannot tell
 * us it is the same folder, so "the same paths are still there" is the closest honest test — and
 * a different folder simply matches nothing and arrives collapsed.
 */
function finishScan(result) {
  root = result.tree;
  scan = result.info;
  const alive = new Set();
  const stack = [root];
  while (stack.length > 0) {
    const node = stack.pop();
    if (!node.children) continue;
    for (const child of node.children) {
      if (!child.dir) continue;
      alive.add(child.path);
      stack.push(child);
    }
  }
  expanded = new Set([...expanded].filter((path) => alive.has(path)));
  renderTree();
  updateToggle();
}

async function openDroppedFolder(entry, extras) {
  const generation = ++scanGeneration;
  beginScan(entry.name);
  const result = await walkEntry(entry, generation);
  if (generation !== scanGeneration || !result) return;
  finishScan(result);
  if (extras && host) host.toast(`Opened "${entry.name}". A drop opens one folder at a time.`);
}

async function openPickedFolder(fileList) {
  // An empty FileList is not an answer this page can act on: a cancelled picker and a folder with
  // no files in it look exactly the same from here, so it says nothing and leaves whatever is
  // open alone. Guessing would either invent an error or throw away a folder the reader is using.
  if (!fileList || fileList.length === 0) return;
  const generation = ++scanGeneration;
  const first = fileList[0].webkitRelativePath || fileList[0].name;
  beginScan(first.split("/")[0] || "folder");
  const result = await walkFiles(fileList, generation);
  if (generation !== scanGeneration || !result) return;
  finishScan(result);
}

function closeFolder() {
  scanGeneration += 1;
  root = null;
  scan = null;
  rows = [];
  expanded = new Set();
  focusPath = null;
  pane.hidden = true;
  treeEl.textContent = "";
  statusEl.textContent = "";
  emptyEl.hidden = true;
  updateToggle();
}

// ---------------------------------------------------------------------------------
// opening a file out of the tree
// ---------------------------------------------------------------------------------

function findNode(path) {
  if (!root) return null;
  const stack = [root];
  while (stack.length > 0) {
    const node = stack.pop();
    if (node.path === path && node !== root) return node;
    if (node.children) for (const child of node.children) stack.push(child);
  }
  return null;
}

/** The `File` behind a node: already in hand for a pick, asked for by callback for a drop. */
function fileOf(node) {
  if (node.file) return Promise.resolve(node.file);
  if (!node.entry || typeof node.entry.file !== "function") return Promise.reject(new Error("no file"));
  return new Promise((resolve, reject) => node.entry.file(resolve, reject));
}

// Below this the pane is a drawer over the workspace, not a column beside it (webapp.css), so
// leaving it open would mean opening a document you cannot see.
const narrow = window.matchMedia("(max-width: 760px)");

function hideDrawerIfNarrow() {
  if (!narrow.matches || !root) return;
  paneVisible = false;
  pane.hidden = true;
  updateToggle();
}

async function activateFile(node) {
  if (!host) return;

  // Which opening of which folder this click belongs to, taken now: reading a file is
  // asynchronous, and a folder opened while it is in flight must not have the resulting tab
  // filed under its own paths.
  const clicked = session;

  // Ask for the tab first. A file already open is a tab to come back to, edits and all — going
  // to disk for it would either throw those away or read a megabyte to discard it.
  if (host.activate(node.path, clicked)) {
    hideDrawerIfNarrow();
    return;
  }

  let file;
  try {
    file = await fileOf(node);
  } catch {
    host.toast("This file couldn't be read.");
    return;
  }

  // The same cap the editor is held to (WebLimitsOptions.DefaultMaxRequestBodyBytes): refuse it
  // here rather than uploading half a megabyte for the server to refuse.
  if (file.size > host.maxBytes) {
    host.toast("This file is over 512 KB.");
    return;
  }

  let text;
  try {
    text = await file.text();
  } catch {
    host.toast("This file couldn't be read.");
    return;
  }

  host.open({ path: node.path, name: node.name, session: clicked, text });
  hideDrawerIfNarrow();
}

function activateRow(path) {
  const node = findNode(path);
  if (!node) return;
  setRoving(path);
  if (node.dir) {
    if (node.unread) return;
    if (expanded.has(path)) expanded.delete(path);
    else expanded.add(path);
    renderTree();
    focusRow(path);
    return;
  }
  void activateFile(node);
}

// ---------------------------------------------------------------------------------
// mouse and keyboard
// ---------------------------------------------------------------------------------

/** Roving tabindex: exactly one row is in the tab order, and it is the one last touched. */
function setRoving(path) {
  focusPath = path;
  for (const el of treeEl.querySelectorAll(".mdr-web-file")) {
    el.setAttribute("tabindex", el.dataset.path === path ? "0" : "-1");
  }
}

function focusRow(path, { scroll = true } = {}) {
  setRoving(path);
  const target = treeEl.querySelector(`.mdr-web-file[data-path="${CSS.escape(path)}"]`);
  if (!target) return;
  target.focus({ preventScroll: true });
  // Not when the list has just grown under the reader's hand: they pressed "show more" to see
  // what came next, and scrolling to the very last of two thousand names is not that.
  if (scroll) target.scrollIntoView({ block: "nearest" });
}

treeEl.addEventListener("click", (event) => {
  const more = event.target.closest(".mdr-web-file-more");
  if (more) {
    shownRows += ROW_BATCH;
    renderTree();
    // The button that was just pressed has been replaced. Put the keyboard back on its successor,
    // or on the last row when that was the last batch, rather than at the top of the document.
    const next = treeEl.querySelector(".mdr-web-file-more");
    if (next) next.focus({ preventScroll: true });
    else if (rows.length > 0) focusRow(rows[rows.length - 1].node.path, { scroll: false });
    return;
  }
  const row = event.target.closest(".mdr-web-file");
  if (!row) return;
  activateRow(row.dataset.path);
});

treeEl.addEventListener("keydown", (event) => {
  const row = event.target.closest(".mdr-web-file");
  if (!row) return;
  const path = row.dataset.path;
  const drawn = Array.from(treeEl.querySelectorAll(".mdr-web-file")).map((el) => el.dataset.path);
  const index = drawn.indexOf(path);
  const node = findNode(path);

  switch (event.key) {
    case "ArrowDown":
      event.preventDefault();
      if (index + 1 < drawn.length) focusRow(drawn[index + 1]);
      break;
    case "ArrowUp":
      event.preventDefault();
      if (index > 0) focusRow(drawn[index - 1]);
      break;
    case "ArrowRight":
      event.preventDefault();
      if (node && node.dir && !node.unread && !expanded.has(path)) {
        expanded.add(path);
        renderTree();
        focusRow(path);
      } else if (index + 1 < drawn.length) {
        focusRow(drawn[index + 1]);
      }
      break;
    case "ArrowLeft": {
      event.preventDefault();
      if (node && node.dir && expanded.has(path)) {
        expanded.delete(path);
        renderTree();
        focusRow(path);
        break;
      }
      const cut = path.lastIndexOf("/");
      if (cut > 0 && drawn.includes(path.slice(0, cut))) focusRow(path.slice(0, cut));
      break;
    }
    case "Home":
      event.preventDefault();
      if (drawn.length > 0) focusRow(drawn[0]);
      break;
    case "End":
      event.preventDefault();
      if (drawn.length > 0) focusRow(drawn[drawn.length - 1]);
      break;
    case "Enter":
    case " ":
      event.preventDefault();
      activateRow(path);
      break;
    default:
      break;
  }
});

allButton.addEventListener("click", () => {
  showAll = !showAll;
  write(KEY_SHOW_ALL, showAll ? "1" : null);
  allButton.setAttribute("aria-pressed", String(showAll));
  shownRows = ROW_BATCH;
  renderTree();
});

closeButton.addEventListener("click", () => closeFolder());

if (toggleButton) {
  toggleButton.addEventListener("click", () => {
    if (!root) {
      folderInput.click();
      return;
    }
    paneVisible = !paneVisible;
    pane.hidden = !paneVisible;
    updateToggle();
  });
}

folderInput.addEventListener("change", () => {
  // The input's own live list, not a copy: clearing the input (which is what makes picking the
  // same folder twice in a row fire `change` again) empties the very list this handler holds.
  const files = Array.from(folderInput.files);
  folderInput.value = "";
  void openPickedFolder(files);
});

// ---------------------------------------------------------------------------------
// the drop
// ---------------------------------------------------------------------------------
//
// Capture phase, at the document, so this runs before links.js's own `drop` listener — the one
// shared with the desktop app, which must keep behaving exactly as it does there (a folder
// dropped on the desktop is a folder the shell opens, §17.4).
//
// `webkitGetAsEntry()` has to be called synchronously: the items are emptied the moment the
// event dispatch ends, and an `await` before the call answers null for every one of them.
//
// This does not stop propagation. links.js still sees the folder in `dataTransfer.files` — a
// zero-byte File with no type, which is exactly what used to make it "Choose a .md, .markdown
// or .txt file" — and still sends its `drop`; `swallowingDrop` is what tells the host to let
// that one go by. Stopping propagation would also take the drop away from the page's own
// bubble-phase listener, which is what puts the drop hint away.
document.addEventListener(
  "drop",
  (event) => {
    const data = event.dataTransfer;
    if (!data || !data.items) return;

    const directories = [];
    let others = 0;
    for (let i = 0; i < data.items.length; i++) {
      const item = data.items[i];
      if (item.kind !== "file") continue;
      let entry = null;
      try {
        entry = item.webkitGetAsEntry();
      } catch {
        entry = null;
      }
      if (entry && entry.isDirectory) directories.push(entry);
      else others += 1;
    }

    if (directories.length === 0) return; // an ordinary file drop: links.js has it

    event.preventDefault();
    swallowingDrop = true;
    // A task, not a microtask: microtask checkpoints run BETWEEN event listeners, so a
    // queueMicrotask here would clear the flag before links.js's listener had run.
    setTimeout(() => {
      swallowingDrop = false;
    }, 0);

    void openDroppedFolder(directories[0], directories.length + others > 1);
  },
  true,
);

// ---------------------------------------------------------------------------------
// the host's side
// ---------------------------------------------------------------------------------

/** True while a drop this module has taken is still being delivered to the rest of the page. */
export function folderDropInProgress() {
  return swallowingDrop;
}

/**
 * Wires the pane to webapp.js.
 *
 * `activate(path, session)` puts an already-open tab for that file back on screen and says so;
 * `open({path, name, session, text})` opens a new one; `toast(text)` is the page's own toast;
 * `maxBytes` is the render body cap.
 */
export function initFolder(callbacks) {
  host = callbacks;
  allButton.setAttribute("aria-pressed", String(showAll));
  updateToggle();
}
