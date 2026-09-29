## Opening one

Drop a `.json` file onto the reader, or use **Open file**. The name settles it: `.json`,
`.jsonl`, `.ndjson` and `.geojson` are read as JSON and everything else as Markdown. There
is nothing to switch on first.

Pasted text has no name, so there is a **Markdown / JSON** control beside the Read button and
you say which it is. There is deliberately no bar that appears to ask "this looks like JSON,
read it as JSON?" — a document can be valid JSON and perfectly good Markdown at the same time,
so a guess would be wrong often enough to be worth dismissing, and a bar people dismiss is a
bar that is in the way. The control is always there and it is never wrong.

The tree opens three levels deep. Below that, branches start folded with their size in the
summary — `{7 keys}`, `[240 items]` — so a folded branch still tells you something. Each level
is a colour, and each one is indented under a rail, so depth is legible without counting
brackets. Top-level keys become entries in the contents panel, which is how you get back to
`dependencies` in a file with four hundred lines above it.

## What it will not do to your data

**Numbers are shown as they were written.** `1.50` stays `1.50`, `1e3` stays `1e3`, and a
nineteen-digit id stays every one of its digits. A viewer that parses to a floating-point
number and prints the result has quietly changed the data, and that is the most common way
these tools lose your trust.

**Key order is kept.** The specification says objects are unordered; every file a person reads
is ordered, and the order was somebody's decision. Duplicate keys are kept too — they are
legal, they usually mean a bug in whatever wrote the file, and dropping one hides it.

**Strings are not interpreted.** No dates are reformatted, no units are guessed at, and a
string that looks like a URL does not become a link. A hostile file should not be able to put
something clickable in front of you by writing a string, so it cannot.

Strings longer than about five hundred characters are cut, with a count of what was left out
after them, so that one enormous base64 blob does not become the whole page.

## When it is not valid

You are told where. The line and the column, the offending line printed with a caret under the
character, and a sentence in plain words: a trailing comma, single quotes where double quotes
belong, an unquoted property name, a comment — each is named, with the fix. Under that is the
document's own text, unwrapped, so the line numbers still count.

A file that is almost valid is described rather than simply refused. A `.jsonl` file, or a
`.json` holding one value per line, is recognised and said out loud instead of failing at the
second line with "expected end of data".

## Limits

A document sent from the browser is capped at 512 KB, the same cap Markdown has. A file whose
tree would be bigger than the page can hold — a small file can nest and repeat its way into a
great deal of it — is shown as its own text with a line saying why, rather than being cut short
halfway down.

Printing works the way it does for Markdown: **PDF** opens your browser's print dialog, and
folded branches are opened for the sheet, because a printout of `{7 keys}` is not a printout of
anything. The document goes to MdReader's server to be rendered and comes back as HTML; it is
held in memory for that request and never written to disk.
