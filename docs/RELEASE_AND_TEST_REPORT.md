# Release and test report

NovaDownload 1.0.0 — Windows installer, browser extensions, verified browser integration and a
native BitTorrent engine.

Everything in this report was executed on the machine that built the release
(Windows, .NET SDK 9.0.319, Inno Setup 6.7.3, Node v24.20.0, Microsoft Edge 156.0.4314.42).
Counts are copied from the tool output, not estimated. Tests that could not be executed here are
listed as not executed, with the reason.

---

## 1. What was delivered

| Area | Delivered |
| --- | --- |
| Installer | `NovaDownload-1.0.0-win-x64-setup.exe` — Inno Setup 6.7.3, self-contained payload, no prerequisite |
| Portable | `NovaDownload-1.0.0-portable-win-x64.zip` — same payload, no installation |
| Browser extensions | Chrome, Edge and Firefox packages, loadable folders plus store-shaped zips |
| Desktop ↔ browser transport | Native messaging host + per-user named pipe + one validation gate in the application |
| Media detection | Layered, read-only detection with explicit, user-visible limits |
| BitTorrent | Native swarm engine (MonoTorrent 3.0.2): magnet links, local `.torrent`, remote `.torrent` URLs, DHT/PEX/LSD, seeding, rate limits, resume |
| `magnet:` handler | Per-user protocol registration, so a magnet click opens the app or hands the link to the instance already running |
| Reproducible build | `installer\build-release.ps1` and `browser-extensions\build-extensions.ps1` |
| Automated tests | 193 unit, 34 integration, 44 extension-package (Node), 17 live-browser checks |
| Documentation | `docs\BROWSER_INTEGRATION.md`, `docs\BITTORRENT.md`, `docs\WINDOWS_INSTALLER.md`, this report |

The existing application was extended, not rewritten: the same `IDownloadService`, the same
`DownloadItem` model, the same queue, history, settings, localization and theme. A browser download
is an ordinary transfer from the moment it is accepted, and it appears in the same list with the
same pause/resume/cancel/retry behaviour.

BitTorrent follows the same rule. `DownloadServiceRouter` is the single `IDownloadService` the
application resolves; it sends swarm sources to `TorrentDownloadService` and everything else to
`HttpDownloadService`, then merges both snapshots newest-first. Above the router nothing knows there
are two engines — the row, the buttons, the counters and the store polling loop are unchanged. See
`docs\BITTORRENT.md`.

No feature was faked to make a table look complete. Where something is impossible (DRM streams,
blob playback, signed URLs), the product says so instead of pretending.

---

## 2. Installer

| Item | Value |
| --- | --- |
| Tool | Inno Setup 6.7.3 (`ISCC.exe`) |
| `AppId` | `{7C2E6B31-1E6D-4C37-9F52-6C2D0E6A4B21}` |
| Uninstall key | `HKLM/HKCU\...\CurrentVersion\Uninstall\{7C2E6B31-…}_is1` |
| Display name / version | `NovaDownload 1.0.0` (version injected with `/DAppVersion=1.0.0`) |
| Minimum Windows | 10.0.17763, x64 |
| Install modes | All users (`C:\Program Files\NovaDownload`) or current user (`%LOCALAPPDATA%\Programs\NovaDownload`) |
| Prerequisite | **None** — self-contained `win-x64` publish, .NET 9 does not need to be installed |
| Payload | 46.2 MB setup, one shared .NET runtime serving the app *and* the native messaging host; includes the BitTorrent engine (`MonoTorrent.*.dll`, 11 assemblies) |

Behaviour that was implemented and checked:

* **Start Menu** group with *NovaDownload* and *Uninstall NovaDownload*; **desktop shortcut** is an
  optional task, unchecked by default.
* **Windows Settings → Apps** entry with display name, version, publisher, icon, install location
  and size (`EstimatedSize`).
* **Clean install and upgrade** share one `AppId`, so an upgrade reuses the previous folder, closes
  a running NovaDownload through the Restart Manager, and refreshes shortcuts, protocol handler and
  uninstall entry in place.
