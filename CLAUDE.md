# SPT-NoMagazineCamo -- working notes for Claude

An addon for 7Bpencil's **Weapon Camo And Stickers** that changes what camo does to
magazines. Two settings in F12, both live:

- **Enabled** -- off hands magazines back to the camo mod untouched.
- **Camo on magazines** -- `None` keeps them unpainted; `Stick to magazine` makes
  the gun's camo stay on a magazine when a reload carries it out of the camo's box.

Revolver and grenade launcher cylinders are never touched. Client-only BepInEx
plugin, one Harmony postfix, **no `spt-*` references**.

**Nothing in this repo has ever run in a raid.** Everything below was read out of
the game assemblies, the camo mod's source and release DLL, and SPT's item database
by static analysis -- which is not the same as it working.

## The box this is built on

| | |
| --- | --- |
| SPT install | `C:\HUH` |
| SPT version | 4.1.5 (updated from 4.1.3 on 2026-09-15; client plugins, server, prepatcher and delta all verified against the 4.1.5 archive) |
| EFT client | `0.16.9.5.40743` -- the same for 4.1.3 and 4.1.5 |
| Camo mod | 1.19.0, installed in `C:\HUH` (byte-identical to the release). References `spt-reflection 4.1.5`; 1.18.0 is the 4.1.2 build |
| Camo mod source | https://github.com/7Bpencil/SPT.WeaponCamoAndStickers |

```
scripts/pack.ps1 -SPTPath C:\HUH                         # once the camo mod is installed and the game has been launched once
scripts/pack.ps1 -SPTPath C:\HUH -CamoModDir <dir> -GameAssembly <patched Assembly-CSharp.dll>
scripts/pack.ps1 -SPTPath C:\HUH -Install                # also copies the DLL into BepInEx/plugins/NoMagazineCamo
```

**Run those through PowerShell, not Bash** -- same `C:HUH` mangling trap as
BarrelHealing and SPT-Casino.

## The Assembly-CSharp on disk is not the one the game runs

**This cost the most time here, and it is true of every repo built against `C:\HUH`.**

SPT ships a binary patch for the game assembly:
`SPT_Runtime\SPT_Data\Launcher\Patches\SPT-core\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll.delta`.
It is **HDiffPatch** format (header `HDIFF13&zstd`), and `SPT.Launcher.exe` applies it
(`ASharpHDiffPatch`) when the game is started. The patch **renames obfuscated types to
readable ones**: `EFT.ObjectsFactory`, `EFT.CameraControl.CameraManager`,
`ItemIconCreator`, `BotCreatorClient`, `IconsHash`, `JsonType.ResourceTypeInfo`...

`C:\HUH` has never been launched through the Launcher -- there is no backup or patched
copy anywhere under it -- so `Managed\Assembly-CSharp.dll` (15,994,432 bytes, dated
2025-10-16) is the **unpatched** original. Against it, the camo mod 1.19.0 had 23
unresolved type references and looked incompatible. Against the patched copy it has
none that matter. SPT-Casino's "verified against C:\HUH, broke on a player's clean
install" is very likely the same thing.

