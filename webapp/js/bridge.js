// bridge.js (web version) — replaces the desktop WebView2 bridge with the same exports
// (ARCHITECTURE §7.3, §15), so main.js, links.js, toc.js, codeblocks.js, enhance.js and
// errorview.js run unchanged in a normal browser tab.
//
// There is no native host here. The "host" is webapp.js, which attaches itself with
// `attachHost(handler)`: every web -> host message the page sends (ready, link, copy,
// rendered, tocVisibilityChanged, retry, log, drop) goes to that handler, and webapp.js
// delivers host -> web messages (theme, render, renderPart, ...) with `deliver(message)`.
// Messages sent before the host attaches (main.js sends `ready` while the modules are
// still being evaluated) are queued and handed over, in order, once it does.

export const PROTOCOL = 1;

const handlers = new Map();
const outbox = [];
let host = null;

function dispatch(message) {
  if (!message || typeof message.type !== "string") return;
  const list = handlers.get(message.type);
  if (!list) return; // unknown types are ignored
  for (const handler of list) {
    try {
      handler(message);
    } catch (err) {
      // A single bad handler must not take down delivery of later messages.
      console.error("mdr bridge: handler for '" + message.type + "' threw", err);
    }
  }
}

function post(message, files) {
  if (!host) {
    outbox.push({ message, files });
    return;
  }
  try {
    // Synchronous on purpose: a `link` opened from a click must stay inside the user gesture.
    host(message, files);
  } catch (err) {
    console.error("mdr bridge: host handler for '" + (message && message.type) + "' threw", err);
  }
}

/**
 * Registers a handler for incoming host -> web messages of the given `type`.
 * Multiple handlers for the same type are all invoked, in registration order.
 */
export function on(type, handler) {
  let list = handlers.get(type);
  if (!list) {
    list = [];
    handlers.set(type, list);
  }
  list.push(handler);
}

/** Sends a web -> host message. */
export function send(message) {
  post(message, null);
}

/** Sends a web -> host message together with files (drops). */
export function sendWithFiles(message, files) {
  post(message, files);
}

/** Convenience helper for `{type:"log", level, message}`. */
export function log(level, message) {
  send({ type: "log", level, message });
}

// ---------------------------------------------------------------------------------
// Web-only: the page-side host (webapp.js)
// ---------------------------------------------------------------------------------

/** Attaches the host: `handler(message, files)` receives every web -> host message, starting with the queued ones. */
export function attachHost(handler) {
  host = handler;
  while (host && outbox.length > 0) {
    const { message, files } = outbox.shift();
    post(message, files);
  }
}

/** Delivers a host -> web message to the page modules, exactly as the desktop host's postMessage would. */
export function deliver(message) {
  dispatch(message);
}
