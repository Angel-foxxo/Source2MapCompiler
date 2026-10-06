using System.Globalization;
using System.IO;
using System.Linq;

namespace Source2MapCompiler;

// Everything the compile can be told, in one place. Each option says how it shows, what it starts as, which games have it and
// what it adds to resourcecompiler's command line. Options sit in groups, which become the cards in the window, and presets
// are just values for options. To add an option, add it to a group below, and to a preset if a preset should set it.

internal enum OptionKind
{
    Toggle, // a checkbox, or the switch in a group's header
    Choice, // one of a few values, all shown as buttons
    Threads, // a thread count, from one to all of them
    Hidden, // only ever set by presets
}

internal sealed class CompileOption
{
    public required string Id { get; init; }

    public string Label { get; init; } = "";

    // shown under the label
    public string? Description { get; init; }

    // the tooltip
    public string? Help { get; init; }

    public OptionKind Kind { get; init; } = OptionKind.Toggle;

    public string[] Choices { get; init; } = [];

    // true or false for a toggle, one of the choices, or nothing for a thread count, which starts at every thread
    public object? Default { get; init; }

    // a different default for some games, or null to keep Default
    public Func<Game, object?> GameDefault { get; init; } = _ => null;

    // folded away under the group's Debug or Advanced section
    public bool Folded { get; init; }

    public Func<Game, bool> Available { get; init; } = _ => true;

    public Func<OptionValues, IEnumerable<string>> Flags { get; init; } = _ => [];

    public object? DefaultFor(Game game)
    {
        return GameDefault(game) ?? Default ?? (Kind == OptionKind.Threads ? Environment.ProcessorCount : null);
    }

    // A value as the settings file keeps it
    public static string Format(object value)
    {
        return value is bool on ? on ? "1" : "0" : Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    // A value read back from the settings file, or null when it's not one this option can have, so an edited file or one
    // from another version can't put a bad value in
    public object? Parse(string text)
    {
        return Kind switch
        {
            OptionKind.Toggle or OptionKind.Hidden => text switch { "1" => true, "0" => false, _ => null },
            OptionKind.Choice => Choices.Contains(text) ? text : null,
            OptionKind.Threads => int.TryParse(text, CultureInfo.InvariantCulture, out var threads) && threads > 0 ? Math.Min(threads, Environment.ProcessorCount) : null,
            _ => null,
        };
    }
}

internal enum GroupColumn
{
    Left,
    Right,
    Bottom,
}

internal sealed class OptionGroup
{
    public required string Name { get; init; }

    public string? Note { get; init; }

    // a note only some games get
    public Func<Game, string?> GameNote { get; init; } = _ => null;

    // the switch in the group's header. While it's off the group's options are greyed out and add nothing to the command line
    public CompileOption? Switch { get; init; }

    public required CompileOption[] Options { get; init; }

    public GroupColumn Column { get; init; }

    // the compile's stages, which Entities only skips
    public bool IsStage { get; init; } = true;

    public string FoldLabel { get; init; } = "Debug";

    // options laid out in this many columns
    public int Columns { get; init; } = 1;
}

internal enum PresetKind
{
    BuiltIn, // only sets the options it's about
    Custom, // each map's own mix of options, kept in the map's _compilepreset file
    Profile, // a mix of options the user named, kept in the settings for every map
}

// A locked profile isn't changed by changing an option while it's picked, which moves to Custom instead, like a built in
// preset does
internal sealed record Preset(string Name, string Help, string Description, IReadOnlyDictionary<string, object> Values, PresetKind Kind = PresetKind.BuiltIn, bool Locked = false)
{
    // Custom and the profiles keep every option as it was last set while they were picked
    public bool User => Kind != PresetKind.BuiltIn;
}

// The options' values, as the flags read them. Options a game doesn't have aren't in it. An option's flags get the values
// for that option, so On(), Choice() and Number() with no id are its own value
internal sealed class OptionValues(IReadOnlyDictionary<string, object> values, Game game, string? option = null)
{
    public Game Game { get; } = game;

    public OptionValues For(CompileOption option) => new(values, Game, option.Id);

