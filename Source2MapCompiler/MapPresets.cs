using System.IO;
using System.Linq;
using ValveKeyValue;

namespace Source2MapCompiler;

// The presets of one map, kept in a file next to it named after it, de_dogtown_compilepreset.vdf for de_dogtown.vmap, so a
// map's compile settings go wherever the map goes. It holds the preset or profile last picked for the map and the map's own
// Custom options. Whatever a newer version puts in the file is kept through a save by an older one
internal sealed class MapPresets
{
    private const string PresetKey = "preset";
    private const string PresetsKey = "presets";

    private KVObject data = KVObject.Collection();

    // the name of the preset last picked, or null when none has been
    public string? Preset { get; set; }

    // the map's own presets by name, Custom and Entities only, as option ids and their values written out
    public Dictionary<string, Dictionary<string, string>> Presets { get; } = [];

    public static string PathFor(string map)
    {
        return Path.Combine(Path.GetDirectoryName(map)!, Path.GetFileNameWithoutExtension(map) + "_compilepreset.vdf");
    }

    // The map's presets, or null when it has none yet
    public static MapPresets? Load(string map)
    {
        var path = PathFor(map);

        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var stored = new MapPresets { data = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream, KVSerializerOptions.DefaultOptions).Root };

        if (stored.data.TryGetValue(PresetKey, out var preset) && (string)preset is { Length: > 0 } name)
        {
            stored.Preset = name;
        }

        if (stored.data.TryGetValue(PresetsKey, out var presets))
        {
            ReadPresets(presets, stored.Presets);
        }

        return stored;
    }

    // A block of presets by name, each with its option ids and their values written out. The settings keep the profiles
    // this way too
    public static void ReadPresets(KVObject block, Dictionary<string, Dictionary<string, string>> presets)
    {
        if (!block.IsCollection)
        {
            return;
        }

        foreach (var (name, options) in block.Children.Where(p => p.Value.IsCollection))
        {
            presets[name] = options.Children.Where(o => !o.Value.IsCollection).ToDictionary(o => o.Key, o => (string)o.Value);
        }
    }

    public static KVObject WritePresets(Dictionary<string, Dictionary<string, string>> presets)
    {
        var block = KVObject.Collection();

        foreach (var (name, options) in presets)
        {
            var preset = KVObject.Collection();

            foreach (var (id, value) in options)
            {
                preset.Add(id, value);
            }

            block.Add(name, preset);
        }

        return block;
    }

    public void Save(string map)
    {
        var saved = KVObject.Collection();

        if (Preset != null)
        {
            saved.Add(PresetKey, Preset);
        }

        saved.Add(PresetsKey, WritePresets(Presets));

        foreach (var child in data.Children.Where(c => c.Key is not (PresetKey or PresetsKey)))
        {
            saved.Add(child.Key, child.Value);
        }

        using (var stream = File.Create(PathFor(map)))
        {
            KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Serialize(stream, saved, "compilepreset");
        }

        data = saved;
    }
}
