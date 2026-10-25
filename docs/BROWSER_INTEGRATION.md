# Browser integration

How the NovaDownload browser extensions hand a media link to the desktop application, what they
can and cannot see, and how to install and diagnose them.

This document describes the shipped implementation. It does not describe a future one.

---

## 1. What it does

One click in the toolbar popup sends a direct media URL to the desktop application, which fetches
it with the **same download engine, queue, history and destination settings** the application
already uses for typed URLs. There is no second engine and no second data model: a browser download
is an ordinary `DownloadItem` from the moment it is accepted.

```
  extension popup / context menu
            |
            |  chrome.runtime.sendNativeMessage("com.novadownload.host", {action:...})
            v
  NovaDownload.NativeHost.exe        (started by the browser, stdio, 4-byte length framed JSON)
            |
            |  named pipe  \\.\pipe\NovaDownload.Browser.<domain>.<user>
            |  (PipeOptions.CurrentUserOnly)
            v
  NovaDownload.App.exe  -->  BrowserRequestProcessor  -->  IDownloadService (existing engine)
                                     |
                                     +--> validation, deduplication, settings gate
```

The application answers with the engine id, the resolved file name and the final destination, so
the popup can say *"Added “clip.mp4” to NovaDownload"* instead of guessing.

### What it deliberately does not do

* **No DRM, no paywall, no stream ripping.** Widevine/PlayReady content, blob:/MSE playback and
  segmented HLS/DASH streams are detected and reported as *not downloadable*, with the reason
  shown to the user. Nothing bypasses a protection.
* **No universal YouTube support.** YouTube (and most large video sites) serve media through
  signed, expiring, origin-locked URLs from a blob-backed player. The extension will not pretend
  otherwise; it lists what the page actually exposes and says so when there is nothing to take.
* **No silent capture.** Looking at a page never queues anything. A download starts only after the
  user clicks a button. Browser downloads are *observed* (listed in the panel) and never redirected.
* **No wide host permissions.** The extension does not ask for access to all sites. A content
  script is injected into the **active tab only, on request**, using `activeTab` + `scripting`.

---

## 2. Pieces and where they live

| Piece | Location in the repository | Shipped as |
| --- | --- | --- |
| Shared extension code | `browser-extensions/common/` | copied into every package |
| Per-browser manifests | `browser-extensions/chrome|edge|firefox/manifest.json` | one manifest per package |
| Extension identity | `browser-extensions/extension-identity.json` | source of truth for ids and the `key` |
| Packaging script | `browser-extensions/build-extensions.ps1` | `release/{chrome,edge,firefox}-extension/` + `.zip` |
| Native messaging host | `src/NovaDownload.NativeHost/` | `NovaDownload.NativeHost.exe` next to the app |
| Shared contracts + validation | `src/NovaDownload.BrowserIntegration/` | DLL referenced by app and host |
| App-side bridge | `src/NovaDownload.App/Services/BrowserIntegrationService.cs`, `App.xaml.cs` | part of the app |
| Settings UI | `src/NovaDownload.App/Views/SettingsView.xaml` ("Browser integration" card) | part of the app |

### Identity

| Value | Value used by this build |
| --- | --- |
| Native messaging host name | `com.novadownload.host` |
| Chromium extension id | `pnemgdlefleemkhignbgaclaelffmbah` |
| Firefox extension id | `novadownload@vanta.local` |
| URL protocol (fallback channel) | `novadownload://` |
| Protocol version | `1` |

The Chromium id is **derived, not invented**: `SHA-256` over the DER SubjectPublicKeyInfo embedded
as `"key"` in the manifest, first 16 bytes, each nibble mapped to `a`–`p`. Because the public key
ships inside the manifest, an *unpacked* extension keeps the same id on every machine — which is
what lets the native messaging manifest allow-list it in advance. Only the public key is shipped;
there is no private key anywhere in the repository.

Verify at any time:

```powershell
& "$env:ProgramFiles\NovaDownload\NovaDownload.NativeHost.exe" --identity
# per-user install: %LOCALAPPDATA%\Programs\NovaDownload
# or, from a build tree:
& build\app\NovaDownload.NativeHost.exe --identity --json
```