    public bool Has(string id) => values.ContainsKey(id);

    public bool On(string? id = null) => values.TryGetValue(id ?? option!, out var value) && value is true;

    public string Choice(string? id = null) => (string)values[id ?? option!];

    public int Number(string? id = null) => (int)values[id ?? option!];
}

internal static class CompileOptions
{
    // set only by the Entities only preset. It rebuilds the entities alone, so it replaces -world and greys out every stage
    public static readonly CompileOption EntitiesOnly = new()
    {
        Id = "entitiesOnly",
        Kind = OptionKind.Hidden,
        Default = false,
        Flags = o => o.On() ? ["-entities"] : [],
    };

    public static readonly OptionGroup[] Groups =
    [
        new()
        {
            Name = "World",
            Column = GroupColumn.Left,
            Switch = new() { Id = "world", Help = "Build world.", Default = true, Flags = o => o.On() && !o.On("entitiesOnly") ? ["-world"] : [] },
            Options =
            [
                new() { Id = "settlePhysics", Label = "Pre-settle physics objects", Help = "Runs physics on physics objects so they don't move when the map loads.", Default = true, Flags = o => o.On() ? [] : ["-nosettle"] },
                new() { Id = "surfaceEffects", Label = "Dynamic surface effects", Help = "Build the world's dynamic surface effects.", Default = true, Flags = o => o.On() ? [] : ["-skipauxfiles"] },
                // rebake surface graph option is disabled when surfaceEffects is disabled
                new() { Id = "rebakeSurfaceGraph", Label = "Rebake Dynamic Surface Effects", Help = "Force surface graph to be rebuilt.", Default = true, Flags = o => o.On() ? ["-rebake_surfacegraph"] : [] },
                new() { Id = "deformables", Label = "Deformable geometry", Help = "Force deformable geometry to be built.", Default = false, Flags = o => o.On() ? ["-deformables forced"] : ["-deformables none"] },
                new() { Id = "debugVisGeometry", Label = "Debug vis geometry", Help = "Debug VIS Geometry.", Default = false, Folded = true, Flags = o => o.On() ? ["-debugvisgeo"] : [] },
                new() { Id = "baseTileMeshOnly", Label = "Only base tile mesh geometry", Help = "Only compile base Tile Mesh geometry.", Default = false, Folded = true, Flags = o => o.On() ? ["-tileMeshBaseGeometry"] : [] },
            ],
        },
        new()
        {
            Name = "Physics",
            Column = GroupColumn.Left,
            Note = "The collision mesh",
            Switch = new() { Id = "physics", Help = "Build collision physics mesh.", Default = true, Flags = o => o.On() ? ["-phys"] : [] },
            Options = [],
        },
        new()
        {
            Name = "Visibility",
            Column = GroupColumn.Left,
            Note = "Skips drawing what can't be seen, needed for maps you ship",
            Switch = new() { Id = "visibility", Help = "Build visibility for optimization. Must be set to On in shipping maps.", Default = true, Flags = o => o.On() ? ["-vis"] : [] },
            Options = [],
        },
        new()
        {
            Name = "Navigation",
            Column = GroupColumn.Left,
            Note = "The nav mesh needed for bots/NPCs to walk on. Also required for bomb damage calculation.",
            Switch = new() { Id = "navigation", Help = "Build navigation mesh for NPCs / CS Bots.", Default = true, GameDefault = g => g == Game.Dota2 ? false : null, Flags = o => o.On() ? ["-nav"] : [] },
            Options =
            [
                new() { Id = "gridNav", Label = "Grid nav", Help = "Build grid navigation mesh for Dota NPCs.", Default = false, Available = g => g == Game.Dota2, Flags = o => o.On() ? ["-gridnav"] : [] },
                new() { Id = "navDebug", Label = "Save debug stages to file", Help = "Saves nav debug stages to a file.", Default = false, Folded = true, Flags = o => o.On() ? ["-navdbg"] : [] },
            ],
        },
        new()
        {
            Name = "Lighting",
            Column = GroupColumn.Right,
            GameNote = g => g == Game.DeskJob ? "This game has no lighting to bake" : g.IsLegacy() ? "Only CPU lightmaps are supported" : null,
            Switch = new()
            {
                Id = "lighting",
                Help = "Bake lightmaps. GPU with RT support required.",
                Default = true,
                GameDefault = g => g is Game.Dota2 or Game.DeskJob ? false : null,
                Flags = o => !o.On() ? ["-nolightmaps"]
                    : o.Game.IsLegacy() ? ["-bakelighting", "-vrad3", "-lightmapDoWeld", "-lightmapLocalCompile"]
                    : ["-bakelighting", "-lightmapDoWeld", "-lightmapLocalCompile"],
            },
            Options =
            [
                new() { Id = "resolution", Label = "Resolution", Help = "Lightmap resolution. 1024 - Standard, 2048 - Final, 8192 - Shipping / Final.", Kind = OptionKind.Choice, Choices = ["512", "1024", "2048", "4096", "8192"], Default = "1024", Flags = o => [$"-lightmapMaxResolution {o.Choice()}"] },
                // vrad3 is given the quality's index
                new() { Id = "quality", Label = "Quality", Help = "Lightmap quality.", Kind = OptionKind.Choice, Choices = ["Fast", "Standard", "Final"], Default = "Standard", Flags = o => [$"-lightmapVRadQuality {Array.IndexOf(["Fast", "Standard", "Final"], o.Choice())}"] },
                new() { Id = "noiseRemoval", Label = "Noise removal", Description = "Filters the path tracing noise out of the bake", Help = "Enable/Disable lightmap denoising.", Default = true, GameDefault = g => g is Game.Dota2 or Game.DeskJob ? false : null, Flags = o => o.On() ? [] : ["-lightmapDisableFiltering"] },
                // older games take the setting as a number either way
                new() { Id = "compression", Label = "Compression", Description = "Smaller lightmaps, at a slight cost in quality", Help = "Enable/Disable lightmap compression.", Default = true, Flags = o => o.Game.IsLegacy() ? [$"-lightmapCompressionDisabled {(o.On() ? 0 : 1)}"] : o.On() ? [] : ["-lightmapCompressionDisabled"] },
                new() { Id = "largeBlocks", Label = "Large bake blocks", Description = "Bakes in fewer, bigger blocks, using more VRAM", Help = "Make larger VRAD3 blocks at the cost of higher VRAM usage.", Default = true, Available = g => !g.IsLegacy(), Flags = o => o.On() ? ["-vrad3LargeBlockSize"] : [] },
                // Valve removed baking on the CPU from CS2 in October 2024, and older games only bake on the CPU anyway
                new() { Id = "cpu", Label = "Bake on the CPU", Help = "Bake lightmaps on the CPU, for GPUs without ray tracing. Much slower and only supported in some (older) games.", Default = false, Available = g => !g.IsLegacy() && g != Game.Cs2, Flags = o => o.On() ? ["-lightmapcpu"] : [] },
                new() { Id = "disableLighting", Label = "Disable lighting calculations", Help = "Disable lighting calculations (useful for debugging texel density/chart allocation).", Default = false, Folded = true, Flags = o => o.On() ? ["-disableLightingCalculations"] : [] },
                new() { Id = "deterministicCharts", Label = "Deterministic charting", Help = "Use Deterministic lightmap charts during bake (the lightmap uvs will be the same given same input geometry.", Default = false, Folded = true, Flags = o => o.On() ? ["-lightmapDeterministicCharts"] : [] },
                new() { Id = "debugPathTrace", Label = "Write debug path trace info", Help = "Write debug Path Trace scene info into a file.", Default = false, Folded = true, Flags = o => o.On() ? ["-write_debug_path_trace_scene_info"] : [] },
            ],
        },
        new()
        {
            Name = "Steam Audio",
            Column = GroupColumn.Right,
            Switch = new() { Id = "steamAudio", Help = "Build Steam Audio data.", Default = true },
            Options =
            [
                new() { Id = "reverb", Label = "Reverb", Help = "Build Steam Audio reverb data.", Default = true, GameDefault = g => g == Game.Dota2 ? false : null, Flags = o => o.On() ? ["-sareverb"] : [] },
                new() { Id = "paths", Label = "Paths", Help = "Build Steam Audio pathing data.", Default = true, GameDefault = g => g == Game.Dota2 ? false : null, Flags = o => o.On() ? ["-sapaths"] : [] },
                new() { Id = "customData", Label = "Custom data", Help = "Build Steam Audio custom data (occlusions and materials).", Default = false, Available = g => !g.IsLegacy(), Flags = o => o.On() ? ["-sacustomdata", $"-sacustomdata_threads {o.Number("audioThreads")}"] : [] },
                new() { Id = "strictBakeMode", Label = "Strict Bake Mode", Help = "", Default = false, Available = g => !g.IsLegacy(), Flags = o => o.On() ? ["-sabakestrictmode"] : [] },
                new() { Id = "steamAudioGPU", Label = "GPU Bake Audio", Help = "", Default = false, Available = g => !g.IsLegacy(), Flags = o => o.On() ? ["-steamaudio_gpu"] : [] },
                // reverb and paths share one thread count, which older games don't take
                new() { Id = "audioThreads", Label = "Threads", Help = "CPU threads used for Steam Audio build.", Kind = OptionKind.Threads, Available = g => !g.IsLegacy(), Flags = o => o.On("reverb") || o.On("paths") ? [$"-sareverb_threads {o.Number()}"] : [] },
            ],
        },
        new()
        {
            Name = "Compiler",
            Column = GroupColumn.Bottom,
            IsStage = false,
            FoldLabel = "Advanced",
            Columns = 2,
            Options =
            [
                // goes at the front of the command line, with the map
                new() { Id = "threads", Label = "Threads", Help = "Amount of CPU threads used by the compiler.", Kind = OptionKind.Threads },
                new() { Id = "saveLog", Label = "Save log to console.log", Help = "Save resourcecompiler log to console.log in game/mod.", Default = false, Flags = o => o.On() ? ["-condebug", "-consolelog"] : [] },
                new() { Id = "vconsole", Label = "Print to VConsole", Help = "Print resourcecompiler data to VConsole (Default port 29000)", Default = false, Flags = o => o.On() ? ["-vconsole", "-vconport 29000"] : [] },
                new() { Id = "compileStats", Label = "Print compile stats", Help = "Print VProf stats at the end of compilation.", Default = true, Flags = o => o.On() ? ["-resourcecompiler_log_compile_stats"] : [] },
                new() { Id = "ignoreSchemaMismatches", Label = "Ignore schema mismatches", Help = "Ignore Schema mismatches.", Default = false, Folded = true, Flags = o => o.On() ? ["-danger_mode_ignore_schema_mismatches"] : [] },
            ],
        },
    ];

