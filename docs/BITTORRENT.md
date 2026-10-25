# BitTorrent

NovaDownload has a native BitTorrent engine. A magnet link, a `.torrent` file or a `.torrent`
URL is a first-class transfer: it enters the same queue as an HTTP download, shows live swarm
telemetry, and is paused, resumed, cancelled and removed by the same buttons.

Engine: **MonoTorrent 3.0.2** (MIT), in `src\NovaDownload.DownloadEngine\Torrenting\`.

---

## 1. What it does

| Capability | State |
| --- | --- |
| Magnet links (`magnet:?xt=urn:btih:…`) | Yes — metadata fetched from the swarm, DHT and trackers both used |
| Local `.torrent` files | Yes — file picker in *New download*, or pass the path on the command line |
| Remote `.torrent` URLs (`https://…/file.torrent`) | Yes — metadata is fetched over HTTP first, the payload is then downloaded over BitTorrent |
| Multi-file torrents | Yes — files land in a folder named after the torrent |
| Trackers, DHT, PEX, LSD | Yes — each can be switched off in *Settings → BitTorrent* |
| Resume after restart | Yes — fast-resume data plus piece hash verification on start |
| Seeding after completion | Yes, and it is a *setting*; turn it off to stop at 100 % |
| Upload / download rate limits | Yes — applied live, no restart |
| UPnP / NAT-PMP port mapping | Yes, optional |
| Selective file download inside a torrent | **No** — see the gaps below |
| Proxy, RSS, scheduling, web seeds | **No** — see the gaps below |

---

## 2. Adding a torrent

Three shapes are accepted by the *New download* field, and they are all validated before the
engine is touched:

```
magnet:?xt=urn:btih:CE61E3074E00985ED27A657434A8633FC784928F&dn=payload.bin
C:\Users\you\Downloads\some-release.torrent
https://example.org/some-release.torrent
```

From the command line:

```powershell
NovaDownload.App.exe --torrent "C:\path\file.torrent"
NovaDownload.App.exe --torrent "magnet:?xt=urn:btih:…"
NovaDownload.App.exe "C:\path\file.torrent"
NovaDownload.App.exe "magnet:?xt=urn:btih:…"
```

The field label switches to *LINK OR TORRENT* and the two hint lines change once the value is
recognised as a torrent source, so the whole form explains itself before the transfer starts.

There is **no** window-level handling of `.torrent` double-clicks: the installer does not
associate the extension, so it never steals the default away from an existing client. Explorer
integration is a deliberate non-feature.

### Clicking a magnet link

`magnet:` **is** registered, because a link clicked anywhere in Windows has no other way in. The
installer writes `HKCU\Software\Classes\magnet\shell\open\command` pointing at the application, and
the application re-writes (or removes) that key on every start from the *Open magnet: links*
setting, so the registry and the checkbox never disagree.

```
HKCU\Software\Classes\magnet\
    (default)                    URL:NovaDownload magnet handler
    URL Protocol                 (empty)
    DefaultIcon                  <install folder>\NovaDownload.App.exe,0
    shell\open\command           "<install folder>\NovaDownload.App.exe" "%1"
```

Windows keeps **one** handler per scheme. Turning the setting on takes `magnet:` from any other
BitTorrent client the user has; turning it off deletes the key and gives the scheme back. The
installer marks the key `uninsdeletekey`, so uninstalling restores the previous situation instead
of leaving a dangling command.

The link reaches the engine through the same bridge as a browser request, with the action
`torrent` instead of `download`:

* if NovaDownload is **not** running, the process starts and queues the transfer;
* if it **is** running, the second process hands the link to the open window over the named pipe
  and exits — one window, one engine, one port, whatever the user clicked.

A torrent source is validated by `TorrentSourceRules.TryNormalize`, shared by the command line,
the bridge gate and the router: a magnet without a `xt=urn:btih:` info hash (40 hex or 32 base32
characters) never reaches the engine, and neither does a `.torrent` path that does not exist.

---

## 3. Where the data lands

| Torrent shape | Result |
| --- | --- |
| Single file | `<destination>\<file name>` |
| Several files | `<destination>\<torrent name>\…` |

The destination is the folder chosen in the form (or `Default download directory`). The row's
*Open folder* button selects the file, or opens the folder for a multi-file torrent, as soon as
the metadata is known.

Nothing is ever written outside the destination folder except the engine cache
(`%LOCALAPPDATA%\NovaDownload\torrent-cache`), which holds the DHT routing table, magnet metadata
and fast-resume data. It is safe to delete while the application is closed.

---

## 4. Status, telemetry, and what the row means

The queue status is a projection of the swarm state; it is not a second source of truth.

