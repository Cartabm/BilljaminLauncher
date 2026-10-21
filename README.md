# The Tower of Billjamin — Gothic Launcher v1.2.0

A Windows 10/11 C# WinForms launcher whose **home window reproduces the provided gothic launcher reference image**. This is a working launcher application, not a static mockup: its image-based controls execute the real game actions, status text comes from the actual installed game and GitHub release, and leaderboard names/scores come from the configured Supabase endpoint.

## Design and assets

- `Assets/launcher-reference.png` is the user-provided 1536×1024 design reference, embedded as the exact home-screen backing plate. It preserves the intricately textured medieval buttons, ornate frames, fantasy artwork, tower and sidebar. **Keep this asset in the project.**
- `Assets/dark-stone-tile.png` is a procedural dark stone tile covering the fictional status values/scoreboard entries in the reference, so they are replaced with **live data** rather than presented as real.
- `MainForm.cs` paints the reference with uniform scaling/letterboxing, and handles hit testing for the original textured button locations, mouse hover, window dragging and resizing. Default size: 1350×900; minimum size: 1250×850.
- Home-screen button zones: sidebar Play (return home), Settings, Leaderboard, About; window Minimize/Close; big green PLAY (launch game), CHECK UPDATES, UPDATE / REPAIR, OPEN GAME FOLDER, Settings gear, VIEW FULL LEADERBOARD.
- The full leaderboard shows up to 10 **real** ranked players. If the leaderboard is down/empty, the app does **not** display the fictional sample names from the background.
- F5 refreshes the game release check. Escape closes a secondary menu. The popup screens use the same dark stone and gold palette.
- STATUS: on opening, the launcher checks for its own update *before* checking the game version; if game assets match the manifest it confirms integrity. When a newer game release is available, PLAY is disabled pending an update. Offline users can play an already-installed game if integrity is not known to have failed.

## Build (Windows with .NET 10 SDK)

Open PowerShell **in the folder containing `TowerOfBilljaminLauncher.csproj`** and run:

```powershell
dotnet publish .\TowerOfBilljaminLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o .\publish
```

The output to test is `publish\BilljaminLauncher.exe`. The screenshot and material tile are embedded inside it; the player won't need separate image files. **This source package has not been compiled or run on Windows in the assistant's environment. Build and test before replacing the installed executable.**

To test safely, run `publish\BilljaminLauncher.exe` from its own build directory without moving it into `%LOCALAPPDATA%\TowerOfBilljamin\Launcher`. Its game actions still operate on the normal installation folder; don't click UPDATE / REPAIR until you intend to install/update game files.

## Release channels

- Game: `https://github.com/Cartabm/Billjamin` — `game.zip` + `manifest.json` on the latest GitHub release.
- Launcher: create a separate public `https://github.com/Cartabm/BilljaminLauncher` repository — `BilljaminLauncher.exe` + `launcher-manifest.json` on the latest GitHub release.

Publish the **launcher**, after testing, as `v1.2.0` in the separate launcher repository. Generate its manifest with:

```powershell
python .\tools\make_launcher_release.py .\publish\BilljaminLauncher.exe 1.2.0 .\LauncherRelease\1.2.0
```

Then attach `LauncherRelease\1.2.0\BilljaminLauncher.exe` and `LauncherRelease\1.2.0\launcher-manifest.json` to that GitHub release. **Do not** publish this to the game repo: the game checks `releases/latest` and expects game assets.

For a later launcher release, increment both `<Version>` in `TowerOfBilljaminLauncher.csproj` and `LauncherVersion` in `LauncherSettings.cs`, then publish new EXE and its SHA-256 manifest.

## How launcher self-update works

On startup, the launcher checks the **separate launcher repository** for a greater version. It downloads a single-file EXE, validates its SHA-256 using `launcher-manifest.json`, starts an out-of-process copy of itself to replace the running file, and restarts. It makes a backup/rollback attempt if replacement fails. Network failures are logged to `%LOCALAPPDATA%\TowerOfBilljamin\Launcher\launcher-update.log` and the existing launcher continues. If an older version was already distributed without a self-updater, players must replace it manually once.

Hash matching authenticates downloaded bytes against the associated public manifest, **not** the publisher: before a widespread launch, use signed release artifacts/manifest and Windows code signing where practical. This keeps the existing game distribution and old installed launcher untouched during development.

## Known design tradeoff

The highest-fidelity reproduction is achieved by embedding the *reference screenshot* as the underlying artwork and painting live fields over its fictional values. This is deliberate. To change the physical locations or text engraved on static button textures, edit/re-export the plate and update hit zones in `Area()` inside `MainForm.cs`. All operational status and leaderboard content is rendered dynamically; the decorative artwork is not separately editable in WinForms.
