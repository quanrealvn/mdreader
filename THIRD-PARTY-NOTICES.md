# Third-Party Notices

MdReader bundles the following third-party browser libraries for offline use in the
WebView2 preview page. No files are loaded from a CDN at runtime.

## highlight.js

- Version: 11.12.0 (distributed via the `@highlightjs/cdn-assets` npm package)
- License: BSD-3-Clause
- Upstream: https://github.com/highlightjs/highlight.js
- Path: `web/vendor/highlight/`
  - `highlight.min.js` — core bundle with the "common" language set (defines global `hljs`)
  - `languages/*.min.js` — additional languages: powershell, dockerfile, dos, fsharp, nginx, protobuf
  - `styles/github.min.css`, `styles/github-dark.min.css` — reference stylesheets

## mermaid

- Version: 12.0.0
- License: MIT
- Upstream: https://github.com/mermaid-js/mermaid
- Path: `web/vendor/mermaid/mermaid.min.js` (IIFE bundle, defines global `mermaid`)

## KaTeX

- Version: 0.18.7
- License: MIT
- Upstream: https://github.com/KaTeX/KaTeX
- Path: `web/vendor/katex/`
  - `katex.min.js` — defines global `katex`
  - `katex.min.css` — references fonts via relative `fonts/` URLs
  - `fonts/*.woff2` — only the `.woff2` variants are vendored (`.woff`/`.ttf` fallbacks dropped)

## Octicons

- Version: 19.38.0 (distributed via the `@primer/octicons` npm package)
- License: MIT
- Upstream: https://github.com/primer/octicons
- Path: `web/vendor/octicons/`
  - `alert-note.svg` (`info-16`), `alert-tip.svg` (`light-bulb-16`), `alert-important.svg`
    (`report-16`), `alert-warning.svg` (`alert-16`), `alert-caution.svg` (`stop-16`) — GitHub
    alert icons, referenced from `alerts.css` via `mask-image`
  - `copy.svg` (`copy-16`) — code block copy-button icon, referenced from `code.css`
    via `mask-image`
  - `check.svg` (`check-16`) — copy-button "copied" state (`code.css`) and the
    checked task-list checkbox (`markdown.css`), both via `mask-image`
  - `LICENSE` — upstream MIT license text

## .NET / NuGet components

MdReader also bundles the following .NET libraries and the .NET runtime itself, published
self-contained inside the application folder.

### Markdig

- Version: 1.4.0
- License: BSD-2-Clause
- Upstream: https://github.com/xoofx/markdig
- Used by: `MdReader.Core` (Markdown parsing/rendering pipeline)

### HtmlSanitizer

- Version: 9.2.1039
- License: MIT
- Upstream: https://github.com/mganss/HtmlSanitizer
- Used by: `MdReader.Core` (sanitizing rendered HTML before display)

### AngleSharp

- Version: 1.7.2
- License: MIT
- Upstream: https://github.com/AngleSharp/AngleSharp
- Used by: `MdReader.Core`, as a dependency of HtmlSanitizer (DOM parsing during sanitization)

### AngleSharp.Css

- Version: 1.0.2
- License: MIT
- Upstream: https://github.com/AngleSharp/AngleSharp.Css
- Used by: `MdReader.Core`, as a dependency of HtmlSanitizer

### Microsoft.Web.WebView2

- Version: 1.0.4191.47
- License: Microsoft's WebView2 SDK license (BSD-3-Clause-style, "Microsoft Corporation" license;
  see the package's `LICENSE.txt`)
- Upstream: https://www.nuget.org/packages/Microsoft.Web.WebView2
- Used by: `MdReader.App` (hosting the document preview page)

### Microsoft.Extensions.DependencyInjection

- Version: 10.0.12
- License: MIT
- Upstream: https://github.com/dotnet/runtime (`src/libraries/Microsoft.Extensions.DependencyInjection`)
- Used by: `MdReader.App` (composition root / service registration)

### .NET runtime

MdReader is published self-contained (`--self-contained true`, win-x64), so a private copy of the
.NET 10 runtime is included in the installed application folder rather than relying on a
system-wide install.

- License: MIT
- Upstream notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
