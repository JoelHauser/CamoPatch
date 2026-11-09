using System;
using System.Collections.Generic;
using Comfort.Common;
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
    /// Only magazines on a painted gun the local player carries, or one the camo mod is showing
    /// in a preview, are ever changed -- in every mode. A bot's gun, another player's, a loose
    /// magazine on the floor: all keep exactly what they shipped with. Picking a painted gun up
    /// off a body brings its magazine under the player, and it follows the setting from then on.
    ///
    /// Drawn the way the camo mod draws its own decals (DecalRenderer.cs, MIT): a cube per
    /// decal into GBuffer0, on exactly the cameras it draws on. The camo mod itself draws at
    /// BeforeLighting (CameraEvent 6, checked in 1.19.0); these go at BeforeReflections.
    ///
    /// The magazine is on the clean stencil only inside two windows: around these draws, and
    /// around the camo mod's buffer (AroundCamoBuffer). Up to 1.3.0 it carried 3 from the
    /// G-buffer through to the restore, and outdoors the game's passes in between -- the sun's
    /// distant shadow mask and Wetting among them -- took it for world rather than hands and
    /// painted flickering patterns onto it during a reload (2026-11-08, gone indoors).
    ///
    /// BeforeReflections, not BeforeLighting, and that is not a detail. The decal shader tests
    /// `Ref [_StencilType] ReadMask 3 Comp Equal` against the stencil buffer as the G-buffer
    /// pass left it, and Unity's deferred reflections pass sits in between and uses that same
    /// buffer for its own probe culling. Drawn after it, the cubes compare against a stencil
    /// whose low two bits are no longer the camo mod's categories -- background geometry can
    /// match 3 and take a magazine-sized box of camo albedo, which is exactly what it looks
    /// like. The stencil restore still belongs at BeforeLighting, so the two jobs go in two
    /// buffers on two events rather than one buffer doing both.
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
        private static readonly HashSet<string> PreviewItems = new HashSet<string>();

        // The local player's weapon in hands, refreshed once a frame. It is not under the Player:
        // WeaponPrefab.Init detaches a firearm to the scene root, and the hands controller only
        // positions it there, so the walk up to a Player never reaches one from a gun in hand.
        private static Transform _yourHands;

        private static readonly List<Draw> Draws = new List<Draw>();
        // Two per camera, on two events: the decal cubes alongside the camo mod's own at
        // BeforeReflections, and the stencil restore at BeforeLighting, once every pass that
        // reads the clean stencil has run and before the lighting passes read it back.
        private static readonly Dictionary<Camera, CommandBuffer> DecalBuffers = new Dictionary<Camera, CommandBuffer>();
        private static readonly Dictionary<Camera, CommandBuffer> RestoreBuffers = new Dictionary<Camera, CommandBuffer>();
        // Directly before the camo mod's buffer at BeforeLighting, as the restore is directly
        // after it: the magazine is on the clean stencil for that one buffer and nothing else.
        private static readonly Dictionary<Camera, CommandBuffer> CleanBuffers = new Dictionary<Camera, CommandBuffer>();
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

        // Runs in every mode, not only when something is set to stick. Which magazines are the
        // player's changes from frame to frame too -- a gun picked up off a body, a preview
        // opened -- and the stencil is the game's lighting category rather than the camo mod's
        // private tag, so it is moved only on magazines the player actually sees on their own
        // painted guns. MagazineStencil's one-shot at spawn is only the fallback for when the
        // camo mod's state could not be reached at all.
        private static bool Active =>
            _available
            && !_failed
            && NoMagazineCamoPlugin.Enabled.Value
            && Camo.Plugin.Instance != null;

        /// <summary>Whether this loop is the thing deciding what magazines get, rather than
        /// MagazineStencil's one-shot at spawn.</summary>
        internal static bool Deciding => Active;

        /// <summary>Whether magazines keep the gun's stencil in their materials and are stamped
        /// onto the clean one only around the decal passes, rather than carrying it from the
        /// G-buffer on. Needs this loop's buffers and the restore's material.</summary>
        internal static bool Stamping => Active && StencilRestore.Available;

        // A buffer is wanted for either job: drawing a magazine's camo onto it, or putting its
        // stencil back before the lighting reads it. The second happens in the keep-clean mode
        // too, where the loop above never runs -- a magazine sits moved off the gun's stencil
        // permanently there, so it needs putting back on every frame.
        private static bool Wanted =>
            _available
            && !_failed
            && NoMagazineCamoPlugin.Enabled.Value
            && Camo.Plugin.Instance != null
            && (Draws.Count > 0 || AnyMoved());

        private static bool AnyMoved()
        {
            var magazines = MagazineStencil.All;
            for (var i = 0; i < magazines.Count; i++)
            {
                if (magazines[i].Alive && magazines[i].IsMoved)
                {
                    return true;
                }
            }

            return false;
        }

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
            foreach (var buffer in DecalBuffers.Values)
            {
                buffer.Clear();
            }

            foreach (var buffer in RestoreBuffers.Values)
            {
                buffer.Clear();
            }

            foreach (var buffer in CleanBuffers.Values)
            {
                buffer.Clear();
            }
        }

        private static void OnPreCull(Camera camera)
        {
            try
            {
                Refresh();

                if (!Wanted || DecalBuffers.ContainsKey(camera) || !CamoDrawsOn(camera))
                {
                    return;
                }

                // Only ever added to a camera that already carries the camo mod's buffer, and the
                // decal one goes on the same event that one is on. Buffers on a single event run
                // in the order they were added, and this is added from a later onPreCull, so it
                // always runs after the camo mod's.
                var decals = new CommandBuffer { name = "[NoMagazineCamo] Sticky magazine camo" };
                camera.AddCommandBuffer(CameraEvent.BeforeReflections, decals);
                DecalBuffers.Add(camera, decals);

                var clean = new CommandBuffer { name = "[NoMagazineCamo] Magazine stencil clean" };
                var restore = new CommandBuffer { name = "[NoMagazineCamo] Magazine stencil restore" };
                AroundCamoBuffer(camera, clean, restore);
                CleanBuffers.Add(camera, clean);
                RestoreBuffers.Add(camera, restore);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Puts `clean` directly before the camo mod's buffer at BeforeLighting and `restore`
        // directly after it. Buffers on one event run in the order they were added, so the camo
        // mod's buffer -- its own object, from its CommandBuffers -- is taken off and put back
        // between ours. It ends up after the game's decal and stencil-shadow buffers rather than
        // before them; it only writes GBuffer0 on weapons and equipment, which those do not touch.
        //
        // Not by comparing against camera.GetCommandBuffers: that hands out fresh wrapper objects,
        // so the camo mod's buffer is never reference-equal to anything in it (the first try,
        // 2026-11-08, put both of ours at the end and left the camo pass painting the magazine).
        // Its name is what is compared, only to check it really is on this camera.
        private static void AroundCamoBuffer(Camera camera, CommandBuffer clean, CommandBuffer restore)
        {
            var renderer = _decalRenderer(Camo.Plugin.Instance);
            CommandBuffer camo = null;
            if (renderer != null)
            {
                _camoBuffers(renderer).TryGetValue(camera, out camo);
            }

            var onCamera = false;
            if (camo != null)
            {
                foreach (var existing in camera.GetCommandBuffers(CameraEvent.BeforeLighting))
                {
                    if (existing.name == camo.name)
                    {
                        onCamera = true;
                        break;
                    }
                }
            }

            if (!onCamera)
            {
                NoMagazineCamoPlugin.Log.LogWarning(
                    $"[NoMagazineCamo] {camera.name}: the camo mod's buffer is not at BeforeLighting; "
                    + "magazine stencil buffers appended instead.");
                camera.AddCommandBuffer(CameraEvent.BeforeLighting, clean);
                camera.AddCommandBuffer(CameraEvent.BeforeLighting, restore);
                return;
            }

            camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, camo);
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, clean);
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, camo);
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, restore);
        }

        private static void OnPreRender(Camera camera)
        {
            if (!DecalBuffers.TryGetValue(camera, out var buffer)
                || !RestoreBuffers.TryGetValue(camera, out var restore)
                || !CleanBuffers.TryGetValue(camera, out var clean))
            {
                return;
            }

            try
            {
                buffer.Clear();
                restore.Clear();
                clean.Clear();
                if (!Wanted || !CamoDrawsOn(camera))
                {
                    return;
                }

                var stamping = Stamping;

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
                        if (stamping)
                        {
                            // The magazines carry the gun's stencil out of the G-buffer; these
                            // cubes test the clean one. On it for these draws only.
                            StencilRestore.QueueClean(buffer, MagazineStencil.All, camera);
                        }

                        started = true;
                    }

                    DrawOnMagazine(draw, buffer);
                }

                if (started)
                {
                    if (stamping)
                    {
                        StencilRestore.Queue(buffer, MagazineStencil.All, camera);
                    }

                    buffer.ReleaseTemporaryRT(NormalsCopy);
                }

                // At BeforeLighting, either side of the camo mod's buffer (AroundCamoBuffer): on
                // the clean stencil while its decals draw, so they skip the magazine, and back on
                // the gun's straight after. Every other pass sees a magazine in hands. With the
                // material fallback the magazine is already clean, so only the restore is needed.
                if (stamping)
                {
                    clean.SetRenderTarget(BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.CameraTarget);
                    StencilRestore.QueueClean(clean, MagazineStencil.All, camera);
                }

                restore.SetRenderTarget(BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.CameraTarget);
                StencilRestore.Queue(restore, MagazineStencil.All, camera);
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
                    magazine.Release();
                    magazines[i] = magazines[magazines.Count - 1];
                    magazines.RemoveAt(magazines.Count - 1);
                    continue;
                }

                var active = magazine.Transform.gameObject.activeInHierarchy;
                Transform root = null;
                Owner owner = default;
                if (!active || !FindOwner(magazine.Transform, out root, out owner))
                {
                    // Not on a painted weapon of the player's, nor in a preview. Either nothing
                    // would paint it, or it is someone else's and is drawn as the camo mod draws it.
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

                // A seat learned before the gun's animation ran is off by a few millimetres:
                // 2026-11-08, an F2000 learned on the player's body during loading, then sat 3 mm
                // from it in the hands for the whole raid, so it never counted as seated and was
                // carried against the wrong seat. A magazine holding still this close to its seat
                // is seated; reloads and magazine checks move it centimetres, not millimetres.
                if (IsNearSeat(now, rest))
                {
                    if (StillFrames(magazine.Transform, now) >= SeatedFrames)
                    {
                        NoMagazineCamoPlugin.Log.LogDebug(
                            $"[NoMagazineCamo] {root.root.name}: magazine seat corrected by "
                            + $"{((Vector3)now.GetColumn(3) - (Vector3)rest.GetColumn(3)).magnitude * 1000f:F1} mm.");
                        Seats[root] = now;
                        magazine.Restore();
                        continue;
                    }
                }
                else
                {
                    Motion.Remove(magazine.Transform);
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

        // Every spawned item the camo mod is drawing, by the transform its decals hang from, and
        // every item it is drawing in a preview.
        private static void BuildRoots()
        {
            Roots.Clear();
            PreviewItems.Clear();
            _yourHands = YourHands();

            var plugin = Camo.Plugin.Instance;
            foreach (var items in _decalCameras(plugin).Values)
            {
                PreviewItems.UnionWith(items);
            }

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

        // The local player's hands object, or null outside a game world (menus, which the
        // preview sets cover) and while the hands hold nothing.
        private static Transform YourHands()
        {
            var world = Singleton<EFT.GameWorld>.Instance;
            if (world == null)
            {
                return null;
            }

            var player = world.MainPlayer;
            if (player == null)
            {
                return null;
            }

            var hands = player.HandsController;
            if (hands == null)
            {
                return null;
            }

            var model = hands.ControllerGameObject;
            return model != null ? model.transform : null;
        }

        // True for a magazine on a painted gun that the local player carries -- in hand, slung,
        // or just picked up off a body -- or on one the camo mod is showing in a preview: weapon
        // inspection and modding, the camo editor, inventory icons, the character screen. The
        // decal root is looked for first, because a dictionary lookup per bone is cheap and most
        // magazines (loose, or on a bare gun) have none; only then does the walk go on up.
        //
        // Two ways to be the player's from there. A slung gun hangs off the player's body, so a
        // Player turns up above it. The gun in hand does not: it sits at the scene root (see
        // _yourHands), so it is recognised by being the local player's hands object instead.
        private static bool FindOwner(Transform magazine, out Transform root, out Owner owner)
        {
            root = null;
            owner = default;

            var transform = magazine.parent;
            for (; transform != null; transform = transform.parent)
            {
                if (Roots.TryGetValue(transform, out owner))
                {
                    root = transform;
                    break;
                }
            }

            if (root == null)
            {
                return false;
            }

            if (PreviewItems.Contains(owner.ItemId))
            {
                return true;
            }

            for (; transform != null; transform = transform.parent)
            {
                if (transform == _yourHands)
                {
                    return true;
                }

                if (transform.TryGetComponent<EFT.Player>(out var player))
                {
                    return player.IsYourPlayer;
                }
            }

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

        // How many frames in a row this magazine has held this pose, counting this one.
        private static int StillFrames(Transform magazine, Matrix4x4 now)
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
            return still.Frames;
        }

        // Within this of its seat, a magazine holding still is seated and the seat was learned a
        // little off. A reload or magazine check takes it centimetres away.
        private const float NearSeatDistance = 0.01f;
        private const float NearSeatCosine = 0.9962f; // 5 degrees

        private static bool IsNearSeat(Matrix4x4 now, Matrix4x4 rest)
        {
            if (((Vector3)now.GetColumn(3) - (Vector3)rest.GetColumn(3)).sqrMagnitude > NearSeatDistance * NearSeatDistance)
            {
                return false;
            }

            return Vector3.Dot(((Vector3)now.GetColumn(1)).normalized, ((Vector3)rest.GetColumn(1)).normalized) >= NearSeatCosine
                && Vector3.Dot(((Vector3)now.GetColumn(2)).normalized, ((Vector3)rest.GetColumn(2)).normalized) >= NearSeatCosine;
        }

        private static bool Learn(Transform root, Transform magazine, Matrix4x4 now, out Matrix4x4 rest)
        {
            if (StillFrames(magazine, now) < SeatedFrames)
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
            foreach (var pair in DecalBuffers)
            {
                if (pair.Key == null)
                {
                    DeadCameras.Add(pair.Key);
                }
            }

            foreach (var camera in DeadCameras)
            {
                DecalBuffers[camera].Release();
                DecalBuffers.Remove(camera);

                if (RestoreBuffers.TryGetValue(camera, out var restore))
                {
                    restore.Release();
                    RestoreBuffers.Remove(camera);
                }

                if (CleanBuffers.TryGetValue(camera, out var clean))
                {
                    clean.Release();
                    CleanBuffers.Remove(camera);
                }
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

            // Refresh drops dead magazines too, but only while the loop is active. This keeps the
            // list, and the material copies Release frees, from growing while it is not.
            MagazineStencil.All.RemoveAll(magazine =>
            {
                if (magazine.Alive)
                {
                    return false;
                }

                magazine.Release();
                return true;
            });
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
