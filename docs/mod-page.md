# No Magazine Camo

An addon for **Weapon Camo And Stickers** that changes how camo treats magazines.

Camo is projected onto a gun from a box around it. Your magazine sits inside that box, so
it gets painted too — and then a reload pulls it out of the box and it turns back to its
stock finish in your hands.

Two ways out of that:

- **Keep magazines clean.** Presets, random bot camos and stickers stay off magazines.
  The rest of the gun is painted as before.
- **Or make the camo stick.** The magazine keeps the gun's camo and stickers the whole way
  out and back in.

Pick one globally in F12, or **per weapon** from a panel under the camo editor — so it
doesn't have to be all of your guns or none of them.

Presets don't need editing, and nothing is saved to your profile. Remove the addon and
everything is exactly as it was.

## Requirements

- SPT 4.1.5
- [Weapon Camo And Stickers](https://github.com/7Bpencil/SPT.WeaponCamoAndStickers) 1.19.0

## Install

Extract the zip into your SPT folder. You should end up with:

```
BepInEx/plugins/NoMagazineCamo/NoMagazineCamo.Client.dll
```

To uninstall, delete the `BepInEx/plugins/NoMagazineCamo` folder.

## Settings

Press F12 and open No Magazine Camo. Both settings apply immediately.

- **Enabled** (default: on). Turn it off and magazines take camo exactly as they would
  without this addon.
- **Camo on magazines** (default: None).
  - **None**: magazines are never painted.
  - **Stick to magazine**: magazines wear the gun's camo, and it stays on them during
    reloads.

### Per weapon

Open the camo editor on a weapon and a **Magazine** panel appears under it, with **Keep
clean** and **Stick**. Until you pick one, that weapon just follows the F12 setting, and
the panel shows you which way that falls. Picking either one sets it on that weapon from
then on.

It belongs to that one weapon rather than to the weapon type, and it stays put when you
switch that weapon's camo preset.

Revolver and grenade launcher cylinders are never affected.

## Performance

**None** costs nothing while you play. **Stick to magazine** draws the gun's camo a second
time, but only while a reload has the magazine out of place.

## Compatibility

- Client-only. No server mod.
- Works with Fika. Each client only changes how magazines look on that client.

## Credits

[7Bpencil](https://github.com/7Bpencil) for Weapon Camo And Stickers. Sticky camo draws
decals the same way that mod's `DecalRenderer` does.
