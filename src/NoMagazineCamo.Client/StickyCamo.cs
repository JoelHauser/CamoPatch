using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Camo = SevenBoldPencil.WeaponCamoAndStickers;

namespace NoMagazineCamo.Client
{
    /// <summary>
    /// Sticky magazine camo: the gun's camo, carried along by a magazine that has left its seat.
    ///
    /// A seated magazine keeps the stencil it shipped with, so the gun's own decals paint it
    /// and this costs nothing. Once an animation moves it off its rest pose -- a reload, in
    /// practice -- it is switched to the clean stencil, which the gun's decals skip, and every
    /// decal is drawn again on stencil 3, moved by exactly the difference between where the
    /// magazine is and where it rests. At the rest pose that difference is nothing, so the
    /// handover is seamless both ways.
    ///
    /// Magazines on a gun with no camo, and loose ones, are handed back untouched: nothing
    /// would paint them either way.
    ///
    /// Drawn the way the camo mod draws its own decals (DecalRenderer.cs, MIT): a cube per
    /// decal into GBuffer0 before lighting, from a command buffer added after the camo mod's
    /// on exactly the cameras it draws on.
    /// </summary>
    internal static class StickyCamo
    {
        private struct Owner
        {
            public string ItemId;
            public Camo.ItemWithDecals Item;
            public List<Camo.DecalInfo> DecalsInfo;
        }

        private struct Draw
        {
            public Transform Magazine;
            public Matrix4x4 FromRest;
            public Owner Owner;
        }

        private struct Still
        {
            public Matrix4x4 Last;
            public int Frames;
        }

        private sealed class Clone
        {
            public Material Material;
            public int Frame = -1;
        }

        // The camo mod offsets every decal cube by half a unit so its handle sits on the
        // projector's face (DecalRenderer.DrawDecal). Copies have to match or they drift.
        private static readonly Vector3 CubeOffset = new Vector3(0f, -0.5f, 0f);
        private static readonly int NormalsCopy = Shader.PropertyToID("_NormalsCopy");

        // Within this of its rest pose a magazine counts as seated: a millimetre, in the decal
        // root's space, and the cosine of about one degree.
        private const float SeatedDistance = 0.001f;
        private const float SeatedCosine = 0.99985f;

        private const int PruneEveryFrames = 300;

        private static AccessTools.FieldRef<Camo.Plugin, Dictionary<string, Camo.ItemsWithDecals>> _itemsWithDecals;
        private static AccessTools.FieldRef<Camo.Plugin, Dictionary<int, string>> _instanceIdToItemId;
        private static AccessTools.FieldRef<Camo.Plugin, Dictionary<Camera, HashSet<string>>> _decalCameras;
        private static AccessTools.FieldRef<Camo.Plugin, Camo.DecalRenderer> _decalRenderer;
        private static AccessTools.FieldRef<Camo.DecalRenderer, Dictionary<Camera, CommandBuffer>> _camoBuffers;
        private static AccessTools.FieldRef<TransformLinks, TransformLinks.CachedTransform[]> _cachedTransforms;

        private static Mesh _cube;
        private static bool _available;
        private static bool _failed;
        private static int _refreshedFrame = -1;

        private static readonly Dictionary<Transform, Owner> Roots = new Dictionary<Transform, Owner>();
        private static readonly List<Draw> Draws = new List<Draw>();
        private static readonly Dictionary<Camera, CommandBuffer> Buffers = new Dictionary<Camera, CommandBuffer>();
        private static readonly Dictionary<Camo.Decal, Clone> Clones = new Dictionary<Camo.Decal, Clone>();
        private static readonly Dictionary<TransformLinks, Dictionary<Transform, TransformLinks.CachedTransform>> RestPoses =
            new Dictionary<TransformLinks, Dictionary<Transform, TransformLinks.CachedTransform>>();
        private static readonly HashSet<int> ReportedNoRestPose = new HashSet<int>();