* **User data survives.** Settings (`%APPDATA%\NovaDownload\`) and the extension packages
  (`%LOCALAPPDATA%\NovaDownload\extensions\`) are never touched by the installer and never deleted
  by the uninstaller — there is no `[UninstallDelete]` section at all. The uninstaller announces
  which folders it kept.
* **`novadownload://` protocol** registered under `HKCU\Software\Classes\novadownload` (per user, the same key the application writes through `UrlProtocolRegistrar`), removed on uninstall. `StartupRequest` parses it, the request is
  validated by the same validator, and a running instance receives it over the pipe.
* **Native messaging registration** is performed by `NovaDownload.NativeHost.exe --register` with
  `runascurrentuser`, so the `HKCU` entries always belong to the account that runs the browser —
  including in an elevated all-users install. Uninstall runs `--unregister` the same way, pinned
  with a `RunOnceId`.
* **Silent install verification.** A `/CURRENTUSER /VERYSILENT` run of the shipped setup exited 0 and registered the URL protocol at `HKCU\Software\Classes\novadownload`; the previous script revision wrote the protocol with `Root: HKCR`, which is the elevated hive, so the per-user install rolled back with Inno exit code 5. The script now writes `Root: HKCU`.
  The run was repeated on the final artifact — the one that ships the BitTorrent engine — with the
  same result: exit code 0, protocol registered, `MonoTorrent.*.dll` present in the install folder,
  and a torrent queued and completed from the installed executable (see §6.6).
  * **Graceful failure.** `build-release.ps1` verifies the staged host with
  `NovaDownload.NativeHost.exe --identity --json` before packaging and aborts with an actionable
  message if the shared-runtime layout would not run.

One compile-time warning is expected and documented: Inno Setup warns that
`PrivilegesRequired=admin` combined with per-user areas (`localappdata`) needs care. The extension
packages are per-user on purpose — an unpacked extension must keep working for the user who
installed it — and the warning is accepted, not hidden.

---

## 3. Browser extensions

| Browser | Package | Manifest | Identity |
| --- | --- | --- | --- |
| Chrome | `release\chrome-extension\` + `.zip` | MV3, `service_worker` | `pnemgdlefleemkhignbgaclaelffmbah` |
| Edge | `release\edge-extension\` + `.zip` | MV3, `service_worker` | same id (byte-identical package) |
| Firefox | `release\firefox-extension\` + `.zip` | MV3, `background.scripts`, `strict_min_version` 128.0 | `novadownload@vanta.local` |

Shared code lives once in `browser-extensions\common\` and is copied into each package by
`build-extensions.ps1`; only the manifest and the `_locales` metadata differ per browser. Both
Chromium packages are byte-identical because the `edge` folder differs only in its display name,
which comes from `_locales`.

**The extension id is derived, not invented.** The manifest embeds a 2048-bit RSA *public* key;
the id is `SHA-256` over its DER bytes, first 16 bytes, each nibble mapped to `a`–`p`. During the
live run the browser reported exactly the id the build derives, and the native messaging manifest
allow-lists it — that is what makes an *unpacked* extension work without any manual id entry.
No private key exists anywhere in the repository.

Features: toolbar popup with connection status, detected-media list, per-item *Download*, a
normal-link *Send this link* path, extension settings (browser-downloads listing, deep network
detection, context-menu entries, connection test), right-click *Send link* / *Send page link*,
English and French catalogs following the browser language, and clear, specific errors when the
application or the native host is missing.

Permissions are deliberately narrow: `activeTab`, `scripting`, `storage`, `contextMenus`,
`downloads`, `nativeMessaging`. No `host_permissions` by default and no `<all_urls>` anywhere;
`webRequest` plus `http://*/*`/`https://*/*` are requested **only** when the user turns on deep
network detection, and the same origin list is used for `permissions.request`, `.remove`,
`.contains` and the listener.

---

## 4. Browser → desktop communication

```
extension → chrome.runtime.sendNativeMessage("com.novadownload.host")
          → NovaDownload.NativeHost.exe   (4-byte LE length prefix + UTF-8 JSON, 1 MiB cap)
          → \\.\pipe\NovaDownload.Browser.<domain>.<user>   (PipeOptions.CurrentUserOnly)
          → NovaDownload.App.exe → BrowserRequestProcessor → IDownloadService (existing engine)
```

* **Local only.** No TCP listener, no HTTP endpoint, no port. A unit test reads the protocol
  sources and fails if `TcpListener` or `HttpListener` ever appears next to
  `NamedPipeServerStream`/`CurrentUserOnly`.
* **Input is untrusted.** `DownloadRequestValidator` runs inside the application (not only in the
  extension) and rejects everything the engine cannot safely fetch: non-`http(s)` schemes,
  `user:pass@` URLs, control characters, over-long URLs, path traversal in the file name, Windows
  reserved device names, UNC/relative/`..` destinations, and malformed request ids.
* **No shell.** Nothing from the wire reaches `cmd`, `powershell` or a process start. The host
  only relays to the pipe, or starts the *known* application executable.
* **Replay and duplicate protection.** A repeated `requestId` returns `DUPLICATE` with the original
  engine id inside a 20 s / 256-entry window; a live transfer of the same URL is not queued twice.
  A *finished* transfer does not block a later request — the engine's existing `file (1).ext`
  logic applies, which is what the live run demonstrated.
* **Settings gate.** With *Browser integration* switched off, downloads are answered `DISABLED`
  with an actionable message while `ping`/`status` still answer.

### Defect found by the live run, and fixed

The first real-browser run failed with *"Error when communicating with the native messaging host"*.
Chromium launches the host as `host.exe "chrome-extension://<id>/" --parent-window=0`; the host's
argument parser classified that origin as an unknown argument, printed usage and exited with code 2
*before* reading the first frame — so the browser saw a closed pipe. A proxy host proved the launch
environment was healthy (working directory, 67 environment variables, write/read both fine), which
isolated the cause to argument parsing.

The host now accepts the arguments browsers append (Chromium's origin and `--parent-window[=N]`,
Firefox's manifest path and extension id), records the caller origin in the log, and still rejects
genuinely unknown arguments with the usage exit code. Two integration tests launch the host with
each browser's argument shape and assert it still answers.

---

## 5. Media detection — what works, what does not

Layered and read-only. Nothing is ever sent until the user clicks a button.

| Layer | Mechanism |
| --- | --- |
| Direct links | `a[href]` with a media extension or a `download` attribute |
| Media elements | `video`/`audio` `currentSrc`, `src`, `<source>` children |
| Preload hints | `link[rel=preload]` with `as=video|audio|track` |
| Page metadata | `og:video`, `og:audio`, `twitter:player:stream` |
| Resource timing | `performance.getEntriesByType('resource')` |
| Browser downloads | `downloads.onCreated` — listed only, never redirected or blocked |
| Deep network scan | optional `webRequest` observation, off by default |

Offered: MP4, WebM, OGV, MOV, M4V, MKV, MP3, M4A, AAC, OGG, OGA, OPUS, WAV, FLAC, FLV.

Refused with a stated reason: HLS/DASH playlists (`.m3u8`, `.mpd`), `blob:`/MSE playback,
`data:`/`filesystem:`, and any non-`http(s)` scheme.

Not supported, and not claimed: DRM (Widevine/PlayReady), paywalls, cookie- or token-protected
media, signed expiring URLs, and therefore **no universal YouTube support**. The extension forwards
a URL, never a browser session; a 401/403 from the server is reported as the server's status.
Where a page exposes nothing (blob-backed players), the popup says so and suggests the deep scan.

---

## 6. Tests actually executed

All four gates were run on the final sources, in this order, on the release build.

| Gate | Command | Result |
| --- | --- | --- |
| Extension packages | `node tests\extension-tests\run-tests.js --package` | **44 passed, 0 failed** |
| Unit tests | `dotnet test tests\NovaDownload.UnitTests` | **193 passed, 0 failed** |
| Integration tests | `dotnet test tests\NovaDownload.IntegrationTests` | **34 passed, 0 failed** |
| Live browser E2E | `powershell -File tools\verify-browser-e2e.ps1` | **17 checks, 17 passed, 0 failed** |

### 6.1 Extension suite (Node, no dependencies)

44 assertions over the packaged folders: manifest validity per browser, byte-identical Chromium
manifests, MV3 service worker without modules, Firefox gecko identity, permission parity, id
derivation from the shipped key, the key being a real 2048-bit RSA `SubjectPublicKeyInfo`, every
referenced file existing, en/fr catalog parity (70 keys), no remote code and no `eval`, detection
classification per format, honest refusal of playlists and blob URLs, file-name derivation and
sanitising, human formatting of sizes and durations, metadata merging, and source hygiene.

This suite caught three real defects during development: a missing `sectionPageLink` fallback, a
`$NAME$` versus placeholder case mismatch, and dangling fallback keys.

### 6.2 Unit suite (193)

Contracts and pure logic: URL/file-name/destination validation, framing (little-endian length,
1 MiB cap, truncated frames, invalid UTF-8, one-byte-at-a-time reads), deduplication windows and
capacity, Chromium id derivation (including that a mutated key yields a different id), the request
processor (settings gate, destination precedence, duplicate handling, engine failures, malformed
payloads), settings persistence and redirection, protocol version parity between C# and the
extension, the torrent source rules (magnet accepted only with a usable `xt=urn:btih:` hash in hex
or base32, remote and local `.torrent` shapes, the length and control-character caps, and that the
router, the gate and the command line agree on every sample), and cross-file invariants: the `.iss`
`AppId` matches `BrowserIntegrationInfo`, the extension identity file matches the constants, both
language catalogs stay in parity, and the installed `.iss` contains `--register`, `--unregister`,
`uninsneveruninstall`, `desktopicon`, `{group}`, and no `[UninstallDelete]`.

### 6.3 Integration suite (34)

Real transports, no mocks on the wire:

* a browser-shaped framed request enters the real per-user pipe and the file that comes out is
  byte-identical to what the local HTTP fixture served — fetched by `HttpDownloadService`, the
  same engine the UI uses;
* the real `NovaDownload.NativeHost.exe` process is driven over stdin/stdout exactly as a browser
  drives it, including the relay of a download all the way to disk, malformed JSON, an unreachable
  application, `--identity --json`, `--status`, and the usage exit code for a genuinely unknown
  argument;
* both browsers' launch-argument shapes are accepted (the regression found in the live run);
* a garbage frame and a `file://` URL are refused before the engine is touched;
* six BitTorrent tests move real swarm traffic over loopback — see 6.6.

### 6.4 Live browser end-to-end (17 checks)

`tools\verify-browser-e2e.ps1` drove a real Microsoft Edge 156.0.4314.42 with the unpacked
`release\edge-extension`, a throw-away profile, a local HTTP fixture and a throw-away settings
folder (`NOVADOWNLOAD_DATA_DIR`), so nothing in the developer's profile was read or written.

| Check | Result |
| --- | --- |
| Host registered for Edge | PASS — manifest path in `HKCU\...\Edge\NativeMessagingHosts` |
| Manifest allow-lists the derived id | PASS — `chrome-extension://pnemgdlefleemkhignbgaclaelffmbah/` |
| Manifest points at the host executable | PASS |
| Bridge pipe name resolved | PASS — `NovaDownload.Browser.desktop-boasn9t.benamar` |
| Application bridge listening | PASS |
| The app used an isolated settings folder | PASS |
| Debugger endpoint reachable | PASS |
| Extension loaded and answering | PASS — runtime id reported by the browser |
| Live id equals the derived id | PASS — `pnemgdlefleemkhignbgaclaelffmbah` |
| Ping through native messaging | PASS — `code=OK server=novadownload-app version=1.0.0` |
| Ping reports the protocol version | PASS — `protocolVersion=1` |
| Download accepted by the application | PASS — engine id returned |
| Engine reports a download id | PASS |
| File on disk with the exact size | PASS — 2 097 152 bytes |
| Downloaded bytes match the served file | PASS — SHA-256 equal to the fixture |
| A finished transfer does not block a new request | PASS — second request accepted |
| The second copy gets its own file name | PASS — `nova-e2e.bin` → `nova-e2e (1).bin` |

### 6.5 Not executed, and why

| Item | Reason |
| --- | --- |
| Live load of the Chrome package | Google Chrome is not installed on this machine |
| Live load of the Firefox package | Mozilla Firefox is not installed on this machine |
| Firefox native messaging run | Same; the Firefox manifest, gecko id and `allowed_extensions` are validated statically, and the host accepts Firefox's launch arguments in an integration test |
| Installer run on a clean virtual machine | No VM available in this environment; the installer compiles cleanly and its script invariants are asserted by unit tests, but a real clean-machine install was not performed here |
| Install → upgrade → uninstall cycle on a real machine | Not executed here for the same reason |
| Store publication | Out of scope; the packages are store-shaped but nothing was uploaded |

### 6.6 BitTorrent suite (7 of the 34 integration tests)

`tests\NovaDownload.IntegrationTests\TorrentEngineTests.cs` builds a torrent from a temporary
payload, seeds it, and downloads it over `127.0.0.1`. No tracker, no DHT, no internet.

| Test | Proves |
| --- | --- |
| `Torrent_File_Source_Is_Detected_And_Seeds_Verified_Data` | Source detection for all four shapes (magnet, local path, `.torrent` URL, plain HTTP); a local `.torrent` hashes existing data and reaches 100 % with the right name, size, path and 40-char v1 info hash |
| `Service_Seeds_And_A_Second_Swarm_Member_Gets_Identical_Bytes` | The service really listens and serves: a bare `ClientEngine` downloads 2 MiB and the bytes are identical; upload telemetry and a positive ratio appear on the seeding row; a live connection never reads as `0 seeds · 0 peers` |
| `Cancel_Removes_The_Partial_Data_And_Stops_The_Row` | Cancel unlinks the partial file and drops the row from the snapshot |
| `A_Partial_Download_Never_Reports_Completed` | A destination holding a quarter of the payload reports roughly 25 % and never a finished row — pins the 0..100 `manager.Progress` scale |
| `A_Repeat_Source_Returns_The_Live_Transfer_Instead_Of_Failing` | The duplicate-source guard returns the existing id instead of letting MonoTorrent throw |
| `Status_And_Control_Round_Trip_Through_The_Seeding_Row` | Pause on a completed-but-seeding row stops the seed, resume restores it, and removing a complete row keeps the data |

These tests found four real defects, all fixed in the engine: a seeding row could not be paused
(pause was gated on `Downloading|Queued`), the ratio read 0.00 forever on a pure seeder (divide by
zero against a nearly empty receive counter), removing a row mid-resume deleted the files of a
transfer whose data was already complete (the decision was taken from transient queue status instead
of `manager.Complete`), and — reported from a real download — a row showed a full bar and a full
byte count within seconds of starting (`manager.Progress` is a 0..100 percentage in MonoTorrent
3.0.2 and was divided as if it were 0..1, so anything past one percent clamped to 100 %).

The same investigation hardened two neighbouring paths: progress is no longer projected from the
bitfield while the engine is hashing (a fastresume file can restore an all-true bitfield and made a
resumed torrent open at 100 % before collapsing), and a row only claims **Completed** once no
`.!mt` partial file is left under the destination, so a stale bitfield cannot fake a finished
transfer. `StopAsync` was also guarded against the "cannot be stopped while it is already in the
Stopping state" race that appeared once in a real session log.

The swarm counters were reported as `0 sources · 0 pairs` while a transfer was running. Measured on
a live swarm: `PeerManager` only classifies a peer once its handshake has finished, so for the first
poll cycles it reports zero seeds and zero peers while three connections are open and data is
flowing. The pair count is now derived from `OpenConnections` (and kept consistent with the seed
count), and the peers the tracker or DHT announced without a connection appear in the tooltip.

A live end-to-end run of the built executable confirmed the same path outside the test harness:
`NovaDownload.App.exe --torrent <path>` logged `queued torrent`, `torrent engine started (listen
port 41613, dht=True, max connections 200)` and `torrent 'payload.bin' complete`, and the 3 MiB file
was on disk after the process exited.

The same run was repeated against the **installed** package, after the release rebuild: the setup
installed per user, the torrent assemblies were present under
`%LOCALAPPDATA%\Programs\NovaDownload`, and the installed `NovaDownload.App.exe --torrent <4 MiB
path>` logged engine start and `torrent 'payload.bin' complete`. That closes the loop between the
test harness, the build output and the artifact a user actually receives.

### 6.7 `magnet:` protocol handler

A magnet link clicked in Windows is a different entry point from the extension, so it was verified
on the installed build rather than in a harness.

| Step | Observed |
| --- | --- |
| Per-user silent install | exit 0; `HKCU\Software\Classes\magnet\shell\open\command` = `"…\NovaDownload.App.exe" "%1"` |
| `Start-Process "magnet:?xt=urn:btih:10553FF0…"` with the app closed | the process started, logged `magnet-protocol: registered for …\NovaDownload.App.exe`, then `startup: 'magnet: protocol' -> OK: '<name>' was added to NovaDownload`, then `torrent engine started` |
| the same click with the app already open | logged `startup: forwarded 'magnet: protocol' to the running instance: OK`; the process count stayed at **1**, no second window, no second engine |
| a second, different magnet link | a second row in the queue, still one process |
| rows after both clicks | `mame-32-roms-snaps-210-games` (659.2 MB) and `tft-unlock-tools-2024-v-6.1.1.1-unlocktool-download` (302.7 MB), both at `0 %` with `0 source · 0 pair` because neither swarm had a live seeder at that moment |

Three assertions cover the same path without the OS: a magnet reaches the engine through the
`torrent` action and keeps its URL, a magnet without an info hash is refused at the gate with
`INVALID_REQUEST`, and switching *Browser integration* off still lets a magnet through while the
same settings refuse a media `download`.

Magnet simulation was not exercised against a real swarm: that needs internet peers, not loopback.
The metadata path is covered by the code and by the failure ceiling below, not by a live swarm test.

---

## 7. Release artifacts

Produced by one command, `powershell -NoProfile -File installer\build-release.ps1`, on
2026-10-26.

| Artifact | Size (bytes) | SHA-256 |
| --- | --- | --- |
| `windows-installer\NovaDownload-1.0.0-win-x64-setup.exe` | 46,194,882 | `4ef4932013ed0cb975f4295198db69487a15faec43290a95b27089c9a96153b0` |
| `portable\NovaDownload-1.0.0-portable-win-x64.zip` | 60,669,407 | `49d37998ebc4ae1221de8e6d6645ba21a5c807e7795e4c17d174dba0c35cd7a9` |
| `chrome-extension.zip` | 35,674 | `0aed35d175187227961dc23b258883fad5fbf34eeee250a3f814315a41e84a90` |
| `edge-extension.zip` | 35,674 | `0aed35d175187227961dc23b258883fad5fbf34eeee250a3f814315a41e84a90` |
| `firefox-extension.zip` | 35,378 | `66272d453fb8a659441d69d30f37cd10b5e98d4d02b21dd775d5463f43f3a61d` |

`release\checksums\SHA256SUMS.txt` lists the same hashes; `release\BUILD-MANIFEST.txt` records the
toolchain (dotnet 9.0.319, node v24.20.0, Inno Setup 6.7.3), the installer `AppId`, the extension
ids, the host name, the build timestamp and the artifact table.

Reproducibility: the extension zips are byte-reproducible (fixed entry timestamps, forward-slash
entry names), which is why `chrome-extension.zip` and `edge-extension.zip` hash identically. The
setup executable is **not** byte-reproducible — Inno Setup embeds a build timestamp — so its hash
identifies this exact artifact, not a rebuild.

No secrets, certificates, keys or credentials are stored in the repository or in the artifacts. The
manifest `key` field is a public key, by design.

---

## 8. Known limitations and honest gaps

1. **Unsigned binaries.** The setup and the executables carry no Authenticode signature. Windows
   SmartScreen will warn until reputation accumulates, and enterprise deployment needs its own
   signed build. `docs\WINDOWS_INSTALLER.md` documents the `signtool` step; no certificate is
   shipped.
2. **No store publication.** Chrome, Edge and Firefox packages are prepared and validated for store
   upload; nothing was submitted. Firefox needs AMO signing for a permanent install — a temporary
   add-on disappears when Firefox closes. That is a Firefox rule, not a product defect.
3. **DRM, blob/MSE, HLS/DASH, signed and cookie-protected media are not supported** and are
   reported as such. There is no bypass and no attempt at one.
4. **Unpacked extensions are never auto-installed** by any browser. The installer copies the
   packages to `%LOCALAPPDATA%\NovaDownload\extensions\` and the documentation explains the
   two-click developer-mode load.
5. **Chrome and Firefox were validated statically, not driven live** — they are not installed on
   this machine.
6. **The installer was compiled and its invariants tested, but a clean-machine install/upgrade/
   uninstall cycle was not executed here.** That is the one step that still needs a real or virtual
   clean Windows machine; `docs\WINDOWS_INSTALLER.md` §8 is the checklist to run against it.
7. **Detection is best-effort by nature.** A page that plays media through a blob/MSE player shows
   nothing in the panel until deep network detection is enabled and playback starts. This is stated
   in the popup, not hidden.
8. **BitTorrent gaps.** A multi-file torrent downloads every file — selective file priority is
   available in the engine API but not wired to the UI. No proxy, no RSS, no bandwidth scheduling,
   no web seeds. Seeding does not survive an application restart by design: completed rows are
   re-added when the source is added again and re-verify instantly. `.torrent` files are not
   associated with the application, deliberately, so no existing default handler is stolen.
9. **The `magnet:` handler is exclusive.** Windows keeps one handler per scheme, so registering it
   takes magnet links from any other BitTorrent client the user has installed. The setting and the
   uninstaller both hand the scheme back; there is no "ask me each time" mode, because Windows has
   no per-link chooser for a registered scheme.
   Private-tracker passkeys are used as embedded and never persisted to settings or logs.
   `docs\BITTORRENT.md` §10 lists the same gaps from the user's side.
9. **Torrent reachability depends on the network.** On a firewalled or CGNAT connection the engine
   still connects out but cannot accept incoming peers, which is slower but functional. UPnP/NAT-PMP
   port mapping is attempted only when the setting is on. A magnet link with no live seeders fails
   after the 5-minute metadata ceiling with an explicit message rather than sitting at 0 %.

---

## 9. Documentation index

| Document | Contents |
| --- | --- |
| `docs\BROWSER_INTEGRATION.md` | Transport, security model, detection layers and limits, extension installation, registration, diagnostics, verification commands |
| `docs\BITTORRENT.md` | Torrent sources, where data lands, status projection, resume/cancel/remove boundaries, port and firewall, settings map, diagnostics, honest gaps, test table |
| `docs\WINDOWS_INSTALLER.md` | Toolchain, one-command release build, install modes, shortcuts, registry, upgrade and uninstall behaviour, signing, troubleshooting, post-install checklist |
| `docs\RELEASE_AND_TEST_REPORT.md` | This report |

## 10. Reproducing everything

```powershell
# 1. full release: publish, extensions, portable zip, installer, checksums, manifest, package tests
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-release.ps1

# 2. contracts and cross-file invariants
dotnet test tests\NovaDownload.UnitTests\NovaDownload.UnitTests.csproj -c Debug

# 3. real pipe, real host process, real engine
dotnet test tests\NovaDownload.IntegrationTests\NovaDownload.IntegrationTests.csproj -c Debug

# 4. real browser (needs Edge; writes nothing outside %TEMP%)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-browser-e2e.ps1
```
