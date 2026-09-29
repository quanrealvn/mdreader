## Writing the math

One dollar sign on each side for math in the middle of a sentence:

```markdown
The area of a circle is $A = \pi r^2$, which follows from the integral.
```

Two dollar signs for a block of its own, centred on the page:

```markdown
$$
\int_{-\infty}^{\infty} e^{-x^2}\,dx = \sqrt{\pi}
$$
```

For a literal dollar sign in a document that also has math in it, put a backslash in
front: `\$40`.

## When a dollar sign is not math

A dollar sign opens math only when the character before it is not a letter or a digit, and
closes it only when the character after it is not one either. So `a$x$b` and `1$x$1` are
left as text, and `$40 or $50` is left alone because the second sign is followed by a
digit. That last case is why prices in a sentence usually survive untouched.

Space directly inside the signs is allowed, but only on both sides at once: `$ x $` is
math and `$x $` is not. The spaces are trimmed off before KaTeX sees them.

## Which version, and where it comes from

KaTeX 0.18.7, bundled with the page along with the twenty font files it needs. Nothing is
fetched from a CDN. It is loaded on first use, so a document without math never downloads
it.

KaTeX covers mathematical notation. It is not a LaTeX document processor: there is no
preamble, no packages, no macro definitions carried between documents, no figures and no
bibliography. KaTeX's own list of supported functions is the reference for what will and
will not work.

## When something does not parse

KaTeX prints the offending source in red where the math would have been, and the rest of
the document renders as normal. That is usually enough to find the mistake: an unmatched
brace, a command from a package KaTeX does not implement, or a stray dollar sign that
closed the math earlier than you meant. When KaTeX gives up on an expression outright,
the source takes its place and the reason is in the tooltip, so hover it.

## It stays text

KaTeX writes HTML with MathML beside it, not an image. The notation can be selected and
copied, a screen reader can read it, and printing to PDF keeps it as text rather than a
bitmap. Math below the fold is typeset as you scroll towards it, so a long paper does not
pay for all of it at once.