    // what every preset that builds the map sets the same way
    private static readonly (string, object)[] Build =
    [
        ("world", true), ("settlePhysics", true), ("surfaceEffects", true), ("deformables", false), ("debugVisGeometry", false),
        ("baseTileMeshOnly", false), ("navDebug", false), ("cpu", false), ("largeBlocks", true), ("compression", true),
        ("noiseRemoval", true), ("disableLighting", false), ("deterministicCharts", false), ("debugPathTrace", false), ("customData", false),
    ];

    // Options a preset leaves out keep their value, so the Compiler card is never touched
    public static readonly Preset[] Presets =
    [
        new("Fast", "Only meant for quickly checking things that don't depend on graphics fidelity.", "Build the world and physics, without vis, nav, lighting or audio",
            Values(("entitiesOnly", false), ("physics", true), ("lighting", false), ("visibility", false), ("navigation", true), ("gridNav", true), ("steamAudio", false))),

        new("Full", "Decent for smaller maps, or for checking a bigger map in game or for a quick test.", "Everything, with standard quality lighting",
            Values(("entitiesOnly", false), ("physics", true), ("lighting", true), ("resolution", "2048"), ("quality", "Standard"), ("visibility", true), ("navigation", true), ("gridNav", true), ("steamAudio", true), ("reverb", true), ("paths", true))),

        new("Final", "Best quality lighting, especially important for medium and big sized levels like a CS2 5V5 level.", "Everything, with final quality lighting, for maps you ship",
            Values(("entitiesOnly", false), ("physics", true), ("lighting", true), ("resolution", "8192"), ("quality", "Final"), ("visibility", true), ("navigation", true), ("gridNav", true), ("steamAudio", true), ("reverb", true), ("paths", true))),

        new("Entities only", "Will rewrite the entity lumps in the map with new ones, without touching anything else, works with mesh entities too.", "Only the entities are rebuilt, every other stage is skipped",
            Values(("entitiesOnly", true), ("physics", false), ("lighting", false), ("visibility", false), ("navigation", false), ("gridNav", false), ("steamAudio", false))),
    ];

