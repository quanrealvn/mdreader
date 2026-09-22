// bridge.js — the ONLY module allowed to touch `chrome.webview`. Everything else in the
// page talks to the host through the functions exported here.
//
// `chrome.webview.addEventListener("message", ...)` hands us an event whose `.data` is
// already the parsed JSON object (PostWebMessageAsJson on the host side), so there is no
// JSON.parse here [architecture §7, verified].

export const PROTOCOL = 1;

const handlers = new Map();

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

const webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;

if (webview) {
  webview.addEventListener("message", (event) => dispatch(event.data));
} else {
  console.error("mdr bridge: chrome.webview is not available on this page");
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

/** Sends a web -> host message (`chrome.webview.postMessage`). */
export function send(message) {
  if (!webview) return;
  webview.postMessage(message);
}

/** Sends a web -> host message together with files (drops), via postMessageWithAdditionalObjects. */
export function sendWithFiles(message, files) {
  if (!webview) return;
  webview.postMessageWithAdditionalObjects(message, files);
}

/** Convenience helper for `{type:"log", level, message}`. */
export function log(level, message) {
  send({ type: "log", level, message });
}
