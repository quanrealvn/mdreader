// icons.js — the small stroke icons the in-document toolbars use (code blocks, diagrams).
//
// They are built as inline SVG rather than loaded as files: these are our own marks, not
// Octicons, and an element built here costs no request, inherits `currentColor` so both themes
// and print work without extra rules, and can't be affected by the CSP's img-src. The alert and
// copy icons keep using the vendored Octicon masks (css/code.css, css/alerts.css).

const SVG_NS = "http://www.w3.org/2000/svg";

/** 24×24 stroke paths, keyed by the name callers ask for. */
const PATHS = {
  "zoom-in": ["M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14M16 16l4.5 4.5", "M8.5 11h5M11 8.5v5"],
  "zoom-out": ["M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14M16 16l4.5 4.5", "M8.5 11h5"],
  reset: ["M20 12a8 8 0 1 1-2.6-5.9", "M20 3v4.5h-4.5"],
  download: ["M12 3v11", "M7.5 10 12 14.5 16.5 10", "M4.5 20h15"],
  wrap: ["M4 6h16", "M4 12h12a3.5 3.5 0 0 1 0 7h-3", "M10.5 16 7.5 19l3 3", "M4 18h2"],
  "line-numbers": ["M4 6h1M4 12h1M4 18h1", "M9 6h11M9 12h11M9 18h11"],
};

/**
 * Builds one icon. Unknown names give an empty (but valid) svg rather than throwing, so a typo
 * can never take a toolbar down.
 * @param {keyof typeof PATHS | string} name
 * @returns {SVGElement}
 */
export function createIcon(name) {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("focusable", "false");
  svg.setAttribute("class", "mdr-icon");
  for (const d of PATHS[name] ?? []) {
    const path = document.createElementNS(SVG_NS, "path");
    path.setAttribute("d", d);
    svg.appendChild(path);
  }
  return svg;
}

/**
 * Builds a toolbar button with an icon and a screen-reader label.
 * @param {string} iconName
 * @param {string} label used for both `aria-label` and the tooltip
 * @param {string} className
 * @returns {HTMLButtonElement}
 */
export function createIconButton(iconName, label, className) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = className;
  button.setAttribute("aria-label", label);
  button.title = label;
  button.appendChild(createIcon(iconName));
  return button;
}
