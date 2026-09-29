## What it does

MdReader turns Markdown into the page GitHub would show you. Headings, pipe tables, task
lists, footnotes, alert blocks, syntax-highlighted code, Mermaid diagrams and KaTeX math
all render.

The reader itself is at the root of this site. Paste a document, or open a `.md` file from
your disk, and read it. There is no account, and nothing is installed to do it.

{{figure:reader}}

## Where your text goes

The browser sends what you paste to the server, the server renders it, sends the HTML
back, and drops it. Nothing is written to disk. The one log line a successful render
leaves behind records how many characters came in, how many came back, and how long it
took. No part of the document goes into it.

Your browser keeps your open tabs in local storage, so they are still there when you come
back. That copy never leaves your machine, and clearing site data removes it.

If you would rather nothing left your machine at all, the Windows app renders on your own
machine. The only requests MdReader makes are for images a document links to, the same as
a browser would.

## Raw HTML is kept, and then cleaned

Real READMEs use HTML: a logo centred with `<p align="center">`, a collapsed section in
`<details>`, keys in `<kbd>`, a light and dark logo pair in `<picture>`. Refusing raw HTML
would break a lot of documents that are otherwise fine, so MdReader keeps it and puts the
whole rendered page through an allow-list sanitizer afterwards.

Nothing that can run or navigate survives that: `script`, `style`, `iframe`, `object`,
`form`, every `on*` attribute, `javascript:` in a link. What is left still looks the way
the author meant it to. The browser's content security policy blocks script from the
document as a second layer, so a miss in the sanitizer would not get to run either.

## The desktop app

MdReader started as a Windows app, and that is still where it does the most.
Double-click a `.md` file and it opens in a tab. A file reloads when you save it, so the
window can sit beside your editor. Tabs come back the next time you open it. There is a
contents panel you can resize, filter and sort, and a split view with the source on one
side. It also has find, zoom, printing and PDF export.

{{figure:windows}}

The desktop app is not something you can download here at the moment. The browser version
is the public one, and it renders every document the same way.

## The same renderer on both sides

The browser version and the app share the parser, the sanitizer, the stylesheet and the
JavaScript that draws diagrams and typesets math. The only difference is where the render
happens. In the app it is a call inside the process; on the web it is one request to the
server. That is why a document you read here is a fair preview of what the app will show,
and why a bug fixed in one is fixed in the other.

## The pages around it

The pages listed below each take one thing MdReader does and show it working: a sample
document in an editor, already rendered beside it, and notes on where the edges are. They
are Markdown files put through the same renderer as everything else, so if the renderer
breaks they say so on every URL rather than only in a test.
