# Windows installer

How the NovaDownload installer is built, what it puts on disk, how upgrades and uninstalls behave,
and how to diagnose a failed install.

---

## 1. Toolchain

| Tool | Version used for the shipped build | Where it comes from |
| --- | --- | --- |
| .NET SDK | 9.0.319 | <https://dotnet.microsoft.com/download> |
| Inno Setup | 6.7.3 | <https://jrsoftware.org/isdl.php> (or `winget install -e --id JRSoftware.InnoSetup`) |
| Node.js | v24.20.0 (only for the extension test suite) | <https://nodejs.org> |

Inno Setup installs per user by default; the compiler lands at
`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`. The build script looks in that location, then in
`%ProgramFiles(x86)%\Inno Setup 6`, then honours an `NOVADOWNLOAD_ISCC` environment variable.
`ISCC.exe` carries no version resource, so the build reads the version from the Inno Setup uninstall
registry entry and records it in `release\BUILD-MANIFEST.txt`.

No other third-party tools are required. Nothing is downloaded at build time beyond the NuGet
packages the projects already reference.

---

## 2. Building the release

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-release.ps1
```

That single command performs the whole release, in order:

1. **Toolchain check** — `dotnet --version`, `node --version`, Inno Setup version, git revision.
2. **Stage the payload** — `dotnet publish` the application *and* the native messaging host
   self-contained for `win-x64` into the same folder (`build\app`), with ReadyToRun and no PDBs.
   Publishing both into one folder means **one** copy of the .NET runtime serves both executables.
3. **Verify the staged host** — runs `NovaDownload.NativeHost.exe --identity --json` and fails the
   build if it does not start or does not report the expected extension id. This is the check that
   proves the shared-runtime layout is sound before it is packaged.
4. **Build the extensions** — calls `browser-extensions\build-extensions.ps1`, then stages the
   three packages and three zips for the installer.
5. **Portable archive** — deterministic zip of `build\app`.
6. **Compile the installer** — `ISCC.exe /DAppVersion=<version> installer\NovaDownload.iss`.
7. **Checksums and manifest** — `release\checksums\SHA256SUMS.txt` and `release\BUILD-MANIFEST.txt`.
8. **Verify the packages** — runs the Node suite in `--package` mode against the release folders.

Useful switches: `-SkipInstaller`, `-SkipPortable`, `-SkipExtensions`, `-SkipTests`,
`-Version 1.2.3`, `-Configuration Release`.

Output layout:

```
release\
  windows-installer\NovaDownload-1.0.0-win-x64-setup.exe     (~46 MB)
  portable\NovaDownload-1.0.0-portable-win-x64.zip           (~61 MB)
  chrome-extension\        chrome-extension.zip
  edge-extension\          edge-extension.zip
  firefox-extension\       firefox-extension.zip
  checksums\SHA256SUMS.txt
  BUILD-MANIFEST.txt
```

The extension zips are byte-reproducible (fixed entry timestamps, forward-slash entry names), so
their SHA-256 is stable across rebuilds. The setup executable is **not** byte-reproducible — Inno
embeds a build timestamp — so treat its hash as "this exact artifact", not as a reproduction check.

To compile the installer alone (after staging):

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\NovaDownload.iss
```

Or open `installer\NovaDownload.iss` in the Inno Setup IDE and press Compile.

---

## 3. What the installer does

### Identity

| Value | Value |
| --- | --- |
| Installer `AppId` | `{7C2E6B31-1E6D-4C37-9F52-6C2D0E6A4B21}` |
| Uninstall registry key | `...\CurrentVersion\Uninstall\{7C2E6B31-1E6D-4C37-9F52-6C2D0E6A4B21}_is1` |
| Display name | `NovaDownload 1.0.0` |
| Display icon | `<install folder>\NovaDownload.App.exe` |
| Minimum Windows | 10.0.17763 (Windows 10 1809) |
| Architecture | x64 only (`x64compatible`) |

The `AppId` is mirrored in `BrowserIntegrationInfo.InstallerAppId`; a unit test reads the `.iss`
file and fails if the two ever drift apart. The same constant is what `AppLocator` uses to find the
application from the native messaging host.

### Installation mode

The wizard offers a choice (all users / current user) via
`PrivilegesRequired=admin` + `PrivilegesRequiredOverridesAllowed=dialog commandline`:

| Mode | Folder | Registry | Needs admin |
| --- | --- | --- | --- |
| All users (default) | `C:\Program Files\NovaDownload` | HKLM (files) + HKCU (protocol + host) | yes |
| Current user (`/CURRENTUSER`) | `%LOCALAPPDATA%\Programs\NovaDownload` | HKCU (protocol + host) | no |

Silent and per-user examples:

