// site-init.js — classic, synchronous script in the <head> of every public page, loaded
// before first paint. It does one thing: put the visitor's theme on <html> before the
// stylesheets are applied, so someone who chose Dark in the reader never sees a white
// page flash first. The key is the reader's own (`mdr.web.theme`), so the choice carries
// across the whole site.
//
// Vendor globals vs. DOM clobbering (§7.3): these are declared here, before any document
// content exists, so that a heading in a rendered document (which becomes an element with
// that id) cannot shadow `window.exports`/`window.module`/`window.define`. Without it, a
// UMD library — KaTeX — probing for `typeof exports === "object"` could attach itself to
// that element instead of `globalThis` and silently never render.
var exports, module, define;

(function () {
  // Scripting is on, and this runs before the first paint. site.css uses the attribute to
  // reserve room for the parts a later module fills in, so revealing them moves nothing.
  // Without scripting it is never set and those parts stay out of the layout entirely.
  document.documentElement.setAttribute("data-js", "");

  var theme = null;

  try {
    var saved = window.localStorage.getItem("mdr.web.theme");
    if (saved === "light" || saved === "dark") theme = saved;
  } catch (e) {
    // Storage blocked (private window, site data off): fall through to the system setting.
  }

  if (!theme) {
    theme = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  document.documentElement.setAttribute("data-theme", theme);
})();
