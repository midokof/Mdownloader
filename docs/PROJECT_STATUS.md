# NovaDownload - Project Status

Last updated: 2026-10-24 15:30 UTC
Status: WORKING BUILD — UI redesign + French/English localization complete, engine verified end-to-end, published
Current milestone: MILESTONE 6 - Bilingual UI + verified transfer engine

## What this is

Independent Windows download manager. C# / WPF / .NET 9, MVVM with DI, layered solution.
No borrowed assets, icons or source from any existing commercial manager.

## Completed Tasks

### Foundation
- [x] Environment audit, SDK pinned (`net9.0-windows`, SDK 9.0.319)
- [x] Solution with 6 projects: Core, Infrastructure, DownloadEngine, BrowserIntegration, App, tests
- [x] Dependencies: `CommunityToolkit.Mvvm 8.3.2`, `Microsoft.Extensions.Hosting 9.0.0`
- [x] Project references fixed (Infrastructure → Core)

### Engine
- [x] Streaming HTTP/HTTPS download into `*.npart`, atomic move to final name on completion
- [x] Real progress, throughput and ETA measured from the socket (no simulated values)
- [x] HTTP Range resume gated on `Accept-Ranges` + `ETag`/`Last-Modified` sent as `If-Range`
- [x] Pause keeps the partial file; resume continues from the exact offset
- [x] Cancel cancels, sets `Canceled`, awaits the writer (≤2 s) so the handle releases, then deletes the part file
- [x] Unique-name conflict resolution (`name (1).ext`, `name (2).ext`, …)
- [x] Bounded concurrency via semaphore, per-download state machine
- [x] Snapshot / observe / remove API consumed by a single UI poller

### UI
- [x] Borderless `WindowChrome` shell: 246px sidebar, 76px header, custom caption buttons
- [x] Live theme swap (light default / dark) persisted to `%APPDATA%\NovaDownload\settings.json`
- [x] Dashboard: four metric cards, aggregate transfer telemetry, recent activity, quick actions, free-space rail, session counters
- [x] Downloads: toolbar chips, per-row badge/name/folder/progress, two-line telemetry, pause/resume/open/cancel/remove, double-click opens Explorer, empty state
- [x] New download: framed URL / filename / folder, paste from clipboard, folder picker, validation strip, Enter to start
- [x] History: completed + failed list, totals, clear
- [x] Settings: default folder, concurrency slider (applies next launch), appearance, about, save
- [x] `DownloadStore` singleton polls once and feeds every view — counters cannot drift

### Localization
- [x] `Localizer` + `LocaleCatalog.En` (fallback) / `LocaleCatalog.Fr`, ~115 keys each, no `.resx`
- [x] UI language follows the Windows display language by default; `Language` in settings can pin `en` or `fr`
- [x] Numbers, units and durations localized (`1,5 Mo` / `1.5 MB`, `Reste 55 s · 32 Mo` / `ETA 55s · 32 MB`, `156,9 Go` / `156.8 GB`)
- [x] Plural handling per language: French counts 0 and 1 as singular (`0 échec`, `1 sur 1 transfert`)
- [x] Live switch through `{loc:Tr Key}` + `LocalizationProxy` — no page rebuild, no restart; persisted immediately
- [x] Settings page carries a Language chip row (System default / English / Français), styled `ChoiceChip`
- [x] `AutomationProperties.AutomationId` on everything the harness drives, so labels can change language freely

### Fixes
- [x] **"Clear finished" appeared dead.** `DownloadStore.Sync` only ran from the item stream, so emptying the
      list produced no tick and no prune. Replaced with a single 200 ms reconcile loop over `Snapshot()`
      that reconciles add **and** remove, including the empty case.
- [x] `RemoveAsync` on a finished transfer now drops the record only — it no longer rewrites `Completed` to
      `Canceled` and never deletes the file on disk.