To build a patched copy without touching the install
([HDiffPatch releases](https://github.com/sisong/HDiffPatch/releases), `windows64` zip):

```
hpatchz -f C:\HUH\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll <the .delta> <out>\Assembly-CSharp.dll
```

The 4.1.5 delta applied to `C:\HUH`'s original gives **16,233,472 bytes**. Pass it as
`-GameAssembly`. The prepatcher (`BepInEx\patchers\spt-prepatch.dll`) does **not**
rename anything: it only patches enums and runs `PluginValidator`.

**The decompile used for everything below was of the unpatched assembly**, which is why
it shows names like `_E6C3`. Names were re-checked by reflection against the patched
4.1.5 copy; control flow was not re-read.

## How the camo mod decides what gets paint

It does not paint parts. Each decal is a cube drawn into GBuffer0 before lighting
(`DecalRenderer.cs`). Its shader (`Unity/.../DecalDynamic.shader`) has:

```
Stencil { Ref [_StencilType]  ReadMask 3  WriteMask 3  Comp Equal  Pass [_StencilPassOperation] }
```

Decals use 2 for weapons, 1 for equipment, 0 for containers
(`Plugin.GetItemStencilType`); erase decals use `DecrementWrap`. Every weapon part's
material says `_StencilType = 2` out of the box, magazines included. The camo mod
already pulls the same lever itself: `Patch_PlayerBody_SetSkin` sets the player's arms
to 1 so gun decals skip them.

**Only the low two bits are compared, and 3 is unused** -- 0 takes the map's own
decals and 1 takes equipment camo. That is why magazines are set to **3**, and it is
what sticky camo draws on. The game's `AmbientHighlight.StencilType` names 0/1/2
Static/Characters/Hands and has no 3.

## How this is put together

```
NoMagazineCamoPlugin.cs   BepInPlugin, hard dependency on 7Bpencil.WeaponCamoAndStickers, the two settings
MagazineStencil.cs        finds magazines as the pool hands them out; Magazine = its materials, clean/restore
StickyCamo.cs             'Stick to magazine': seated test, stencil switching, the extra decal draws
```

### Finding magazines (every mode)

- **Hook: `AssetPoolObject.OnGetFromPool()`** -- public, virtual, non-generic, in both
  assemblies. `PopOrCreate` calls it on a popped object; `SetupGameObjectWithoutPool`
  calls it after `Init(resourceType, isStub: true)`. The only override is
  `PlayerPoolObject`'s, which calls `base`.
- **`ItemTemplate.IsChildOf("5448bc234bdc2d3c308b4569")`** (Magazine) and **not**
  `IsChildOf("610720f290b75a49ff2e5e25")` (CylinderMagazine, parent of
  SpringDrivenCylinder). Node IDs checked in `SPT_Data\database\templates\items.json`.
- **`AssetPoolObject.ResourceType`** is a protected struct field -- `_E6C3` unpatched,
  `JsonType.ResourceTypeInfo` patched -- holding `public ItemTemplate ItemTemplate`.
  Both looked up **by field name**, which is why the rename does not matter.
- **Materials are copied** (`renderer.materials`) and their original stencil kept, so
  `Restore()` puts back exactly what shipped. Renderers under a nested pool object
  (cartridges) are skipped.

### Sticky camo

**Every magazine that moves during a reload is a child of the gun.** From the reload
states in `Player.cs`: `OnMagAppeared` does
`SetupMod(MagazineSlot.Slot, CreateItem(newMagazine, isAnimated: true))` -- the new
magazine is mounted on the gun the moment it appears in the hand -- and the old one is
only removed at `OnMagPuttedToRig` (`RemoveMod`). `SetupMod` parents the model to the
slot's **bone** (`InsertItem` -> `SetParent(Bone)`), and the animation moves that bone.
All magazine models are `MagazineInHandsVisualController : WeaponModPoolObject`.

Once per frame (first camera's `onPreCull`):

1. Map every spawned camo item's `SimpleDecalsHost.DecalsRoot` to its decals (camo
   mod's private `ItemsWithDecals` + `InstanceIdToItemId`).
2. For each live magazine, walk up to a decal root. None: `Restore()`.
3. Compute its **rest pose** in root space, taking each bone's local pose from
   `TransformLinks._cachedTransforms` where the game has one (what `ResetPositions`
   puts animated bones back to when a weapon returns to the pool).
4. Within 1mm / ~1 degree of rest: **`Restore()`** -- the gun's own decals paint it.
   Otherwise **`Clean()`** and queue a draw.

Then per camera (`onPreRender`), into a command buffer that is only ever added to a
camera **already carrying the camo mod's own buffer** (`DecalRenderer.CommandBuffers`),
so it always runs after it: every visible decal again, with the decal root's matrix
replaced by `magazine.localToWorld * inverse(rest)`, mirrors included, through a
per-decal material clone refreshed once a frame and set to stencil 3. Preview cameras
are filtered through the camo mod's `DecalCameras`, like its own draw.

Asking the camo mod which cameras it draws on, rather than copying its
`CanCameraSeeDecals`, is deliberate: that method goes through `CameraManager`, which
only exists in the patched assembly.

## Untested, and what to look for

- **Does `_cachedTransforms` hold the magazine bone?** It is serialized into each weapon
  prefab, so it cannot be read from code. If not, the log names the weapon and its
  magazine behaves as vanilla in sticky mode -- not broken, just not sticky. **Check
  this first.**
- **Stencil 3 in the game's own shaders.** Look for a magazine lit or shadowed
  differently from the gun in first person, in both modes.
- **Erase decals** are copied too; an eraser over the magazine should keep erasing it
  mid-reload.
- **Idle animation.** If a gun's idle nudges its magazine past 1mm, sticky camo draws
  continuously for that gun -- still correct, just not free.
- **Cost.** During a reload a painted gun's decals are drawn twice. Nothing extra
  otherwise.
- **BepInEx debug log** lists every magazine and how many stencil materials it has.

## Publishing

- GUID `com.mybutthasarash.nomagazinecamo`, following SPT-Casino's registered prefix.
- Version lives in the csproj `<Version>` and `PluginVersion`; `pack.ps1` refuses to
  pack if they disagree.
- The zip holds only `BepInEx/plugins/NoMagazineCamo/NoMagazineCamo.Client.dll`.
- The Forge's addon guidelines page (https://sp-mod.com/addon/guidelines/2658) is
  behind a login and **has not been read**. Check it before uploading.
- `releases/mod-page.md` is the draft page text.

## Where this was left off

2026-09-15: 1.1.0 -- F12 settings and sticky camo -- built against the patched 4.1.5
Assembly-CSharp and the camo mod 1.19.0 release DLL. 1.0.0 (clean only, stencil 1) was
built but never released. Installed to `C:\HUH\BepInEx\plugins\NoMagazineCamo`
(built with `-GameAssembly` pointing at the patched copy, since the install had not been
launched yet). Not committed, never run.
