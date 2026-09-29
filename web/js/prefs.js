// prefs.js — the few reading preferences the page remembers for the reader rather than for a
// document: code wrapping and code line numbers (§7.3).
//
// They live in localStorage, not in AppSettings, because the desktop shells and the web version
// all serve the page from one origin and a per-user WebView2 profile, so one store covers every
// host without a protocol message. Storage is not guaranteed: it throws in a private window and
// with site data blocked, and the macOS shell's web view uses a non-persistent store (§8.6), so
// every access is guarded and a failure just means "the default". Nothing here may ever throw,
// because it runs during render.

const PREFIX = "mdr.";

/** Preference keys. Values are stored as "1"/"0" so a corrupt value reads as the default. */
export const CODE_WRAP = "code.wrap";
export const CODE_LINE_NUMBERS = "code.lineNumbers";

/** @type {Set<(key: string, value: boolean) => void>} */
const listeners = new Set();

/** @type {Storage | null} captured once; null when storage isn't usable at all */
const store = openStore();

function openStore() {
  try {
    const storage = window.localStorage;
    // Reading is not enough: Chromium hands out a Storage object that throws on write when site
    // data is blocked, so prove a round trip before trusting it.
    const probe = PREFIX + "probe";
    storage.setItem(probe, "1");
    storage.removeItem(probe);
    return storage;
  } catch {
    return null;
  }
}

/**
 * Reads a boolean preference.
 * @param {string} key one of the exported key constants
 * @param {boolean} fallback returned when the value is missing, unreadable or not "1"/"0"
 * @returns {boolean}
 */
export function getFlag(key, fallback = false) {
  if (!store) return fallback;
  try {
    const raw = store.getItem(PREFIX + key);
    if (raw === "1") return true;
    if (raw === "0") return false;
    return fallback;
  } catch {
    return fallback;
  }
}

/**
 * Writes a boolean preference and notifies this page's listeners. A write that fails (quota,
 * blocked storage) is not an error: the preference simply doesn't survive the session.
 * @param {string} key
 * @param {boolean} value
 */
export function setFlag(key, value) {
  try {
    store?.setItem(PREFIX + key, value ? "1" : "0");
  } catch {
    // Ignored on purpose; the in-page change below still takes effect.
  }
  notify(key, value);
}

/**
 * Subscribes to preference changes, whether they were made on this page or in another view
 * sharing the same profile (a second tab in the desktop app, a second browser tab on the web).
 * @param {(key: string, value: boolean) => void} listener
 */
export function onFlagChange(listener) {
  listeners.add(listener);
}

function notify(key, value) {
  for (const listener of listeners) {
    try {
      listener(key, value);
    } catch (err) {
      console.error("mdr prefs: listener for '" + key + "' threw", err);
    }
  }
}

// Another view of the same origin changed a preference. `storage` never fires for the page that
// made the change, so this only ever carries other views' writes.
window.addEventListener("storage", (event) => {
  if (event.storageArea !== store || typeof event.key !== "string") return;
  if (!event.key.startsWith(PREFIX)) return;
  const key = event.key.slice(PREFIX.length);
  if (key !== CODE_WRAP && key !== CODE_LINE_NUMBERS) return;
  notify(key, event.newValue === "1");
});
