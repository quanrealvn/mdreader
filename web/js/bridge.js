// bridge.js — the ONLY module allowed to touch the host transport. Everything else in the
// page talks to the host through the functions exported here.
//
// There are two transports, one per platform, and nothing above this file can tell which is
// in use:
//
//   Windows / WebView2 — `chrome.webview.addEventListener("message", ...)` hands us an event
//   whose `.data` is already the parsed JSON object (PostWebMessageAsJson on the host side),
//   so there is no JSON.parse on that path [architecture §7, verified].
//
//   macOS / WKWebView — `window.webkit.messageHandlers.mdreader.postMessage(text)` going up,
//   and the host calling `window.__mdrHostMessage(obj)` through evaluateJavaScript coming
//   down. WKWebView has no structured host message, so the page sends JSON text and the host
//   sends a JavaScript value (JSON already is one), which is the same number of parses.

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
const webkitHost =
  window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.mdreader
    ? window.webkit.messageHandlers.mdreader
    : null;

if (webview) {
  webview.addEventListener("message", (event) => dispatch(event.data));
} else if (webkitHost) {
  // Defined before any document content exists, and locked down, so content can neither replace
  // it nor shadow it with an element id (§7.3, "no implicit globals" / DOM clobbering).
  Object.defineProperty(window, "__mdrHostMessage", {
    value: dispatch,
    writable: false,
    enumerable: false,
    configurable: false,
  });
} else {
  console.error("mdr bridge: no host transport is available on this page");
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
  if (webview) {
    webview.postMessage(message);
  } else if (webkitHost) {
    webkitHost.postMessage(JSON.stringify(message));
  }
}

/**
 * Sends a web -> host message together with files (drops).
 *
 * WebView2 carries the dropped files themselves, with their real paths, through
 * postMessageWithAdditionalObjects. WebKit gives JavaScript a File with no path at all, so on
 * macOS the host takes the drop off the pasteboard before WebKit sees it and this path is never
 * reached; the plain message is still sent, so a drop that did arrive here is at least logged.
 */
export function sendWithFiles(message, files) {
  if (webview) {
    webview.postMessageWithAdditionalObjects(message, files);
  } else {
    send(message);
  }
}

/** Convenience helper for `{type:"log", level, message}`. */
export function log(level, message) {
  send({ type: "log", level, message });
}
