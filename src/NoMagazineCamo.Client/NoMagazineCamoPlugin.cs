using System;
using System.ComponentModel;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace NoMagazineCamo.Client
{
    public enum MagazineCamoMode
    {
        [Description("None")]
        None,

        [Description("Stick to magazine")]
        StickToMagazine,
    }

    /// <summary>
    /// An addon for 7Bpencil's Weapon Camo And Stickers that changes what camo does to
    /// magazines: either keeps it off them entirely, or makes it stay on them when a reload
    /// carries them out of the camo's box.
    ///
    /// The camo mod does not paint parts. Every decal is a box projected onto the screen,
    /// and it draws only on pixels whose material stencil matches the decal's -- 2 for a
    /// gun, which every weapon part, magazines included, says out of the box. Everything
    /// here works by changing that number on magazine materials. See CLAUDE.md.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CamoModGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class NoMagazineCamoPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.nomagazinecamo";
        public const string PluginName = "No Magazine Camo";
        public const string PluginVersion = "1.0.0";

        // The camo mod's own BepInPlugin GUID, read out of its source. Hard: without it
        // there is no camo to change, and sticky camo reads its decals directly.
        public const string CamoModGuid = "7Bpencil.WeaponCamoAndStickers";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<MagazineCamoMode> MagazineCamo;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind(
                "Magazines",
                "Enabled",
                true,
                "Off: magazines take camo exactly as they would without this addon. "
                + "Takes effect immediately.");

            MagazineCamo = Config.Bind(
                "Magazines",
                "Camo on magazines",
                MagazineCamoMode.None,
                "None: magazines are never painted.\n"
                + "Stick to magazine: magazines wear the gun's camo, and it stays on them when a "
                + "reload pulls them out of the camo's box.\n"
                + "Takes effect immediately. Revolver and grenade launcher cylinders are never touched.\n"
                + "A weapon set to something other than Default in the camo editor ignores this.");

            if (!MagazineStencil.Resolve())
            {
                return;
            }

            MagazineChoices.Load();
            StickyCamo.Install();
            Harmony.CreateAndPatchAll(typeof(MagazineStencil), PluginGuid);
            InstallEditorPanel();
            InstallAmbient();

            Enabled.SettingChanged += OnSettingChanged;
            MagazineCamo.SettingChanged += OnSettingChanged;

            Log.LogInfo("[NoMagazineCamo] loaded");
        }

        // Separately, and not fatally: without it the F12 setting still works, and so does every
        // per-weapon choice already made -- there is just no way to change one in the editor.
        private static void InstallEditorPanel()
        {
            try
            {
                Harmony.CreateAndPatchAll(typeof(EditorPanel), PluginGuid);
            }
            catch (Exception e)
            {
                Log.LogError(
                    "[NoMagazineCamo] the camo editor's window could not be hooked, so the "
                    + "per-weapon magazine setting has no panel. Weapons already set keep their "
                    + $"setting, and the F12 setting still works.\n{e}");
            }
        }

        // Also separately: without it magazines kept off the gun's camo are lit flatly, which is
        // ugly but is exactly how they looked before this pass existed.
        private static void InstallAmbient()
        {
            try
            {
                Harmony.CreateAndPatchAll(typeof(MagazineAmbient), PluginGuid);
            }
            catch (Exception e)
            {
                Log.LogError(
                    "[NoMagazineCamo] the game's ambient pass could not be hooked, so a magazine kept "
                    + $"off the gun's camo will be lit differently from the gun.\n{e}");
            }
        }

        private static void OnSettingChanged(object sender, EventArgs e)
        {
            StickyCamo.Reset();
            MagazineStencil.ApplySettings();
        }
    }
}
