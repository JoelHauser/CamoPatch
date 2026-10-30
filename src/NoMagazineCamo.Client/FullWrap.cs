using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Camo = SevenBoldPencil.WeaponCamoAndStickers;

namespace NoMagazineCamo.Client
{
    /// <summary>
    /// Makes a camo decal's Max Angle of 0 paint every surface inside its box.
    ///
    /// The decal shader drops a pixel when <c>dot(normal, decal Y axis) &lt; _MaxAngle</c>. At
    /// 0 that keeps only surfaces facing the projection, so anything side-on -- the top of a
    /// sight hump on a gun painted from the side -- sits right on the cut-off, and the normal
    /// map's grain decides pixel by pixel which side of it each one lands: a speckled gap. The
    /// editor's slider stops at 0, but the shader does not clamp, so a value below -1 turns the
    /// test off and the box paints through. Only the material is changed; the camo mod's
    /// DecalInfo, its files and what Fika sends all keep the 0 the slider shows.
    /// </summary>
    [HarmonyPatch(typeof(Camo.Decal))]
    internal static class FullWrap
    {
        // Below -1 so dot + 1.01 can never come out negative, however the normal is rounded.
        private const float WrapAll = -1.01f;

        // The slider's own minimum, with room for float noise. Anything above it keeps the
        // camo mod's behaviour exactly.
        private const float Threshold = 0.0005f;

        private static readonly int MaxAngleId = Shader.PropertyToID("_MaxAngle");

        // What the camo mod last asked for, per decal -- the material only holds what we wrote.
        private static readonly ConditionalWeakTable<Camo.Decal, StrongBox<float>> Requested =
            new ConditionalWeakTable<Camo.Decal, StrongBox<float>>();

        [HarmonyPatch(nameof(Camo.Decal.ChangeMaxAngle))]
        [HarmonyPostfix]
        private static void AfterChangeMaxAngle(Camo.Decal __instance, float maxAngle)
        {
            Requested.GetValue(__instance, _ => new StrongBox<float>()).Value = maxAngle;
            Apply(__instance);
        }

        // Init sets the angle before the paint mode, and an eraser must never wrap, so the
        // mode decides again whenever it changes.
        [HarmonyPatch(nameof(Camo.Decal.ChangePaintMode))]
        [HarmonyPostfix]
        private static void AfterChangePaintMode(Camo.Decal __instance)
        {
            Apply(__instance);
        }

        /// <summary>Re-applies the setting to every decal that exists, for an F12 change.</summary>
        internal static void ApplyAll()
        {
            foreach (var decal in Resources.FindObjectsOfTypeAll<Camo.Decal>())
            {
                Apply(decal);
            }
        }

        private static void Apply(Camo.Decal decal)
        {
            try
            {
                var material = decal.DecalMaterial;
                if (material == null || !Requested.TryGetValue(decal, out var requested))
                {
                    return;
                }

                // In Erase mode the shader reads _MaxAngle as a mask alpha cut-off instead;
                // below 0 the eraser would wipe its whole box and ignore its mask.
                var erase = material.IsKeywordEnabled(decal.DecalMaterialKeywordErase);
                var wrap = NoMagazineCamoPlugin.FullWrapAtZero.Value && !erase && requested.Value < Threshold;
                material.SetFloat(MaxAngleId, wrap ? WrapAll : requested.Value);
            }
            catch (System.Exception e)
            {
                NoMagazineCamoPlugin.Log.LogError($"[NoMagazineCamo] full wrap could not be applied to a decal.\n{e}");
            }
        }
    }
}