- [x] `PauseAsync` accepts `Queued`, so a transfer waiting on the semaphore can be paused before it starts.
- [x] Downloads list no longer calls `View.Refresh()` on every tick (no selection/scroll churn).
- [x] Counted labels are composed in view models — XAML trims the leading space of `<Run Text=" done"/>`.

### Verification
- [x] 34 unit tests green (`Formatters`, `DownloadItem`, catalog fallback, French formatting, plural rule)
- [x] 11 integration tests green against a raw-socket fixture server (exact bytes, pause/resume + Range, name
      conflict, cancel cleanup, invalid URLs, snapshot/remove, completed-record removal, queued pause)
- [x] Manual end-to-end: 32 MB transfer completed with **SHA256 identical to source**
- [x] Manual pause froze at 2.9 MB with matching `.npart` length, resume continued from that offset
- [x] Dashboard, sidebar and session counters updated live during the transfer
- [x] Theme persisted across restart
- [x] **Clear finished verified live**: 32 MB row → toolbar button → list empty, file still 33 554 432 bytes on disk
- [x] **Language verified live**: French at startup (Windows = fr-FR) → chip English → whole UI + row text in
      English (`Finished 15:18`, `2 MB`) → chip French → back, `Language` persisted as `Auto`
- [x] Release solution build: 0 warnings, 0 errors
- [x] Published framework-dependent `win-x64` to `D:\Mdownloader\publish` and launched it

## Build Results

```
dotnet build NovaDownload.sln -c Release   ->  0 warnings, 0 errors
dotnet publish src\NovaDownload.App -c Release -r win-x64 --self-contained false -o publish
```

## Test Results

```
NovaDownload.UnitTests         : 34 passed, 0 failed
NovaDownload.IntegrationTests  : 11 passed, 0 failed
```

The unit suite disables parallelization on purpose: the UI language is process-wide state and one test
class asserts on French text while another asserts on English.

## Verification harness (`tools/`)

| Script | Purpose |
|---|---|
| `test-server.ps1` | Throttled, Range-capable, ETag fixture server on `127.0.0.1:8099` (`-ChunkDelayMs` sets the rate) |
| `capture-window.ps1` | DPI-aware `PrintWindow` capture of the live window |
| `ui-act.ps1` | UIA — `-List` dumps every named element, or drive one element by `AutomationId` (`invoke` / `select` / `value`) |
| `ui-nav.ps1` | UIA — selects a sidebar page by accessible name, then captures |
| `ui-download.ps1` | UIA — fills the URL field and starts a transfer |
| `ui-click.ps1` | UIA — invokes a button by accessible name |
| `ui-probe.ps1` | Dumps buttons / edits / radios with accessible names |

`ui-act.ps1` is the language-proof entry point: it addresses elements by `AutomationId`, so the same
sequence verifies the app in French and in English.

## Screenshots

| File | What it shows |
|---|---|
| `docs/screenshots/fr-dashboard-active.png` | French dashboard, 480,8 Ko/s live, `10 % du volume`, `156,9 Go` |
| `docs/screenshots/fr-downloads-active.png` | French queue, singular chips (`1 en cours`, `0 terminé`), `Reste 55 s · 32 Mo` |
| `docs/screenshots/en-dashboard-active.png` | Same transfer, English: `486 KB/s`, `31% of payload`, `156.8 GB` |
| `docs/screenshots/en-downloads-active.png` | English queue chips (`1 of 1 transfer`) |
| `docs/screenshots/en-settings.png` | Language chips: System default / English / Français |

## Known limitations

- Only `en` and `fr` exist; any other Windows display language falls back to English.
- Plural handling is a two-form rule (singular/plural per language), not a CLDR engine.
- Concurrency setting is read at service construction, so a change takes effect on the next launch (stated in the UI).
- Downloads are in-memory per session; history is not yet persisted to SQLite.
- BrowserIntegration project is scaffolded but the extension/native host pair is not wired.
- No scheduled retry/backoff for failed transfers yet.

## Next Concrete Action

Persist `DownloadItem` history + resumable state to SQLite so restarts keep the queue and history.
