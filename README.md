# LightMarkdownWriter

A light, fast markdown editor for Windows 11 with a live, GitHub-style preview.

**[Download the latest release](https://github.com/lucasriechelmann/LightMarkdownWriter/releases/latest)** · [Website](https://lucasriechelmann.github.io/LightMarkdownWriter/)

![LightMarkdownWriter: editor and live preview side by side](docs/screenshot.png)

## Features

- **Live preview** in GitHub style (light/dark):
  - GFM tables, task lists, footnotes and strikethrough
  - GitHub alerts (`> [!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]`, `[!CAUTION]`)
  - YAML front matter shown as a collapsed box (left out of exports)
  - highlight.js code highlighting with a Copy button on each code block, and Mermaid diagrams
  - math with KaTeX (`$inline$` and `$$block$$`) and emoji shortcodes (`:tada:`)
  - links like `other.md#setup` open the file at that heading; double-click the preview to jump to the source line
  - everything bundled, so it works offline
- **Two-way scroll sync** between editor and preview. It follows source lines, so tall images and diagrams don't make the panes drift apart.
- **Split, editor-only or preview-only** views.
- **Find your way around:** a heading outline (Ctrl+Shift+O), go to line (Ctrl+G), recent files (also in the taskbar jump list), and Ctrl+Shift+T to reopen closed tabs.
- **Tabs that survive restarts.** Closing never asks to save: every tab, its unsaved text, caret and scroll position come back next time.
- **Edits from other programs are detected.** A clean tab reloads in place; a tab with unsaved edits asks you what to do.
- **Editing helpers:**
  - markdown syntax colours
  - bold/italic/code/link/code-block shortcuts
  - lists continue on Enter
  - find & replace with regex
  - drag files in to open them, or drag images in to insert links
- **Status bar** with line and column, word count, line endings and encoding. Click the last two to convert, for example CRLF ↔ LF or adding/removing the UTF-8 BOM.
- **Crash-safe.** An unexpected error saves all tabs and unsaved text, then lets you keep working.
- **Export** as a standalone HTML file or a PDF, or **copy as HTML** that pastes cleanly into Outlook, Word or Teams. Exports are always light-themed.
- **Windows 11 look:** Mica, Fluent controls, and your Windows light/dark theme and accent colour.
- **One small exe.** A single portable file, one window, and a second launch opens its files in the running window.

## Install

There are two downloads on each [release](https://github.com/lucasriechelmann/LightMarkdownWriter/releases):

| Download | What you get |
|---|---|
| `LightMarkdownWriter-Setup.exe` (installer) | .NET is included, so nothing else to install. It installs for your user only (no admin) and adds a Start-menu entry, an uninstaller, and an optional `.md` file association. |
| `LightMarkdownWriter.exe` (portable) | One 15 MB file you can run from anywhere. Needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). |

**Requirements:**
- Windows 10 1809 or later. Built and tested on Windows 11.
- The Microsoft Edge WebView2 Runtime, which comes with Windows 11.

## Keyboard shortcuts

| Action | Shortcut |
|---|---|
| New / Open / Save / Save as | Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S |
| Close tab / reopen closed tab | Ctrl+W or middle-click the tab / Ctrl+Shift+T |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Editor / Split / Preview | Ctrl+1 / Ctrl+2 / Ctrl+3 |
| Go to line / outline | Ctrl+G / Ctrl+Shift+O |
| Bold / Italic / Inline code | Ctrl+B / Ctrl+I / Ctrl+E |
| Link / Code block | Ctrl+K / Ctrl+Shift+K |
| Find / Replace | Ctrl+F / Ctrl+H |
| Next / previous match | F3 / Shift+F3 (Enter / Shift+Enter in the find box) |
| Match case / whole word / regex | Alt+C / Alt+W / Alt+R (in the find box) |
| Replace all | Ctrl+Alt+Enter (in the replace box) |
| Export HTML / Export PDF / Copy as HTML | Ctrl+Shift+E / Ctrl+P / Ctrl+Shift+C |
| Settings | Ctrl+, |

## Opening .md files with it

The installer does this for you; keep its *Open .md and .markdown files* option ticked. With the portable exe, go to **Settings (Ctrl+,) → Files → Register**.

Either way, LightMarkdownWriter is added to *Open with* for `.md` and `.markdown` files. If no other app is the default yet, double-clicking a `.md` file then opens it. This only touches your user account (HKCU) and needs no admin rights. If you move the portable exe, register again.

**If another app already opens `.md` files**, for example Notepad++ or VS Code: Windows lets only you change a default app.
- The installer's last page and the Settings page both offer **Open Default apps**, which goes straight to LightMarkdownWriter.
- There, click **Set default** for `.md`.
- The Settings page shows whether double-click currently opens LightMarkdownWriter.

## Build the installer

`./installer/build-installer.ps1` builds `artifacts/installer/LightMarkdownWriter-Setup.exe`; the version is in its file properties. It needs [Inno Setup 6](https://jrsoftware.org/isinfo.php).

## Build

```powershell
dotnet build
dotnet test
```

Run it with `dotnet run --project src/LightMarkdownWriter`, optionally passing files to open. `--theme=light` or `--theme=dark` overrides the theme setting for that run.

### Publish a portable exe

```powershell
dotnet publish src/LightMarkdownWriter -p:PublishProfile=win-x64
```

This produces a single `artifacts/publish/LightMarkdownWriter.exe` of about 15 MB. It's framework-dependent and ReadyToRun-compiled, with the preview page, libraries and WebView2 loader embedded. Copy it anywhere and run it.

## Where things are stored

Everything lives in `%LOCALAPPDATA%\LightMarkdownWriter\`:

| Path | Contents |
|---|---|
| `settings.json` | Theme, font, font size, word wrap, line numbers |
| `session.json` | Open tabs, caret/scroll positions, window placement, view mode |
| `backups\` | Unsaved text of open tabs; removed once a tab is saved or closed |
| `WebView2\` | The preview's browser cache |
| `recent.json` | Your recent files |
| `crash.log` | Details of unexpected errors, if any happened (no document text) |

To reset the app, delete the folder while the app is closed.

## How it's built

- **WPF on .NET 10** with [WPF-UI](https://github.com/lepoco/wpfui) for the Windows 11 look.
- **[AvalonEdit](https://github.com/icsharpcode/AvalonEdit)** for the editor.
- **[Markdig](https://github.com/xoofx/markdig)** renders the markdown. Every block carries its source line, which is what drives scroll sync.
- **WebView2** shows the preview:
  - [github-markdown-css](https://github.com/sindresorhus/github-markdown-css), [highlight.js](https://highlightjs.org/), [Mermaid](https://mermaid.js.org/) and [KaTeX](https://katex.org/) are embedded in the exe.
  - The preview page's Content-Security-Policy blocks scripts that come from markdown content.

[PLAN.md](PLAN.md) has the design notes and the reasons behind the less obvious choices.

## License

[MIT](LICENSE)
