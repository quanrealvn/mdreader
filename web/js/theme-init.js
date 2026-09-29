// theme-init.js — classic, synchronous script. Sets `data-theme` from the page's
// `?theme=` query parameter (default: light) before first paint, so there is no
// light->dark flash while the module scripts (which are deferred until after
// parsing) spin up.
//
// Vendor globals vs. DOM clobbering (§7.3): declaring these here, before any document
// content exists, means a content heading like `## Exports` (which gets id="exports")
// can't shadow `window.exports`/`window.module`/`window.define` via DOM clobbering.
// Without this, a UMD library (KaTeX) probing for `typeof exports === "object"` /
// `typeof define === "function"` could attach itself to that <h2> element instead of
// `globalThis`, and silently never render.
var exports, module, define;

(function () {
  var params = new URLSearchParams(window.location.search);
  var theme = params.get("theme") === "dark" ? "dark" : "light";
  document.documentElement.setAttribute("data-theme", theme);
})();