```powershell
NovaDownload-1.0.0-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="%TEMP%\nova-install.log"
NovaDownload-1.0.0-win-x64-setup.exe /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
NovaDownload-1.0.0-win-x64-setup.exe /ALLUSERS   /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

### Files and shortcuts

| Item | Location |
| --- | --- |
| Application, engine, runtime, native host | `<install folder>` |
| Start Menu group | *NovaDownload* → *NovaDownload*, *Uninstall NovaDownload* |
| Desktop shortcut | optional task, **unchecked** by default |
| Browser extension packages | `%LOCALAPPDATA%\NovaDownload\extensions\{chrome,edge,firefox}` + the three zips |

The extension packages are per user and marked `uninsneveruninstall`: an unpacked extension keeps
working after NovaDownload is uninstalled, because the browser is still loading files from that
folder. The installer also offers a post-install entry that opens that folder.

### Registry

```
HKCU\Software\Classes\novadownload                      -> "URL:NovaDownload Protocol", "URL Protocol"
HKCU\Software\Classes\novadownload\DefaultIcon          -> "<install folder>\NovaDownload.App.exe,0"
HKCU\Software\Classes\novadownload\shell\open\command   -> "<install folder>\NovaDownload.App.exe" "%1"

HKCU\Software\Classes\magnet                            -> "URL:NovaDownload magnet handler", "URL Protocol"
HKCU\Software\Classes\magnet\DefaultIcon                -> "<install folder>\NovaDownload.App.exe,0"
HKCU\Software\Classes\magnet\shell\open\command         -> "<install folder>\NovaDownload.App.exe" "%1"
```

The integration keys are written per user (`Root: HKCU` plus the full subkey path in the script), the
same hive the application itself writes through `UrlProtocolRegistrar`, so no admin rights are
needed. All values are removed on uninstall (`uninsdeletekey` / `uninsdeletevalue`).

`magnet:` is a real handler, not a fallback: clicking a magnet link in any browser or in Explorer
opens NovaDownload and queues the transfer. Windows keeps a single handler per scheme, so this
replaces whatever BitTorrent client held `magnet:` before. The *Open magnet: links* setting in the
application rewrites or deletes the same key at runtime, and uninstalling removes it, which hands
the scheme back rather than leaving a command pointing at a deleted executable.

Native messaging registration is **not** done with `[Registry]` entries: the installer runs

```
<install folder>\NovaDownload.NativeHost.exe --register
```

with `runascurrentuser`, so the three `HKCU\...\NativeMessagingHosts\com.novadownload.host`
entries always belong to the user who is installing, even in an all-users elevated install. That is
also why uninstall runs `--unregister` with `runascurrentuser` and a `RunOnceId`.

### Dependencies

**There is no prerequisite.** The payload is a self-contained `win-x64` publish, so .NET 9 does not
have to be installed, and no runtime version mismatch is possible. The cost is size (~46 MB
compressed); the benefit is that a clean machine installs without a second download, without a
reboot and without elevation in per-user mode.

---

## 4. Upgrade

Upgrading means running a newer setup over the installed copy. Because the `AppId` is stable, Inno
Setup:

* reuses the previous install folder (`UsePreviousAppDir`, default on),
* closes a running NovaDownload through the Restart Manager before replacing files
  (`CloseApplications=yes`), and does **not** restart it (`RestartApplications=no`),
* keeps `%APPDATA%\NovaDownload\settings.json` (settings, language, destination, history) and
  `%LOCALAPPDATA%\NovaDownload\extensions` untouched, because neither lives in the install folder,
* updates the Start Menu shortcut, the uninstall entry and the URL protocol command in place.

Verified behaviour: settings, queue history and the registered native messaging host survive an
upgrade, and the version shown in *Windows Settings → Apps* follows the new setup.

Because the native host path is absolute, upgrading to a **different install folder** requires
re-running `--register`; the installer does that automatically at the end of every install.

---

## 5. Uninstall

Use *Settings → Apps → NovaDownload → Uninstall*, the Start Menu entry, or:

```powershell
& "$env:LOCALAPPDATA\Programs\NovaDownload\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
```

What is removed: the install folder, the Start Menu group, the desktop shortcut (if created), the
`novadownload://` and `magnet:` protocol registry keys, the native messaging registry entries and
manifests (via `--unregister`), and the uninstall entry itself.

What is deliberately kept, and announced in a message at the end of the uninstall:

| Kept | Why |
| --- | --- |
| `%APPDATA%\NovaDownload\` (settings, queue history) | Reinstalling restores the user's setup |
| `%LOCALAPPDATA%\NovaDownload\extensions\` | Unpacked browser extensions are still loaded from there |
| `%LOCALAPPDATA%\NovaDownload\logs\` | Diagnostics |
| `%LOCALAPPDATA%\NovaDownload\torrent-cache\` | DHT routing table, magnet metadata and fast-resume data; deleting it only costs a re-check, never downloaded data |
| The torrents themselves | They live in the destination folder the user chose, which the installer never writes to |

There is **no** `[UninstallDelete]` section and no silent deletion of profile data anywhere in the
script; a unit test asserts both. Deleting those folders by hand is the documented way to remove
everything.

---

## 6. Code signing

The setup executable and the application binaries in this release are **not signed**. Windows
SmartScreen will therefore show *"Windows protected your PC"* until enough reputation accumulates,
and an enterprise deployment will need an exception or its own signed build.

To sign, after `build-release.ps1` and before distribution:

```powershell
$cert = "..."; # path to a .pfx, or a certificate in the store
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $cert installer\..\release\windows-installer\NovaDownload-1.0.0-win-x64-setup.exe
```

`SignTool` parameters can also be passed to ISCC (`SignTool=` in `[Setup]`); they are intentionally
absent from the shipped script so that it runs on a machine with no certificate. No certificate,
key or password is stored in this repository.

---

## 7. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Inno Setup 6 was not found` | ISCC missing | Install Inno Setup or set `NOVADOWNLOAD_ISCC` |
| `MSB3021 / MSB3027` while publishing | NovaDownload is running from the build output | Close it; the build also stops stale copies under the repository automatically |
| `--identity failed in the staged folder` | Shared-runtime layout unusable on this machine | Publish the host into its own folder (the script's error message says so) |
| Installer refuses to start: "requires Windows 10.0.17763" | Too old a build of Windows 10 | Update Windows, or rebuild with a lower `MinVersion` |
| Uninstall entry missing | Installer was cancelled before *Finish* | Re-run the setup |
| Browser says the native host is not registered | Installed per user by a different account, or the host path moved | `NovaDownload.NativeHost.exe --register` as the account that runs the browser |
| Extensions not visible in the browser | Unpacked extensions are never auto-installed | Load the folder from `edge://extensions` / `chrome://extensions` (see `BROWSER_INTEGRATION.md`) |
| `PrivilegesRequired admin ... per-user areas` warning at compile time | Expected: the extension folder is intentionally per user | No action; documented above |
| Silent install (for example `/CURRENTUSER /VERYSILENT`) exiting with code 5, the log showing `RegCreateKeyEx ... code 5` and `User canceled the installation process` | The URL protocol entries were written with `Root: HKCR`, which is the elevated hive for an all-users install; the per-user run therefore could not write there and Inno rolled back | Fixed: the entries now use `Root: HKCU` with the full subkey path. The install completes with exit code 0 and registers HKCU\Software\Classes\novadownload |
| A torrent sits at 0 peers / 0 B/s and never leaves `Metadata` | Incoming connections blocked: Windows Defender Firewall prompt was dismissed, the listen port is not forwarded, or the network is CGNAT. Trackerless magnet links also need DHT to leave the local network | Allow `NovaDownload.App.exe` on **private** networks in *Windows Defender Firewall → Allow an app*; enable *Settings → BitTorrent → Ask the router to open the port* (UPnP / NAT-PMP); or forward port **51413** TCP+UDP on the router. Check reachability from outside with `Test-NetConnection -ComputerName <ip> -Port 51413`. Outgoing-only still works, just slower |
| A torrent listening port is already in use | Another client (qBittorrent, Transmission…) holds 51413 | Change *Settings → BitTorrent → Listen port*; the engine cannot bind and falls back to no incoming peers |
| Torrent row fails with "No peer returned the torrent metadata" | Magnet link with no live seeders, or a v2-only hash against a v1 swarm | Verify the link has seeders; re-copy it from the source. The 5-minute ceiling is deliberate — see `BITTORRENT.md` §9 |

Installation log: run with `/LOG="%TEMP%\nova-install.log"`. Uninstall log:
`%TEMP%\unins000.log` (or the path shown in the uninstall dialog).

---

## 8. Post-install verification checklist

```powershell
# 1. files and version
Get-Item "$env:ProgramFiles\NovaDownload\NovaDownload.App.exe" | Select-Object Name, VersionInfo
# (per-user install: %LOCALAPPDATA%\Programs\NovaDownload)

# 2. uninstall entry
Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C2E6B31-1E6D-4C37-9F52-6C2D0E6A4B21}_is1" |
    Select-Object DisplayName, DisplayVersion, InstallLocation

# 3. native messaging registration (all three browsers)
& "$env:ProgramFiles\NovaDownload\NovaDownload.NativeHost.exe" --status

# 4. URL protocol
Get-ItemProperty "Registry::HKEY_CURRENT_USER\Software\Classes\novadownload\shell\open\command"

# 5. extension packages
Get-ChildItem "$env:LOCALAPPDATA\NovaDownload\extensions"

# 6. the app starts and the bridge listens
Start-Process "$env:ProgramFiles\NovaDownload\NovaDownload.App.exe"
Get-Content "$env:LOCALAPPDATA\NovaDownload\logs\novadownload.log" -Tail 20
```