        // Where a gun's magazine sits, once that has been established, by decal root. Shared
        // across magazines: a fresh one handed in by a reload inherits the seat the one it
        // replaces was watched in.
        private static readonly Dictionary<Transform, Matrix4x4> Seats = new Dictionary<Transform, Matrix4x4>();

        // How long each magazine has held still, while no seat is known for its gun yet.
        private static readonly Dictionary<Transform, Still> Motion = new Dictionary<Transform, Still>();
        private static readonly HashSet<int> LearnedSeat = new HashSet<int>();

        // The loop earns its keep only when something, somewhere, is set to stick: that is the
        // one answer that changes from frame to frame. With nothing sticky, every magazine just
        // stays clean, and MagazineStencil's one-shot at spawn does that for free.
        private static bool Active =>
            _available
            && !_failed
            && NoMagazineCamoPlugin.Enabled.Value
            && (NoMagazineCamoPlugin.MagazineCamo.Value == MagazineCamoMode.StickToMagazine
                || MagazineChoices.AnyStick)
            && Camo.Plugin.Instance != null;

        /// <summary>Whether this loop is the thing deciding what magazines get, rather than
        /// MagazineStencil's one-shot at spawn.</summary>
        internal static bool Deciding => Active;

        internal static void Install()
        {
            try
            {
                // All by name. The first five are the camo mod's private state; the last is the
                // rest pose the game restores a weapon's animated bones to.
                _itemsWithDecals = AccessTools.FieldRefAccess<Camo.Plugin, Dictionary<string, Camo.ItemsWithDecals>>("ItemsWithDecals");
                _instanceIdToItemId = AccessTools.FieldRefAccess<Camo.Plugin, Dictionary<int, string>>("InstanceIdToItemId");
                _decalCameras = AccessTools.FieldRefAccess<Camo.Plugin, Dictionary<Camera, HashSet<string>>>("DecalCameras");
                _decalRenderer = AccessTools.FieldRefAccess<Camo.Plugin, Camo.DecalRenderer>("DecalRenderer");
                _camoBuffers = AccessTools.FieldRefAccess<Camo.DecalRenderer, Dictionary<Camera, CommandBuffer>>("CommandBuffers");
                _cachedTransforms = AccessTools.FieldRefAccess<TransformLinks, TransformLinks.CachedTransform[]>("_cachedTransforms");
            }
            catch (Exception e)
            {
                NoMagazineCamoPlugin.Log.LogError(
                    "[NoMagazineCamo] 'Stick to magazine' is unavailable -- Weapon Camo And Stickers or the game "
                    + $"has changed. Magazines will be kept clean instead.\n{e.Message}");
                return;
            }

            _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            _available = true;

            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
        }

        /// <summary>Forgets this frame's work, so a settings change shows on the next one.</summary>
        internal static void Reset()
        {
            Draws.Clear();
            _refreshedFrame = -1;
            foreach (var buffer in Buffers.Values)
            {
                buffer.Clear();
            }
        }

