## What renders

The parser is Markdig, configured with the extensions GitHub turns on and no others:

- **Pipe tables**, with the colons in the dash row setting column alignment
- **Task lists**: `- [ ]` and `- [x]`
- **Strikethrough**: `~~gone~~`
- **Footnotes**: `[^1]` in the text and `[^1]: the note` anywhere below
- **Definition lists**: a term on one line, then a colon, three spaces and the definition
  on the next
- **Alert blocks**: a blockquote beginning `> [!NOTE]`, and the other four kinds
- **Autolinks**, so a bare `https://…` becomes a link without the angle brackets
- **Emoji shortcodes** such as `:rocket:`
- **Math** between dollar signs, typeset by KaTeX
- **Mermaid** diagrams in a fenced block marked `mermaid`
- **Heading anchors** built the way GitHub builds them, so `#getting-started` works
- **YAML front matter**, which is recognised and then hidden rather than printed

Syntax highlighting comes from highlight.js and covers its common language set plus
PowerShell, Dockerfile, Batch, F#, nginx and Protocol Buffers.

## What does not render

Worth knowing before you go looking for a bug:

- No `==highlight==`, `++inserted++`, `^superscript^` or `~subscript~`. Those are Markdig
  extras that GitHub does not have, and the point here is to match GitHub. Use `<mark>`,
  `<ins>`, `<sup>` and `<sub>` instead; all four survive the sanitizer.
- No smart quotes or automatic dashes. What you type is what you get.
- No `style` attributes and no `<style>` blocks. They are removed, so a document cannot
  restyle the page it is being read in.
- No page-break control, because Markdown has none and the HTML that could carry one is
  removed with the rest.

## Where the render happens

On the server, not in your browser. The text goes up as JSON, comes back as HTML, and the
page inserts it. Diagrams, math and syntax highlighting are then done in the browser,
which is why those three need JavaScript and the rest of the page does not.

A document can be up to two megabytes. Past that the request is refused with a message
rather than being cut short. There is also a rate limit of sixty renders a minute per
visitor. The editor here debounces to well under that, so you will not meet it by typing.

## The heading anchors

Heading ids follow GitHub's rule: lower-case, spaces to hyphens, punctuation dropped, and
a numeric suffix when the same text appears twice. A link to `#installation` in your
document should land on the same heading here as it does on the repository page.

Ids that a document supplies itself are kept, except that anything starting with `mdr-`
is renamed. That prefix belongs to the page's own elements, and a document does not get
to take one over.
