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
- **Collection pages** — browsable index pages for every subdirectory
- **Customizable branding** — site title, footer, primary/secondary colors, custom logo, dark or light navbar
- **YAML configuration** — You can edit directly in the app, or choose to edit the YAML files per artifact directly for fine-grained control.
- **Built-in preview server** — one click to serve and open in your browser, no external tools needed
- **One-click generation** — static HTML output written directly alongside your files

## How it works

1. Open dir2site and click **Choose…** to select your site project folder
2. Fill in site settings (title, colors, logo)
3. Click **Generate Site** — the static site is written to a `_site/` subfolder inside your project folder
4. Click **▶ Start** to launch the preview server, then open it in your browser

## Platform support

| Platform | Architecture |
|---|---|
| Windows | x64 |
| macOS | x64, ARM64 (Apple Silicon) |
| Linux | x64 |

## License

Dir2Site is licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).

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
