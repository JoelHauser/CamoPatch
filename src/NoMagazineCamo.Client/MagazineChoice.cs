using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NoMagazineCamo.Client
{
    /// <summary>What one weapon does with its magazine. Default hands the choice to F12.</summary>
    internal enum MagazineChoice
    {
        [Description("Default")]
        Default,

        [Description("Keep clean")]
        None,

        [Description("Stick to magazine")]
        StickToMagazine,
    }

    /// <summary>
    /// The per-weapon choices, by the camo mod's item id -- the same id it files a weapon's
    /// decals under in `items/&lt;id&gt;.json`, so this is per weapon in the stash, not per weapon
    /// type, and it survives a preset change the same way the decals' own id does.
    ///
    /// Kept in this mod's own file. The camo mod's JSON carries a schema version it checks, and
    /// writing a field of ours into it would be a field it strips or trips over on its next
    /// update.
    /// </summary>
    internal static class MagazineChoices
    {
        private static readonly Dictionary<string, MagazineChoice> Choices =
            new Dictionary<string, MagazineChoice>();

        private static string _path;
        private static bool _loaded;

        /// <summary>Whether any weapon is set to stick. Kept up to date rather than scanned,
        /// because the per-frame loop asks every frame: with nothing sticky anywhere there is
        /// nothing for it to work out, and the cheap path at spawn covers the rest.</summary>
        internal static bool AnyStick { get; private set; }

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
        };

        internal static void Load()
        {
            // Qualified: Assembly-CSharp has a Paths of its own in the global namespace, and
            // it wins over the using.
            _path = Path.Combine(BepInEx.Paths.ConfigPath, "NoMagazineCamo", "magazines.json");
            _loaded = true;

            try
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                var stored = JsonConvert.DeserializeObject<Dictionary<string, MagazineChoice>>(
                    File.ReadAllText(_path), Settings);
                if (stored == null)
                {
                    return;
                }

                foreach (var pair in stored)
                {
                    if (pair.Value != MagazineChoice.Default)
                    {
                        Choices[pair.Key] = pair.Value;
                    }
                }

                Recount();

                NoMagazineCamoPlugin.Log.LogInfo(
                    $"[NoMagazineCamo] {Choices.Count} weapon(s) have their own magazine setting");
            }
            catch (Exception e)
            {
                // A file that cannot be read is left alone rather than overwritten: it is the
                // only copy of these choices, and every weapon just falls back to F12.
                _path = null;
                NoMagazineCamoPlugin.Log.LogError(
                    $"[NoMagazineCamo] could not read the per-weapon magazine settings; every weapon "
                    + $"will follow the F12 setting and nothing will be saved over them.\n{e}");
            }
        }

        internal static MagazineChoice For(string itemId)
        {
            if (itemId != null && Choices.TryGetValue(itemId, out var choice))
            {
                return choice;
            }

            return MagazineChoice.Default;
        }

        /// <summary>The choice with Default resolved against F12. What actually gets applied.</summary>
        internal static MagazineCamoMode Resolve(string itemId)
        {
            switch (For(itemId))
            {
                case MagazineChoice.None:
                    return MagazineCamoMode.None;
                case MagazineChoice.StickToMagazine:
                    return MagazineCamoMode.StickToMagazine;
                default:
                    return NoMagazineCamoPlugin.MagazineCamo.Value;
            }
        }

        internal static void Set(string itemId, MagazineChoice choice)
        {
            if (string.IsNullOrEmpty(itemId) || !_loaded)
            {
                return;
            }

            if (choice == MagazineChoice.Default)
            {
                if (!Choices.Remove(itemId))
                {
                    return;
                }
            }
            else
            {
                if (Choices.TryGetValue(itemId, out var current) && current == choice)
                {
                    return;
                }

                Choices[itemId] = choice;
            }

            Recount();

            // The same two steps the F12 handler takes: this can start or stop the per-frame
            // loop, and every magazine already out has to be put where the new answer says.
            StickyCamo.Reset();
            MagazineStencil.ApplySettings();
            Save();
        }

        private static void Recount()
        {
            foreach (var choice in Choices.Values)
            {
                if (choice == MagazineChoice.StickToMagazine)
                {
                    AnyStick = true;
                    return;
                }
            }

            AnyStick = false;
        }

        private static void Save()
        {
            if (_path == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                // Written beside the real file and moved over it, so a crash mid-write cannot
                // leave a half-written file where every weapon's choice lives.
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(Choices, Settings));
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                File.Move(temporary, _path);
            }
            catch (Exception e)
            {
                NoMagazineCamoPlugin.Log.LogError(
                    $"[NoMagazineCamo] could not save the per-weapon magazine settings. The choice is "
                    + $"live for this session but will not come back.\n{e}");
            }
        }
    }
}
