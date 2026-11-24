# LightMarkdownWriter

A light markdown editor for Windows with a live, GitHub-style preview. It's designed for Windows 11 and also supports Windows 10 1809+.

- Language and UI: WPF on .NET 10, with WPF-UI for the Fluent/Mica look.
- Editor: AvalonEdit. Markdown → HTML: Markdig. Preview: WebView2.
- Shipped as one portable, framework-dependent exe. A GitHub Pages site lives in `docs/`.
- Solo project. MIT license. Repo: github.com/lucasriechelmann/LightMarkdownWriter.

## Projects

| Path | What it is |
|---|---|
| `src/LightMarkdownWriter/` | The app (WinExe). Everything runs in this one project. |
| `tests/LightMarkdownWriter.Tests/` | xUnit tests for non-UI logic. It references the app project and targets `net10.0-windows` with WPF. |
| `docs/` | GitHub Pages site (`index.html`, favicon, README screenshot). Served from `main` `/docs`. |
| `tools/make-icon.ps1` | Regenerates `src/LightMarkdownWriter/Assets/app.ico` and the 16–256 px sizes. |
| `PLAN.md` | **Design log.** The original plan plus the reasons behind non-obvious decisions. Keep it, and add new decisions to it. |

Inside the app project:
- `ViewModels/`: MVVM state for tabs and documents.
- `Views/`: preview, find bar and settings window.
- `Editing/`: AvalonEdit add-ons.
- `Services/`: rendering, export, session, settings, file association, single instance.
- `Assets/Preview/`: the preview web page and vendored JS/CSS, embedded into the exe.

## Standing rules

- **Search before creating.** Check `Services/`, `Editing/` and the view models for an existing helper before adding one. For example, use `MediaTypes.ForPath` for content types, `PreviewAssets` for embedded web assets and `ExportService.EmbedLocalImages` for data URIs.
- **Windows 10 1809+ stays supported** (as the README says). The app is designed and tested on Windows 11, and Windows 10 keeps working with WPF-UI's own fallbacks (no Mica). Don't introduce Windows 11-only APIs without a fallback.
- **Keep it light.**
  - No new runtime dependencies without a reason worth writing in PLAN.md.
  - Web libraries are vendored, at pinned versions, in `Assets/Preview/vendor/`. They're never loaded from a CDN at runtime.
- **The preview is a native window (HWND airspace).**
  - Nothing WPF can be drawn over it: no popups, toasts or overlay dialogs on top of the preview.
  - Use the command bar, a separate window, or the editor side instead.
- **Untrusted markdown.** Keep the preview's CSP (no inline scripts), and keep the `doc.local` path-containment check in `PreviewView`.
- **Never lose user text.** Changes to session, backup or save logic need a test or a verified run where you close and reopen the app.
- **Shortcuts are defined once.** They live in `Window.InputBindings` or come from `AddKeyBinding(...)`. `MainWindow.OnPreviewKeyDown` replays them while the preview has focus, so don't add a second copy.

## Build, test, release

```powershell
dotnet build
dotnet test
dotnet run --project src/LightMarkdownWriter -- file.md   # --theme=light|dark|system overrides the theme setting
```

**Verifying a change:** `dotnet test` must pass. UI changes are checked in the running app.

**Release** (a manual process for now):
1. Bump `<Version>` in `src/LightMarkdownWriter/LightMarkdownWriter.csproj`, then commit and push.
2. Run `dotnet publish src/LightMarkdownWriter -p:PublishProfile=win-x64`. This produces `artifacts/publish/LightMarkdownWriter.exe`.
3. Create `SHA256SUMS.txt` with the format `<sha256>  LightMarkdownWriter.exe`.
4. Smoke-test the exe from an empty folder.
5. Tag the commit the exe was built from: `git tag -a vX.Y.Z <commit>`. The exe's ProductVersion carries `+<commit>`.
6. Push the tag, then run `gh release create vX.Y.Z <exe> SHA256SUMS.txt --notes-file ...`.

The site's download button uses `/releases/latest/download/LightMarkdownWriter.exe`, so the asset must keep that exact file name.

**Git:** commit straight to `main` with Conventional Commits (`feat:`, `fix:`, `docs:`…). No Co-Authored-By trailers.

## Where to look

- Before adding or changing a data entity (document, session, settings), a service, or how the app, preview page and WebView2 talk to each other, read `.claude/knowledge/architecture.md`.
- Before writing new code in an area you haven't touched this session, read `.claude/knowledge/conventions.md`. It also lists hard-won gotchas (xshd rules, airspace, WebView2 mappings, testing tricks).
- Before working on a feature, find its owning code in `.claude/knowledge/features.md`.
- For *why* something was built a certain way, read `PLAN.md`.

## Team rules

<!-- manual:start -->
<!-- manual:end -->

## Keeping this context current

> In any change that alters the data schema, adds, removes, or renames a project or module, moves a feature to different code, or makes an architecture decision, update CLAUDE.md and the files in `.claude/knowledge/` in the same change, without being asked. When the change records an architecture decision, write the decision and its reason into the knowledge files. Before finishing such a change, re-read the affected sections and confirm they still match the code. Stale context is a bug.

Here, "data schema" includes `session.json`, `settings.json`, the session/backup format, and the messages exchanged with `preview.js`.
