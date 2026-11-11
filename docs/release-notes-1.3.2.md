## Changed

- **Removed "Max Angle 0 covers everything".** Weapon Camo And Stickers 1.20.0 now fixes
  the patchy camo on side-on edges itself: Max Angle can go below 0, and new layers start
  at -0.1. Keeping this addon's version would have overridden those new values. To fix an
  existing layer, set its Max Angle below 0 in the camo editor.
- The old `Max Angle 0 covers everything` line may stay in your F12 config file. It no
  longer does anything and is safe to ignore or delete.
- Magazine handling (None / Stick to magazine, and the per-weapon panel) is unchanged.

## Install

Extract into your SPT folder, replacing the old version. Requires Weapon Camo And Stickers 1.18.0 or newer; 1.20.0 recommended.
