# No Magazine Camo

An addon for **Weapon Camo And Stickers** that changes how camo treats magazines.

- **Keep magazines clean.** Presets, random bot camos and stickers stay off magazines.
  The rest of the gun is painted as before.
- **Or make the camo stick.** Normally a reload pulls the magazine out of the camo's box,
  and it turns back to its stock finish in your hands. With this on, the magazine keeps
  the gun's camo the whole way out and back in.

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

## Settings

Press F12 and open No Magazine Camo. Both settings apply immediately.

- **Enabled** (default: on). Turn it off and magazines take camo exactly as they would
  without this addon.
- **Camo on magazines** (default: None).
  - **None**: magazines are never painted.
  - **Stick to magazine**: magazines wear the gun's camo, and it stays on them during
    reloads.

Revolver and grenade launcher cylinders are never affected.

## Performance

**None** costs nothing while you play. **Stick to magazine** draws the gun's camo a second
time, but only while a reload has the magazine out of place.

## Compatibility

- Client-only. No server mod.
- Works with Fika. Each client only changes how magazines look on that client.
