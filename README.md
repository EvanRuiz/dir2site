<div align="center">
  <img src="Assets/app/dir2site-icon.svg" alt="dir2site" width="250"/>
</div>

# Dir2Site

Turn any folder into a polished static website — instantly.

Dir2Site is a open-source cross-platform desktop application that walks your local directory structure and generates a ready-to-serve static site. Point it at a folder full of photos, PDFs, or documents, configure a few settings, and click **Generate**. A built-in preview server lets you review the results immediately in your browser.

**Your filesystem is your CMS.** Metadata is stored as YAML files alongside your content — one file per artifact, human-readable and diff-friendly. Check your whole site into a git or other source-control repository, track every change, and collaborate with standard source-control tools. No database, no lock-in.

> **Alpha Stage:** Dir2Site is under active development. Expect rough edges, missing features, and breaking changes. Contributions and feedback welcome.

## Features

- **Photo galleries** — full-screen browsing with deep-zoom viewer (OpenSeadragon), optional overlay annotations
- **PDF viewer** — embedded document reader (BookReader)
- **Markdown articles** — render `.md` files as clean web pages
- **Videos** — drop in a YouTube `.url` shortcut; plays inline on the collection page
- **Collection pages** — browsable index pages for every subdirectory
- **Customizable branding** — site title, primary/secondary colors, custom logo, dark or light navbar
- **Multi-column footer** — columns of icon-and-label links, edited in the app and stored in `dir2site.yaml`
- **YAML configuration** — Site settings are editable in the app; per-artifact metadata lives in a YAML sidecar you edit in any text editor, and the app shows you what it holds.
- **Built-in preview server** — one click to serve and open in your browser, no external tools needed
- **One-click generation** — static HTML output written directly alongside your files

## How it works

1. Open dir2site and click **Choose…** to select your site project folder
2. Fill in site settings (title, colors, logo)
3. Click **Generate Site** — the static site is written to a `_site/` subfolder inside your project folder
4. Click **▶ Start** to launch the preview server, then open it in your browser

If you have deleted or renamed something since the last run, generating finds the pages and images
it left behind in `_site` and asks whether to remove them. Leaving them means they stay on your
published site. Files starting with a dot — a hand-placed `.htaccess`, a `.well-known/` folder —
are never touched, since dir2site didn't put them there.

## Artifact settings

Every artifact — photo, PDF, article, video — has a YAML file beside it holding its settings.
Dir2Site writes one the first time it sees the file, listing every setting that artifact type
accepts, blank or at its default. A yaml written before a setting existed gains it on the next scan,
so what's in the file is always the whole menu.

That last part means the app adds lines to files you already had — the first scan after an upgrade
will show up as a diff across your project. It only ever *adds* settings, blank, at the end of the
file; values you wrote, your comments and your key order are left exactly as they were, and the app
says how many files it touched when the scan finishes.

