<h1 align="center">Exo Shaders</h1>
<p align="center">
  <strong>No more shader compilation stutter.</strong> Exo Shaders compiles your games' shaders into your graphics
  driver's cache before you play, so a game doesn't stop to compile one the first time it meets a new effect.
</p>

Exo Shaders is the shader precompiler in [Exo Launcher](https://github.com/ImAvgErix/ExoLauncher)'s System > Shaders.
It ships with Exo and runs as its add-on; download Exo Launcher to use it.

> [!NOTE]
> Exo Shaders is an **unofficial fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller)**, based on
> SCSKiller and not affiliated with or endorsed by it. SCSKiller's own app and downloads are on its
> [repository](https://github.com/BlueHeisenberg/SCSKiller).

## What it does

- **Finds your games** in every launcher Exo Launcher reads, and shows which are compiled, which need it, and why.
- **Compiles ahead of time**, in its own process with the game closed: it reads the shaders a game ships with, works
  out the pipelines the game will build and compiles them into the driver's cache.
- **Keeps a queue**, and compiles again by itself when a driver update clears the cache, while your PC is idle.
- **Stays out of your way**: a compile pauses while you play, gives its memory back when paused, and holds its memory
  use down so the rest of your PC stays quick.
- **Records when it has to**: for a game whose files don't give its pipelines, an optional recorder learns them while
  you play a few minutes, then they compile.
- **Leaves anti-cheat games alone** beyond reading their files: no launching, no injecting and no recorder, unless you
  allow it for that game at your own risk.

Exo Shaders fixes shader compilation stutter only, not traversal or streaming stutter, and a shader it couldn't find or
record can still compile in game.

## Requirements

| | |
|---|---|
| App | [Exo Launcher](https://github.com/ImAvgErix/ExoLauncher) on Windows 11 x64 |
| GPU | NVIDIA or AMD |
| Games | DirectX 12, and DirectX 11 (on AMD after a recording) |

## How it fits

Exo Shaders is a headless service and a page that Exo shows in its System room; Exo talks to it only through messages
([engine/CONTRACT.md](engine/CONTRACT.md)). Its changes from SCSKiller are in
[engine/CHANGELOG.md](engine/CHANGELOG.md); SCSKiller's own are in [CHANGELOG.md](CHANGELOG.md).

## Build

The .NET SDK in `global.json` and Node.js:

```
pwsh engine\build\publish-service.ps1
cd ui
npm ci
npm run build
```

The service lands in `engine\out\service` and the page in `ui\dist`; Exo Launcher's release build takes both from
there. The native helpers come prebuilt from SCSKiller's release ([engine/prebuilt](engine/prebuilt)); their source is
[proxy](proxy). Design: [ARCHITECTURE.md](ARCHITECTURE.md).

## License

GPL-3.0-or-later ([LICENSE](LICENSE)), with an additional permission for Oodle, the Windows App SDK and graphics driver
libraries ([LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt)). Copyright the SCSKiller authors and Exo Shaders'
contributors. Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Not affiliated with SCSKiller, any GPU maker, engine maker or game publisher. All product names are trademarks of their
owners.
