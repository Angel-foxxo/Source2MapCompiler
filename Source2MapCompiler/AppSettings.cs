using System.IO;
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
    private const string MapKey = "map";
    private const string ProfilesKey = "profiles";

    /// <summary>Where the settings are kept, under the user's application data folder: %AppData% on Windows, ~/.config on Linux.</summary>
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Source2MapCompiler", FileName);

    // where they were kept while the app was CS2 Map Compiler, which they're copied from the first time
    private static string OldFilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CS2MapCompiler", FileName);

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

    // the map last opened, or null when none has been
    public string? Map { get; set; }

    // the user's profiles by name, in the order they were made, each as option ids and their values written out
    public Dictionary<string, Dictionary<string, string>> Profiles { get; } = [];

    /// <summary>Everything the file holds, so what this version does not know survives a save.</summary>
    private KVObject data = KVObject.Collection();

    /// <summary>The settings as saved, or none when nothing has been saved yet.</summary>
    public static AppSettings Load()
    {
        var settings = new AppSettings();

        if (!File.Exists(FilePath) && File.Exists(OldFilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.Copy(OldFilePath, FilePath);
        }

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

        if (settings.data.TryGetValue(MapKey, out var map) && (string)map is { Length: > 0 } path)
        {
            settings.Map = path;
        }

        if (settings.data.TryGetValue(ProfilesKey, out var profiles))
        {
            MapPresets.ReadPresets(profiles, settings.Profiles);
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

        if (Map != null)
        {
            saved.Add(MapKey, Map);
        }

        if (Profiles.Count > 0)
        {
            saved.Add(ProfilesKey, MapPresets.WritePresets(Profiles));
        }

        foreach (var child in data.Children)
        {
            if (child.Key is not (VersionKey or ThemeKey or AccentKey or MapKey or ProfilesKey))
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

    /// <summary>The version as major, minor and patch, the way it is written and compared, whatever parts it came with.</summary>
    private static Version ThreeParts(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