| Setting | What it does | Default |
|---|---|---|
| `caption` | The title on the card and the page | the filename, tidied up |
| `credit` | Attribution line under the caption | blank |
| `date` | Shown under the credit; free text, so `1890` and `March 1890` both work | blank |
| `url` | An external source or reference to link out to | blank |
| `url-text` | The words of that link; blank uses the address itself | blank |
| `home` | Also show this artifact on the home page — see [below](#featuring-an-item-on-the-home-page-home-true) | `false` |
| `parent-cover` | Make this the picture for its folder's card — see [below](#choosing-a-folders-picture-parent-cover-grandparent-cover) | blank |
| `grandparent-cover` | The same, one level further up | `false` |

Some types add their own: `photographer` on a photo, `author` and `publishOriginal` on a PDF,
`provider`, `videoId` and `start` on a video.

Anything else you find in a yaml — `id`, `preview`, `previewLarge`, `image`, `overlays` — belongs to
Dir2Site, which fills it in and overwrites it as the site is generated. Leave those alone.

A misspelled setting is reported as a warning when you generate, because YAML has no way of knowing
that `parentcover` was meant to be `parent-cover` and would otherwise just sit there doing nothing.

### Linking to a source (`url`, `url-text`)

An artifact often came from somewhere — a catalogue entry, an archive record, the page you found it
on. Put the address in `url` and the words in `url-text`:

```yaml
type: photo
caption: Portrait of a Stranger
credit: Unknown photographer
url: https://example.org/archive/1890/portrait
url-text: See the archive record
```

The link appears on the artifact's own page, under the credit line, with an "opens in a new window"
icon and in your site's secondary color. Leave `url-text` blank and the address itself is the link
text — filling in only the address never means no link at all.

Cards don't carry the link; the card's one job is to take you to the artifact.

Videos differ in where the link goes, not in how it works: a video has no page of its own, so its
link sits on the card. A blank `url` there means the `.url` shortcut's own address, which stays
opt-in — the player already offers YouTube's — so a video with neither `url` nor `url-text` carries
no link. See [Adding videos](docs/adding-videos.md).

An address is published only if it's one a browser would follow to another page — `http`, `https`,
`mailto` or somewhere within your own site. Anything else is left off, rather than turned into a
link that runs when clicked, and generating says which file it was so a perfectly innocent `ftp://`
doesn't just quietly vanish.

## Markdown articles

Drop a `.md` file into your project folder and it becomes an article page. Edit the body in any
editor; metadata (caption, credit, date) lives in the YAML sidecar like every other artifact. The
Markdown is rendered to HTML for the site, previewed live in the app, and a thumbnail of the
rendered page is generated for collection cards.

### Folders holding a single item

A folder with exactly one artifact in it publishes that artifact as the folder's own page, rather
than a collection page whose only content is one card. So `-About/Our Story.md` is served at
`/About/` — clicking **About** in the menu shows the article itself.

Two things are deliberately left alone: a folder holding only a video (videos play inline and have
no page of their own, so there is nothing to promote), and a folder holding only another folder
(collapsing chains of folders gets surprising quickly).

### Choosing a folder's picture (`parent-cover`, `grandparent-cover`)

A folder's card is illustrated by whichever artifact inside it sorts first, which is rarely the one
that says what the collection is. Add `parent-cover: true` to an artifact's YAML to choose it
instead:

```yaml
type: photo
caption: The one that says what this is
parent-cover: true
```

It also becomes the folder page's `og:image`, so a shared link shows the same picture. Only the
folder the artifact sits in is affected — that's what "parent" names.

A folder that holds nothing but sub-folders has no artifacts of its own to choose from. Mark one a
level deeper with `grandparent-cover: true` and it illustrates that folder too:

```
Trips/                      card shows Cherry Blossom.jpg
Trips/Japan/Cherry Blossom.jpg    grandparent-cover: true
```

A `grandparent-cover` never outranks a real direct child, so a folder with its own photos still
shows one of those.

(`cover: true` is the older spelling of `parent-cover` and still works. Where a project carries
both, `parent-cover` decides — including when it says `false`.)

### Featuring an item on the home page (`home: true`)

Add `home: true` to an artifact's YAML and it also gets a card on the home page, wherever in the
tree it actually lives. The card links to the artifact's real page — a video plays in place, as it
does anywhere else — and the artifact keeps its ordinary card in its own folder, so nothing moves.
Featured cards come after the home page's own contents.

`parent-cover`, `grandparent-cover` and `home` are set in the YAML sidecar rather than in the app.
The app shows an artifact's metadata but doesn't edit it — the sidecar is where per-artifact
settings live, for these as for `publishOriginal` and everything else.

### Menu-only sections (`-`-folders)

A folder whose name starts with a hyphen (e.g. `-About`) appears in the menu but not as a card on
its parent page, and it sits after the ordinary folders in the menu. Use it for the sections a site
needs but isn't presenting — About, Contact, Colophon.

```
Photographs/          shown in the menu and as a card
Documents/            shown in the menu and as a card
-About/               shown in the menu only, last
```

The hyphen is an instruction to the generator, not part of the name: `-About` is published at
`/About/` and shows as "About" everywhere a visitor can see.

### Footer-only sections (`--`-folders)

Double the hyphen and the menu entry goes too: a `--`-folder gets its page and nothing else — no
card, no nav. Use it for the pages nobody browses to and everybody expects to find at the bottom of
the page: privacy, terms, credits.

The recommended arrangement is one `--Footer/` folder holding all of them, rather than a marked
folder each:

```
--Footer/Privacy.md      published at /Footer/Privacy/, linked from the footer
--Footer/Use.md          published at /Footer/Use/
--Footer/Credits.md      published at /Footer/Credits/
```

Nothing enforces that shape — the marker works on any folder — but it keeps the project root
readable and puts the footer's pages where someone looking for them would look. `--Footer/` itself
still publishes a collection page at `/Footer/` listing them, which nothing links to and which is
out of both the menu and the cards.

Both hyphens are stripped, so `--Footer` is published at `/Footer/`. As with the other markers, two
folders that would publish to the same address are reported by Generate Site rather than one
quietly overwriting the other.

### Folders featured on the home page (`+`-folders)

A folder whose name ends in a plus (e.g. `Newspapers+`) also gets a card on the home page, however
deep it sits. It is the folder-shaped counterpart of `home: true`, and the way to say which folders
in the tree are worth a direct link from the front door.

```
Archive/Newspapers+/    a card in Archive, and a card on the home page
```

Like the hyphen, the plus is stripped everywhere a visitor can see: the folder is published at
`/Archive/Newspapers/`, and its breadcrumbs still show the full path it really lives at. The two
markers are independent, so `-Newspapers+` is a folder reachable from the menu and the home page
but not from its parent's listing.

Because the markers are stripped, `Newspapers+` and a plain `Newspapers` beside it would publish to
the same address and one would overwrite the other. Generate Site reports that rather than letting
a folder's pages vanish quietly.

### Breadcrumbs on cards

A card featured on the home page carries the folders its item sits in — "Trips › Japan › Kyoto" — on
a small quiet line above its name, using the same labels as that item's page shows in its breadcrumb
bar. It is what makes such a card legible: something pulled onto the home page from three levels
down otherwise arrives with nothing but its own name. This is not optional and needs no setting.

Ordinary cards don't show a trail, because on a folder page it would be the breadcrumb bar directly
above them, repeated once per card. If you want it anyway — every card self-describing wherever it
appears — tick **Card Titles → Include Breadcrumbs** in Site Settings. The setting is stored in
`dir2site.yaml` as `cardBreadcrumbs` and is off unless it says otherwise. Top-level cards never show
a trail: the home page is their only ancestor.

### Static media (`_media` and other `_`-folders)

To include images or other assets that should **not** become artifacts of their own, put them in a
folder whose name starts with an underscore (e.g. `_media`). Any `_`-prefixed folder is copied
verbatim into the generated site and is never scanned for artifacts. Reference it from your Markdown
with a path relative to the `.md` file:

```
MyArticle.md
_media/myfigure.webp
```

```markdown
![My figure](_media/myfigure.webp)
```

### Figures (floated images with captions)

To place an image to the side with a caption — the way a portrait sits beside an article — use a
fenced **figure container** rather than raw HTML. It styles cleanly in the generated site and is
approximated in the card thumbnail:

```markdown
^^^
![](_media/portrait.jpg){.figure-right width=220}
^^^ Albert Einstein, c. 1947
```

Use `.figure-right`, `.figure-left`, or `.figure-center`; `{width=…}` sets the image width — which
is honoured as written, where a figure without one is capped at 45% of the column — and the
text after the closing `^^^` is the caption. A `:::figure-right … :::` container and raw HTML also
work — the `^^^` / `:::` forms are preferred as they need no inline styles and render consistently.

See **[Writing Markdown articles](docs/writing-articles.md)** for the full reference.

## The footer

Every page ends with the same footer. Out of the box that is one line — the **Footer text** field in
Site Settings, which allows HTML so it can hold a link — but it can also carry columns of links,
edited with the **Footer…** button beside it and stored in `dir2site.yaml`:

```yaml
footerColor: "#101c32"
footerItems:
  - column: 1
    icon: bi-youtube
    iconColor: "#ff0000"
    iconBackground: "#ffffff"
    title: Watch on YouTube
    link: https://example.com/channel
    note: 12,000+ views
  - column: 2
    icon: bi-info-circle
    title: About
    link: -About/Our Story.md
  - column: 3
    icon: bi-lock
    title: Privacy
    link: --Footer/Privacy.md
```

Rows sharing a `column` are stacked together and columns run left to right, in the order the rows
are written. An empty column number closes up rather than leaving a gap, so numbering 1 and 3 gives
two columns.

**`link`** takes one of three forms, told apart by how it starts:

| Written as | Means |
| --- | --- |
| `https://…`, `http://…`, `mailto:…` | an address off the site; opens in a new tab |
| `/privacy/` | a path within the site, for a page dir2site didn't generate |
| `-About/Our Story.md` | a file or folder in the project, resolved to wherever it publishes |

The third form is the one to reach for: it follows the artifact, so a page published at a folder's
own address because it is the only thing in that folder is still linked correctly. A link naming
something that isn't in the project is reported by Generate Site and left out of the footer.

**`icon`** is a [Bootstrap Icons](https://icons.getbootstrap.com/) name, with or without its `bi-`
prefix, and `iconColor` tints it.

**Brand icons color themselves.** `icon: bi-youtube` alone renders the real mark — red, with a
white play triangle — and the same goes for Facebook, Instagram, LinkedIn, Mastodon, GitHub, Bluesky
and the rest of Bootstrap's brand set. You don't have to know a brand's hex code, and you can't
accidentally ship a logo that looks wrong.

Each mark is filled the way its own drawing needs. YouTube's triangle is cut out of the middle of a
solid badge; Facebook's "f" runs through the bottom of its circle, so the white behind it has to be
circular to cover the letter without spilling past the curve; and marks like X, TikTok and Bluesky
are silhouettes with nothing cut out of them at all, so they get their color and nothing behind.

Naming either color yourself turns that off, so a mark that should match the rest of the column
rather than shout is one line:

```yaml
  - icon: bi-youtube
    iconColor: "#999999"    # deliberately muted; no brand fill applied
```

**`iconBackground`** is what makes the above work, and is there if you need it directly. Bootstrap's
brand icons are a single shape with the inner symbol cut out — `bi-youtube` is a rounded rectangle
whose play triangle is a *hole* — so on a dark footer that triangle would show the band color.
`iconBackground` fills the cut-out, and the glyph itself masks everything around it. Ordinary
single-color icons don't need it.

**`note`** is a caption line under the link — a maintainer's name, a view count. It is plain text;
`footer:` remains the one field that takes HTML, which is where the copyright line with its `<br>`
belongs.

**`footerColor`** is the band's background, defaulting to the primary color so the footer matches
the navbar. Text and link colors follow from it: a dark color gets light text, a light one dark.

Pages that only belong in the footer want a [`--`-folder](#footer-only-sections---folders), which
keeps them out of the menu as well as the cards.

## PDFs

Drop a `.pdf` into your project folder and it gets a page of its own with an embedded reader
(BookReader): each page is rendered to an image at generation time, and the reader pages through
those. The source PDF itself is not published, so visitors read the document but don't get the file.

### Offering the file (`publishOriginal: true`)

To publish the PDF itself as well, set `publishOriginal` in its yaml:

```yaml
type: pdf
caption: The Riverbend Type Specimen
publishOriginal: true
```

The source file is copied into the site next to the artifact's page, and the page gains a
**Download PDF** link. With `publishOriginal: false` — the default — no copy is made and no link
appears; if the file had already been published, the next generate offers to take it back down.

## Videos

Drop a Windows internet shortcut (`.url`) pointing at a YouTube video into your project folder and
it becomes a Video artifact:

```ini
[InternetShortcut]
URL=https://www.youtube.com/watch?v=AbCdEfGhIjK&t=1m30s
```

The video id and start offset are read from the link, the poster frame is downloaded for the card
and folder thumbnails, and — unlike every other artifact type — the video gets no page of its own:
it plays **inline on the collection page**. When it ends, the poster returns over the player instead
of YouTube's related-video end screen. A `.url` pointing anywhere other than a supported video is
ignored rather than turned into a broken card.

See **[Adding videos](docs/adding-videos.md)** for the full reference.

> **Tip:** when checking your project into git, ignore the generated output with `/_site/` — do
> **not** use a blanket `_*` rule, or you'll exclude `_media` and other static-asset folders.

## Platform support

| Platform | Architecture |
|---|---|
| Windows | x64 |
| macOS | x64, ARM64 (Apple Silicon) |
| Linux | x64 |

## License

Dir2Site is licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).

Third-party open-source components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Build from source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/EvanRuiz/dir2site
cd dir2site
dotnet run
```

To publish a self-contained release build:

```bash
# macOS (Apple Silicon)
dotnet publish -r osx-arm64 -c Release

# Windows
dotnet publish -r win-x64 -c Release

# Linux
dotnet publish -r linux-x64 -c Release
```

## Demos / Test Data

Demo and test sites (with source projects) are in the [dir2site-demos](https://github.com/EvanRuiz/dir2site-demos) repository.

### Demo: Famous Physicists

- Preview Generated Static Site: [Famous Physicists Demo](https://evanruiz.github.io/dir2site-demos/physicists/_site/)
- Source Project Directory: [Project Directory](https://github.com/EvanRuiz/dir2site-demos/tree/main/docs/physicists)

> Note: Demo content (images, biography, papers) was generated or collected by AI for testing purposes. Any inaccuracies are unintentional — please open an issue to report them.
