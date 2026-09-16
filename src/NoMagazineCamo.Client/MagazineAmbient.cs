using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NoMagazineCamo.Client
{
    /// <summary>
    /// Gives a magazine on the clean stencil the ambient light it would have had on the gun's.
    ///
    /// The stencil is not only the camo mod's lever. The game's own ambient pass reads the same
    /// `_StencilType` property, and applies ambient as one full-screen quad per category --
    /// Static 0, Characters 1, Hands 2 -- each stencil-tested, each with its own multiplier
    /// lerped along an intensity curve by the sun's angle. There is no category 3, so a magazine
    /// moved out of the camo's reach also falls out of every ambient quad and goes unlit.
    ///
    /// So one more quad is added, matching the Hands entries -- the category a weapon is in --
    /// with the stencil set to 3. A magazine then takes exactly the ambient its gun does.
    ///
    /// It draws through a **copy** of the game's ambient material, which matters more than it
    /// looks: a CommandBuffer holds a material by reference, not by value, and the game mutates
    /// AmbientMaterial's stencil inside the loop that queues its own quads. Setting that
    /// material's stencil here would change every quad the game had already queued, and take the
    /// world's ambient light with it.
    /// </summary>
    [HarmonyPatch(typeof(AmbientHighlight), nameof(AmbientHighlight.UpdateAmbientBuffer))]
    internal static class MagazineAmbient
    {
        private static readonly int StencilTypeId = Shader.PropertyToID("_StencilType");
        private static readonly int HighlightMultiplierId = Shader.PropertyToID("_HighlightMultiplier");
        private static readonly int AmbientBlurId = Shader.PropertyToID("_AmbientBlur");

        // The category a weapon is in, and so the one a magazine should be lit like.
        private const AmbientHighlight.StencilType WeaponCategory = AmbientHighlight.StencilType.Hands;

        private static Material _material;
        private static MaterialPropertyBlock _properties;
        private static Mesh _quad;
        private static int _refreshedFrame = -1;

        private static bool _failed;
        private static bool _reportedNoCategory;

        [HarmonyPostfix]
        private static void Postfix(AmbientHighlight __instance, CommandBuffer ambientBuffer)
        {
            // Thrown here this would surface inside the game's ambient pass, so it reports once
            // and stops. Magazines go back to being unlit on the clean stencil; nothing else
            // changes.
            if (_failed || !NoMagazineCamoPlugin.Enabled.Value)
            {
                return;
            }

            // Only when the stencil cannot be put back. With StencilRestore working, a magazine is
            // on the weapon's own stencil by the time this runs and already takes the weapon's
            // ambient -- an extra quad for a value nothing is on would light nothing.
            if (StencilRestore.Available)
            {
                return;
            }

            try
            {
                var settings = __instance._highlightSettings;
                var source = __instance.AmbientMaterial;
                if (settings == null || settings.Length == 0 || source == null || !TODSkyProvider.IsAvailable)
                {
                    return;
                }

                if (!EnsureMaterial(source))
                {
                    return;
                }

                var time = 0f - TODSkyProvider.Instance.LightObject.transform.forward.y;
                var drew = false;

                foreach (var entry in settings)
                {
                    if (entry == null || entry.StencilTypeToUse != WeaponCategory)
                    {
                        continue;
                    }

                    var t = entry.HighlightIntensityCurve.Evaluate(time);

                    // Property blocks are copied into the buffer when the draw is queued, unlike
                    // the material, so one block can carry a different value per quad.
                    _properties.SetFloat(AmbientBlurId, 1f / __instance.AmbientBlur);
                    _properties.SetFloat(
                        HighlightMultiplierId,
                        Mathf.Lerp(entry.HighlightMinMultiplier, entry.HighlightMaxMultiplier, t));

                    ambientBuffer.DrawMesh(_quad, Matrix4x4.identity, _material, 0, 0, _properties);
                    drew = true;
                }

                if (!drew && !_reportedNoCategory)
                {
                    _reportedNoCategory = true;
                    NoMagazineCamoPlugin.Log.LogWarning(
                        "[NoMagazineCamo] the game's ambient pass has no Hands entry, so a magazine kept "
                        + "off the gun's camo will be lit differently from the gun. Nothing is broken; it "
                        + "will look flat.");
                }
            }
            catch (Exception e)
            {
                _failed = true;
                NoMagazineCamoPlugin.Log.LogError(
                    "[NoMagazineCamo] the magazine ambient pass stopped after an error. Magazines kept "
                    + $"off the gun's camo will look flat; nothing else is affected.\n{e}");
            }
        }

        // Not named Prepare: that is one of the names Harmony reserves on a patch class
        // (alongside Cleanup, TargetMethod and TargetMethods), and it will call it itself --
        // with nulls -- and throw the whole class out when it fails.
        private static bool EnsureMaterial(Material source)
        {
            if (_quad == null)
            {
                _quad = AmbientHighlight.GetQuadMesh();
            }

            if (_properties == null)
            {
                _properties = new MaterialPropertyBlock();
            }

            if (_material == null)
            {
                _material = new Material(source) { name = "[NoMagazineCamo] Magazine ambient" };
                _refreshedFrame = -1;
            }

            // Kept in step with the game's material once a frame, so anything it changes there --
            // blend modes, spherical harmonics -- reaches this copy too. The stencil is set after,
            // because that is the one thing this copy exists to differ in.
            var frame = Time.frameCount;
            if (_refreshedFrame != frame)
            {
                _refreshedFrame = frame;
                _material.CopyPropertiesFromMaterial(source);
                _material.SetInt(StencilTypeId, (int)MagazineStencil.CleanStencil);
            }

            return _quad != null;
        }
    }
}
