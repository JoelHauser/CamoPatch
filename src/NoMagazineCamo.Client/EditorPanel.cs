using System;
using HarmonyLib;
using UnityEngine;
using Camo = SevenBoldPencil.WeaponCamoAndStickers;

namespace NoMagazineCamo.Client
{
    /// <summary>
    /// The per-weapon magazine choice, as a small panel docked under the camo editor while it is
    /// open on a weapon.
    ///
    /// Drawn as a window of its own rather than as a row inside the camo editor: that window's
    /// height is worked out section by section before anything is drawn
    /// (CalculateDecalsWindowHeight and friends), so a row added to it would have to be added to
    /// that arithmetic as well, in a mod whose layout is not ours to keep in step.
    ///
    /// It follows the editor's own rect and UI scale, so it stays put when the editor is dragged
    /// or the screen is resized.
    /// </summary>
    [HarmonyPatch(typeof(Camo.CamoEditor), nameof(Camo.CamoEditor.DrawWindow))]
    internal static class EditorPanel
    {
        // Far away from the ids the camo editor uses for its own windows.
        private const int WindowId = 0x6E6D6301;

        private const int Margin = 4;
        private const int Padding = 10;

        // Measured from the window's own top-left, which is the border, not the content: the
        // title sits in the first TitleHeight of it.
        private const int TitleHeight = 22;
        private const int RowHeight = 26;
        private const int Gap = 6;
        private const int LabelHeight = 20;
        private const int Height = TitleHeight + RowHeight + Gap + LabelHeight + Padding;

        // No Default button. A weapon that has never been set follows F12 already, and the
        // toolbar shows which way that falls -- so the third button only ever restated what the
        // other two were showing.
        private static readonly GUIContent[] Options =
        {
            new GUIContent("Keep clean", "This weapon's magazine is never painted."),
            new GUIContent("Stick", "This weapon's camo stays on its magazine through a reload."),
        };

        private static readonly GUIContent Title = new GUIContent("Magazine");

        // What the window callback needs, handed over from the postfix. IMGUI runs both on the
        // same thread, in the same call.
        private static string _itemId;
        private static float _width;

        private static bool _failed;

        [HarmonyPostfix]
        private static void Postfix(Camo.CamoEditor __instance)
        {
            // An exception thrown here would surface inside the camo mod's OnGUI and take its
            // editor down with it, so this goes quiet after the first one.
            if (_failed)
            {
                return;
            }

            try
            {
                if (!__instance.IsOpened
                    || __instance.ItemType != Camo.ItemType.Weapon
                    || string.IsNullOrEmpty(__instance.ItemId))
                {
                    return;
                }

                _itemId = __instance.ItemId;

                var editor = __instance.WindowRect;
                _width = editor.width;

                // Above the editor instead when there is no room under it.
                var scale = Camo.CamoEditor.CalculateUIScale();
                var screenHeight = scale > 0f ? Screen.height / scale : Screen.height;
                var below = editor.yMax + Margin;
                var y = below + Height > screenHeight ? editor.y - Height - Margin : below;

                var matrix = GUI.matrix;
                try
                {
                    GUI.matrix = Camo.CamoEditor.CalculateUIScaleMatrix();
                    GUI.Window(WindowId, new Rect(editor.x, y, _width, Height), Draw, Title);
                }
                finally
                {
                    // Restored even if the draw throws: everything the camo mod draws after this
                    // is laid out through the same matrix.
                    GUI.matrix = matrix;
                }
            }
            catch (Exception e)
            {
                _failed = true;
                NoMagazineCamoPlugin.Log.LogError(
                    "[NoMagazineCamo] the magazine panel stopped after an error. The F12 setting "
                    + $"still works, and weapons already set keep their setting.\n{e}");
            }
        }

        private static void Draw(int id)
        {
            var width = Mathf.Max(0f, _width - (Padding * 2));

            // Shows what this weapon actually does, whether that was set here or inherited from
            // F12. Clicking the one already lit sets it on the weapon, which is what stops it
            // following F12 afterwards.
            var current = MagazineChoices.Resolve(_itemId) == MagazineCamoMode.None ? 0 : 1;
            var next = GUI.Toolbar(new Rect(Padding, TitleHeight, width, RowHeight), current, Options);
            if (next != current)
            {
                MagazineChoices.Set(
                    _itemId,
                    next == 0 ? MagazineChoice.None : MagazineChoice.StickToMagazine);
            }

            GUI.Label(new Rect(Padding, TitleHeight + RowHeight + Gap, width, LabelHeight), Describe());
        }

        private static string Describe()
        {
            return MagazineChoices.For(_itemId) == MagazineChoice.Default
                ? "Following the F12 setting."
                : "Set on this weapon.";
        }
    }
}
