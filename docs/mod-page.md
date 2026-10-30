# No Magazine Camo

An addon for **Weapon Camo And Stickers** that fixes two things about how camo sits on
your guns.

## Magazines

Camo is projected onto a gun from a box around it, a bit like a slide projector. Your
magazine sits inside that box, so it gets painted too. During a reload it slides out of
the box and turns back to its stock finish in your hands.

Pick what you want instead:

- **Keep clean.** Magazines never get camo or stickers. The rest of the gun is painted as
  usual.
- **Stick.** Magazines wear the gun's camo and keep it all the way through a reload.

Set it for all guns in F12, or **per weapon** from the **Magazine** panel that appears
under the camo editor. Revolver and grenade launcher cylinders are never touched.

## Full camo coverage

The projector only paints surfaces that face it. Edges that run along its beam, like the
top of a sight hump when the camo comes from the side, end up patchy or bare, even with
**Max Angle** at 0.

With this addon, a camo layer set to **Max Angle 0** paints everything inside its box:
those edges, and the other side of the gun too. Any value above 0 works as it always did,
so stickers are unaffected unless you set them to 0.

Surfaces that run along the beam can still look a little stretched. That's how projected
camo works, not a missing patch.

## Settings

Press F12 and open No Magazine Camo. Changes apply immediately.

- **Enabled** (on): turns the magazine handling on or off.
- **Camo on magazines** (None): **None** keeps magazines clean, **Stick to magazine**
  makes the camo stick. A weapon with its own choice in the editor panel ignores this.
- **Max Angle 0 covers everything** (on): turn off to get the camo mod's own Max Angle 0.

## Requirements

- SPT 4.1.5
- [Weapon Camo And Stickers](https://github.com/7Bpencil/SPT.WeaponCamoAndStickers) 1.19.0

## Install

Extract the zip into your SPT folder. You should end up with:

```
BepInEx/plugins/NoMagazineCamo/NoMagazineCamo.Client.dll
```

To uninstall, delete the `BepInEx/plugins/NoMagazineCamo` folder. Your presets aren't
edited and nothing is saved to your profile, so everything goes back to how it was.

## Compatibility

- Client-only. No server mod.
- Works with Fika. Each player only changes how things look on their own screen.

## Credits

[7Bpencil](https://github.com/7Bpencil) for Weapon Camo And Stickers.