    // what a map starts with until it has a preset of its own
    public const string NewMapPreset = "Full";

    // picked when the options match none of the presets
    public static readonly Preset Custom = new("Custom", "Your own mix of options. Changing any option picks this, or the preset it matches", "Your own mix of options", new Dictionary<string, object>(), PresetKind.Custom);

    public static Preset Profile(string name, IReadOnlyDictionary<string, object> values, bool locked)
    {
        var description = locked
            ? "Your own profile, locked. Changing an option moves to Custom, unlock the profile to change it"
            : "Your own profile, unlocked. Changing an option while it's picked saves it into the profile";

        return new(name, "Your own profile, which every map can use", description, values, PresetKind.Profile, locked);
    }

    public static IEnumerable<CompileOption> All => Groups.SelectMany(g => g.Switch is { } s ? [s, .. g.Options] : g.Options).Append(EntitiesOnly);

    // Custom or a profile as its file keeps it
    public static Dictionary<string, string> ToText(IReadOnlyDictionary<string, object> values)
    {
        return All.Where(o => values.ContainsKey(o.Id)).ToDictionary(o => o.Id, o => CompileOption.Format(values[o.Id]));
    }

    // Custom or a profile read back from its file, without anything that isn't an option or a value it can have
    public static Dictionary<string, object> FromText(IReadOnlyDictionary<string, string> text)
    {
        return All
            .Select(o => (o.Id, Value: text.TryGetValue(o.Id, out var value) ? o.Parse(value) : null))
            .Where(o => o.Value != null)
            .ToDictionary(o => o.Id, o => o.Value!);
    }

