using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace NoMagazineCamo.Client
{
    /// <summary>One magazine model, and the materials on it that carry a stencil.</summary>
    internal sealed class Magazine
    {
        public readonly AssetPoolObject PoolObject;
        public readonly Transform Transform;

        private readonly Material[] _materials;
        private readonly float[] _originals;

        // NaN while the materials hold what they shipped with.
        private float _applied = float.NaN;

        public Magazine(AssetPoolObject poolObject, List<Material> materials)
        {
            PoolObject = poolObject;
            Transform = poolObject.transform;
            _materials = materials.ToArray();
            _originals = new float[_materials.Length];
            for (var i = 0; i < _materials.Length; i++)
            {
                _originals[i] = _materials[i].GetFloat(MagazineStencil.StencilTypeId);
            }
        }

        // Unity's null: a pool object destroyed along with its pool compares equal to null.
        public bool Alive => PoolObject != null;

        public bool HasMaterials => _materials.Length > 0;

        /// <summary>Out of reach of every decal the camo mod draws by default.</summary>
        public void Clean()
        {
            if (_applied == MagazineStencil.CleanStencil)
            {
                return;
            }

            Set(MagazineStencil.CleanStencil);
            _applied = MagazineStencil.CleanStencil;
        }

        /// <summary>Back to what the model shipped with, so the gun's decals paint it again.</summary>
        public void Restore()
        {
            if (float.IsNaN(_applied))
            {
                return;
            }

            for (var i = 0; i < _materials.Length; i++)
            {
                if (_materials[i] != null)
                {
                    _materials[i].SetFloat(MagazineStencil.StencilTypeId, _originals[i]);
                }
            }

            _applied = float.NaN;
        }

        private void Set(float value)
        {
            foreach (var material in _materials)
            {
                if (material != null)
                {
                    material.SetFloat(MagazineStencil.StencilTypeId, value);
                }
            }
        }
    }

    /// <summary>
    /// Finds every magazine model as the game hands it out, and keeps it out of reach of the
    /// camo mod's decals.
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
        private static readonly List<Material> Materials = new List<Material>();

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
            All.RemoveAll(magazine => !magazine.Alive);

            foreach (var magazine in All)
            {
                // Sticky camo starts from clean too, and hands seated magazines back to the
                // gun's decals on its next frame.
                if (NoMagazineCamoPlugin.Enabled.Value)
                {
                    magazine.Clean();
                }
                else
                {
                    magazine.Restore();
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
                    $"[NoMagazineCamo] {template._name}: {Materials.Count} material(s) carry a stencil");
                Materials.Clear();

                if (!magazine.HasMaterials)
                {
                    return;
                }

                All.Add(magazine);
                if (NoMagazineCamoPlugin.Enabled.Value)
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

        private static Magazine Collect(AssetPoolObject poolObject)
        {
            Materials.Clear();
            Renderers.Clear();
            poolObject.GetComponentsInChildren(true, Renderers);

            foreach (var renderer in Renderers)
            {
                if (OwnerOf(renderer) != poolObject || !CarriesStencil(renderer.sharedMaterials))
                {
                    continue;
                }

                // Copies, not the shared asset. Nothing guarantees a magazine a material of its
                // own, and changing a shared one would change whatever else on the gun uses it.
                foreach (var material in renderer.materials)
                {
                    if (material != null && material.HasProperty(StencilTypeId))
                    {
                        Materials.Add(material);
                    }
                }
            }

            Renderers.Clear();
            return new Magazine(poolObject, Materials);
        }

        private static bool CarriesStencil(Material[] materials)
        {
            foreach (var material in materials)
            {
                if (material != null && material.HasProperty(StencilTypeId))
                {
                    return true;
                }
            }

            return false;
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
