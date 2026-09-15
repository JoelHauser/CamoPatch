# No Magazine Camo — working notes

An addon for 7Bpencil's **Weapon Camo And Stickers** that changes what camo does to
magazines. Client-only BepInEx plugin, two Harmony postfixes, **no `spt-*` references**.

**[`docs/internals.md`](docs/internals.md) is the real document** — how it works, and
every place it reaches into the camo mod. Read that first. This file is only the things
you need in order to *work on* the repo.

## Layout

```
src/NoMagazineCamo.Client/
  NoMagazineCamoPlugin.cs   BepInPlugin, hard dependency on the camo mod, the two F12 settings
  MagazineStencil.cs        finds magazines as the pool hands them out; clean/restore their materials
  StickyCamo.cs             the per-frame loop: seat test, stencil switching, the extra decal draws
  MagazineChoice.cs         the per-weapon override and its file
  EditorPanel.cs            the panel docked under the camo editor
  MagazineAmbient.cs        the ambient quad that relights magazines on the clean stencil
scripts/pack.ps1            build + zip into dist/
docs/internals.md           how it works, for other modders
docs/mod-page.md            draft text for the mod page
```

## Building

`-SPTPath` is required, and must be an SPT install with the camo mod in it. Run these
through **PowerShell, not Bash** — Bash mangles Windows paths passed as arguments.

```powershell
scripts\pack.ps1 -SPTPath <install>                 # build, zip into dist\
scripts\pack.ps1 -SPTPath <install> -Install        # also copy the DLL into that install
scripts\pack.ps1 -SPTPath <install> -CamoModDir <dir> -GameAssembly <patched Assembly-CSharp.dll>
```

Releases are published through GitHub Releases. No binaries are committed.

## The Assembly-CSharp on disk is not always the one the game runs

**This is the trap that costs the most time here.** SPT ships a binary patch for the game
assembly at
`SPT_Runtime\SPT_Data\Launcher\Patches\SPT-core\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll.delta`.
It is HDiffPatch format (header `HDIFF13&zstd`) and `SPT.Launcher.exe` applies it when the
game is first started. It **renames obfuscated types to readable ones**:
`EFT.ObjectsFactory`, `EFT.CameraControl.CameraManager`, `ItemIconCreator`,
`BotCreatorClient`, `IconsHash`, `JsonType.ResourceTypeInfo`.

An install that has never been launched through the Launcher still holds the unpatched
original. Compiled against that, the camo mod looks like it has ~23 unresolved type
references and appears incompatible; against the patched copy it has none that matter.

To build a patched copy without touching an install
([HDiffPatch releases](https://github.com/sisong/HDiffPatch/releases), `windows64` zip):

```
hpatchz -f <install>\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll <the .delta> <out>\Assembly-CSharp.dll
```

Pass the result as `-GameAssembly`. The prepatcher (`BepInEx\patchers\spt-prepatch.dll`)
renames nothing — it only patches enums and runs `PluginValidator`.

Two consequences worth keeping in mind:

- Anything resolved **by field name** survives the rename; anything resolved by type name
  may not. That is why `AssetPoolObject.ResourceType` and its `ItemTemplate` are looked up
  by name, and why `CanCameraSeeDecals` is not copied from the camo mod.
- "Verified against my install, broke on someone's clean install" is usually this.

## Conventions

- GUID `com.mybutthasarash.nomagazinecamo`.
- The version lives in **two** places — the csproj `<Version>` and `PluginVersion` — and
  `pack.ps1` refuses to pack if they disagree.
- The zip holds only `BepInEx/plugins/NoMagazineCamo/NoMagazineCamo.Client.dll`. No README
  at the top of the zip: a loose file in an archive meant to be extracted over an SPT
  folder lands in the install root as litter.
- No `spt-*` references, deliberately. SPT's `PluginValidator` only rejects a plugin whose
  `spt-*` references disagree with the running server, and one with none is skipped
  outright. Harmony comes from BepInEx itself.
- Never modify the installed camo mod or its data. This addon is read-only against it.

## Status

- **`Stick to magazine` and the per-weapon panel are both confirmed working in a raid.**
- Still unexercised: `Enabled = false`, and the `None` mode since the per-weapon work
  landed.
- The Forge's addon guidelines (https://sp-mod.com/addon/guidelines/2658) are behind a
  login and **have not been read**. Check them before uploading.
- The mod logs **one** line a session at default BepInEx levels once sticky camo works:
  `sticky magazine camo is live`. Everything else is `Debug` — per-weapon seat learning,
  which of the three ways the prefab pose came up empty, the cached transform names and
  the magazine's bone chain. `Debug` is **not** in `LogLevels` in a stock `BepInEx.cfg`,
  so add it under `[Logging.Disk]` before asking anyone for a log.
