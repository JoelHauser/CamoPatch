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

        // One per stencil value written. In practice two: 3 around the decal passes, 2 after them.
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
        /// onto it.
        /// </summary>
        internal static void Queue(CommandBuffer buffer, List<Magazine> magazines, Camera camera)
        {
            Queue(buffer, magazines, camera, -1);
        }

        /// <summary>
        /// Queues the same magazines to be drawn onto the clean stencil instead, for the decal
        /// passes that have to skip them. Always followed by <see cref="Queue(CommandBuffer, List{Magazine})"/>
        /// once those passes have run.
        /// </summary>
        internal static void QueueClean(CommandBuffer buffer, List<Magazine> magazines, Camera camera)
        {
            Queue(buffer, magazines, camera, (int)MagazineStencil.CleanStencil);
        }

        // How far toward the eye the redraw is pulled, as a fraction of its distance: about a
        // millimetre at arm's length.
        //
        // Drawn exactly where the magazine is, the redraw z-fights the magazine's own depth: a
        // different shader computes the same position a rounding error apart, so LessEqual
        // passes on some pixels and fails on others, in a pattern that changes every frame.
        // Up to 1.3.0 that left speckles of the restore undone -- on the clean stencil, which is
        // lit wrong, and most visibly in sunlight (2026-11-08, "flickers outdoors, not indoors").
        // Once the clean stencil was stamped the same way, the misses let the camo mod paint
        // the magazine, flashing indoors as well.
        //
        // Scaling about the eye moves every vertex along its own view ray, so the outline on
        // screen is exactly the same and only the depth changes. Whatever is really in front
        // of the magazine -- a finger on it, the gun -- is still in front.
        private const float TowardEye = 0.998f;

        // stencil < 0: each magazine's own.
        private static void Queue(CommandBuffer buffer, List<Magazine> magazines, Camera camera, int stencil)
        {
            var eye = camera.transform.position;
            var pull = Matrix4x4.Translate(eye) * Matrix4x4.Scale(Vector3.one * TowardEye) * Matrix4x4.Translate(-eye);

            if (!Available)
            {
                return;
            }

            foreach (var magazine in magazines)
            {
                // Only the ones actually moved. A seated magazine in sticky mode still carries the
                // stencil it shipped with and needs nothing doing to it.
                if (!magazine.Alive || !magazine.IsMoved)
                {
                    continue;
                }

                var material = MaterialFor(stencil < 0 ? magazine.OriginalStencil : stencil);
                if (material == null)
                {
                    continue;
                }

                // LOD0 only. The LOD1 copy is enabled too -- the LOD group hides it, not its
                // enabled flag -- and drawn here its coarser outline would put the gun's stencil on
                // the ground around the magazine, which then loses its shadow.
                foreach (var renderer in magazine.Drawn)
                {
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    // Every sub-mesh, not just the first: DrawRenderer draws one, and a part left out
                    // would stay on the clean stencil through the lighting and come out dark.
                    // A plain mesh is drawn pulled toward the eye (TowardEye). A skinned one has no
                    // mesh to draw at a matrix without baking it, so it keeps DrawRenderer and the
                    // z-fighting risk; magazines seen so far are plain meshes.
                    if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                    {
                        var mesh = filter.sharedMesh;
                        var matrix = pull * renderer.localToWorldMatrix;
                        for (var i = 0; i < Math.Max(1, mesh.subMeshCount); i++)
                        {
                            buffer.DrawMesh(mesh, matrix, material, i);
                        }

                        continue;
                    }

                    var subMeshes = SubMeshCount(renderer);
                    for (var i = 0; i < subMeshes; i++)
                    {
                        buffer.DrawRenderer(renderer, material, i);
                    }
                }
            }
        }

        private static int SubMeshCount(Renderer renderer)
        {
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                mesh = skinned.sharedMesh;
            }
            else if (renderer.TryGetComponent<MeshFilter>(out var filter))
            {
                mesh = filter.sharedMesh;
            }

            return mesh != null ? Math.Max(1, mesh.subMeshCount) : 1;
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

            // UI/Default takes its depth test from unity_GUIZTestMode. Left unset, the magazine's
            // stencil would be written over whatever is in front of it as well -- the arms, which
            // the camo mod deliberately keeps on a different value.
            //
            // Set on this material, never as a global. Up to 1.1.0 it was set globally around the
            // draws and then "put back" to Always, which is not what the game leaves it at: every
            // world-space TextMeshPro takes its depth test from the same global, so from then on
            // all of them drew through walls -- AmandsSense's item and exfil labels among them
            // (DrakiaXYZ/SPT-AmandsSense#6). A value on the material overrides the global for
            // this material alone and touches nothing else.
            material.SetInt(GuiZTestModeId, (int)CompareFunction.LessEqual);

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
