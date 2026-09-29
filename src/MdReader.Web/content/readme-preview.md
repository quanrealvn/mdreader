## What this is for

A README is read far more often than it is edited, and the edit that breaks it is usually
small: a table whose dash row has the wrong number of columns, an alert with the wrong
keyword, a `<details>` that swallowed the rest of the file. Those are obvious the moment
you see the rendered page and invisible in the source.

Paste the file here before you commit it. The extensions below are the ones GitHub turns
on, so a table that lines up here lines up there.

## The five alert kinds

GitHub's callouts are a blockquote whose first line names the kind:

```markdown
> [!NOTE]
> Useful information a reader should notice even when skimming.

> [!TIP]
> Optional, and helpful.

> [!IMPORTANT]
> Something a reader needs in order to succeed.

> [!WARNING]
> Needs immediate attention because of the risk.

> [!CAUTION]
> The consequences of doing it anyway.
```

The keyword is case-insensitive and the brackets and exclamation mark are required. A
blockquote that starts with anything else stays an ordinary blockquote, which is the most
common way to get this wrong.

Misspell the keyword and you get a third thing: `> [!WARN]` makes a grey callout titled
"Warn", where GitHub would have left the blockquote alone. If a callout here is grey
rather than coloured, check the spelling.

## What will not look the same

**Relative links and images.** The server has no copy of your repository, so
`./docs/setup.md` and `images/logo.png` have nothing to point at. Relative images are
dropped rather than shown broken, and relative links stay in the text but lead nowhere.
An image with a full `https://` address loads normally, which covers badges. The sample
above has none, so that opening this page does not put you on a badge service's logs.

**GitHub's own additions.** GitHub puts a link icon beside every heading and rewrites
relative paths against the default branch. Neither happens here.

**Raw HTML that GitHub allows and this does not.** Both sides filter HTML; the lists are
not identical. `details`, `summary`, `kbd`, `sup`, `sub`, `picture`, `img` with `width`
and `align`, and `p align="center"` all survive here. `style` attributes, `<style>`
blocks and anything that could run or navigate do not.

## A quick checklist

- Does the table have the same number of columns in the header, the dash row and every
  body row?
- Is every alert's first line one of the five keywords, in brackets, after the exclamation
  mark?
- Does every `<details>` have its `</details>`?
- Do the fenced code blocks all close, and do the language tags name a language?
- Are the badge URLs absolute?