| Row shows | Swarm state | Meaning |
| --- | --- | --- |
| Queued | `Starting` | Manager created, connection setup in progress |
| Downloading | `Metadata` | Magnet only: waiting for the info dictionary to arrive from a peer |
| Downloading | `Hashing` / `FetchingHashes` | Verifying existing pieces on disk |
| Downloading | `Downloading` | Real transfer |
| Completed | `Seeding` | Every piece verified; still offering them to the swarm |
| Completed → Paused | `Paused` | Seeding stopped by you; data intact, resumable |
| Paused | `Paused` | Transfer stopped by you; verified pieces kept |
| Failed | `Error` | Message taken from the engine's error, shown in the row |

Telemetry, refreshed twice a second:

* **primary line** — download speed; while seeding it switches to upload speed;
* **row progress** — the bar, the whole percentage (`35 %`) and `downloaded / total` in bytes;
* **secondary line** — `N seeds · M peers`, counted from **live connections**: the engine's own peer
  manager only classifies a peer once its handshake and bitfield have arrived, so the pair figure is
  derived from the open connection count instead of from that lagging list. Seeds plus peers always
  add up to the number of peers actually connected;
* **tooltip** — swarm phase, info hash, ratio, upload rate, plus how many peers the tracker, DHT or
  peer exchange announced without a connection yet.

Ratio is `uploaded / max(downloaded, torrent size)`. A pure seeder receives almost nothing, so
the torrent's own size is the denominator floor — otherwise a seed would divide by zero and
report 0.00 forever.

Progress comes from the engine's bitfield: `manager.Progress` is a **0..100 percentage** in
MonoTorrent 3.0.2, and the row scales it against the torrent's size. While the engine sits in
`Hashing`, `HashingPaused`, `FetchingHashes`, `Metadata` or `Starting`, the bitfield may have been
restored from a fastresume file and is not evidence of anything yet, so the row holds its last
verified figure instead of trusting it. A resumed torrent therefore never opens at 100 % and then
collapses back to its real figure.

The row shows **Completed** only when the engine reports every piece *and* no `.!mt` file is left
under the destination. In-flight data is written as `<name>.!mt` and renamed on the final write;
that rename is the one piece of evidence a stale bitfield cannot fake.

---

## 5. Resume, cancel, remove — the destructive boundaries

| Action | What happens to the data |
| --- | --- |
| Pause | Nothing. Verified pieces stay on disk |
| Resume | Re-hashes only what is needed, continues from verified pieces |
| Cancel | **Deletes the partial data** for that torrent |
| Remove from list (transfer unfinished) | Same as Cancel |
| Remove from list (data complete) | **Keeps every byte**; only stops the swarm session |
| Uninstall the application | Keeps `%LOCALAPPDATA%\NovaDownload\torrent-cache`; the data lives where you put it |

The "is this complete?" decision is made from the manager (`manager.Complete`), discounted by the
`.!mt` check described in §4, and never from the transient queue status — so a row that is
mid-resume can never be mistaken for an unfinished transfer and lose its files, and a row whose
bits came from a fastresume file can never keep partial data under the "complete" exemption.

An `InfoHash` the engine already holds cannot be added twice — that is a MonoTorrent rule. Adding
the same source again returns the live transfer instead of throwing.

---

## 6. Network, port, firewall

Default listen port **51413**, TCP and UDP (UDP is the DHT socket). Change it in
*Settings → BitTorrent*; it takes effect on the next launch.

* Port forwarding (UPnP / NAT-PMP) is requested only when `Ask the router to open the port` is
  enabled. Disable it on networks where the router must not be touched.
* Windows Defender Firewall asks once for `NovaDownload.App.exe`. Allowing it (private networks)
  is what makes you connectable. Without it, outgoing connections still work — you simply cannot
  accept incoming peers, which is slower but functional.
* DHT is what makes a trackerless magnet link work. Turning it off means only tracker-announced
  peers are reachable.
* No proxy support: the engine connects directly.

Verifying the port from another machine:

```powershell
Test-NetConnection -ComputerName <your-ip> -Port 51413
```

---

## 7. Settings

*Settings → BitTorrent* maps one-to-one onto `AppSettings`:

| Setting | Field | Applied |
| --- | --- | --- |
| Listen port | `TorrentListenPort` | Next launch |
| DHT | `TorrentEnableDht` | Next launch |
| PEX | `TorrentEnablePex` | Next launch |
| Local peer discovery | `TorrentEnableLsd` | Next launch |
| Router port mapping | `TorrentEnablePortForwarding` | Next launch |
| Seed after completion | `TorrentSeedAfterCompletion` | Immediately for new completions |
| Upload limit (KiB/s, 0 = unlimited) | `TorrentMaxUploadKib` | Immediately |
| Download limit (KiB/s, 0 = unlimited) | `TorrentMaxDownloadKib` | Immediately |
| Connections per torrent | `TorrentConnectionsPerTorrent` | Next launch |
| Total connections | `TorrentMaxConnections` | Immediately |
| Open `magnet:` links (takes the scheme from another client) | `TorrentHandleMagnetLinks` | Immediately — the registry key is written or deleted on the spot |

AppSettings is JSON; an older `settings.json` without these keys loads with the defaults above,
so upgrading from 1.0.0 needs no migration.

---

## 8. The same queue, one entry point

`DownloadServiceRouter` is the only `IDownloadService` the application resolves. It sends

* `magnet:`, an existing `.torrent` path, or an `http(s)` URL ending in `.torrent` to
  `TorrentDownloadService`;
* everything else to `HttpDownloadService`;

and merges both snapshots newest-first. `PauseAsync` / `ResumeAsync` / `CancelAsync` /
`RemoveAsync` fan out to both engines — an id belongs to exactly one of them, and each side
ignores ids it does not own, so no second routing table can go stale.

Browser integration is unchanged: it still validates `http`/`https` only. A browser will never
hand a magnet link to the desktop application, because the extension protocol only carries media
URLs.

---

## 9. Diagnostics

```powershell
# engine session line, every start
Get-Content "$env:LOCALAPPDATA\NovaDownload\logs\novadownload.log" -Tail 20
```

Logged events: engine start (port, DHT, connection ceiling), torrent completion, failure with the
engine's message, remote metadata fetch, and pause/resume/remove failures. A torrent that never
leaves `Metadata` is almost always one of: no seeders, no tracker, DHT blocked by the network, or
the magnet carrying only a v2 hash for a v1 swarm.

The metadata wait has a hard ceiling (`TorrentEngineOptions.MetadataTimeout`, 5 minutes). After
that the row fails with an explicit message instead of sitting at 0 % forever.

---

## 10. Honest gaps

1. **No selective file download.** A multi-file torrent downloads everything. The engine API to
   set per-file priority exists (`TorrentManager.SetFilePriorityAsync`) and is the next step.
2. **No proxy, no RSS, no bandwidth scheduling, no web seeds.** None of it is wired.
3. **Magnet links from a second instance do not forward to the running one.** The `novadownload://`
   protocol forwards over the browser bridge, which only validates `http`/`https`; a torrent passed
   on the command line to a second instance is queued by that instance, in its own window.
4. **Seeding does not survive an application restart by design.** Completed rows are re-added only
   when you add the source again; the data is verified instantly, so this is cheap.
5. **No `.torrent` file association**, deliberately — it would steal the default handler.
6. **Private-tracker credentials** (passkeys embedded in announce URLs) are used as-is from the
   `.torrent`; they are never stored in settings or logs.

---

## 11. Verification

`tests\NovaDownload.IntegrationTests\TorrentEngineTests.cs` runs real BitTorrent traffic over the
loopback interface — no tracker, no DHT, no internet:

| Test | Proves |
| --- | --- |
| `Torrent_File_Source_Is_Detected_And_Seeds_Verified_Data` | Source detection for all four shapes; a local `.torrent` hashes existing data and reaches 100 % with the right name, size, path and info hash |
| `Service_Seeds_And_A_Second_Swarm_Member_Gets_Identical_Bytes` | The service really listens and serves: a bare `ClientEngine` downloads 2 MiB and the bytes are identical; upload telemetry and a positive ratio appear on the seeding row; a connected peer is never reported as `0 seeds · 0 peers` |
| `Cancel_Removes_The_Partial_Data_And_Stops_The_Row` | Cancel unlinks the partial file and drops the row |
| `A_Partial_Download_Never_Reports_Completed` | A destination holding a quarter of the payload reports roughly 25 %, never a finished row — pins the 0..100 `manager.Progress` scale |
| `A_Repeat_Source_Returns_The_Live_Transfer_Instead_Of_Failing` | The duplicate-source guard returns the existing id |
| `Status_And_Control_Round_Trip_Through_The_Seeding_Row` | Pause on a seeding row stops the seed, resume restores it, and removing a complete row keeps the data |

Pause and remove were both fixed *by* these tests: the first version refused to pause a
completed-but-seeding row, and removed a mid-resume row by deleting its files. The premature
"Terminé" report was found the same way: `manager.Progress` reads 0..100, the row divided it as if
it were 0..1, so any progress past one percent rendered as a full bar and a full byte count.