    // The options a game has, at their defaults
    public static Dictionary<string, object> DefaultsFor(Game game)
    {
        return All.Where(o => o.Available(game)).ToDictionary(o => o.Id, o => o.DefaultFor(game)!);
    }

    // The arguments for resourcecompiler, up to the output folder, which goes after -outroot
    public static string BuildArguments(IReadOnlyDictionary<string, object> values, Game game, string? mapPath)
    {
        var o = new OptionValues(values, game);
        var input = Path.GetExtension(mapPath)?.Equals(".txt", StringComparison.OrdinalIgnoreCase) == true ? "-filelist" : "-i";
        List<string> args = [$"-threads {o.Number("threads")}", "-fshallow", "-maxtextureres 256", "-dxlevel 110", "-quiet", "-html", "-unbufferedio", input, $"\"{mapPath}\"", "-noassert"];

        args.AddRange(EntitiesOnly.Flags(o.For(EntitiesOnly)));

        foreach (var group in Groups)
        {
            if (group.Switch is { } toggle)
            {
                args.AddRange(toggle.Flags(o.For(toggle)));

                if (!o.On(toggle.Id))
                {
                    continue;
                }
            }

            foreach (var option in group.Options.Where(option => o.Has(option.Id)))
            {
                args.AddRange(option.Flags(o.For(option)));
            }
        }

        args.AddRange(game.IsLegacy() ? ["-retail", "-breakpad", "-nompi", "-nop4", "-outroot "] : ["-retail", "-breakpad", "-nop4", "-outroot "]);
        return string.Join(" ", args);
    }

    private static Dictionary<string, object> Values(params (string Id, object Value)[] values)
    {
        return Build.Concat(values).ToDictionary(v => v.Item1, v => v.Item2);
    }
}
