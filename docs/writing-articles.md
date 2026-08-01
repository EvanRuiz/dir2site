<!-- SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors -->
<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
# Writing Markdown articles

Drop a `.md` file into your project folder and Dir2Site turns it into an **Article** page: the body
is rendered to HTML for the site, shown live in the app, and a thumbnail of the rendered page is
generated for collection cards.

This page covers the conventions and the bits beyond plain Markdown.

---

## The basics

- **One file = one article.** `MyArticle.md` becomes a page at `MyArticle/` in the generated site.
- **Metadata lives in the sidecar**, not the body. Dir2Site creates `MyArticle.md.yaml` next to your
  file on first scan:

  ```yaml
  type: markdown
  caption: My Article      # the title shown on cards and in the app
  credit:                  # optional attribution line
  ```

  Edit these in the app or directly in the YAML. The Markdown body holds the article content.

---

## Markdown you can use

Standard Markdown plus the common GitHub-style extensions are supported:

- Headings (`#`, `##`, …), **bold**, *italic*, `inline code`
- Lists (ordered / unordered / task lists), block quotes
- [Links](https://example.com) and autolinks
- Fenced code blocks with language hints
- Tables (pipe tables)
- Footnotes, definition lists, and other "advanced" Markdown extras

Raw HTML in the body is passed through to the published page, but prefer the conventions below — they
render consistently and need no inline styles.

---

## Images

Reference an image with a path **relative to the `.md` file**:

```markdown
![A short description](_media/diagram.png)
```

Plain images render at a sensible size and flow inline with the text.

### Static media: the `_media` folder

Put images and other assets that are **not** standalone artifacts into a folder whose name starts
with an underscore — by convention `_media`:

```
MyArticle.md
_media/diagram.png
_media/portrait.jpg
```

Any `_`-prefixed folder is copied verbatim into the generated site and is never scanned as its own
artifact. Reference its contents with relative paths as shown above.

---

## Figures (images with a caption, optionally floated)

To place an image to the side of the text with a caption — the way a portrait sits beside a bio —
use a **figure block** (`^^^`) instead of raw HTML:

```markdown
^^^
![](_media/portrait.jpg){.figure-right width=220}
^^^ Albert Einstein, c. 1947

The article text flows beside the figure, then continues at full width below it.
```

- The text after the closing `^^^` is the caption (optional).
- `{.figure-right width=220}` sets the alignment and image width:
  - `.figure-right` — floats right, text wraps on the left (portrait beside an intro)
  - `.figure-left` — floats left, text wraps on the right
  - `.figure-center` — centered block, text above and below (hero image, diagram)
  - with no `.figure-*` class, a `^^^` block is simply a centered figure
- `{width=220}` sets the image width in pixels (optional).

### Alternative: figure containers and raw HTML

Both of these also work and produce the same result — use whichever you prefer:

```markdown
:::figure-right
![](_media/portrait.jpg){width=220}

Albert Einstein, c. 1947
:::
```

Raw HTML (e.g. a `<div style="float:right">…</div>`) is supported too, for backward compatibility.
The `^^^` and `:::` forms are preferred — they need no inline styles and render consistently in the
published page, the in-app preview, and the card thumbnail.

---

## Links between pages

Link to other content using paths relative to your `.md` file; Dir2Site adjusts them for the
generated site automatically:

```markdown
See also the [Brownian motion paper](brownian-motion.md).
```

Each article publishes as its own folder, so `brownian-motion.md` becomes `brownian-motion/` in the
generated site. Point the link at either one — the `.md` form is remapped for you, which lets links
keep working in an editor that resolves them against your source files. Any `#anchor` or `?query`
you add is kept.

Absolute URLs (`https://…`), rooted paths (`/…`), and anchors (`#section`) are left untouched.

---

## Where your article shows up

The same `.md` is used three ways:

1. **Published page** — full HTML rendered with real CSS (floats, widths, and styling apply exactly).
2. **In-app preview** — a live render of the body while you work.
3. **Card thumbnail** — a generated image of the rendered article for collection pages.

The thumbnail is an **approximation** of the published page (it has no full CSS engine): a leading
floated figure is shown beside the first paragraph with the rest flowing below, and images are
scaled down. The published page is always the source of truth for exact layout.

---

## Tips

- Keep one lead figure near the top for the best-looking card thumbnail.
- When checking your project into git, ignore the generated output with `/_site/` — do **not** use a
  blanket `_*` rule, or you'll exclude `_media` and other static-asset folders.
