# No Magazine Camo

An addon for [Weapon Camo And Stickers](https://github.com/7Bpencil/SPT.WeaponCamoAndStickers) that changes how camo treats magazines in SPT.

> **Pre-release.** Confirmed working in a raid against SPT 4.1.5 and Weapon Camo And Stickers 1.18.0.

## What it does

Weapon Camo And Stickers projects camo onto a gun from a box around it. The magazine sits inside that box, so it gets painted too. During a reload it slides out of the box and suddenly turns back to its stock finish.

This addon gives you two choices:

- **Keep magazines clean.** Presets, random bot camos and stickers stay off magazines, and the rest of the gun is painted as usual.
- **Make the camo stick.** Magazines wear the gun's camo and stickers, and keep them all the way through a reload.

You can pick either one for **each weapon separately**, from a panel under the camo editor, so it doesn't have to be all of your guns or none of them.

Presets don't need editing, and nothing is saved to your profile. Remove the addon and everything looks the way it did before.

## Requirements

- SPT 4.1.5
- [Weapon Camo And Stickers](https://github.com/7Bpencil/SPT.WeaponCamoAndStickers) 1.18.0 or newer — tested on 1.18.0

## Install

1. Download the latest zip from [Releases](https://github.com/JoelHauser/CamoPatch/releases).
2. Extract it into your SPT folder. You should end up with:

   ```
   BepInEx/plugins/NoMagazineCamo/NoMagazineCamo.Client.dll
   ```

To uninstall, delete the `BepInEx/plugins/NoMagazineCamo` folder.

## Settings

Press **F12** and open **No Magazine Camo**. Both settings apply immediately.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | On | Off: magazines take camo exactly as they would without this addon. |
| Camo on magazines | None | **None**: magazines are never painted.<br>**Stick to magazine**: magazines wear the gun's camo and stickers, and keep them during reloads. |

### Per weapon

Open the camo editor on a weapon and a **Magazine** panel appears under it:

| Choice | What it does |
| --- | --- |
| Keep clean | This weapon's magazine is never painted, whatever F12 says. |
| Stick | This weapon's camo stays on its magazine through a reload, whatever F12 says. |

Until you pick one, the weapon just follows the F12 setting, and the panel shows you which way that falls. Picking either button sets it on that weapon from then on, and F12 no longer moves it.

The choice belongs to that one weapon, not to the weapon type, and it stays put when you switch the weapon's camo preset. Weapons you never touch are not recorded at all.

Choices live in `BepInEx/config/NoMagazineCamo/magazines.json`. Delete a weapon's line to put it back on the F12 setting, or delete the file to reset every weapon.

Revolver and grenade launcher cylinders are never affected. They keep their camo like the rest of the gun.

## Performance

- **None** adds no cost while you play.
- **Stick to magazine** adds no cost while a magazine is seated. During a reload, the gun's camo is drawn a second time until the magazine is back in place.

## Compatibility

- Client-only. No server mod.
- Works with Fika. Each client only changes how magazines look on that client.
- Nothing belonging to Weapon Camo And Stickers is modified — not its files, its presets, or its saved data. This addon only reads.

## Known limitations

- **A gun you pick up mid-reload won't stick until the reload finishes.** Sticky camo learns where a gun's magazine sits by watching a seated one for a moment. Until it has, that gun's magazine behaves as if the addon were off in sticky mode.
- **Two magazines in motion at once.** When the old and new magazines are both out mid-reload, camo meant for one can briefly show on the other if they pass close together.
- **A Weapon Camo And Stickers update could break sticky camo.** It reads a few of that mod's internals to know what to redraw. If they move, sticky camo reports once in the log and turns itself off, leaving "keep magazines clean" working and your magazines' materials exactly as they shipped.
- **Lighting.** If a magazine looks lit differently from the rest of the gun in first person, please open an issue with a screenshot.

## How it works

Weapon Camo And Stickers only draws camo on surfaces marked as weapon parts. The addon gives magazines a different mark that none of that camo matches, the same way Weapon Camo And Stickers already keeps paint off your hands.

With **Stick to magazine**, a seated magazine keeps its normal mark and is painted by the gun's own camo. Once a reload moves it, the addon switches its mark and redraws the gun's camo and stickers on it, shifted by exactly how far the magazine has moved. It works out where "seated" is for a gun by watching its magazine while it is at rest.

**[`docs/internals.md`](docs/internals.md)** is the full version: the stencil trick, where the seated pose comes from, and a complete list of everything this reaches into inside Weapon Camo And Stickers.

## Building from source

You need:

- The .NET SDK (any version that can build `net472`)
- An SPT 4.1.5 install with Weapon Camo And Stickers installed
- **The game started at least once through the SPT Launcher.** The Launcher patches the game's `Assembly-CSharp.dll` on first launch, and the addon has to compile against the patched version.

Then, from PowerShell:

```powershell
scripts\pack.ps1 -SPTPath "C:\path\to\SPT"            # build and pack dist\NoMagazineCamo_V<version>.zip
scripts\pack.ps1 -SPTPath "C:\path\to\SPT" -Install   # also copy the DLL into that install
```

Optional parameters:

- `-CamoModDir <folder>`: build against a Weapon Camo And Stickers DLL that isn't installed in `-SPTPath`.
- `-GameAssembly <file>`: build against a patched `Assembly-CSharp.dll` elsewhere, if the install hasn't been launched yet.

Use PowerShell rather than Git Bash: Bash can mangle Windows paths passed to `-SPTPath`.

## Credits

- [7Bpencil](https://github.com/7Bpencil) for Weapon Camo And Stickers. Sticky camo draws decals the same way that mod's `DecalRenderer` does.

## License

MIT. See [LICENSE](LICENSE).
