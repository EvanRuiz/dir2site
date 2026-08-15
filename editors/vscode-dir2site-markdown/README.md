# dir2site Markdown preview — a VS Code extension

Makes VS Code's built-in Markdown preview render the way dir2site does, so what you see while
writing matches the generated site.

```markdown
^^^
![](_media/portrait.jpg){.figure-right width=220}
^^^ Albert Einstein, c. 1947
```

Without it, VS Code shows the `^^^` lines as literal text and ignores `{.figure-right width=220}`.

It also turns on hard line breaks, because dir2site enables Markdig's
`UseSoftlineBreakAsHardlineBreak` — a single newline is a `<br>`, so a hand-wrapped paragraph keeps
its shape instead of reflowing. That affects every Markdown file previewed in the window, not just
dir2site ones, so it can be turned off with `dir2siteMarkdown.hardLineBreaks`.

## Renamed in 0.1.5

This was `dir2site figures` (`dir2site.dir2site-figures`) until it grew past figures. VS Code keys an
extension by publisher and name, so the new one installs alongside the old rather than over it — the
app's install removes the old one, and until it is gone the app keeps offering the update. The
setting moved to `dir2siteMarkdown.hardLineBreaks`; the old key is still honoured for anyone who had
set it, so nobody's choice is quietly discarded.

## Why an extension is needed

dir2site renders Markdown with [Markdig](https://github.com/xoofx/markdig), whose *Figures*
extension provides `^^^` and whose *GenericAttributes* extension provides `{…}`. VS Code's preview
uses [markdown-it](https://github.com/markdown-it/markdown-it) and targets CommonMark, which has
neither — and no widely-used extension supplies them. VS Code does support
[contributing a markdown-it plugin](https://code.visualstudio.com/api/extension-guides/markdown-extension),
which is what this does.

Both rules are written out longhand in `markdown-it-dir2site.js` rather than pulled from
npm, so there is nothing to install or bundle.

## Rebuilding the packaged copy

The app ships `Assets/editors/dir2site-markdown.vsix` and installs it from a button. That file is
committed rather than built, so the normal build needs no Node toolchain — which means it goes
stale the moment anything here changes. After editing:

```bash
scripts/package-vscode-extension.sh
```

then commit the result. `BundledVsCodeExtensionTests` compares the packaged plugin and stylesheet
against these sources byte-for-byte and names the script when they disagree.

## Install

Nothing is published to the marketplace. Either:

**Run it from source** — open this folder in VS Code and press <kbd>F5</kbd>. A second window opens
with the extension loaded; open a Markdown file there and show the preview.

**Install it locally** — package it into a `.vsix` and install that:

```bash
npx @vscode/vsce package
code --install-extension dir2site-markdown-0.1.5.vsix
```

## What it produces

Matching what Markdig emits, so the same CSS applies:

```html
<figure>
<p><img src="_media/portrait.jpg" class="figure-right" width="220" alt=""></p>
<figcaption>Albert Einstein, c. 1947</figcaption>
</figure>
```

Attribute order and self-closing style differ from Markdig's output; nothing downstream depends on
either.

Supported in `{…}`: `.class` (repeatable), `#id`, and `key=value` with or without quotes.

## Limitations

- **The stylesheet is a hand-kept copy** of the figure rules in `Assets/templates/site-css.html`.
  Change one and the other needs the same change; nothing enforces it.
- **Floated figures use `:has()`**, matching the site CSS. VS Code's preview is Chromium-based and
  supports it; a much older build would fall back to an un-floated figure rather than break.
- **Only the figure syntax is covered.** dir2site enables all of Markdig's advanced extensions, so
  other constructs — `:::` custom containers, definition lists, footnotes, abbreviations — still
  won't render in the preview.
- **Relative image paths resolve differently.** dir2site emits article pages one directory deeper
  than the source file and rewrites `../` accordingly; the preview resolves paths relative to the
  file you are editing. Images generally appear correctly here and are correct in the built site,
  but the two are not resolving the same string.
