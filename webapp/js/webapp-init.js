// webapp-init.js — classic, synchronous script, loaded right after theme-init.js and before
// first paint. Applies the visitor's saved preferences so there is no flash:
//   data-theme  ?theme=light|dark (this visit only) > saved Light/Dark > the system setting
//   data-style  ?style=colorful|classic (this visit only) > saved style > colorful
//   data-view   "reader" when the visitor left while reading; else "paste"
(function () {
  var root = document.documentElement;

  function read(key) {
    try {
      return window.localStorage.getItem(key);
    } catch (e) {
      return null; // storage disabled or blocked
    }
  }

  var params = new URLSearchParams(window.location.search);

  var theme = params.get("theme");
  if (theme !== "light" && theme !== "dark") {
    var savedTheme = read("mdr.web.theme");
    if (savedTheme === "light" || savedTheme === "dark") {
      theme = savedTheme;
    } else {
      theme = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    }
  }
  root.setAttribute("data-theme", theme);

  var style = params.get("style");
  if (style !== "colorful" && style !== "classic") {
    style = read("mdr.web.style") === "classic" ? "classic" : "colorful";
  }
  root.setAttribute("data-style", style);

  var view = read("mdr.web.view") === "reader" && read("mdr.web.doc") ? "reader" : "paste";
  root.setAttribute("data-view", view);
})();
