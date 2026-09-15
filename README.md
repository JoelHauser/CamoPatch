# No Magazine Camo

An addon for [Weapon Camo And Stickers](https://github.com/7Bpencil/SPT.WeaponCamoAndStickers) that changes how camo treats magazines in SPT.

> **Pre-release.** Built and checked against SPT 4.1.5 and Weapon Camo And Stickers 1.19.0, but not yet tested in a raid.

## What it does

Weapon Camo And Stickers projects camo onto a gun from a box around it. The magazine sits inside that box, so it gets painted too. During a reload it slides out of the box and suddenly turns back to its stock finish.

This addon gives you two choices:

- **Keep magazines clean.** Presets, random bot camos and stickers stay off magazines, and the rest of the gun is painted as usual.
- **Make the camo stick.** Magazines wear the gun's camo and stickers, and keep them all the way through a reload.

Presets don't need editing, and nothing is saved to your profile. Remove the addon and everything looks the way it did before.

## Requirements

- SPT 4.1.5
- [Weapon Camo And Stickers](https://github.com/7Bpencil/SPT.WeaponCamoAndStickers) 1.19.0

## Install

1. Download `NoMagazineCamo_V1.0.0.zip` from the [`releases`](releases) folder.
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

Revolver and grenade launcher cylinders are never affected. They keep their camo like the rest of the gun.

## Performance

- **None** adds no cost while you play.
- **Stick to magazine** adds no cost while a magazine is seated. During a reload, the gun's camo is drawn a second time until the magazine is back in place.

## Compatibility

- Client-only. No server mod.
- Works with Fika. Each client only changes how magazines look on that client.

## Known limitations

- **Some guns may not stick.** Sticky camo works out how far a magazine has moved from its seated position using data the game stores for each weapon. If a weapon doesn't store that position for its magazine, its magazine behaves as if the addon were off in sticky mode, and the BepInEx log names the weapon.
- **Two magazines in motion at once.** When the old and new magazines are both out mid-reload, camo meant for one can briefly show on the other if they pass close together.
- **Lighting.** If a magazine looks lit differently from the rest of the gun in first person, please open an issue with a screenshot.

## How it works

Weapon Camo And Stickers only draws camo on surfaces marked as weapon parts. The addon gives magazines a different mark that none of that camo matches, the same way Weapon Camo And Stickers already keeps paint off your hands.

With **Stick to magazine**, a seated magazine keeps its normal mark and is painted by the gun's own camo. Once a reload moves it, the addon switches its mark and redraws the gun's camo and stickers on it, shifted by exactly how far the magazine has moved.

[`CLAUDE.md`](CLAUDE.md) has the full technical notes: which game code is hooked, what was verified, and what is still untested.

## Building from source

You need:

- The .NET SDK (any version that can build `net472`)
- An SPT 4.1.5 install with Weapon Camo And Stickers 1.19.0 installed
- **The game started at least once through the SPT Launcher.** The Launcher patches the game's `Assembly-CSharp.dll` on first launch, and the addon has to compile against the patched version.

Then, from PowerShell:

```powershell
scripts\pack.ps1 -SPTPath "C:\path\to\SPT"            # build and pack releases\NoMagazineCamo_V<version>.zip
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
