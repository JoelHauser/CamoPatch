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
        public const string PluginVersion = "1.1.0";

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
                + "Takes effect immediately. Revolver and grenade launcher cylinders are never touched.");

            if (!MagazineStencil.Resolve())
            {
                return;
            }

            StickyCamo.Install();
            Harmony.CreateAndPatchAll(typeof(MagazineStencil), PluginGuid);

            Enabled.SettingChanged += OnSettingChanged;
            MagazineCamo.SettingChanged += OnSettingChanged;

            Log.LogInfo("[NoMagazineCamo] loaded");
        }

        private static void OnSettingChanged(object sender, EventArgs e)
        {
            StickyCamo.Reset();
            MagazineStencil.ApplySettings();
        }
    }
}
