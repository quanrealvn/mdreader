## The syntax, in one look

A pipe table is a header row, a row of dashes, and then the body:

```markdown
| Package | Version | Notes            |
| ------- | ------- | ---------------- |
| markdig | 1.4.0   | The parser       |
| katex   | 0.18.7  | Math             |
| mermaid | 12.0.0  | Diagrams         |
```

The dash row is what makes it a table. Without it, the pipes are just pipes.

## Alignment

Colons in the dash row set the alignment of the column they sit in:

```markdown
| Left  | Centre | Right |
| :---- | :----: | ----: |
| a     | b      | c     |
```

A colon on the left aligns left, which is also the default. On the right, right. On both
ends, centred. The number of dashes makes no difference, and neither does whether the
pipes line up. Padding the source into neat columns is for whoever opens the file next;
the renderer ignores it.

## The three things that go wrong

**A different number of columns in each row.** The header decides how many there are.
Extra cells in a body row are dropped and missing ones come out empty, with no error to
tell you.

**A pipe inside a cell.** Escape it as `\|`. A bare pipe starts the next cell, which
shifts every cell after it.

**A line break inside a cell.** There is no way to do it in Markdown: a row has to stay
on one line. `<br>` inside the cell is the usual workaround and it renders here.

## Leading and trailing pipes

Both of these are the same table:

```markdown
| a | b |
| - | - |
| 1 | 2 |

a | b
- | -
1 | 2
```

The outer pipes are optional. Keeping them is worth the two characters: a row that starts
with a pipe is obviously a table row, and an accidental line break in the file is easier
to spot.
