# Exo Shaders changelog

Exo Shaders is Exo Launcher's shader precompiler add-on, an unofficial fork of
[SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) (GPL-3.0-or-later). SCSKiller's own changes are in the
repository's root `CHANGELOG.md`.

## [Unreleased]

**Precompiled shaders built on this PC**

- A compiled DirectX 12 game also gets Windows' precompiled shader database (Advanced Shader Delivery) built on the PC
  from its compile, with the graphics driver's own compiler, at idle priority after the compile. Exo registers it for
  the game's exe, and the game then loads those pipelines ready-made: not bound by the driver cache's size or cleanup.
  A driver update rebuilds it while the PC is idle. A game with a database from its store keeps the store's.
  Microsoft's compiler is downloaded the first time it's needed. Needs Windows 11 24H2 and a driver with Windows'
  compiler support (NVIDIA RTX 621.01 and newer).
- A game a store delivered precompiled shaders for, installed again from another store (or as another copy), gets
  them too: the store's database lists every pipeline the game builds, independent of driver and folder, so Exo builds
  this PC's database from it for that copy, with no compile or recording of its own. Matched by the exe's file name and
  the game's name together, never over a database of the copy's own.

**Statuses and times you can trust**

- An Unreal game some of whose files no key opens (Teamfight Tactics) reads "Ready · some files encrypted" and compiles
  the rest. It said "Not supported" while its compile was offered and ran. Only a game whose shaders all stay
  encrypted is not supported.
- A game says what it waits for. One whose launcher clears the shader cache at its first start of a new install or
  season (Marvel Rivals), or whose Vulkan shaders come with Steam's download at its first start, says "Start it once
  from Steam" instead of asking for a recording. One whose ray tracing alone needs a recording reads "Ready, ray tracing
  needs a recording", and Compile does the rest.
- Compile times are learned on this PC. A game's estimate is its own compile's rate on this driver; until it has one, a
  stand-in marked as rough: its own rate on another driver or from a stopped compile, its engine's on this driver, this
  PC's, or the vendor's default. Reading its shaders, planning and preparing its files count in the time. A stale game
  is estimated for its new pipelines, and after a game update for its new ones up to all of them, since its cache may no
  longer hold the rest. On NVIDIA 621 every estimate had fallen to 450 pipelines a second, because the rates compiles
  measured were looked up under another key: Counter-Strike 2 read 5 min, where its one cold compile took 1 h 22 min.
- A rewarm onto a cache that still held the game no longer counts as a compile: NVIDIA kept DirectX 11 caches across
  the 621 update, and VALORANT's 33 s rewarm made its 4-min compile read "under a minute". Such a rate is never kept as
  the game's, and its time isn't shown as the last compile's. A compile's time no longer counts the minutes it was
  paused while you played.

**Built on SCSKiller 1.3.0's own source**

- The engine is SCSKiller 1.3.0 built from its published source, in place of the engine recovered from the
  1.3.0-beta.2 program. The branch shares SCSKiller's history, so later SCSKiller releases merge in.
- Everything Exo Shaders added is carried over unchanged for Exo: the headless service and its protocol, the fixed
  settings policy, the quarter-of-RAM memory budget, the driver-update task, Exo's page, and the readers and launchers
  below. The native helpers are SCSKiller 1.3.0's own (`engine/prebuilt/SOURCE.md`).
- The game page follows 1.3.0's: Left out says how many shaders weren't seen in a recording yet, and with recording on
  it says to keep playing instead of asking for a recording.
- Games no longer mention the community database ("in the community database for Patreon supporters", "not in the
  community database yet"): it needs a Patreon sign-in, which Exo Shaders never has.
- A game whose new shaders wait to compile when the PC is idle is queued again after Exo restarts, and one held back
  by another compile is queued once that compile ends.
- On a PC with 64 GB of RAM or more, compiling gets a quarter of the RAM (SCSKiller 1.3.0's Auto), not 16 GB.
- Every game with the recorder is checked for anti-cheat at each pass again: skipping unchanged games could miss an
  update that installs anti-cheat deeper in the game folder.

**From SCSKiller 1.3.0**

- Fixes from the final release: Wuthering Waves from its own launcher is read as Unreal; a busy PC no longer loses a
  game's recorder for good; a big compile at its memory limit keeps its threads; The Last of Us Part II Remastered and
  other games with Sony's crash reporter compile for the game's own exe; Mafia: The Old Country and other games that
  start NVIDIA's crash reporting with shader debug info use their compiled shaders; STAR WARS: Galactic Racer compiles
  into the cache the game reads on AMD.
- Naughty Dog games, God of War and God of War Ragnarök, Ghost of Tsushima, Ratchet & Clank: Rift Apart, the Riot
  Client's games, and DirectX 11 games on AMD, as in 1.3.0.

**New engine: Glacier 2**

- 007 First Light compiles without a recording: Exo Shaders reads IO Interactive's Glacier 2 archives (RPKG) and the
  shaders in the game's material effects, each with the root signature the game creates for it. It was "Not
  supported yet". Its ray tracing pipelines still come from a recording. Updates that patch the archives are read
  too: a patch's effects replace the ones it updates.

**Engine-support updates**

- Exo Shaders now reads SCSKiller's engine-support updates, as SCSKiller does when you are signed out: after a scan, at
  most about once a day, the rules that decide what a game compiles, game verdicts and Maximum suggestions, each used
  only when it is signed with SCSKiller's own key. The first one adds Black Myth: Wukong's engine.
- Official recordings SCSKiller publishes for everyone, and the shared upscaler pipelines, download without an
  account.
- Nothing is sent: no Report a problem, no recording sharing, no diagnostics, no daily check, no Patreon or backer
  sign-in and no community database.

**Carried over from the decompiled engine**

- Games from every launcher Exo reads: the Epic Games Launcher (also its registry data folder), Legendary and Heroic,
  GOG Galaxy (HKLM and HKCU), Exo's own gogdl installs and Heroic's GOG list, Amazon Games and Nile, Ubisoft Connect,
  Battle.net and EA (both registry views, Origin's folders), Xbox's ModifiableWindowsApps, and Exo's own library.
- Sekiro: Shadows Die Twice, Marvel's Spider-Man Remastered and Miles Morales (their asset archives), Unity games that
  compress shaders with zstd, and Unreal keys from a game's public key list when its exe is protected (Wuthering Waves).
- A faster planner (the same plans) and a lighter idle engine.
