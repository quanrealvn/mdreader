## How it works

Two steps, and the second one is your browser's.

1. The Markdown is rendered to a page, the same way every other page on this site is.
2. **Save as PDF** opens the browser's print dialog with only that page on the sheet. Pick
   "Save as PDF" as the destination and choose where it goes.

There is no conversion service and no queue. Your text reaches MdReader's server, which
renders it and sends HTML back; the PDF itself is written by your browser, on your
machine. Nothing about the document is stored on the way.

## What survives

**Syntax colours.** Code keeps its highlighting. The grey block behind it only prints if
you switch on the "Background graphics" option, which browsers keep off by default.

**Diagrams.** A Mermaid diagram is an SVG, so it prints as a drawing rather than a
picture of one. Zoom into the PDF and the lines stay sharp. The button redraws them in the
light palette first, so a dark-themed screen still gives a readable page.

**Math.** KaTeX writes HTML, not images, so the notation stays selectable text in the
PDF.

**Page breaks in sensible places.** Code blocks, tables and alert boxes are asked not to
split across two sheets, which browsers honour whenever the block fits on one.

## What you cannot control

Page size, orientation and margins are whatever you choose in the print dialog. MdReader
sets none of them, which means your browser's defaults apply and there is no setting here
to override them.

There is no way to force a page break. Markdown has no syntax for one, and the HTML that
could carry one is removed by the sanitizer along with everything else that can carry a
`style` attribute. Long documents break where they break. Splitting the file at a heading
and printing the parts separately is the only reliable way to control it.

## Doing it from the desktop app

The Windows app has two ways out. Ctrl+P opens the same print dialog as here, and
Ctrl+Shift+S ("Export as PDF") skips it: pick a file name and the PDF is written straight
out, with the code-block backgrounds already switched on. The app does that when the file
is on your disk and you would rather not paste it anywhere.
