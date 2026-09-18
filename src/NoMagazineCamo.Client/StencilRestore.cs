using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NoMagazineCamo.Client
{
    /// <summary>
    /// Puts a magazine's stencil back to the one it shipped with, after the camo passes have read
    /// it and before the lighting passes do.
    ///
    /// The stencil is not a free tag. It is the game's lighting category -- 0 Static, 1
    /// Characters, 2 Hands -- and the camo mod assigns each item the one it genuinely belongs to,
    /// which is why weapons are 2 and equipment is 1. A magazine in first person is lit correctly
    /// at 2 and at nothing else: 0, 1 and 3 all render it dark. So moving it off 2 to keep camo
    /// away from it cannot be left standing by the time the lighting runs.
    ///
    /// It does not have to be. The stencil buffer is just a buffer, and the passes are ordered:
    ///
    ///     G-buffer (the magazine writes the clean stencil)
    ///     BeforeReflections -- the camo mod's decals, then ours
    ///     the deferred reflections pass, which takes the stencil buffer for its own culling
    ///     BeforeLighting -- this
    ///     the lighting passes
    ///     AfterLighting -- the game's ambient highlight
    ///
    /// The reflections pass is why the decals are not here too: after it, the low two bits are
    /// no longer the camo mod's categories to test against. See StickyCamo.
    ///
    /// Drawing the magazine's own renderers here with Comp Always / Pass Replace and no colour
    /// writes puts 2 back for everything after it. The decal passes see the clean stencil; the
    /// lighting passes see the weapon's.
    ///
    /// The material is UI/Default, which is the one stock shader that exposes its whole stencil
    /// state as properties (_Stencil, _StencilComp, _StencilOp, _StencilReadMask,
    /// _StencilWriteMask) along with _ColorMask -- so this needs no shader of its own.
    /// </summary>
    internal static class StencilRestore
    {
        private static readonly int StencilId = Shader.PropertyToID("_Stencil");
        private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
        private static readonly int StencilOpId = Shader.PropertyToID("_StencilOp");
        private static readonly int StencilReadMaskId = Shader.PropertyToID("_StencilReadMask");
        private static readonly int StencilWriteMaskId = Shader.PropertyToID("_StencilWriteMask");
        private static readonly int ColorMaskId = Shader.PropertyToID("_ColorMask");
        private static readonly int GuiZTestModeId = Shader.PropertyToID("unity_GUIZTestMode");

        // Only the two bits the camo shader compares are ours to touch.
        private const int WriteMask = 3;

        // One per stencil value written back. In practice this holds exactly one, for 2.
        private static readonly Dictionary<int, Material> Materials = new Dictionary<int, Material>();

        private static Shader _shader;
        private static bool _looked;
        private static bool _reportedMissing;

        internal static bool Available
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    _shader = Shader.Find("UI/Default");
                    if (_shader == null && !_reportedMissing)
                    {
                        _reportedMissing = true;
                        NoMagazineCamoPlugin.Log.LogError(
                            "[NoMagazineCamo] UI/Default was not found, so a magazine's stencil cannot be "
                            + "put back before lighting. Magazines kept off the gun's camo will look flat.");
                    }
                }

                return _shader != null;
            }
        }

        /// <summary>
        /// Queues the magazines that are currently moved off their own stencil to be drawn back
        /// onto it. Appended to the same buffer the decals were drawn from, so it always runs
        /// after them.
        /// </summary>
        internal static void Queue(CommandBuffer buffer, List<Magazine> magazines)
        {
            if (!Available)
            {
                return;
            }

            var queued = false;

            foreach (var magazine in magazines)
            {
                // Only the ones actually moved. A seated magazine in sticky mode still carries the
                // stencil it shipped with and needs nothing doing to it.
                if (!magazine.Alive || !magazine.IsMoved)
                {
                    continue;
                }

                var material = MaterialFor(magazine.OriginalStencil);
                if (material == null)
                {
                    continue;
                }

                foreach (var renderer in magazine.Renderers)
                {
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    if (!queued)
                    {
                        // UI/Default takes its depth test from this global. Left at its usual
                        // Always, the magazine's stencil would be written over whatever is in
                        // front of it as well -- the arms, which the camo mod deliberately keeps
                        // on a different value.
                        buffer.SetGlobalFloat(GuiZTestModeId, (float)CompareFunction.LessEqual);
                        queued = true;
                    }

                    buffer.DrawRenderer(renderer, material);
                }
            }

            if (queued)
            {
                buffer.SetGlobalFloat(GuiZTestModeId, (float)CompareFunction.Always);
            }
        }

        private static Material MaterialFor(int stencil)
        {
            if (Materials.TryGetValue(stencil, out var material) && material != null)
            {
                return material;
            }

            if (!Available)
            {
                return null;
            }

            if (Materials.Count == 0)
            {
                NoMagazineCamoPlugin.Log.LogInfo(
                    "[NoMagazineCamo] putting magazine stencils back before lighting, through UI/Default");
            }

            material = new Material(_shader) { name = $"[NoMagazineCamo] Stencil {stencil}" };
            material.SetInt(StencilId, stencil);
            material.SetInt(StencilCompId, (int)CompareFunction.Always);
            material.SetInt(StencilOpId, (int)StencilOp.Replace);
            material.SetInt(StencilReadMaskId, 255);
            material.SetInt(StencilWriteMaskId, WriteMask);
            material.SetInt(ColorMaskId, 0);

            Materials[stencil] = material;
            return material;
        }

        internal static void Clear()
        {
            foreach (var material in Materials.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }

            Materials.Clear();
        }
    }
}
