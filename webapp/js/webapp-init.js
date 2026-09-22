// webapp-init.js — classic, synchronous script, loaded right after theme-init.js and before
// first paint. Applies the visitor's saved preferences so there is no flash:
//   data-theme  ?theme=light|dark (this visit only) > saved Light/Dark > the system setting
//   data-style  ?style=colorful|classic (this visit only) > saved style > colorful
//   data-view   "reader" when the active tab (session restore) has a document; else "paste"
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

  function activeTabHasDocument() {
    var raw = read("mdreader.session.v1");
    if (raw) {
      try {
        var data = JSON.parse(raw);
        var tabs = data && data.tabs;
        if (Array.isArray(tabs) && tabs.length > 0) {
          var active = null;
          for (var i = 0; i < tabs.length; i++) {
            if (tabs[i] && tabs[i].id === data.activeId) {
              active = tabs[i];
              break;
            }
          }
          if (!active) active = tabs[0];
          return !!(active && typeof active.markdown === "string" && active.markdown.trim().length > 0);
        }
      } catch (e) {
        return false;
      }
    }
    // Not migrated yet: fall back to the legacy single-document keys.
    return read("mdr.web.view") === "reader" && !!read("mdr.web.doc");
  }

  root.setAttribute("data-view", activeTabHasDocument() ? "reader" : "paste");
})();
