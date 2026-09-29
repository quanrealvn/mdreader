# MdReader

Markdown, rendered the way GitHub would show it — headings, pipe tables, task lists, alerts,
code highlighting, Mermaid diagrams and KaTeX math — in a browser tab, at
<https://mdreader.online>.

Paste something in, drop a file on the page, or drop a whole folder and read it with a file
tree beside the text. There's a [Markdown cheat sheet](https://mdreader.online/markdown-cheat-sheet)
too, with every piece of syntax next to what it renders to.

## What it does

- GitHub-flavored Markdown: alerts, tables, footnotes, task lists, heading anchors
- Code highlighting, Mermaid diagrams and KaTeX math, each loaded only when a document uses it
- Drop a folder and browse it in a tree; the documents you opened come back next visit
- Side by side: type on the left, see it rendered on the right
- Contents panel you can resize, filter and sort, plus find, zoom, print and PDF export
- Light or dark theme
- Installable as a PWA, and it keeps working offline

Nothing you read is uploaded anywhere you didn't ask for: rendering happens on the server for
text you paste, and the documents you open stay in your browser.

## Running it

You need the .NET 10 SDK.

```
dotnet run --project src/MdReader.Web
```

That serves the reader at <http://localhost:5000> along with the public pages, which are the
Markdown files in `src/MdReader.Web/content` put through the same renderer as everything else.

`docker build -f Dockerfile .` builds the image fly.io deploys, the same way `fly deploy` does.

## Layout

- `src/MdReader.Core` — the renderer: Markdig, the HTML sanitizer's allow-list, heading ids,
  path policy. No UI, no server.
- `src/MdReader.Web` — the server: endpoints, rate limiting, the public pages.
- `web/` — the reader page itself: markup, stylesheets, the scripts that run beside a document.
- `webapp/` — what makes it a web page rather than a desktop window: the browser's `index.html`,
  a `fetch`-based bridge, the service worker and the PWA manifest.

`src/MdReader.Core` and `web/` are shared with the desktop build, which keeps its own copy of
both. A change to either belongs in both copies.

## License

MIT
