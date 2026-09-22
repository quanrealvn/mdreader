// errorview.js — error view show/hide. Wires the Retry button to send `retry`.

import { send } from "./bridge.js";

const mdrContent = document.getElementById("mdr-content");
const errorSection = document.getElementById("mdr-error");
const titleEl = document.getElementById("mdr-error-title");
const messageEl = document.getElementById("mdr-error-message");
const pathEl = document.getElementById("mdr-error-path");
const retryButton = document.getElementById("mdr-error-retry");

/** Shows the error view with `{title, message, path}` and hides the content article. */
export function showError(info) {
  if (titleEl) titleEl.textContent = info.title || "";
  if (messageEl) messageEl.textContent = info.message || "";
  if (pathEl) pathEl.textContent = info.path || "";
  if (errorSection) errorSection.hidden = false;
  if (mdrContent) mdrContent.hidden = true;
}

/** Hides the error view and reveals the content article again. */
export function hideError() {
  if (errorSection) errorSection.hidden = true;
  if (mdrContent) mdrContent.hidden = false;
}

if (retryButton) {
  retryButton.addEventListener("click", () => send({ type: "retry" }));
}
