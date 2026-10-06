using System.IO;
using System.Linq;
using ValveKeyValue;

namespace Source2MapCompiler;

/// <summary>The look of the app: the system's light or dark, or one of them whatever the system is.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// What the user has set, kept as settings.txt in the user's application data folder in the key values format of the game's own files.
/// The file records the version of the app that saved it, and whatever a newer version put in it is kept through a save by an older one.
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "settings.txt";

    private const string VersionKey = "version";
    private const string ThemeKey = "theme";
    private const string AccentKey = "accent";
    private const string GameKey = "game";
    private const string CustomGamesKey = "customgames";
    private const string GamesKey = "games";

    /// <summary>Where the settings are kept, under the user's application data folder: %AppData% on Windows, ~/.config on Linux.</summary>
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Source2MapCompiler", FileName);

    /// <summary>The version of this app, as three parts, which is what it writes into the file.</summary>
    public static Version AppVersion { get; } = ThreeParts(typeof(AppSettings).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The version of the app that last saved the file, or null when nothing has been saved yet or the file does not say.</summary>
    public Version? SavedBy { get; private set; }

    /// <summary>Whether the file was saved by a newer app than this one, which may have put in it what this one does not know.</summary>
    public bool SavedByNewerApp => SavedBy != null && SavedBy > AppVersion;

    /// <summary>The look of the app, following the system unless set.</summary>
    public AppTheme Theme { get; set; }

    /// <summary>The accent colour set over the theme's own, as #RRGGBB, or null for the theme's.</summary>
    public string? Accent { get; set; }

    // the folder of the game picked last, or null when none has been
    public string? GameFolder { get; set; }

    // the folders of the games picked with Custom path, which Steam doesn't know of
    public List<string> CustomGames { get; } = [];

    // what's remembered for each game, by its name
    private readonly Dictionary<string, GameSettings> games = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything the file holds, so what this version does not know survives a save.</summary>
    private KVObject data = KVObject.Collection();

    /// <summary>The settings as saved, or none when nothing has been saved yet.</summary>
    public static AppSettings Load()
    {
        var settings = new AppSettings();

        if (!File.Exists(FilePath))
        {
            return settings;
        }

        using var stream = File.OpenRead(FilePath);
        settings.data = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream, KVSerializerOptions.DefaultOptions).Root;

        if (settings.data.TryGetValue(VersionKey, out var version) && Version.TryParse((string)version, out var parsed))
        {
            settings.SavedBy = ThreeParts(parsed);
        }

        if (settings.data.TryGetValue(ThemeKey, out var theme) && Enum.TryParse((string)theme, ignoreCase: true, out AppTheme parsedTheme))
        {
            settings.Theme = parsedTheme;
        }

        if (settings.data.TryGetValue(AccentKey, out var accent) && (string)accent is { Length: > 0 } hex)
        {
            settings.Accent = hex;
        }

        if (settings.data.TryGetValue(GameKey, out var game) && (string)game is { Length: > 0 } folder)
        {
            settings.GameFolder = folder;
        }

        if (settings.data.TryGetValue(CustomGamesKey, out var custom) && custom.IsCollection)
        {
            settings.CustomGames.AddRange(custom.Children.Where(c => !c.Value.IsCollection).Select(c => (string)c.Value).Where(f => f.Length > 0));
        }

        if (settings.data.TryGetValue(GamesKey, out var games) && games.IsCollection)
        {
            foreach (var (name, block) in games.Children.Where(g => g.Value.IsCollection))
            {
                settings.games[name] = GameSettings.Read(block);
            }
        }

        return settings;
    }

    public void Save()
    {
        // the version and the settings first, then whatever else was in the file as it was
        var saved = KVObject.Collection();
        saved.Add(VersionKey, AppVersion.ToString());
        saved.Add(ThemeKey, Theme.ToString().ToLowerInvariant());

        if (Accent != null)
        {
            saved.Add(AccentKey, Accent);
        }

        if (GameFolder != null)
        {
            saved.Add(GameKey, GameFolder);
        }

        if (CustomGames.Count > 0)
        {
            var custom = KVObject.Collection();

            foreach (var (folder, index) in CustomGames.Select((f, i) => (f, i)))
            {
                custom.Add((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), folder);
            }

            saved.Add(CustomGamesKey, custom);
        }

        var games = KVObject.Collection();

        foreach (var (name, game) in this.games.Where(g => !g.Value.IsEmpty))
        {
            games.Add(name, game.Write());
        }

        saved.Add(GamesKey, games);

        foreach (var child in data.Children)
        {
            if (child.Key is not (VersionKey or ThemeKey or AccentKey or GameKey or CustomGamesKey or GamesKey))
            {
                saved.Add(child.Key, child.Value);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        using (var stream = File.Create(FilePath))
        {
            KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Serialize(stream, saved, "settings");
        }

        data = saved;
        SavedBy = AppVersion;
    }

    // What's remembered for a game, made empty the first time it's asked for
    internal GameSettings For(Game game)
    {
        var name = game.ToString().ToLowerInvariant();

        if (!games.TryGetValue(name, out var settings))
        {
            games[name] = settings = new GameSettings();
        }

        return settings;
    }

    /// <summary>The version as major, minor and patch, the way it is written and compared, whatever parts it came with.</summary>
    private static Version ThreeParts(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}

// What's remembered for one game: the map opened last, the user's profiles, and the preset and options picked while no map
// is open. A game's maps and options are its own, so switching games brings back where that game was left. Whatever a
// newer version keeps for a game is kept through a save by an older one
internal sealed class GameSettings
{
    private const string MapKey = "map";
    private const string ProfilesKey = "profiles";
    private const string OptionsKey = "options";

    private KVObject data = KVObject.Collection();

    // the map last opened, or null when none has been or it was closed
    public string? Map { get; set; }

    // the user's profiles by name, in the order they were made, each as option ids and their values written out
    public Dictionary<string, Dictionary<string, string>> Profiles { get; } = [];

    // the preset and options picked while no map is open, which a map keeps in its own file instead
    public MapPresets? Options { get; set; }

    public bool IsEmpty => Map == null && Profiles.Count == 0 && Options == null && !data.Children.Any();

    public static GameSettings Read(KVObject block)
    {
        var settings = new GameSettings { data = block };

        if (block.TryGetValue(MapKey, out var map) && (string)map is { Length: > 0 } path)
        {
            settings.Map = path;
        }

        if (block.TryGetValue(ProfilesKey, out var profiles))
        {
            MapPresets.ReadPresets(profiles, settings.Profiles);
        }

        if (block.TryGetValue(OptionsKey, out var options) && options.IsCollection)
        {
            settings.Options = MapPresets.Read(options);
        }

        return settings;
    }

    public KVObject Write()
    {
        var saved = KVObject.Collection();

        if (Map != null)
        {
            saved.Add(MapKey, Map);
        }

        if (Profiles.Count > 0)
        {
            saved.Add(ProfilesKey, MapPresets.WritePresets(Profiles));
        }

        if (Options != null)
        {
            saved.Add(OptionsKey, Options.Write());
        }

        foreach (var child in data.Children.Where(c => c.Key is not (MapKey or ProfilesKey or OptionsKey)))
        {
            saved.Add(child.Key, child.Value);
        }

        data = saved;
        return saved;
    }
}
