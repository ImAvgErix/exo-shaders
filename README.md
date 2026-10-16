# Exo Shaders

Exo Shaders is the shader precompiler behind **Library > Shaders** in [Exo Launcher](https://github.com/ImAvgErix/ExoLauncher). It compiles each game's shaders for the graphics driver you have now, so a game doesn't stutter the first time it meets a new effect, and compiles them again after a driver update while your PC is idle.

It is an unofficial fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) by the SCSKiller authors, released under the same license. It is not affiliated with or endorsed by the SCSKiller authors. Exo Shaders makes no calls to SCSKiller's servers: no account, no community recordings, no diagnostics and no daily check. It only downloads the codec libraries and public archive keys a game needs, from GitHub.

## What's here

| Folder | What it is |
| --- | --- |
| `engine/` | The engine service (`ExoShaders.Service`, .NET), built on SCSKiller's core. Exo starts it hidden and talks to it as newline-delimited JSON over its standard input and output (`engine/CONTRACT.md`). |
| `engine/prebuilt/` | SCSKiller's native helpers, unmodified, from SCSKiller 1.3.0-beta.2 (see `engine/prebuilt/SOURCE.md`). |
| `ui/` | The Shaders page (React), shown inside Exo in its own frame. |
| `install-addon.ps1` | Installs a local build as Exo's shader add-on for this PC. |
| `test/` | Scripts used to test the engine on a real PC. |

Exo Launcher ships a built copy of this program in its installer, under `addons\shaders` in its app folder. Exo Launcher itself is a separate, proprietary program; it runs Exo Shaders as its own process and is not covered by this license.

## Build

Requires the .NET SDK in `engine/global.json` and Node 24.

```powershell
pwsh engine\build\publish-service.ps1   # engine -> engine\out\service
cd ui; npm ci; npm run build; cd ..     # page   -> ui\dist
pwsh install-addon.ps1                  # optional: install for Exo on this PC
```

## License

GPL-3.0-or-later ([LICENSE](LICENSE)), with an additional permission under section 7 for the Oodle compression library, the Windows App SDK and graphics driver libraries ([LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt)). Copyright (C) the SCSKiller authors and Exo Shaders contributors. Third-party components are listed in [engine/THIRD-PARTY-NOTICES.md](engine/THIRD-PARTY-NOTICES.md).

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
