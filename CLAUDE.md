# No Magazine Camo — working notes

An addon for 7Bpencil's **Weapon Camo And Stickers** that changes what camo does to
magazines. Client-only BepInEx plugin, three Harmony postfixes, **no `spt-*` references**.

**[`docs/internals.md`](docs/internals.md) is the real document** — how it works, and
every place it reaches into the camo mod. Read that first. This file is only the things
you need in order to *work on* the repo.

## Layout

```
src/NoMagazineCamo.Client/
  NoMagazineCamoPlugin.cs   BepInPlugin, hard dependency on the camo mod, the two F12 settings
  MagazineStencil.cs        finds magazines as the pool hands them out; clean/restore their materials
  StickyCamo.cs             the per-frame loop: seat test, stencil switching, the extra decal draws,
                            and the buffer the restore below is appended to
  MagazineChoice.cs         the per-weapon override and its file
  EditorPanel.cs            the panel docked under the camo editor
  StencilRestore.cs         puts the magazine's stencil back before the lighting reads it
  MagazineAmbient.cs        fallback ambient quad, for when the restore is not possible
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

Built against Weapon Camo And Stickers **1.19.0**. The floor is **1.18.0** --
`CamoEditor.CalculateUIScaleMatrix`, which the per-weapon panel uses, does not exist in
1.17.0; everything else it touches is present from 1.17.0 on. Check the installed version
with `(Get-Item <dll>).VersionInfo.FileVersion`, and what a build actually linked against
with Cecil's `AssemblyReferences` — a stale reference is invisible otherwise.

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
- **`Prepare`, `Cleanup`, `TargetMethod` and `TargetMethods` are reserved names on a
  `[HarmonyPatch]` class.** Harmony calls them itself, passing nulls for arguments it does
  not recognise, and throws the whole class out if one fails — reported as a patching
  exception rather than as your bug. A private helper named `Prepare` cost a full
  build-and-test cycle here, with the patch silently never applying.
- **The carried decals go at `CameraEvent.BeforeReflections`, not `BeforeLighting`.** That
  is the event the camo mod uses, and the reason is the stencil: Unity's deferred
  reflections pass runs between the two and takes the stencil buffer for its own probe
  culling, so a decal cube drawn after it tests `Comp Equal 3` against bits that are no
  longer the camo mod's categories. Background geometry then matches and takes a
  magazine-sized box of camo albedo painted onto the world. The stencil restore still has
  to be at `BeforeLighting`, so the two jobs are two buffers on two events. Shipped in
  1.1.0; `docs/internals.md` has the full ordering.
- A `CommandBuffer` holds a **material by reference, not by value**. Mutating a material
  the game also queues draws with rewrites its already-queued draws, which is why both the
  ambient quad and the decal clones draw through copies.

## Status

- **`Stick to magazine`, the per-weapon panel and the stencil restore are all confirmed
  working in a raid.**
- Still unexercised: `Enabled = false`, and the `None` mode since the per-weapon work
  landed.
- **The stencil is a lighting category, not a free tag.** Measured: a magazine is lit
  correctly at 2 and dark at 0, 1 *and* 3. Do not go looking for a spare value again --
  see `docs/internals.md`.
- The Forge's addon guidelines (https://sp-mod.com/addon/guidelines/2658) are behind a
  login and **have not been read**. Check them before uploading.
- At default BepInEx levels the mod says little: `loaded`, one `settings` line (repeated
  only when an F12 setting changes), the count of weapons with their own choice, and then
  at most two more, once each — `putting magazine stencils back before lighting` and
  `sticky magazine camo is live`. Those last two are the ones worth asking for: they mean
  the restore found `UI/Default` and that a gun's seat was learned.
- Everything else is `Debug` — per-weapon seat learning, which of the three ways the
  prefab pose came up empty, the cached transform names and the magazine's bone chain.
  `Debug` is **not** in `LogLevels` in a stock `BepInEx.cfg`, so add it under
  `[Logging.Disk]` before asking anyone for a log.