`chromiumId` and `chromiumIdCalc` must be identical.

---

## 3. The one-click workflow, step by step

1. **The page is opened.** No script runs. The extension holds no host permission for it.
2. **The user opens the popup.** The popup asks the background worker for the active tab, which
   injects the read-only scanner into *that* tab only (`activeTab`, granted by the user's click).
3. **The scanner reads what the DOM already exposes.** `<video>`, `<audio>`, `<source>`, media
   `<a href>` links, `<link rel=preload>` hints, `og:video`/`og:audio`/`twitter:player:stream`
   metadata, plus the page's own resource-timing entries.
4. **The popup lists what it found** with file name, format, size and duration when the browser
   actually knows them — and marks playlists and blob URLs as unsupported with the reason.
5. **The user clicks *Download* on one item.** That is the only action that can start anything.
6. **The extension builds a request** (`action: "download"`, absolute URL, optional file name,
   optional page URL, optional MIME hint, `requestId`) and sends it with `sendNativeMessage`.
7. **The browser launches the host.** Chromium/Firefox start `NovaDownload.NativeHost.exe`
   themselves because the extension manifest and the native messaging manifest agree on the id.
8. **The host validates the frame, then relays it** to the running application over the per-user
   pipe. If the application is not running, the host starts it (one attempt, 20 s budget) and
   retries once.
9. **The application validates everything again** (URL scheme, length, credentials, file name,
   destination), checks the setting, checks for an in-flight transfer of the same URL and for a
   replayed `requestId`, then calls the existing engine.
10. **The engine downloads** into the configured destination, showing up in the normal list with
    pause/resume/cancel/retry/queue and the usual history.
11. **The popup shows the outcome**: the accepted file name, "already in the queue", or a precise
    error ("NovaDownload is not running", "the native host is not registered", "browser integration
    is turned off", "playlist streams are not supported").

If the application window is configured to come forward on browser downloads
(*Bring NovaDownload to the front when a download arrives*, on by default), the app restores and
focuses itself when a request is accepted.

---

## 4. Detection: what is attempted, and what is honest

Detection is layered. Every layer is **read-only** and every layer is optional in the sense that
nothing is sent until the user clicks.

| Layer | Mechanism | Permission | Honest limits |
| --- | --- | --- | --- |
| Direct links | `a[href]` whose extension or `download` attribute says media | activeTab | Only exposes URLs already in the markup |
| Media elements | `video`/`audio` `currentSrc` and `src`, plus `<source>` children | activeTab | Empty for blob-backed players |
| Preload hints | `link[rel=preload]` with `as=video|audio|track` | activeTab | Sites rarely use it |
| Page metadata | `og:video`, `og:audio`, `twitter:player:stream` | activeTab | Often a wrapper page, not the file |
| Resource timing | `performance.getEntriesByType('resource')` | activeTab | Sees what the tab fetched; needs playback to have started |
| Browser downloads | `downloads.onCreated` | `downloads` | **Listed only** — never redirected, never blocked |
| Deep network scan | `webRequest.onBeforeRequest` (observational) | optional `webRequest` + `http/https` hosts, off by default | Sees media requests on every site; costs a permission grant |

Formats recognised and offered: **MP4, WebM, OGV, MOV, M4V, MKV, MP3, M4A, AAC, OGG, OGA, OPUS,
WAV, FLAC, FLV** (plus any direct link the user marks as a download).

Formats recognised but **refused with an explanation**:

| Case | What the user sees |
| --- | --- |
| `.m3u8` / `.mpd` | *Playlist stream (HLS/DASH) — NovaDownload downloads single files, not segmented streams.* |
| `blob:` / MSE playback | *Playback URL generated by the page (blob:/MSE) — there is no direct file to download.* |
| `data:` / `filesystem:` | Same class of refusal, with the scheme named |
| Anything not http/https | *Only http and https links can be sent.* |

**Authentication and signed URLs.** If a file needs the browser's cookies, a bearer token, a
short-lived signature or a `Referer` check, the desktop application will get a 401/403 and the
transfer will fail with the server's status. That is a real limitation, stated here rather than
papered over: the extension forwards a URL, not a browser session. There is no cookie sharing,
by design.

`<all_urls>` is never requested. The extension declares `activeTab`, `scripting`, `storage`,
`contextMenus`, `downloads`, `nativeMessaging`, and **optionally** `webRequest` with
`http://*/*` + `https://*/*` only when the user flips *Deep network detection* in the extension
settings (a browser permission prompt appears at that moment, and it can be revoked).

---

## 5. Transport and security model

### Native messaging (browser ↔ host)

* Framing: 4-byte **little-endian** length prefix, then that many bytes of UTF-8 JSON — the
  documented Chrome/Firefox wire format, identical on the pipe.
* Hard cap: **1 MiB** per message, checked before allocating the body.
* Invalid UTF-8 is rejected rather than replaced; truncated headers/bodies throw instead of
  returning partial data.
* `sendNativeMessage` (one message per host launch) is used instead of a long-lived `connectNative`
  port: the browser manages process lifetime and there is no half-open socket to babysit.

The browser does not launch the host bare. Chromium appends the calling origin and a window handle
(`NovaDownload.NativeHost.exe "chrome-extension://<id>/" --parent-window=0`), Firefox appends the
manifest path and the extension id. The host **accepts and ignores** those arguments — treating
them as unknown would make it print usage and exit before reading the first frame, which surfaces
in the browser as *"Error when communicating with the native messaging host"*. A test in
`NativeHostProcessTests` launches the host with both browsers' argument shapes and asserts it
still answers.

### Named pipe (host ↔ app)

* Name: `NovaDownload.Browser.<sanitized domain>.<sanitized user>` (`NOVADOWNLOAD_PIPE_NAME`
  overrides it for tests).
* Created with `PipeOptions.CurrentUserOnly`: the DACL restricts the pipe to the interactive user,
  so another account on the same machine cannot connect even knowing the name.
* **No TCP, no HTTP listener, no port.** A unit test asserts the protocol file contains no
  `TcpListener`/`HttpListener`.
* One request per connection, 20 s per-connection budget, one framed response.

### What the application will and will not accept

Everything arriving from a browser is untrusted input. `DownloadRequestValidator` is the only gate
and it runs **in the app**, not only in the extension:

| Input | Rule |
| --- | --- |
| `action` | must be one of `ping`, `status`, `open`, `download`, `torrent` (case-insensitive) |
| `url` | absolute, `http`/`https` only, ≤ 2048 chars, no control characters, no `user:pass@`, fragment stripped |
| `url` (action `torrent`) | magnet URI carrying `xt=urn:btih:` (40 hex or 32 base32), an existing local `.torrent`, or an `http(s)` URL ending in `.torrent`; ≤ 8192 chars, no control characters |
| `fileName` | ≤ 180 chars after sanitising; separators/`:`/control characters/`.`/`..` rejected; Windows reserved device names rejected; invalid characters replaced |
| `directory` | absolute local path only; UNC/`\\?\`/relative/`..` segments rejected; drive must exist |
| `requestId` | `[A-Za-z0-9._:-]{1,64}` |
| payload size | 1 MiB framing cap, plus a 256 KiB application cap |

No part of a request is ever passed to a shell, `cmd`, `powershell`, or a process start. The host
never takes a command from the wire: it only relays the request to the pipe (or starts the *known*
application executable to service it).

The `torrent` action exists for the `magnet:` protocol handler and the command line, which reach the
engine through the same pipe. It is deliberately **not** part of what the extension may use: the
extension never sends it, and a `download` request carrying a magnet URL is still refused with
`UNSUPPORTED_SCHEME` — a swarm transfer has to be tagged as such. The *Browser integration* setting
does not gate it either: switching the extension off must not stop a magnet link from opening the
application.

### Settings gate

*NovaDownload → Settings → Browser integration → Accept downloads sent by the browser extensions*
must be on. When it is off, every download request is answered `DISABLED` with an actionable
message, and the popup shows it. `ping`/`status` still answer so the popup can explain itself.

### Duplicate protection

Two independent guards:

1. **Replay id** — the same `requestId` inside a 20 s, 256-entry window returns `DUPLICATE` with
   the original engine id; the engine is not called again.
2. **In-flight URL** — a live (not finished) transfer with the same URL returns `DUPLICATE`.
   Finished transfers deliberately do **not** block a new request: downloading the same file again
   later is a legitimate user action, and the engine's existing name-conflict logic
   (`file (1).mp4`) applies.

---

## 6. Installing the extensions

The installer copies the packages to `%LOCALAPPDATA%\NovaDownload\extensions\` (per user, and
deliberately **not** removed on uninstall, so an unpacked extension keeps working). You can also
build them yourself:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File browser-extensions\build-extensions.ps1
# -> release\chrome-extension\  release\edge-extension\  release\firefox-extension\  (+ deterministic .zip)
```

### Chrome / Edge (unpacked, no store submission)

1. Open `chrome://extensions` (Chrome) or `edge://extensions` (Edge).
2. Enable **Developer mode**.
3. **Load unpacked** → select
   `%LOCALAPPDATA%\NovaDownload\extensions\chrome` (or `...\edge`, or `release\edge-extension`).
4. The extension appears with the id `pnemgdlefleemkhignbgaclaelffmbah`. If you see a different
   id, the manifest lost its `"key"` — reload it from the package the build produced.
5. Open the popup → **Settings** → **Test connection**.

Chrome and Edge share one codebase and one package shape; the `edge` package differs only in the
name metadata, so either package loads in either browser. Both are prepared for store upload as a
zip with `manifest.json` at the archive root (`release\chrome-extension.zip`).

### Firefox

1. Open `about:debugging#/runtime/this-firefox`.
2. **Load Temporary Add-on…** → pick `manifest.json` inside
   `%LOCALAPPDATA%\NovaDownload\extensions\firefox`.
3. Firefox requires `strict_min_version` 128.0 for `optional_host_permissions`; the temporary
   add-on disappears when Firefox closes (that is Firefox's rule for unsigned builds).
4. A permanently installable build needs signing by AMO, which is outside this repository's scope.

**No store publication is claimed or performed by this project.** The packages are store-shaped
(MV3 for Chromium, `browser_specific_settings.gecko.id` for Firefox, no remote code, no
`host_permissions`) but nothing has been uploaded.

---

## 7. Native messaging registration

The installer runs `NovaDownload.NativeHost.exe --register` as the invoking user (per user, no
admin rights). The same command is available from **Settings → Browser integration → Register**.

What `--register` writes:

| Item | Path |
| --- | --- |
| Chromium manifest | `%LOCALAPPDATA%\NovaDownload\NativeMessaging\com.novadownload.host.json` |
| Firefox manifest | `%LOCALAPPDATA%\NovaDownload\NativeMessaging\com.novadownload.host.firefox.json` |
| Chrome registration | `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.novadownload.host` |
| Edge registration | `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.novadownload.host` |
| Firefox registration | `HKCU\Software\Mozilla\NativeMessagingHosts\com.novadownload.host` |

The registry default value is the **full path to the manifest**, which is what Chromium and Firefox
expect. The manifest itself contains the absolute host path and the allow-list:

```json
{
  "name": "com.novadownload.host",
  "description": "NovaDownload browser integration host",
  "path": "C:\\Program Files\\NovaDownload\\NovaDownload.NativeHost.exe",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://pnemgdlefleemkhignbgaclaelffmbah/"]
}
```

Firefox uses `"allowed_extensions": ["novadownload@vanta.local"]` instead of `allowed_origins`.

Manual registration (portable builds, or after moving the folder):

```powershell
& "D:\NovaDownload\NovaDownload.NativeHost.exe" --register
& "D:\NovaDownload\NovaDownload.NativeHost.exe" --status     # what is registered right now
& "D:\NovaDownload\NovaDownload.NativeHost.exe" --unregister # remove everything
```

`--register` writes the registry entries **even for browsers that are not installed yet**, so
installing Chrome afterwards works without re-running anything.

### The URL protocol fallback

The installer also registers `novadownload://`. It is a *fallback*, not the primary channel: it
cannot be used by the extension (browsers block `sendNativeMessage`-less navigation from a popup
click chain in a way that would need a visible navigation), but it lets a link on a web page open
NovaDownload directly:

```
novadownload://download?url=https%3A%2F%2Fexample.com%2Fclip.mp4&name=clip.mp4
```

The application parses it with `StartupRequest`, validates it with the same validator, and forwards
it to a running instance through the same pipe (then exits without opening a second window).
`--url <value>` on the command line is equivalent.

---

## 8. Extension settings

| Setting | Default | Effect |
| --- | --- | --- |
| List media that the browser itself downloads | on | Subscribes to `downloads.onCreated` and lists direct media in the panel. Observation only. |
| Deep network detection | off | Requests the optional `webRequest` + host permission, then lists media requests on any site. |
| "Send to NovaDownload" context menu entries | on | Adds *Send link* / *Send page link* to the right-click menu. |
| Connection test | — | Sends `ping` and prints the app version, the protocol version and the default destination. |

The popup follows the browser's own language (French and English catalogs ship, with an English
fallback compiled in so a missing catalog can never render a raw key).

---

## 9. Diagnostics

| Symptom | Cause | Fix |
| --- | --- | --- |
| *Error when communicating with the native messaging host* on every call | The host process exited before answering — usually an unhandled start-up failure or an argument it refused | Run `NovaDownload.NativeHost.exe --status` from a terminal; check `%LOCALAPPDATA%\NovaDownload\logs\native-host.log` for a `host started` line. No line means it never reached the loop |
| *The NovaDownload native messaging host is not registered for this browser* | No registry entry / manifest, usually after moving the install folder | `NovaDownload.NativeHost.exe --register`, then reload the extension |
| *NovaDownload is not running* on every click | Host cannot find the app | `--status` prints the resolved application path and the source; check `--app` |
| Popup status says *Browser integration is turned off* | Setting disabled | Settings → Browser integration → enable |
| Download accepted but fails with 403/401 | Signed/cookie-protected URL | Not supported by design; download from the page itself or use a direct link |
| *Playlist stream* message | HLS/DASH | Not supported; single files only |
| Nothing detected on a page that clearly plays video | blob/MSE player | Enable *Deep network detection*, start playback, rescan |
| Extension id differs from `pnemgdlefleemkhignbgaclaelffmbah` | Manifest lost `"key"` | Reload from `release\edge-extension` |

Log files:

| File | Contents |
| --- | --- |
| `%LOCALAPPDATA%\NovaDownload\logs\novadownload.log` | app startup, bridge start, accepted requests |
| `%LOCALAPPDATA%\NovaDownload\logs\native-host.log` | host relay decisions, launch attempts, failures |
| `%LOCALAPPDATA%\NovaDownload\startup.log` | only written when the app fails during startup |
| `%APPDATA%\NovaDownload\settings.json` | settings (or `%NOVADOWNLOAD_DATA_DIR%\settings.json` when that variable is set) |

Turning on **Verbose** (`NovaDownload.NativeHost.exe --verbose`) mirrors host diagnostics to
stderr; the browser discards stderr, so run the host from a terminal to see it.

---

## 10. Verifying the integration

Three independent layers, all runnable on this machine:

```powershell
# 1. extension code, catalogs, manifests, ids, detection logic, source hygiene (Node, no deps)
node tests\extension-tests\run-tests.js --package

# 2. contracts, validation, framing, dedupe, processor, installer/manifest parity (xUnit)
dotnet test tests\NovaDownload.UnitTests\NovaDownload.UnitTests.csproj

# 3. real pipe + real host process + real engine over a local HTTP fixture (xUnit)
dotnet test tests\NovaDownload.IntegrationTests\NovaDownload.IntegrationTests.csproj

# 4. a real browser: loads the unpacked extension, then drives the whole chain over CDP
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-browser-e2e.ps1
```

`tools\verify-browser-e2e.ps1` is the only one that needs a browser. It starts a throw-away
profile, loads `release\edge-extension`, navigates a tab to the extension's options page through
the DevTools protocol, calls the same code the popup calls, and asserts that a byte-identical file
lands in a temporary folder. It reports each check as PASS/FAIL and exits non-zero on failure.

It launches the real `NovaDownload.App.exe` with `NOVADOWNLOAD_DATA_DIR` pointing at a throw-away
folder, so the run never reads or writes the developer's own settings (and starts from the
defaults, where browser integration is on). The same variable is honoured by
`JsonSettingsService` and is the supported way to run the application against an isolated profile.
