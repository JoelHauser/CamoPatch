using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace NoMagazineCamo.Client
{
    /// <summary>One magazine model, and the renderers on it whose materials carry a stencil.</summary>
    internal sealed class Magazine
    {
        public readonly AssetPoolObject PoolObject;
        public readonly Transform Transform;

        /// <summary>The renderers this magazine draws with, for redrawing it into the stencil
        /// buffer once the camo pass no longer needs it moved off the gun's value.</summary>
        public readonly Renderer[] Renderers;

        /// <summary>The stencil this magazine shipped with -- in practice 2, the weapon's, which
        /// is also the lighting category a weapon in hands belongs to.</summary>
        public readonly int OriginalStencil;

        // Filled the first time the magazine is cleaned. Until then it renders the materials it
        // shipped with, shared with whatever else uses them, and costs nothing -- which is most
        // magazines in a raid, since only the player's own painted guns are ever cleaned.
        private readonly List<Material> _copies = new List<Material>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly List<float> _originals = new List<float>();
        private bool _copied;

        // NaN while the materials hold what they shipped with.
        private float _applied = float.NaN;

        public Magazine(AssetPoolObject poolObject, List<Renderer> renderers, int originalStencil)
        {
            PoolObject = poolObject;
            Transform = poolObject.transform;
            Renderers = renderers.ToArray();
            OriginalStencil = originalStencil;
        }

        // Unity's null: a pool object destroyed along with its pool compares equal to null.
        public bool Alive => PoolObject != null;

        public bool HasMaterials => Renderers.Length > 0;

        /// <summary>Whether this magazine is currently moved off the stencil it shipped with, and
        /// so needs putting back before the lighting passes read it.</summary>
        public bool IsMoved => !float.IsNaN(_applied);

        /// <summary>Out of reach of every decal the camo mod draws by default.</summary>
        public void Clean()
        {
            if (_applied == MagazineStencil.CleanStencil)
            {
                return;
            }

            CopyMaterials();
            foreach (var material in _materials)
            {
                if (material != null)
                {
                    material.SetFloat(MagazineStencil.StencilTypeId, MagazineStencil.CleanStencil);
                }
            }

            _applied = MagazineStencil.CleanStencil;
        }

        /// <summary>Back to what the model shipped with, so the gun's decals paint it again.</summary>
        public void Restore()
        {
            if (float.IsNaN(_applied))
            {
                return;
            }

            for (var i = 0; i < _materials.Count; i++)
            {
                if (_materials[i] != null)
                {
                    _materials[i].SetFloat(MagazineStencil.StencilTypeId, _originals[i]);
                }
            }

            _applied = float.NaN;
        }

        /// <summary>
        /// Destroys the copies once the model is gone. Unity never frees what renderer.materials
        /// hands out on its own, so without this every magazine ever cleaned would leak them.
        /// </summary>
        public void Release()
        {
            foreach (var copy in _copies)
            {
                if (copy != null)
                {
                    UnityEngine.Object.Destroy(copy);
                }
            }

            _copies.Clear();
            _materials.Clear();
            _originals.Clear();
        }

        // Copies, not the shared asset. Nothing guarantees a magazine a material of its own,
        // and changing a shared one would change whatever else on the gun uses it.
        private void CopyMaterials()
        {
            if (_copied)
            {
                return;
            }

            _copied = true;
            foreach (var renderer in Renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                foreach (var material in renderer.materials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    _copies.Add(material);
                    if (material.HasProperty(MagazineStencil.StencilTypeId))
                    {
                        _materials.Add(material);
                        _originals.Add(material.GetFloat(MagazineStencil.StencilTypeId));
                    }
                }
            }

            NoMagazineCamoPlugin.Log.LogDebug(
                $"[NoMagazineCamo] {Transform.name}: first cleaned ({_materials.Count} stencil material(s))");
        }
    }

    /// <summary>
    /// Finds every magazine model as the game hands it out. Which of them are kept out of reach
    /// of the camo mod's decals is decided once a frame by StickyCamo -- only those on a painted
    /// gun the local player carries, or one the camo mod is showing in a preview.
    ///
    /// Hooked on AssetPoolObject.OnGetFromPool because every model the game shows comes out
    /// through it -- pooled ones from PopOrCreate, unpooled stubs from
    /// SetupGameObjectWithoutPool -- and both set ResourceType before calling it. The change
    /// lives on the model's own materials, so it goes wherever the model goes.
    /// </summary>
    [HarmonyPatch(typeof(AssetPoolObject), nameof(AssetPoolObject.OnGetFromPool))]
    internal static class MagazineStencil
    {
        // items.json node IDs. Every magazine descends from Magazine; CylinderMagazine sits
        // under it, and SpringDrivenCylinder under that. Cylinders are left alone.
        private const string MagazineNode = "5448bc234bdc2d3c308b4569";
        private const string CylinderMagazineNode = "610720f290b75a49ff2e5e25";

        // The camo mod's decal shader compares only the low two bits of the stencil (ReadMask
        // 3, Comp Equal) with the decal's _StencilType, and it only ever uses 0, 1 and 2. 3 is
        // the one value nothing it draws by default can match: 0 would take the map's own
        // decals and 1 would take equipment camo. It is also what sticky camo draws on.
        internal const float CleanStencil = 3f;
        internal static readonly int StencilTypeId = Shader.PropertyToID("_StencilType");

        internal static readonly List<Magazine> All = new List<Magazine>();

        // Both by name, never by metadata token. ResourceType is protected; its type is an
        // obfuscated struct with no writable name, holding a public ItemTemplate field.
        private static FieldInfo _resourceType;
        private static FieldInfo _itemTemplate;

        // Every pool object already looked at, magazine or not. OnGetFromPool runs for every
        // shell casing and cartridge, so a second visit has to cost one lookup.
        private static readonly HashSet<int> Seen = new HashSet<int>();

        private static readonly List<Renderer> Renderers = new List<Renderer>();
        private static readonly List<Renderer> Used = new List<Renderer>();

        private static bool _failed;

        internal static bool Resolve()
        {
            _resourceType = AccessTools.Field(typeof(AssetPoolObject), "ResourceType");
            if (_resourceType == null)
            {
                NoMagazineCamoPlugin.Log.LogError(
                    "[NoMagazineCamo] AssetPoolObject.ResourceType was not found -- this game build differs "
                    + "from the one this was written for. Not patching; magazines will take camo as before.");
                return false;
            }

            _itemTemplate = AccessTools.Field(_resourceType.FieldType, "ItemTemplate");
            if (_itemTemplate == null || _itemTemplate.FieldType != typeof(ItemTemplate))
            {
                NoMagazineCamoPlugin.Log.LogError(
                    "[NoMagazineCamo] ResourceType has no ItemTemplate field -- this game build differs "
                    + "from the one this was written for. Not patching; magazines will take camo as before.");
                return false;
            }

            return true;
        }

        /// <summary>Puts every known magazine where the current settings say it belongs.</summary>
        internal static void ApplySettings()
        {
            All.RemoveAll(magazine =>
            {
                if (magazine.Alive)
                {
                    return false;
                }

                magazine.Release();
                return true;
            });

            foreach (var magazine in All)
            {
                // With the per-frame loop running, what each magazine gets depends on the weapon
                // it is on, which only that loop knows. Hand them all back and let it decide on
                // its next frame.
                if (StickyCamo.Deciding || !NoMagazineCamoPlugin.Enabled.Value)
                {
                    magazine.Restore();
                }
                else
                {
                    magazine.Clean();
                }
            }
        }

        [HarmonyPostfix]
        private static void Postfix(AssetPoolObject __instance)
        {
            // An exception here would surface in the game's pool code, not in this mod, so it
            // is caught, reported once, and the mod goes quiet.
            if (_failed)
            {
                return;
            }

            try
            {
                if (!Seen.Add(__instance.GetInstanceID()))
                {
                    return;
                }

                var template = _itemTemplate.GetValue(_resourceType.GetValue(__instance)) as ItemTemplate;
                if (template == null || !IsMagazine(template))
                {
                    return;
                }

                var magazine = Collect(__instance);
                NoMagazineCamoPlugin.Log.LogDebug(
                    $"[NoMagazineCamo] {template._name}: {magazine.Renderers.Length} renderer(s) carry a stencil");

                if (!magazine.HasMaterials)
                {
                    return;
                }

                All.Add(magazine);

                // Only when there is no per-frame loop to do it per weapon. Without one there is
                // no sticky camo either, so keeping every magazine clean is the whole mod.
                if (!StickyCamo.Deciding && NoMagazineCamoPlugin.Enabled.Value)
                {
                    magazine.Clean();
                }
            }
            catch (Exception e)
            {
                _failed = true;
                NoMagazineCamoPlugin.Log.LogError(
                    $"[NoMagazineCamo] stopped after an error; magazines will take camo as before.\n{e}");
            }
        }

        private static bool IsMagazine(ItemTemplate template)
        {
            return template.IsChildOf(MagazineNode) && !template.IsChildOf(CylinderMagazineNode);
        }

        // Only the renderers are kept here. Their materials are copied the first time the
        // magazine is cleaned (Magazine.Clean), so a magazine that never is costs nothing.
        private static Magazine Collect(AssetPoolObject poolObject)
        {
            Used.Clear();
            Renderers.Clear();
            poolObject.GetComponentsInChildren(true, Renderers);

            // They are all the same in practice; the first is as good an answer as any, and 2 is
            // the right fallback for a weapon part.
            var originalStencil = -1;

            foreach (var renderer in Renderers)
            {
                if (OwnerOf(renderer) != poolObject)
                {
                    continue;
                }

                var stencil = StencilOf(renderer.sharedMaterials);
                if (stencil < 0)
                {
                    continue;
                }

                Used.Add(renderer);
                if (originalStencil < 0)
                {
                    originalStencil = stencil;
                }
            }

            Renderers.Clear();
            var magazine = new Magazine(poolObject, Used, originalStencil < 0 ? 2 : originalStencil);
            Used.Clear();
            return magazine;
        }

        // The stencil of the first material that carries one, or -1 if none does.
        private static int StencilOf(Material[] materials)
        {
            foreach (var material in materials)
            {
                if (material != null && material.HasProperty(StencilTypeId))
                {
                    return Mathf.RoundToInt(material.GetFloat(StencilTypeId));
                }
            }

            return -1;
        }

        // The nearest pool object at or above a renderer. A cartridge riding in the magazine is
        // a pool object of its own and goes back to its own pool, so it is left alone. Walked by
        // hand because GetComponentInParent skips inactive objects, and a model coming out of
        // the pool has not necessarily been switched on yet.
        private static AssetPoolObject OwnerOf(Renderer renderer)
        {
            for (var transform = renderer.transform; transform != null; transform = transform.parent)
            {
                if (transform.TryGetComponent<AssetPoolObject>(out var owner))
                {
                    return owner;
                }
            }

            return null;
        }
    }
}
