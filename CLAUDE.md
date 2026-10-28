# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

OwnIDM is a Windows download manager inspired by Internet Download Manager: C# / .NET 8, WPF UI, MIT licensed.
`README.md` (in Spanish) describes every user-facing feature in detail.

## Commands

Requires Windows 10/11 and the .NET 8 SDK.

```powershell
dotnet build OwnIDM.sln -c Release          # output goes to build\Release\ (not bin\)
.\build\Release\OwnIDM.exe

dotnet test tests\OwnIDM.Core.Tests                                         # all tests
dotnet test tests\OwnIDM.Core.Tests --filter "FullyQualifiedName~YtDlpToolTests"   # one class
dotnet test tests\OwnIDM.Core.Tests --filter "FullyQualifiedName~DownloadEngineTests.Segmented_download_produces_identical_file_using_several_connections"

# Local test installer (needs Inno Setup 6: winget install JRSoftware.InnoSetup)
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1      # -> dist\OwnIDM-Setup-<ver>.exe + latest.json
```

There is no separate lint step. Tests cover `OwnIDM.Core` only (xUnit); the WPF app has no tests.
`tests/OwnIDM.Core.Tests/TestServer.cs` is a local Kestrel server on a random loopback port that serves the
same payload with/without range support, flaky connections, etc. Reuse it for engine tests instead of hitting the network.

## Releases

Releases are built only by GitHub Actions (`.github/workflows/release.yml`), never from a PC:
1. Bump `<Version>` in `Directory.Build.props` (the single source of the version) and commit.
2. `powershell -ExecutionPolicy Bypass -File installer/publish-release.ps1 -Notes "..."` tags `v<version>` and pushes.
3. The workflow checks the tag matches `<Version>`, runs tests, builds (`build-installer.ps1 -BuildOnly`), has SignPath
   sign the binaries, packs with Inno Setup (`-SkipBuild`), signs the installer, and publishes the installer + `latest.json`.

Without the `SIGNPATH_API_TOKEN` secret the release is published unsigned. `UpdateFeed` in `Directory.Build.props`
is embedded as assembly metadata and is the URL of the `latest.json` the in-app updater reads.

## Architecture

Three executables plus a browser extension, all ending up in the same `build\<Configuration>\` folder
(both `OwnIDM.App` and `OwnIDM.NativeHost` set `OutDir` there, and the extension is copied to `build\<Config>\extension`):

- **`src/OwnIDM.Core`** (`net8.0`, no UI): all download logic. Depends only on MonoTorrent.
- **`src/OwnIDM.App`** (`net8.0-windows`, WPF → `OwnIDM.exe`): windows and dialogs written as code-behind (no MVVM framework),
  binding directly to Core models. WinForms is referenced only for the tray `NotifyIcon`; its global usings are removed on purpose.
- **`src/OwnIDM.NativeHost`**: Chrome native-messaging host (length-prefixed JSON on stdin/stdout). Forwards each message
  to the running app over the named pipe, launching `OwnIDM.exe` if it isn't running.
- **`extension/`**: Chromium MV3 extension (`background.js`, `popup.*`, `panel.js`). Captures downloads, detects
  HLS/DASH/media in pages, and talks to the host through `chrome.runtime.connectNative` (a port, so the service worker
  stays alive while the user decides). Its `manifest.json` has a fixed key so the extension ID is always
  `jnnkhifhlmmlofgelokpmjcgpflmgpld`, the only origin the host allows.

### Message flow browser → app

`extension` → `OwnIDM.NativeHost` → named pipe `OwnIDM.Pipe.<user>` (`Core/Ipc/Ipc.cs`, `IpcMessage` with `type`/`kind`)
→ `IpcServer` in `App.xaml.cs` → `MainWindow.HandleIpc` on the UI thread. The pipe reply (accepted/declined) is sent only
after the user decides in the dialog; "accepted" is what lets the browser cancel its own download. A second
`OwnIDM.exe` instance (single-instance mutex) uses the same pipe to hand over its URL / magnet / `.torrent` and exits.

### Download pipeline

`DownloadManager` (`Core/Manager`) owns the `ObservableCollection<DownloadItem>`, the queue, and one downloader per kind,
and dispatches in `RunAsync` on `DownloadItem.Kind`:

| Kind | Downloader |
|---|---|
| `Http` | `Engine/DownloadEngine` — multi-connection ranged download with dynamic segmentation (a finished connection splits the largest remaining segment) |
| `Hls`, `Dash` (`IsStream`) | `Hls/HlsDownloader` (also handles DASH via `Dash/DashManifest`); `Ffmpeg` remuxes/merges audio when available |
| `YtDlp` | `YtDlp/YtDlpDownloader` — drives the external yt-dlp process |
| `Torrent` | `Torrents/TorrentDownloader` (MonoTorrent) |

The manager is driven from the UI thread: `MainWindow` runs a 500 ms `DispatcherTimer` that calls `Manager.Tick()`, which
refreshes progress/speed and saves state when dirty. `Persistence` serializes items (including segment state for resume)
and `AppSettings` as JSON in `%APPDATA%\OwnIDM`. In-progress `.part` files live in `%LOCALAPPDATA%\OwnIDM\Temp`.

Other Core pieces: `Tools/ToolManager` downloads yt-dlp, ffmpeg and Deno on demand into `%LOCALAPPDATA%\OwnIDM\tools`,
verifying SHA-256 (explicit path in settings > OwnIDM's copy > PATH); `Update/AppUpdater` downloads the new installer with
the app's own engine, checks its hash and that its signature matches the running exe; `Scheduling/QueueScheduler`,
`Grabber/SiteGrabber` (site crawler).

## Conventions

- The UI and user-facing strings (including exception messages shown to the user) are **in Spanish**; the app forces
  the `es-ES` culture at startup. Code comments and identifiers are in English.
- Antivirus heuristics matter for this app (it writes to the registry, watches the clipboard, downloads and runs tools):
  keep the version resource fields in `Directory.Build.props`, and don't bundle third-party tools in the installer.
- `docs/ANALISIS-*.md`, `bugs/` and root `*.jpg` are local-only (gitignored). `docs/PLAN-ESCALADO.md` is the product roadmap.
