# How No Magazine Camo works

Written for anyone reading the source — and particularly for [7Bpencil](https://github.com/7Bpencil),
whose [Weapon Camo And Stickers](https://github.com/7Bpencil/SPT.WeaponCamoAndStickers)
this is an addon for. It lists every place this mod reaches into that one, so that if any
of it is a problem, it is at least easy to find.

Nothing here modifies Weapon Camo And Stickers, its files, or its saved data. This is a
separate BepInEx plugin with a hard dependency on it.

Built against **1.19.0**. The floor is **1.18.0**, for one reason:
`CamoEditor.CalculateUIScaleMatrix`, which the per-weapon panel uses to match the
editor's UI scale, does not exist in 1.17.0. Every other member in the table below is
present in 1.17.0, 1.18.0 and 1.19.0 alike.

## The problem

Camo is projected onto a gun from a box around it. A magazine sits inside that box, so it
gets painted. During a reload the magazine slides out of the box and reverts to its stock
finish in your hands, then pops back to painted when it seats.

This addon offers two answers: keep the magazine out of the paint entirely, or carry the
paint along with it.

## The lever: stencil 3

Decals are drawn as cubes into GBuffer0 before lighting, and the decal shader compares a
stencil:

```
Stencil { Ref [_StencilType]  ReadMask 3  WriteMask 3  Comp Equal  Pass [_StencilPassOperation] }
```

`Plugin.GetItemStencilType` uses 2 for weapons, 1 for equipment and 0 for containers.
Every weapon part's material says `_StencilType = 2` out of the box, magazines included.

Only the low two bits are compared, and **3 is unused**. That is the whole basis of this
mod: a magazine set to 3 is skipped by every decal Weapon Camo And Stickers draws by
default, while 0 would pick up the map's own decals and 1 would pick up equipment camo.

This is the same lever the camo mod already pulls on itself — `Patch_PlayerBody_SetSkin`
sets the player's arms to 1 so gun decals skip them.

Materials are copied per magazine (`renderer.materials`, not `sharedMaterials`) and their
original stencil is kept, so removing the mod restores exactly what shipped. The copy is
made the first time a magazine is actually cleaned, not at spawn, and destroyed once the
magazine's pool object is gone -- Unity never frees `renderer.materials` copies itself.

**Only the player's own magazines are ever moved to 3.** The stencil is the game's
lighting category (see below), so it is kept off every model it does not need to be on.
A magazine is changed only if it hangs under a painted gun's decal root **and** either
that item is in one of the camo mod's `DecalCameras` preview sets (weapon inspect and
modding, the camo editor, inventory icons, the character screen) or the nearest
`EFT.Player` above it has `IsYourPlayer`, or it is under the local player's hands object
(`GameWorld.MainPlayer.HandsController.ControllerGameObject`). That last test is needed
because a firearm in hands is **not** under its `Player`: `WeaponPrefab.Init` detaches it
to the scene root. Bots' guns, other players' guns and loose magazines keep what they
shipped with. A painted gun picked up off a body is the player's from then on, slung or in
hand, and its magazine follows the player's setting from that frame.

## Finding magazines

Harmony postfix on **`AssetPoolObject.OnGetFromPool()`** — every model the game shows
comes out through it, and both `EFT.ObjectsFactory.PopOrCreate` and
`SetupGameObjectWithoutPool` set `ResourceType` before calling it.

Those two are the **game's** methods, not Weapon Camo And Stickers'. This addon does not
patch `ObjectsFactory`, and does not depend on how that mod tracks object creation — it
finds magazines through the game's own pool, independently. What it does need from that
mod is only the mapping in the table further down.

A magazine is `ItemTemplate.IsChildOf("5448bc234bdc2d3c308b4569")` (Magazine) and not
`IsChildOf("610720f290b75a49ff2e5e25")` (CylinderMagazine). Revolver and grenade launcher
cylinders are deliberately left alone — they read as part of the gun, not as something
that leaves it.

`AssetPoolObject.ResourceType` is a protected field whose type is obfuscated, so it and
its `ItemTemplate` are both looked up **by field name**. That is what makes this survive
SPT's launcher patch, which renames the type but not the field.

## Sticky camo

Every magazine that moves during a reload is a child of the gun. From the reload states in
`Player`: `OnMagAppeared` does `SetupMod(MagazineSlot.Slot, CreateItem(newMagazine, isAnimated: true))`,
so the new magazine is mounted the moment it appears in the hand, and the old one is only
removed at `OnMagPuttedToRig`. `SetupMod` parents the model to the slot's bone, and the
animation moves that bone.

Once per frame, on the first camera's `onPreCull`:

This loop runs in **every** mode, not only when something is set to stick: which
magazines are the player's is the answer that changes from frame to frame.

1. Map each spawned item's `SimpleDecalsHost.DecalsRoot` to its decals, and collect the
   item ids in every `DecalCameras` preview set.
2. For each live magazine, walk up to a decal root, then on to the owning `Player` or the
   local player's hands object. Not the player's (see above) — restore it.
3. The weapon resolves to keep-clean: mark it clean and stop. Otherwise compare its pose in root space against where it sits when seated.
4. Seated: restore it, and the gun's own decals paint it as usual, for free.
   Not seated: mark it clean and queue a draw.

"Mark it clean" no longer touches the magazine's materials (since 1.3.1): they keep the
gun's stencil, 2, and the clean stencil is stamped into the buffer only around the decal
passes that need it. See "The stencil is a lighting category" below.

Then per camera, on `onPreRender`, into a command buffer added only to a camera that
**already carries one of `DecalRenderer.CommandBuffers`**, on **`BeforeReflections`**.
The magazine is stamped to 3, every visible decal is drawn again with the decal root's
matrix replaced by `magazine.localToWorld * inverse(seatedPose)`, mirrors included,
through a per-decal material clone refreshed once a frame and set to stencil 3, and the
magazine is stamped back to 2 — all inside that one buffer.

The camo mod itself draws at **`BeforeLighting`** (`CameraEvent` 6 in 1.19.0's
`DecalRenderer`; earlier versions of this document said otherwise). Ours does not follow
it there, because Unity's deferred reflections pass runs in between and uses the stencil
buffer for its own probe culling. A cube testing `Comp Equal 3` after it can match
background geometry and paint a magazine-sized box of camo albedo onto it. The camo mod's
own decals test 0, 1 and 2, which that does not disturb.

At the seated pose that replacement matrix collapses to exactly the decal root's own
`localToWorldMatrix`, so the handover in and out of a reload is seamless by construction
rather than by tuning.

### Where "seated" comes from

The first attempt read `TransformLinks._cachedTransforms` — the bone poses
`ResetPositions` restores when a weapon returns to the pool. **It never answers.**
`TransformLinks.CacheTransforms` has no callers anywhere in `Assembly-CSharp` (checked
with a Cecil scan over every method body), so that array is editor-baked prefab data, and
on every weapon tested it holds nothing between the decal root and the magazine.

So the seat is learned by watching instead: a magazine that holds still for 12 frames is
a seated magazine, and its pose is cached per decal root. Because the cache is keyed on
the gun rather than on the magazine, a fresh magazine handed in by a reload inherits the
seat of the one it replaces, and is sticky on the way in as well as out.

A gun first seen mid-reload has no seat yet and behaves as vanilla until its magazine
settles once.

## The per-weapon panel

Weapon Camo And Stickers files a weapon's decals under `items/<itemId>.json`, where the
id comes from `Plugin.GetOriginalItemId` — so camo is already per weapon in the stash
rather than per weapon type. The per-weapon magazine choice is keyed the same way, and
kept in this mod's own file at `BepInEx/config/NoMagazineCamo/magazines.json`.

Deliberately **not** written into the camo mod's own JSON: those files carry a
`SchemaVersion` that is checked on load, and an extra field of ours would be a field a
later version strips or trips over. Presets are left alone for the same reason — the
magazine choice belongs to the weapon, not to a set of decals.

The panel is a Harmony postfix on `CamoEditor.DrawWindow`, drawn as a `GUI.Window` of its
own docked under the editor's live `WindowRect` and under its
`CalculateUIScaleMatrix()`. It is not a row inside the editor's window because
`CalculateDecalsWindowHeight` and its siblings work that window's height out section by
section before anything is drawn, and a row added to it would mean editing that
arithmetic from the outside.

`Plugin.OnGUI` calls `DrawWindow` on two different types — `CamoEditor` and
`CamoEditorError` — so patching the first fires once per frame.

## Everything this touches in Weapon Camo And Stickers

Public, used as-is:

| Member | Why |
| --- | --- |
| `Plugin.Instance` | the entry point for everything below |
| `Plugin.MirrorLeftRight` | so mirrored decals are mirrored the same way |
| `SimpleDecalsHost.DecalsRoot` | the transform a weapon's decals hang from |
| `ItemWithDecals.Decals`, `ItemsWithDecals.Items`, `.DecalsInfo` | the decals to redraw |
| `Decal.DecalTransform`, `.DecalMaterial`, `.DecalMaterialKeywordErase` | cloned per decal for the stencil-3 pass |
| `DecalInfo.IsVisible`, `.MirrorMode` | skip hidden decals, match mirroring |
| `ItemType` | the panel is only shown for weapons |
| `CamoEditor.IsOpened`, `.ItemId`, `.ItemType`, `.WindowRect` | where and whether to dock the panel |
| `CamoEditor.CalculateUIScale`, `.CalculateUIScaleMatrix` | so the panel matches the editor's scale |

Private, read by name through Harmony's `AccessTools`:

| Member | Why |
| --- | --- |
| `Plugin.ItemsWithDecals` | which items have decals, and what they are |
| `Plugin.InstanceIdToItemId` | to go from a spawned object to its item id |
| `Plugin.DecalCameras` | so preview cameras draw only their own item, like the camo mod's own pass; and which items are in a preview, so their magazines follow the player's setting |
| `Plugin.DecalRenderer` → `DecalRenderer.CommandBuffers` | to know which cameras decals are drawn on |

In the game itself, beyond `AssetPoolObject.OnGetFromPool`, a postfix on
`AmbientHighlight.UpdateAmbientBuffer` adds the fallback ambient quad described above,
and `AmbientHighlight.StencilType` / `GetQuadMesh` are read for it.

`CanCameraSeeDecals` is deliberately **not** copied: it goes through
`EFT.CameraControl.CameraManager`, which only exists under that name in the
launcher-patched assembly. Asking whether a camera already carries one of the camo mod's
command buffers is the answer that is correct on every build.

Harmony patches, all postfixes: `CamoEditor.DrawWindow`, `Decal.ChangeMaxAngle` and
`Decal.ChangePaintMode` (full wrap, below) and, in the game itself,
`AssetPoolObject.OnGetFromPool`.

### Full wrap at Max Angle 0

The decal shader drops a pixel when `dot(G-buffer normal, decal Y axis) < _MaxAngle`. At
the slider's minimum of 0 that keeps only surfaces facing the projection, so side-on
surfaces sit exactly on the cut-off and the normal map's grain decides pixel by pixel which
side each lands on -- a speckled gap along, say, the top of a sight hump on a gun painted
from the side. The shader does not clamp `_MaxAngle`, so with **Max Angle 0 covers
everything** on, a postfix on `Decal.ChangeMaxAngle` writes -1.01 to the decal's material
instead, and the box paints every surface in it, far side included (planar projection, so
side-on faces still show the texture stretched).

- Only the material changes. `DecalInfo.MaxAngle` -- what the editor shows, what is saved
  and what Fika sends -- keeps 0. The requested value is kept per decal in a
  `ConditionalWeakTable` so turning the setting off writes it back.
- **Erase decals are never changed**: in Erase mode the shader reads `_MaxAngle` as a mask
  alpha cut-off, and below 0 the eraser would wipe its whole box. `Init` calls
  `ChangeMaxAngle` before `ChangePaintMode`, so the paint-mode postfix decides again.
- The magazine clones copy the material's properties every frame, so they wrap too.

**If any of these private reads are unwelcome, or there is a supported way to ask the
same questions, say so and it will be changed.** This mod never writes to the camo mod's
state or its files. The one thing it writes is `_MaxAngle` on a decal's own material, for
full wrap above.

## Failure behaviour

Every entry point is wrapped. If the private fields cannot be resolved at load, sticky
camo reports once and turns itself off, leaving the simple "keep magazines clean" mode
working. If the per-frame loop or the panel throws, it reports once, reverts what it
changed, and goes quiet — the camo mod is never left in a modified state, and a magazine's
materials are always restored to the stencil they shipped with.

## The stencil is a lighting category, not a free tag

**This is the part worth knowing before copying the trick, and it cost the most to find.**

The values in `GetItemStencilType` are not arbitrary tags chosen to keep decals apart.
They are the game's own lighting categories -- `AmbientHighlight.StencilType` names 0
Static, 1 Characters, 2 Hands -- and each item type is assigned the one it genuinely
belongs to. Armour is worn on a character, so it is Characters. A weapon is in your
hands, so it is Hands. Decals separating cleanly is a *consequence* of correct
categorisation, not the reason for it.

So there is no spare value. Measured in first person, a magazine is lit correctly at 2
and at nothing else: **0, 1 and 3 all render it dark.** Moving it off 2 to keep camo away
from it cannot be left standing by the time the lighting runs.

Nor can it be left standing through the passes *before* the lighting. Up to 1.3.0 the
magazine's materials wrote 3 in the G-buffer and it stayed 3 until a restore at
`BeforeLighting`; everything in between -- `Wetting`, the sun's distant-shadow mask,
SSAO, `Stencil Shadow` -- saw a magazine that was not hands. So since 1.3.1 the materials
keep 2 and the magazine is on 3 only inside two short windows. As logged on a stock
outdoor map:

```
G-buffer            the magazine writes the gun's stencil, 2
BeforeReflections   ... Wetting,
                    [ours] stamp 3 · carried decals · stamp 2,
                    AOSix, static decals
the deferred reflections pass
BeforeLighting      AO, SetupDistantShadow, ComputeDistantShadowMask, SSAO Mask,
                    game decals, Stencil Shadow,
                    [ours] stamp 3, [WeaponCamoAndStickers] Deferred Decals, [ours] stamp 2
the lighting passes
AfterLighting       AmbientHighlight, one stencil-tested full-screen quad per category
```

The `BeforeLighting` window needs our two buffers directly either side of the camo mod's,
and buffers on one event run in the order they were added. So the camo mod's buffer -- its
own object, from `DecalRenderer.CommandBuffers` -- is removed and re-added between ours.
It cannot be found by comparing against `camera.GetCommandBuffers`: that returns fresh
wrapper objects, never reference-equal to the camo mod's.

A stamp draws the magazine's LOD0 meshes with `Comp Always` / `Pass Replace` /
`ColorMask 0`, writing 3 or 2.

**Pulled toward the eye.** A stamp drawn exactly where the magazine is z-fights it: a
different shader computes the same depth a rounding error apart, so `LessEqual` passes on
a random subset of pixels that changes every frame. Up to 1.3.0 that left speckles of the
restore undone -- lit as "not hands", and only really visible in sunlight, so it showed
as a flicker on reloads outdoors and never indoors. Each mesh is therefore drawn with
`DrawMesh` at its `localToWorldMatrix` scaled about the camera by 0.998: every vertex
moves along its own view ray, so the outline on screen is identical and only the depth
shrinks, by about a millimetre at arm's length. Anything genuinely in front of the
magazine is still in front. A `SkinnedMeshRenderer` (none seen on a magazine) falls back
to `DrawRenderer` and the old risk.

The material is **`UI/Default`**, the one stock shader that exposes its whole stencil
state as properties (`_Stencil`, `_StencilComp`, `_StencilOp`, `_StencilReadMask`,
`_StencilWriteMask`) alongside `_ColorMask`, so this needs no shader of its own. It takes
its depth test from `unity_GUIZTestMode`, which is set to `LessEqual` **on the restore
material itself** -- unset, a magazine would write its stencil over whatever is in front
of it, including the arms the camo mod deliberately keeps on a different value.

**Never as a global.** Up to 1.1.0 it was set globally around the draws and then put
"back" to `Always`, which is not what the game leaves it at. Every world-space
TextMeshPro takes its depth test from that same global, so from the first restore on,
all of them drew through walls -- reported as AmandsSense's item and exfil labels
showing through walls (DrakiaXYZ/SPT-AmandsSense#6). A value on the material overrides
the global for that material only.

`MagazineAmbient` is the fallback for when that is not possible: if `UI/Default` cannot
be found, nothing can be stamped, so the magazine's materials are switched to 3 as before
1.3.1, the stencil stays at 3, and an extra ambient quad matching the Hands entries is
added to `AmbientHighlight`'s own buffer instead, through a **copy** of
`AmbientMaterial`. That copy is load-bearing rather than tidy -- a `CommandBuffer` holds
a material by reference, and the game mutates that material's stencil inside the loop
queueing its own quads, so setting it there would change every quad already queued and
take the world's ambient light with it.

## Logging

One line a session at default BepInEx levels, once sticky camo has a seat to work from.
Everything else is `Debug`, which a stock `BepInEx.cfg` does not write to disk. A shared
log is not the place to repeat per-weapon diagnostics.

## Known limitations

- **A gun picked up mid-reload** won't stick until its magazine settles once.
- **Two magazines in motion at once.** While the old and new magazine are both out, camo
  meant for one can briefly show on the other if they pass close together.
- **Idle animation.** If a gun's idle moves its magazine more than a millimetre from its
  seat, sticky camo draws continuously for that gun — still correct, just not free — and
  the seat is never learned.
- **Cost.** During a reload a painted gun's decals are drawn twice. Nothing extra
  otherwise.