        private static void OnPreCull(Camera camera)
        {
            try
            {
                Refresh();

                if (Draws.Count == 0 || Buffers.ContainsKey(camera) || !CamoDrawsOn(camera))
                {
                    return;
                }

                // Only ever added to a camera that already carries the camo mod's buffer, so this
                // one always runs after it.
                var buffer = new CommandBuffer { name = "[NoMagazineCamo] Sticky magazine camo" };
                camera.AddCommandBuffer(CameraEvent.BeforeLighting, buffer);
                Buffers.Add(camera, buffer);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void OnPreRender(Camera camera)
        {
            if (!Buffers.TryGetValue(camera, out var buffer))
            {
                return;
            }

            try
            {
                buffer.Clear();
                if (Draws.Count == 0 || !Active || !CamoDrawsOn(camera))
                {
                    return;
                }

                // The camo mod's own split: a preview camera draws only the items it was given,
                // and every other camera it draws on -- the player's, and the scope's -- draws
                // everything. null here means everything.
                _decalCameras(Camo.Plugin.Instance).TryGetValue(camera, out var previewItems);

                var started = false;
                foreach (var draw in Draws)
                {
                    if (draw.Magazine == null || (previewItems != null && !previewItems.Contains(draw.Owner.ItemId)))
                    {
                        continue;
                    }

                    if (!started)
                    {
                        buffer.GetTemporaryRT(NormalsCopy, -1, -1);
                        buffer.Blit(BuiltinRenderTextureType.GBuffer2, NormalsCopy);
                        buffer.SetRenderTarget(BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.CameraTarget);
                        started = true;
                    }

                    DrawOnMagazine(draw, buffer);
                }

                if (started)
                {
                    buffer.ReleaseTemporaryRT(NormalsCopy);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Asked of the camo mod rather than re-derived. Its own test goes through
        // EFT.CameraControl.CameraManager, which not every game build has under that name, and
        // having a buffer is the one answer that is right on all of them.
        private static bool CamoDrawsOn(Camera camera)
        {
            var renderer = _decalRenderer(Camo.Plugin.Instance);
            return renderer != null && _camoBuffers(renderer).ContainsKey(camera);
        }

        // Once per frame, before the first camera culls: decide which magazines are off their
        // seat and set every magazine's stencil to match.
        private static void Refresh()
        {
            var frame = Time.frameCount;
            if (_refreshedFrame == frame)
            {
                return;
            }

            _refreshedFrame = frame;
            Draws.Clear();

            if (frame % PruneEveryFrames == 0)
            {
                Prune();
            }

            if (!Active)
            {
                return;
            }

            BuildRoots();

            var magazines = MagazineStencil.All;
            for (var i = magazines.Count - 1; i >= 0; i--)
            {
                var magazine = magazines[i];
                if (!magazine.Alive)
                {
                    magazines[i] = magazines[magazines.Count - 1];
                    magazines.RemoveAt(magazines.Count - 1);
                    continue;
                }

                if (!magazine.Transform.gameObject.activeInHierarchy
                    || !FindOwner(magazine.Transform, out var root, out var owner))
                {
                    // Not on a weapon the camo mod is drawing. Nothing would paint it either way.
                    magazine.Restore();
                    continue;
                }

                if (MagazineChoices.Resolve(owner.ItemId) == MagazineCamoMode.None)
                {
                    magazine.Clean();
                    continue;
                }

                var now = root.worldToLocalMatrix * magazine.Transform.localToWorldMatrix;
                if (!RestPose(root, magazine.Transform, now, out var rest))
                {
                    // Nothing knows where this magazine sits yet. Vanilla until a seated one
                    // has been watched long enough to learn it.
                    magazine.Restore();
                    continue;
                }

                if (IsSeated(now, rest))
                {
                    // Seated is the one pose worth remembering: it is the answer for every
                    // later magazine on this gun, including one that appears mid-reload.
                    Seats[root] = now;
                    magazine.Restore();
                    continue;
                }

                magazine.Clean();
                Draws.Add(new Draw
                {
                    Magazine = magazine.Transform,
                    FromRest = rest.inverse,
                    Owner = owner,
                });
            }
        }

        // Every spawned item the camo mod is drawing, by the transform its decals hang from.
        private static void BuildRoots()
        {
            Roots.Clear();

            var plugin = Camo.Plugin.Instance;
            var itemsWithDecals = _itemsWithDecals(plugin);

            foreach (var pair in _instanceIdToItemId(plugin))
            {
                if (!itemsWithDecals.TryGetValue(pair.Value, out var items)
                    || !items.Items.TryGetValue(pair.Key, out var item)
                    || item.Decals.Count == 0)
                {
                    continue;
                }

                // Weapons use SimpleDecalsHost. Skinned equipment uses a different host and
                // carries no magazines.
                if (item.DecalsHost is Camo.SimpleDecalsHost host && host.DecalsRoot != null)
                {
                    Roots[host.DecalsRoot] = new Owner
                    {
                        ItemId = pair.Value,
                        Item = item,
                        DecalsInfo = items.DecalsInfo,
                    };
                }
            }
        }

        private static bool FindOwner(Transform magazine, out Transform root, out Owner owner)
        {
            for (var transform = magazine.parent; transform != null; transform = transform.parent)
            {
                if (Roots.TryGetValue(transform, out owner))
                {
                    root = transform;
                    return true;
                }
            }

            root = null;
            owner = default;
            return false;
        }

        // Where this magazine sits when it is seated, in the decal root's space.
        //
        // First choice is the weapon's own prefab data: the bone poses ResetPositions puts an
        // animated weapon back to. On every weapon looked at so far that data holds nothing
        // between the decal root and the magazine, so the second choice is to watch for it --
        // a magazine that has held still for a few frames is a seated magazine, and its pose
        // is the seat for every magazine that gun takes afterwards.
        private static bool RestPose(Transform root, Transform magazine, Matrix4x4 now, out Matrix4x4 rest)
        {
            // Seats first: it is a dictionary lookup, and once a gun has been seen with a
            // seated magazine it answers this without walking anything.
            if (Seats.TryGetValue(root, out rest))
            {
                return true;
            }

            if (FromPrefab(root, magazine, out rest))
            {
                return true;
            }

            return Learn(root, magazine, now, out rest);
        }

        // The magazine's pose with every bone between it and the root put back where the game
        // resets it to. False unless at least one of those bones actually has a rest pose:
        // without one this would rebuild the pose the magazine is already in, which reads as
        // seated no matter where the magazine has got to.
        private static bool FromPrefab(Transform root, Transform magazine, out Matrix4x4 rest)
        {
            var restPoses = RestPosesAbove(root, out var links);
            rest = Matrix4x4.identity;
            var anyRestPose = false;

            for (var transform = magazine; transform != null && transform != root; transform = transform.parent)
            {
                var position = transform.localPosition;
                var rotation = transform.localRotation;

                if (restPoses != null && restPoses.TryGetValue(transform, out var cached))
                {
                    position = cached.Position;
                    rotation = cached.Rotation;
                    anyRestPose = true;
                }

                rest = Matrix4x4.TRS(position, rotation, transform.localScale) * rest;
            }

            if (!anyRestPose)
            {
                ReportNoPrefabPose(root, magazine, links, restPoses);
            }

            return anyRestPose;
        }

        // A magazine that has not moved for this many frames is taken to be seated. Long enough
        // that no part of a reload animation holds still through it.
        private const int SeatedFrames = 12;

        private static bool Learn(Transform root, Transform magazine, Matrix4x4 now, out Matrix4x4 rest)
        {
            Motion.TryGetValue(magazine, out var still);

            if (still.Frames > 0 && IsSeated(now, still.Last))
            {
                still.Frames++;
            }
            else
            {
                still.Frames = 1;
            }

            still.Last = now;
            Motion[magazine] = still;

            if (still.Frames < SeatedFrames)
            {
                rest = Matrix4x4.identity;
                return false;
            }

            if (LearnedSeat.Add(root.GetInstanceID()))
            {
                // One line a session at a level that reaches disk, then quiet: this fires per
                // weapon, and a shared log is not the place to say the same thing forty times.
                if (LearnedSeat.Count == 1)
                {
                    NoMagazineCamoPlugin.Log.LogInfo(
                        $"[NoMagazineCamo] sticky magazine camo is live -- learned where {root.root.name}'s "
                        + "magazine sits by watching it.");
                }
                else
                {
                    NoMagazineCamoPlugin.Log.LogDebug(
                        $"[NoMagazineCamo] learned where {root.root.name}'s magazine sits.");
                }
            }

            Seats[root] = now;
            rest = now;
            return true;
        }

        // Says which of the three ways this can come up empty actually happened. Debug, not a
        // warning: it is expected on every weapon looked at so far, and nothing is wrong when it
        // happens -- the seat gets learned by watching instead.
        private static void ReportNoPrefabPose(
            Transform root,
            Transform magazine,
            TransformLinks links,
            Dictionary<Transform, TransformLinks.CachedTransform> restPoses)
        {
            if (!ReportedNoRestPose.Add(root.GetInstanceID()))
            {
                return;
            }

            string reason;
            if (links == null)
            {
                reason = "no TransformLinks on or above its camo root";
            }
            else if (restPoses == null || restPoses.Count == 0)
            {
                reason = $"TransformLinks on '{links.name}' has an empty _cachedTransforms";
            }
            else
            {
                reason = $"TransformLinks on '{links.name}' caches {restPoses.Count} transform(s), none of them "
                    + "between its camo root and its magazine";
            }

            NoMagazineCamoPlugin.Log.LogDebug(
                $"[NoMagazineCamo] {root.root.name}: {reason}. Falling back to watching for a seated magazine.");

            if (restPoses != null)
            {
                NoMagazineCamoPlugin.Log.LogDebug(
                    $"[NoMagazineCamo] {root.root.name} cached: {string.Join(", ", Names(restPoses.Keys))}");
            }

            var chain = new List<string>();
            for (var transform = magazine; transform != null && transform != root; transform = transform.parent)
            {
                chain.Add(transform.name);
            }

            NoMagazineCamoPlugin.Log.LogDebug(
                $"[NoMagazineCamo] {root.root.name} magazine chain: {string.Join(" < ", chain.ToArray())} < {root.name}");
        }

        private static string[] Names(IEnumerable<Transform> transforms)
        {
            var names = new List<string>();
            foreach (var transform in transforms)
            {
                names.Add(transform == null ? "<destroyed>" : transform.name);
            }

            return names.ToArray();
        }

        private static Dictionary<Transform, TransformLinks.CachedTransform> RestPosesAbove(
            Transform root, out TransformLinks links)
        {
            links = null;
            for (var transform = root; transform != null && links == null; transform = transform.parent)
            {
                transform.TryGetComponent(out links);
            }

            if (links == null)
            {
                return null;
            }

            if (!RestPoses.TryGetValue(links, out var table))
            {
                table = new Dictionary<Transform, TransformLinks.CachedTransform>();
                var cached = _cachedTransforms(links);
                if (cached != null)
                {
                    foreach (var entry in cached)
                    {
                        if (entry.Transform != null)
                        {
                            table[entry.Transform] = entry;
                        }
                    }
                }

                RestPoses.Add(links, table);
            }

            return table;
        }

        private static bool IsSeated(Matrix4x4 now, Matrix4x4 rest)
        {
            if (((Vector3)now.GetColumn(3) - (Vector3)rest.GetColumn(3)).sqrMagnitude > SeatedDistance * SeatedDistance)
            {
                return false;
            }

            return Vector3.Dot(((Vector3)now.GetColumn(1)).normalized, ((Vector3)rest.GetColumn(1)).normalized) >= SeatedCosine
                && Vector3.Dot(((Vector3)now.GetColumn(2)).normalized, ((Vector3)rest.GetColumn(2)).normalized) >= SeatedCosine;
        }

        private static void DrawOnMagazine(Draw draw, CommandBuffer buffer)
        {
            var decals = draw.Owner.Item.Decals;
            var decalsInfo = draw.Owner.DecalsInfo;

            // Stands in for the decal root's localToWorldMatrix in the camo mod's own draw: equal
            // to it while the magazine is seated, and following the magazine once it is not.
            var carried = draw.Magazine.localToWorldMatrix * draw.FromRest;

            var count = Math.Min(decals.Count, decalsInfo.Count);
            for (var i = 0; i < count; i++)
            {
                var decal = decals[i];
                var info = decalsInfo[i];
                if (!info.IsVisible || decal == null || decal.DecalMaterial == null)
                {
                    continue;
                }

                var material = CloneOf(decal);
                var transform = decal.DecalTransform;
                DrawDecal(buffer, carried, transform.localPosition, transform.localRotation, transform.localScale, material);

                switch (info.MirrorMode)
                {
                    case Camo.DecalMirrorMode.Enabled:
                        DrawMirrored(buffer, carried, transform, material, true);
                        break;
                    case Camo.DecalMirrorMode.EnabledNoFlip:
                        DrawMirrored(buffer, carried, transform, material, false);
                        break;
                }
            }
        }

        private static void DrawMirrored(CommandBuffer buffer, Matrix4x4 carried, Transform transform, Material material, bool flipHorizontally)
        {
            var position = transform.localPosition;
            var rotation = transform.localRotation;
            var scale = transform.localScale;
            Camo.Plugin.MirrorLeftRight(ref position, ref rotation, ref scale, flipHorizontally);
            DrawDecal(buffer, carried, position, rotation, scale, material);
        }

        private static void DrawDecal(CommandBuffer buffer, Matrix4x4 carried, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            buffer.DrawMesh(_cube, carried * Matrix4x4.TRS(position, rotation, scale) * Matrix4x4.Translate(CubeOffset), material);
        }

        // A copy of the decal's material that draws on the clean stencil. Refreshed from the
        // original once a frame, so edits made in the camo editor show on the copy as well.
        private static Material CloneOf(Camo.Decal decal)
        {
            if (!Clones.TryGetValue(decal, out var clone))
            {
                clone = new Clone { Material = new Material(decal.DecalMaterial) };
                Clones.Add(decal, clone);
            }

            var frame = Time.frameCount;
            if (clone.Frame != frame)
            {
                clone.Frame = frame;
                var source = decal.DecalMaterial;
                var erase = decal.DecalMaterialKeywordErase;
                clone.Material.CopyPropertiesFromMaterial(source);
                clone.Material.SetKeyword(erase, source.IsKeywordEnabled(erase));
                clone.Material.SetFloat(MagazineStencil.StencilTypeId, MagazineStencil.CleanStencil);
            }

            return clone.Material;
        }

        private static readonly List<Camera> DeadCameras = new List<Camera>();
        private static readonly List<Camo.Decal> DeadDecals = new List<Camo.Decal>();
        private static readonly List<TransformLinks> DeadLinks = new List<TransformLinks>();
        private static readonly List<Transform> DeadTransforms = new List<Transform>();

        private static void Prune()
        {
            foreach (var pair in Buffers)
            {
                if (pair.Key == null)
                {
                    DeadCameras.Add(pair.Key);
                }
            }

            foreach (var camera in DeadCameras)
            {
                Buffers[camera].Release();
                Buffers.Remove(camera);
            }

            foreach (var pair in Clones)
            {
                if (pair.Key == null)
                {
                    DeadDecals.Add(pair.Key);
                }
            }

            foreach (var decal in DeadDecals)
            {
                UnityEngine.Object.Destroy(Clones[decal].Material);
                Clones.Remove(decal);
            }

            foreach (var links in RestPoses.Keys)
            {
                if (links == null)
                {
                    DeadLinks.Add(links);
                }
            }

            foreach (var links in DeadLinks)
            {
                RestPoses.Remove(links);
            }

            PruneTransforms(Seats);
            PruneTransforms(Motion);

            DeadCameras.Clear();
            DeadDecals.Clear();
            DeadLinks.Clear();
        }

        private static void PruneTransforms<T>(Dictionary<Transform, T> table)
        {
            foreach (var pair in table)
            {
                if (pair.Key == null)
                {
                    DeadTransforms.Add(pair.Key);
                }
            }

            foreach (var transform in DeadTransforms)
            {
                table.Remove(transform);
            }

            DeadTransforms.Clear();
        }

        private static void Fail(Exception e)
        {
            if (_failed)
            {
                return;
            }

            _failed = true;
            Reset();
            MagazineStencil.ApplySettings();
            NoMagazineCamoPlugin.Log.LogError(
                $"[NoMagazineCamo] 'Stick to magazine' stopped after an error; magazines are kept clean instead.\n{e}");
        }
    }
}
