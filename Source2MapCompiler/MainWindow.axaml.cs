using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ValveKeyValue;
using Wacton.Unicolour;

namespace Source2MapCompiler;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "the compile process is disposed as soon as it exits, and the lightmap preview when the window closes")]
public partial class MainWindow : Window
{
    private string? cs2dir;
    private string? resourcecompiler;
    private string? mapname;
    private string? mappath;
    private string? outputpath;
    private string? arg;
    private Process? process;

    /// <summary>The compile running, from its start to the end of its output.</summary>
    private Task? compileTask;

    /// <summary>Whether the compile running was cancelled, so its end is not reported as completed.</summary>
    private bool cancelled;

    /// <summary>The compile log, and the lines printed since it was last shown, which arrive from the compiler's threads.</summary>
    private readonly ConcurrentQueue<LogLine> pendingLines = new();

    // the coloured runs of the log, by where they are in it
    private readonly List<LogSpan> logSpans = [];

    // whether nothing has been logged yet, so the next line needs no line break before it
    private bool logEmpty = true;

    // whether the log keeps its newest line in view, until it's scrolled away from the bottom, and again once it's scrolled back
    private bool followLog = true;

    private LightmapPreviewController? lightmapPreview;

    private ResourceMonitor? resourceMonitor;

    // how far the compile running has got, and the line it's still printing, which comes in on the output's thread
    private CompileProgress? progress;
    private Stopwatch progressClock = new();
    private string unfinishedLine = "";

    // the reader ends the line it was printing and starts the next at once, so the log takes both at once too
    private readonly System.Threading.Lock logLock = new();

    // where the line still being printed starts in the log, and its coloured runs, which go when the log next changes. -1
    // when there's none shown
    private int liveStart = -1;
    private int liveSpans;
    private bool emptyBeforeLive;
    private string shownLive = "";

    private ResourceGraph? cpuHistory, memoryHistory, gpuHistory;

    public MainWindow()
    {
        InitializeComponent();

        logEditor.TextArea.TextView.LineTransformers.Add(new LogColorizer(logSpans, span => span.Kind switch
        {
            LogKind.Error => Brush("ErrorTextBrush"),
            LogKind.App => Brush("HeadingBrush"),
            _ => CompilerBrush(span.Color),
        }));
        ActualThemeVariantChanged += (_, _) => logEditor.TextArea.TextView.Redraw();
        logEditor.TextArea.SelectionBrush = Brush("AccentSoftBrush");
        logEditor.TextArea.SelectionForeground = null;
        // it's read only, so there's nothing to type at
        logEditor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        // an editor lets its text scroll up past its end, a log stops at its last line
        logEditor.Options.AllowScrollBelowDocument = false;
        // nothing's undone in a log, and the line being printed is replaced many times a second
        logEditor.Document.UndoStack.SizeLimit = 0;
        logEditor.Loaded += (_, _) =>
        {
            if (logEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
            {
                viewer.ScrollChanged += OnLogScrolled;
            }
        };

        // the compiler prints faster than lines can be shown one by one, so what it printed is shown a few times a second
        new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => FlushLog()).Start();

        // the profiles are the game's own, so they come with it
        SetPresets([CompileOptions.EntitiesOnly, CompileOptions.Custom], []);
        presetList.SelectionChanged += OnPresetChanged;
        BuildOptions();
        HelpSystemEventReg();

        Loaded += Form1_Load;
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
        {
            // the colours Windows' Resource Monitor draws them in
            cpuHistory = new ResourceGraph(cpuGraph, "#D04545");
            memoryHistory = new ResourceGraph(memoryGraph, "#B8901A");
            gpuHistory = new ResourceGraph(gpuGraph, "#1C8EA0");
            resourceMonitor = new ResourceMonitor(usage => Dispatcher.UIThread.Post(() => ShowUsage(usage)));
        }

        Closed += (_, _) =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
            {
                lightmapPreview?.Dispose();
                resourceMonitor?.Dispose();
            }
        };
    }

    private void ShowUsage(ResourceUsage usage)
    {
        var memory = usage.RamGigabytes / usage.RamTotalGigabytes * 100;
        var vram = usage.VramTotalGigabytes is { } total ? $"{usage.VramGigabytes:0.0} / {total:0.0} GB" : $"{usage.VramGigabytes:0.0} GB";

        resourceGraphs.IsVisible = true;
        gpuCard.IsVisible = usage.Gpu != null;
        cpuTitle.Text = $"CPU – {usage.Cpu:0.0}%";
        memoryTitle.Text = $"Memory – {memory:0.0}% ({usage.RamGigabytes:0.0} / {usage.RamTotalGigabytes:0} GB)";
        gpuTitle.Text = $"GPU – {usage.Gpu:0.0}% ({vram})";
        cpuHistory?.Add(usage.Cpu);
        gpuHistory?.Add(usage.Gpu ?? 0);
        memoryHistory?.Add(memory);
    }

    // Lists the games installed through Steam and those picked with Custom path before, and picks the one picked last, or else
    // the first, the one preferred. Picking it brings back where it was left
    private async void Form1_Load(object? sender, RoutedEventArgs e)
    {
        var settings = LoadSettings();

        foreach (var installed in Games.Installed())
        {
            AddGame(installed);
        }

        foreach (var folder in settings?.CustomGames ?? [])
        {
            if (Listed(folder) == null && Games.Find(folder) is { } info)
            {
                AddGame(new InstalledGame(info, folder));
            }
        }

        if (gameList.ItemCount > 0)
        {
            gameList.SelectedItem = (settings?.GameFolder is { } last ? Listed(last) : null) ?? gameList.Items[0];
        }
        else
        {
            await CS2Validator();
        }
    }

    // The game in the dropdown whose folder this is, or null when it isn't there
    private ComboBoxItem? Listed(string folder)
    {
        return gameList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals((string?)item.Tag, folder, StringComparison.OrdinalIgnoreCase));
    }

    // Adds a game to the dropdown, with its folder as the tooltip, since two installs of a game share its name
    private ComboBoxItem AddGame(InstalledGame installed)
    {
        var item = new ComboBoxItem { Content = installed.Info.Name, Tag = installed.Folder };
        ToolTip.SetTip(item, installed.Folder);
        gameList.Items.Add(item);
        return item;
    }

    private async void OnGameChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (gameList.SelectedItem is ComboBoxItem { Tag: string folder })
        {
            cs2dir = folder;
            ToolTip.SetTip(gameList, cs2dir);
            SaveSettings(settings => settings.GameFolder = folder);
            await CS2Validator();
            RestoreMap();
            UpdateArgLabel();
        }
    }

    private async Task CS2Validator()
    {
        if (cs2dir == null || Games.Find(cs2dir) is not { } found)
        {
            SetStatus(wststatusPill, wststatus, "Not Found", found: false);
            button1.IsEnabled = false;
            await MessageDialog.ShowAsync(this, MessageKind.Danger, "Source2 Map Compiler", "No Source 2 game found! Please install one through Steam or set the path manually with Custom Path!");
            return;
        }

        game = found.Game;
        SetPresets(Own, GameProfiles());
        BuildOptions();

        if (File.Exists(Path.Combine(cs2dir, "resourcecompiler.exe")))
        {
            SetStatus(wststatusPill, wststatus, "Found", found: true);
            resourcecompiler = Path.Combine(cs2dir, "resourcecompiler.exe");
            button1.IsEnabled = true;
        }
        else
        {
            SetStatus(wststatusPill, wststatus, "Not Found", found: false);
            button1.IsEnabled = false;
            await MessageDialog.ShowAsync(this, MessageKind.Warning, "Source2 Map Compiler", "Please Install Workshop Tools!");
        }
    }

    /// <summary>Shows whether the game or its tools were found, in the pill's colour as well as its text.</summary>
    private static void SetStatus(Border pill, TextBlock label, string text, bool found)
    {
        label.Text = text;
        pill.Classes.Set("found", found);
        pill.Classes.Set("missing", !found);
    }

    private static bool IsTextFile(string? path)
    {
        return Path.GetExtension(path)?.Equals(".txt", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool IsCompiling()
    {
        return compileTask is { IsCompleted: false };
    }

    private string ArgumentBuilder()
    {
        return CompileOptions.BuildArguments(values, game, mappath);
    }

    private void UpdateArgLabel()
    {
        string myarg = ArgumentBuilder();
        cmdLine.Text = myarg;
    }

    private async void button1_Click(object? sender, RoutedEventArgs e)
    {
        if (IsCompiling())
        {
            await MessageDialog.ShowAsync(this, MessageKind.Warning, "Source2 Map Compiler", "A compilation is already in progress. Please wait for it to complete or cancel it first.");
            return;
        }
        if (string.IsNullOrEmpty(outputpath))
        {
            await MessageDialog.ShowAsync(this, MessageKind.Danger, "Source2 Map Compiler", "No .vmap is specified.");
            return;
        }
        button1.IsEnabled = false;
        arg = ArgumentBuilder() + string.Format(null, "\"{0}\"", outputpath);
        compileTask = ProcessThread();
    }

    /// <summary>
    /// Runs resourcecompiler with the arguments built, showing everything it prints in the log, until it exits or is cancelled.
    /// </summary>
    private async Task ProcessThread()
    {
        cancelled = false;
        statusLabel.Text = "Compiling";

        var stopwatch = Stopwatch.StartNew();
        progress = new CompileProgress(new OptionValues(values, game), Vrad3Folder());
        progressClock = stopwatch;
        compileBar.Value = 0;
        compileBar.IsVisible = true;
        int exitCode;

        process = new Process();

        try
        {
            process.StartInfo.FileName = resourcecompiler;
            process.StartInfo.Arguments = arg;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            //* Start process

            process.Start();
            Log("(Source2MapCompiler) Compile started with parameters:\n " + resourcecompiler + " " + arg + "\nTime: " + DateTime.Now + "\n", LogKind.App);

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    CompilerJob.Add(process);
                }
                catch (Win32Exception exception)
                {
                    Log("(Source2MapCompiler) resourcecompiler will keep running if the app closes during the compile: " + exception.Message + "\n", LogKind.Error);
                }
            }

            if (OperatingSystem.IsWindows() && BakesLightmapsOnGpu() && Vrad3Folder() is { } vrad3Folder)
            {
                if (lightmapPreview == null)
                {
                    lightmapPreview = new LightmapPreviewController(this, message => Log("(Source2MapCompiler) " + message + "\n", LogKind.App));
                    lightmapPreview.Available += (_, _) => lightmapPreviewButton.IsEnabled = true;
                }

                lightmapPreview.Start(process.Id, vrad3Folder);
            }

            await Task.WhenAll(ReadCompilerOutput(process.StandardOutput, true), ReadCompilerOutput(process.StandardError, false), process.WaitForExitAsync());
            exitCode = process.ExitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Log("(Source2MapCompiler) Could not start resourcecompiler: " + exception.Message + "\n", LogKind.Error);
            statusLabel.Text = "Compile failed to start";
            return;
        }
        finally
        {
            process.Dispose();
            process = null;
            button1.IsEnabled = true;
            progress = null;
            compileBar.IsVisible = false;

            if (OperatingSystem.IsWindows())
            {
                lightmapPreview?.Stop();
            }
        }

        if (cancelled)
        {
            statusLabel.Text = "Compile cancelled";
            return;
        }

        Log("(Source2MapCompiler) Compile completed! - " + DateTime.Now + (exitCode == 0 ? "" : $" (exit code {exitCode})") + "\n", LogKind.App);
        statusLabel.Text = (exitCode == 0 ? "Compile completed" : $"Compile exited with code {exitCode}") + $" in {stopwatch.Elapsed:hh\\:mm\\:ss}";
    }

    // The lightmap preview only works with GPU bakes
    private bool BakesLightmapsOnGpu()
    {
        var options = new OptionValues(values, game);
        return options.On("lighting") && !options.On("cpu") && !game.IsLegacy();
    }

    // For a map in content\<addons>\<addon> this is game\<addons>\<addon>\_vrad3, and null for maps outside an addon
    private string? Vrad3Folder()
    {
        if (cs2dir == null || mappath == null)
        {
            return null;
        }

        // content\csgo_addons\<addon>\maps\...\<map>.vmap
        for (var folder = Directory.GetParent(mappath); folder?.Parent?.Parent != null; folder = folder.Parent)
        {
            if (folder.Parent.Name.EndsWith("_addons", StringComparison.OrdinalIgnoreCase) && folder.Parent.Parent.Name.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                // cs2dir is game\bin\win64
                var game = Directory.GetParent(cs2dir)!.Parent!.FullName;
                return Path.Combine(game, folder.Parent.Name, folder.Name, "_vrad3");
            }
        }

        return null;
    }

    // With -html resourcecompiler ends its lines with <br/> instead of newlines, so its output is read as it comes and split
    // there. That happens off the UI thread, which picks the lines up from the queue. The line still being printed is shown
    // too, as the dots of a step's bar come in long before its line ends
    private async Task ReadCompilerOutput(StreamReader reader, bool printing)
    {
        var buffer = new char[4096];
        var unfinished = "";
        int read;

        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var lines = (unfinished + new string(buffer, 0, read)).Split(["<br/>", "\r\n", "\n"], StringSplitOptions.None);
            unfinished = lines[^1];

            lock (logLock)
            {
                foreach (var line in lines[..^1])
                {
                    pendingLines.Enqueue(LogLine.FromHtml(line));
                }

                if (printing)
                {
                    unfinishedLine = unfinished;
                }
            }
        }

        lock (logLock)
        {
            if (unfinished.Length > 0)
            {
                pendingLines.Enqueue(LogLine.FromHtml(unfinished));
            }

            if (printing)
            {
                unfinishedLine = "";
            }
        }
    }

    private void button2_Click(object? sender, RoutedEventArgs e)
    {
        if (IsCompiling())
        {
            cancelled = true;

            try
            {
                // the compiler's own helpers go with it
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                // it exited on its own in the meantime
            }

            Log("(Source2MapCompiler) Compile cancelled! - " + DateTime.Now + "\n", LogKind.Error);
        }
        else
        {
            Log("(Source2MapCompiler) Compile already exited! - " + DateTime.Now + "\n", LogKind.App);
        }
    }

    private async void button3_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the game executable",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Executable Files") { Patterns = [.. Games.Executables] }],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } file && Path.GetDirectoryName(file) is { } folder && Games.Find(folder) is { } info)
        {
            // a game outside Steam's libraries joins the dropdown, and is listed again next time
            if (Listed(folder) is not { } item)
            {
                item = AddGame(new InstalledGame(info, folder));
                SaveSettings(settings =>
                {
                    if (!settings.CustomGames.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        settings.CustomGames.Add(folder);
                    }
                });
            }

            gameList.SelectedItem = item;
        }
    }

    private async void button4_Click(object? sender, RoutedEventArgs e)
    {
        if (cs2dir == null)
        {
            await MessageDialog.ShowAsync(this, MessageKind.Warning, "Source2 Map Compiler", "Set the game path with Custom Path first.");
            return;
        }

        string[] addonDirectories = {
            "csgo_addons",
            "hlvr_addons",
            "citadel_addons",
            "dota_addons",
            "testbed_addons",
            "steamtours_addons"
        };

        // the folder of the map that's open, or else the game's addons
        IStorageFolder? initialDirectory = Path.GetDirectoryName(mappath) is { } mapFolder && Directory.Exists(mapFolder)
            ? await StorageProvider.TryGetFolderFromPathAsync(mapFolder)
            : null;

        if (initialDirectory == null)
        {
            foreach (string addonDir in addonDirectories)
            {
                string path = Path.Combine(Directory.GetParent(cs2dir)!.Parent!.Parent!.FullName, "content", addonDir);
                if (Directory.Exists(path))
                {
                    initialDirectory = await StorageProvider.TryGetFolderFromPathAsync(path);
                    break;
                }
            }
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open .vmap",
            AllowMultiple = false,
            SuggestedStartLocation = initialDirectory,
            FileTypeFilter =
            [
                new FilePickerFileType("Hammer Map File") { Patterns = ["*.vmap"] },
                new FilePickerFileType("Map List") { Patterns = ["*.txt"] },
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } file)
        {
            SetMap(file);
            SaveSettings(settings => settings.For(game).Map = file);
        }
    }

    // The map's path below its content folder, so content\csgo_addons\x\maps\x.vmap gives csgo_addons\x\maps\x.vmap. Null for
    // a map list, which builds several, or a map outside a content folder
    private string? MapBelowContent()
    {
        if (mappath == null || IsTextFile(mappath))
        {
            return null;
        }

        for (var folder = Directory.GetParent(mappath); folder != null; folder = folder.Parent)
        {
            if (folder.Name.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(folder.FullName, mappath);
            }
        }

        return null;
    }

    // resourcecompiler puts what it builds under the output root at the map's path below the content folder, so
    // content\csgo_addons\x\maps\x.vmap becomes <output>\csgo_addons\x\maps\x.vpk
    private string? CompiledMapPath()
    {
        return outputpath != null && MapBelowContent() is { } map ? Path.Combine(outputpath, Path.ChangeExtension(map, ".vpk")) : null;
    }

    // The output root that puts the map in the folder picked, which is that folder without the map's folders below content
    // on its end, so picking game\csgo_addons\x\maps gives game. A folder that doesn't end in them is taken as the root
    private string OutputRoot(string picked)
    {
        if (MapBelowContent() is not { } map || Path.GetDirectoryName(map) is not { Length: > 0 } below)
        {
            return picked;
        }

        var root = new DirectoryInfo(picked);

        foreach (var part in below.Split(Path.DirectorySeparatorChar).Reverse())
        {
            if (root == null || !root.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
            {
                return picked;
            }

            root = root.Parent;
        }

        return root?.FullName ?? picked;
    }

    // Where the map will be written, or the output root when that can't be known
    private void ShowOutput()
    {
        ShowPath(outputdir, CompiledMapPath() ?? outputpath ?? "N/A");
    }

    // A path shortened in the middle to fit its line, so the whole of it is in its tooltip
    private static void ShowPath(SelectableTextBlock label, string path)
    {
        label.Text = path;
        ToolTip.SetTip(label, path);
    }

    private void SetMap(string file)
    {
        mappath = file;
        mapname = Path.GetFileName(file);
        outputpath = Directory.GetParent(cs2dir!)!.Parent!.FullName;
        ShowPath(mapLabel, mappath);
        ShowOutput();
        Title = $"Source2 Map Compiler - {Path.GetFileNameWithoutExtension(file)}";
        button5.IsEnabled = true;
        UseMapPresets(Remembered(() => MapPresets.Load(file)));
        UpdateArgLabel();
    }

    // The game's map from last time, unless its file is gone. Without one no map is open, and the options are those the game
    // was left with while none was
    private void RestoreMap()
    {
        if (cs2dir == null)
        {
            return;
        }

        var remembered = LoadSettings()?.For(game);

        if (remembered?.Map is { } file && File.Exists(file))
        {
            SetMap(file);
            return;
        }

        mappath = mapname = outputpath = null;
        mapLabel.Text = "N/A";
        ToolTip.SetTip(mapLabel, null);
        ShowOutput();
        Title = "Source2 Map Compiler";
        button5.IsEnabled = false;
        UseMapPresets(remembered?.Options);
        UpdateArgLabel();
    }

    // Remembering the map and its presets is a convenience, so a file that can't be read or written doesn't stop anything, it
    // just isn't remembered
    private static T? Remembered<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyValueException)
        {
            return null;
        }
    }

    private static void Remember(Action write)
    {
        try
        {
            write();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyValueException)
        {
        }
    }

    private static AppSettings? LoadSettings()
    {
        return Remembered(AppSettings.Load);
    }

    private static void SaveSettings(Action<AppSettings> change)
    {
        Remember(() =>
        {
            var settings = AppSettings.Load();
            change(settings);
            settings.Save();
        });
    }

    private async void button5_Click(object? sender, RoutedEventArgs e)
    {
        // the picker starts where the map goes now, or the nearest folder to it that's there yet
        var current = CompiledMapPath() is { } compiled ? Path.GetDirectoryName(compiled) : outputpath;
        var start = current == null ? null : new DirectoryInfo(current);

        while (start is { Exists: false })
        {
            start = start.Parent;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Change Output",
            AllowMultiple = false,
            SuggestedStartLocation = start == null ? null : await StorageProvider.TryGetFolderFromPathAsync(start.FullName),
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
        {
            outputpath = OutputRoot(folder);
            ShowOutput();
        }
    }

    // The game the tools belong to, CS2 until another is found. It decides which options there are
    private Game game = Game.Cs2;

    // every option the game has, by id. The controls, the presets and the command line all read and write this
    private Dictionary<string, object> values = [];

    // the control showing each option
    private readonly Dictionary<string, Control> optionControls = [];

    // each group's card, and the panel of its options that its switch greys out
    private readonly List<(OptionGroup Group, Border Card, Panel? Options)> cards = [];

    // the built in presets, the map's own Entities only and Custom, then the user's profiles
    private Preset[] presets = [];

    // kept with a profile's options in the settings, which no option has as its id
    private const string ProfileLockedKey = "locked";

    // the name of the preset picked, which is picked again when the options are rebuilt for another game. Until a map is
    // opened it's the one new maps get
    private string? pickedPreset = CompileOptions.NewMapPreset;

    // set while a preset is applied, so its own changes don't pick a preset
    private bool applyingPreset;

    // set while the preset list is moved to the preset the options match, so that doesn't apply it
    private bool selectingPreset;

    // set while a control is moved to its option's value, so that isn't taken as a change
    private bool showingValues;

    // Builds the cards for the game's options, at their defaults
    private void BuildOptions()
    {
        values = CompileOptions.DefaultsFor(game);
        optionControls.Clear();
        cards.Clear();
        leftColumn.Children.Clear();
        rightColumn.Children.Clear();
        bottomColumn.Children.Clear();

        foreach (var group in CompileOptions.Groups)
        {
            var column = group.Column switch
            {
                GroupColumn.Left => leftColumn,
                GroupColumn.Right => rightColumn,
                _ => bottomColumn,
            };

            column.Children.Add(BuildCard(group));
        }

        PickAgain(pickedPreset);
        UpdateArgLabel();
    }

    // Entities only and Custom with the options they were left with for the map
    private static Preset[] MapOwn(MapPresets? stored)
    {
        return [.. new[] { CompileOptions.EntitiesOnly, CompileOptions.Custom }.Select(preset => stored?.Presets.GetValueOrDefault(preset.Name) is { } options ? preset with { Values = CompileOptions.FromText(options) } : preset)];
    }

    private Preset? SelectedPreset => presetList.SelectedIndex >= 0 ? presets[presetList.SelectedIndex] : null;

    // the map's own presets, Entities only and Custom
    private Preset[] Own => [.. presets.Where(p => p.Kind is PresetKind.EntitiesOnly or PresetKind.Custom)];

    private IEnumerable<Preset> Profiles => presets.Where(p => p.Kind == PresetKind.Profile);

    private void SetPresets(IEnumerable<Preset> own, IEnumerable<Preset> profiles)
    {
        presets = [.. CompileOptions.Presets, .. own, .. profiles];
        selectingPreset = true;
        presetList.Items.Clear();

        foreach (var preset in presets)
        {
            var item = new ListBoxItem { Content = PresetContent(preset) };
            ToolTip.SetTip(item, preset.Help);
            presetList.Items.Add(item);
        }

        selectingPreset = false;
    }

    // A preset's name, under its icon when it has one, as the toolbar's buttons have theirs
    private static Control PresetContent(Preset preset)
    {
        var name = new TextBlock { Text = preset.Name, HorizontalAlignment = HorizontalAlignment.Center };

        if (preset.Icon == null)
        {
            return name;
        }

        var icon = new Image { Source = PresetIcon(preset.Icon), Width = 28, Height = 28 };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);
        return new StackPanel { Spacing = 3, Children = { icon, name } };
    }

    // each preset icon, loaded once, as the preset list is rebuilt whenever a map is opened
    private static readonly Dictionary<string, Bitmap> presetIcons = [];

    private static Bitmap PresetIcon(string icon)
    {
        if (!presetIcons.TryGetValue(icon, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Source2MapCompiler/assets/buttons/preset_{icon}.png"));
            presetIcons[icon] = bitmap = new Bitmap(stream);
        }

        return bitmap;
    }

    // Takes the map's presets and picks the one it was left with. A map with none yet starts over from the defaults, with the
    // preset new maps get, rather than with what the map before it was left with
    private void UseMapPresets(MapPresets? stored)
    {
        SetPresets(MapOwn(stored), [.. Profiles]);

        if (stored == null)
        {
            foreach (var (id, value) in CompileOptions.DefaultsFor(game))
            {
                SetValue(id, value);
            }
        }

        PickAgain(stored == null ? CompileOptions.NewMapPreset : stored.Preset);
    }

    // Picks a preset again, after the options are rebuilt for another game or another map is opened. A profile or Entities
    // only stays picked, anything else moves to the preset the options match, as it would have when they were set
    private void PickAgain(string? name)
    {
        var picked = presets.FirstOrDefault(p => p.Name == name);

        if (picked != null)
        {
            pickedPreset = name;
            ApplyPreset(picked);
        }

        if (picked is { Kind: PresetKind.Profile or PresetKind.EntitiesOnly })
        {
            ShowPicked(picked);
        }
        else
        {
            SelectMatchingPreset();
        }
    }

    // Moves the list to a preset without applying it
    private void ShowPicked(Preset preset)
    {
        selectingPreset = true;
        presetList.SelectedIndex = Array.IndexOf(presets, preset);
        selectingPreset = false;
        UpdateStages();
    }

    private async void OnNewProfile(object? sender, RoutedEventArgs e)
    {
        if (await AskProfileName("New profile", "Name the profile. It starts with the options as they are now, and every map can use it.", "Create", "Profile name") is not { } name)
        {
            return;
        }

        // a profile can't be Entities only, so one made from it builds what it had turned on
        SetValue(CompileOptions.EntitiesOnlyId, false);
        var profile = CompileOptions.Profile(name, new Dictionary<string, object>(values), locked: true);
        SetPresets(Own, [.. Profiles, profile]);
        ShowPicked(profile);
        UpdateArgLabel();
        SaveProfiles();
        RememberPreset();
    }

    private async void OnRenameProfile(object? sender, RoutedEventArgs e)
    {
        if (SelectedPreset is not { Kind: PresetKind.Profile } profile || await AskProfileName("Rename profile", $"Rename the profile {profile.Name} to", "Rename", profile.Name) is not { } name)
        {
            return;
        }

        var renamed = profile with { Name = name };
        SetPresets(Own, Profiles.Select(p => p == profile ? renamed : p));
        ShowPicked(renamed);
        SaveProfiles();
        RememberPreset();
    }

    private void OnLockProfile(object? sender, RoutedEventArgs e)
    {
        if (SelectedPreset is not { Kind: PresetKind.Profile } profile)
        {
            return;
        }

        var changed = CompileOptions.Profile(profile.Name, profile.Values, lockProfileBox.IsChecked == true);
        SetPresets(Own, Profiles.Select(p => p == profile ? changed : p));
        ShowPicked(changed);
        SaveProfiles();
    }

    private async void OnDeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (SelectedPreset is not { Kind: PresetKind.Profile } profile || !await MessageDialog.AskAsync(this, MessageKind.Danger, "Delete profile", $"Delete the profile {profile.Name}? Every map shares it, and this can't be undone.", "Delete"))
        {
            return;
        }

        SetPresets(Own, Profiles.Where(p => p != profile));
        SaveProfiles();
        SelectMatchingPreset();
        UpdateStages();
        RememberPreset();
    }

    // A profile's name, which can't be empty or be any preset's already, or null when none was given
    private async Task<string?> AskProfileName(string title, string message, string confirm, string placeholder)
    {
        var name = (await MessageDialog.AskTextAsync(this, MessageKind.Info, title, message, confirm, placeholder))?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            await MessageDialog.ShowAsync(this, MessageKind.Warning, title, $"There's already a preset called {name}.");
            return null;
        }

        return name;
    }

    // The profiles are kept in the settings for the game, in the order they were made, for every map of it to use
    private void SaveProfiles()
    {
        SaveSettings(settings =>
        {
            var profiles = settings.For(game).Profiles;
            profiles.Clear();

            foreach (var profile in Profiles)
            {
                var text = CompileOptions.ToText(profile.Values);
                text[ProfileLockedKey] = profile.Locked ? "1" : "0";
                profiles[profile.Name] = text;
            }
        });
    }

    // The game's profiles as they're kept
    private IEnumerable<Preset> GameProfiles()
    {
        return LoadSettings()?.For(game).Profiles.Select(p => CompileOptions.Profile(p.Key, CompileOptions.FromText(p.Value), p.Value.GetValueOrDefault(ProfileLockedKey) != "0")) ?? [];
    }

    private Border BuildCard(OptionGroup group)
    {
        var content = new StackPanel();
        var heading = new TextBlock { Classes = { "heading" }, Text = group.Name };

        if (group.Switch is { } toggle)
        {
            var header = new DockPanel { Classes = { "stage" } };
            var control = Toggle(toggle, new ToggleSwitch());
            DockPanel.SetDock(control, Dock.Right);
            header.Children.Add(control);
            header.Children.Add(heading);
            content.Children.Add(header);
        }
        else
        {
            content.Children.Add(heading);
        }

        foreach (var note in new[] { group.Note, group.GameNote(game) })
        {
            if (note != null)
            {
                content.Children.Add(new TextBlock { Classes = { "note" }, Text = note });
            }
        }

        var available = group.Options.Where(o => o.Available(game)).ToArray();
        Panel? options = null;

        if (available.Length > 0)
        {
            options = new StackPanel();
            options.Children.Add(Layout(available.Where(o => !o.Folded).ToArray(), group.Columns));

            if (available.Where(o => o.Folded).ToArray() is { Length: > 0 } folded)
            {
                options.Children.Add(new Expander { Classes = { "debug" }, Header = group.FoldLabel, Content = Layout(folded, 1) });
            }

            content.Children.Add(options);
        }

        var card = new Border { Classes = { "card" }, Child = content };
        cards.Add((group, card, options));
        return card;
    }

    // Lays options out one under another, or across a few columns
    private Panel Layout(CompileOption[] options, int columns)
    {
        if (columns == 1)
        {
            var stack = new StackPanel();

            for (var i = 0; i < options.Length; i++)
            {
                stack.Children.Add(Row(options[i], i == 0 ? null : options[i - 1]));
            }

            return stack;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))) };

        for (var i = 0; i < options.Length; i++)
        {
            if (i % columns == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var row = Row(options[i], null);
            Grid.SetRow(row, i / columns);
            Grid.SetColumn(row, i % columns);
            grid.Children.Add(row);
        }

        return grid;
    }

    // The control for an option, with its label, and its help as a tooltip
    private Control Row(CompileOption option, CompileOption? previous)
    {
        Control row;

        switch (option.Kind)
        {
            case OptionKind.Choice:
                var list = new ListBox { Name = option.Id, Classes = { "segmented" }, ItemsSource = option.Choices, SelectedItem = values[option.Id] };
                list.SelectionChanged += (_, _) =>
                {
                    if (list.SelectedItem is string item)
                    {
                        OnOptionChanged(option, item);
                    }
                };
                optionControls[option.Id] = list;
                row = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Classes = { "label" }, Text = option.Label, Margin = new Thickness(0, previous == null ? 6 : 10, 0, 6) },
                        list,
                    },
                };
                break;

            case OptionKind.Threads:
                var number = new NumericUpDown { Name = option.Id, Classes = { "threads" }, Width = 120, Maximum = Environment.ProcessorCount, Value = (int)values[option.Id] };
                // a box whose text was cleared means every thread
                number.ValueChanged += (_, _) => OnOptionChanged(option, (int)(number.Value ?? number.Maximum));
                optionControls[option.Id] = number;
                row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, previous == null ? 0 : 4, 0, 0),
                    Children = { new TextBlock { Classes = { "label" }, Text = option.Label }, number },
                };
                break;

            default:
                var box = new CheckBox();

                if (option.Description != null)
                {
                    box.Classes.Add("described");
                    box.Content = new StackPanel { Children = { new TextBlock { Text = option.Label }, new TextBlock { Classes = { "note" }, Text = option.Description, Margin = new Thickness(0) } } };

                    // a little more room under a row of choices
                    if (previous?.Kind == OptionKind.Choice)
                    {
                        box.Margin = new Thickness(0, 8, 0, 4);
                    }
                }
                else
                {
                    box.Content = option.Label;
                }

                row = Toggle(option, box);
                break;
        }

        ToolTip.SetTip(row, option.Help);
        ToolTip.SetShowOnDisabled(row, true);
        return row;
    }

    private ToggleButton Toggle(CompileOption option, ToggleButton toggle)
    {
        toggle.Name = option.Id;
        toggle.IsChecked = (bool)values[option.Id];
        toggle.IsCheckedChanged += (_, _) => OnOptionChanged(option, toggle.IsChecked == true);
        ToolTip.SetTip(toggle, option.Help);
        ToolTip.SetShowOnDisabled(toggle, true);
        optionControls[option.Id] = toggle;
        return toggle;
    }

    private void OnOptionChanged(CompileOption option, object value)
    {
        if (showingValues)
        {
            return;
        }

        values[option.Id] = value;

        if (!applyingPreset)
        {
            // an unlocked profile and Entities only keep what's changed, anything else moves to what the options match
            if (SelectedPreset is not ({ Kind: PresetKind.Profile, Locked: false } or { Kind: PresetKind.EntitiesOnly }))
            {
                SelectMatchingPreset();
            }

            UpdateStages();
            RememberPreset();
        }

        UpdateArgLabel();
    }

    // Sets an option and moves its control to the value
    private void SetValue(string id, object value)
    {
        values[id] = value;
        showingValues = true;

        switch (optionControls.GetValueOrDefault(id))
        {
            case ToggleButton toggle:
                toggle.IsChecked = (bool)value;
                break;
            case ListBox list:
                list.SelectedItem = value;
                break;
            case NumericUpDown number:
                number.Value = (int)value;
                break;
        }

        showingValues = false;
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (selectingPreset || presetList.SelectedIndex < 0)
        {
            UpdateStages();
            return;
        }

        ApplyPreset(presets[presetList.SelectedIndex]);
        RememberPreset();
    }

    private void ApplyPreset(Preset preset)
    {
        applyingPreset = true;
        SetValue(CompileOptions.EntitiesOnlyId, preset.Kind == PresetKind.EntitiesOnly);

        // options the game doesn't have, like grid nav outside Dota, are left out
        foreach (var (id, value) in preset.Values.Where(v => values.ContainsKey(v.Key)))
        {
            SetValue(id, value);
        }

        applyingPreset = false;
        UpdateStages();
        UpdateArgLabel();
    }

    // Remembers the preset picked for the map, and for Custom or a profile, the options it's now left with, so they come back
    // with the map. Custom and Entities only are kept in the map's file and the profiles in the settings. Only the user's own changes get here,
    // not the list moving to the preset the options match. Without a map they're kept in the settings for the game
    private void RememberPreset()
    {
        if (presetList.SelectedIndex < 0)
        {
            return;
        }

        var preset = presets[presetList.SelectedIndex];

        // a locked profile is left as it is, even by picking it, which would otherwise fill in the options it doesn't have
        if (preset is { User: true, Locked: false })
        {
            // options this game doesn't have keep what they were set to for the games that do
            var kept = new Dictionary<string, object>(preset.Values);

            foreach (var (id, value) in values)
            {
                kept[id] = value;
            }

            presets[presetList.SelectedIndex] = preset = preset with { Values = kept };
        }

        pickedPreset = preset.Name;

        if (preset is { Kind: PresetKind.Profile, Locked: false })
        {
            SaveProfiles();
        }

        void Keep(MapPresets stored)
        {
            stored.Preset = preset.Name;

            if (preset.Kind is PresetKind.Custom or PresetKind.EntitiesOnly)
            {
                stored.Presets[preset.Name] = CompileOptions.ToText(preset.Values);
            }
        }

        if (mappath is { } map)
        {
            Remember(() =>
            {
                var stored = MapPresets.Load(map) ?? new MapPresets();
                Keep(stored);
                stored.Save(map);
            });
        }
        else
        {
            SaveSettings(settings => Keep(settings.For(game).Options ??= new MapPresets()));
        }
    }

    // Moves the preset list to the preset the options match, or to Custom when they match none
    private void SelectMatchingPreset()
    {
        var match = Array.FindIndex(presets, preset => !preset.User && preset.Values.All(v => !values.TryGetValue(v.Key, out var value) || Equals(value, v.Value)));

        selectingPreset = true;
        presetList.SelectedIndex = match >= 0 ? match : Array.FindIndex(presets, preset => preset.Name == CompileOptions.Custom.Name);
        selectingPreset = false;
    }

    // Greys out the options of a group that's switched off, and the lighting while Entities only is picked
    private void UpdateStages()
    {
        // the lighting can't be baked with Entities only, so it's kept off and greyed out while that's picked
        var entitiesOnly = values.GetValueOrDefault(CompileOptions.EntitiesOnlyId) is true;

        if (entitiesOnly && values.GetValueOrDefault("lighting") is true)
        {
            SetValue("lighting", false);
        }

        var options = new OptionValues(values, game);

        foreach (var (group, card, panel) in cards)
        {
            if (group.Switch?.Id == "lighting")
            {
                card.IsEnabled = !entitiesOnly;
                ToolTip.SetTip(card, entitiesOnly ? "Lighting can't be baked with Entities only" : null);
                ToolTip.SetShowOnDisabled(card, true);
            }

            if (panel != null && group.Switch is { } toggle)
            {
                panel.IsEnabled = options.On(toggle.Id);
            }
        }

        presetNote.Text = SelectedPreset?.Description;
        renameProfileButton.IsEnabled = deleteProfileButton.IsEnabled = lockProfileBox.IsEnabled = SelectedPreset?.Kind == PresetKind.Profile;
        lockProfileBox.IsChecked = SelectedPreset is { Kind: PresetKind.Profile, Locked: true };
    }

    private readonly Dictionary<string, string> _helpText = new Dictionary<string, string>
    {
        {"labelCancel", "Cancel build."},
        {"labelCustomPath", "Pick a game's executable yourself, for a game Steam doesn't know of."},
        {"labelgamestatus", "The game to compile with, from the Source 2 games installed through Steam and any picked with Custom Path."},
        {"labeltoolstatus", "Current tools status. resourcecompiler.exe must be present."},
        {"labeloverrideoutput", "Override map vpk output path."},
        {"labelopenvmap", "Open .vmap file."},
        {"labelCompile", "Begin map compilation."},
    };

    /// <summary>Every control tagged with a help text shows it as a tooltip, disabled ones too so they still say what they are.</summary>
    private void HelpSystemEventReg()
    {
        foreach (var control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is string tag && _helpText.TryGetValue(tag, out string? helpText))
            {
                ToolTip.SetTip(control, helpText);
                ToolTip.SetShowOnDisabled(control, true);
            }
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        await new SettingsWindow().ShowDialog(this);
    }

    /// <summary>Adds the app's own message to the log, a line for each of its lines.</summary>
    private void Log(string text, LogKind kind)
    {
        foreach (var line in text.Split('\n'))
        {
            pendingLines.Enqueue(LogLine.App(line, kind));
        }
    }

    // a brush of the theme, as it is now
    private IBrush? Brush(string key)
    {
        return this.TryFindResource(key, ActualThemeVariant, out var brush) ? brush as IBrush : null;
    }

    // resourcecompiler's colours are made for a dark console, so the light theme remaps them in OKLCH, whose lightness is how
    // light a colour looks. A colour stands out by its chroma, which sRGB only has room for at middling lightness, so colours
    // are darkened no further than that. Greys have no chroma, they stand out by being brighter than the text, which on a
    // light background means darker, so their lightness is mirrored
    private ImmutableSolidColorBrush CompilerBrush(Color color)
    {
        if (ActualThemeVariant == ThemeVariant.Light)
        {
            var (lightness, chroma, hue) = new Unicolour(ColourSpace.Rgb255, color.R, color.G, color.B).Oklch;
            lightness = chroma < 0.03 ? 1 - lightness : Math.Min(lightness, 0.6);
            color = Color.Parse(new Unicolour(ColourSpace.Oklch, lightness, chroma, hue).MapToRgbGamut().Hex);
        }

        return new ImmutableSolidColorBrush(color);
    }

    // Shows the lines printed since the last time, following them down when the log was already at its end, and how far the
    // compile has got from them. The line still being printed goes last, in place of what it was the time before, so a
    // step's bar fills in the way it does in a console
    private void FlushLog()
    {
        List<LogLine> lines = [];
        string live;

        lock (logLock)
        {
            while (pendingLines.TryDequeue(out var line))
            {
                lines.Add(line);
            }

            live = unfinishedLine;
        }

        var liveLine = LogLine.FromHtml(WithoutOpenTag(live));

        if (progress is { } compile)
        {
            lines.ForEach(line => compile.Line(line.Text));
            compile.Partial(liveLine.Text);
            compileBar.Value = compile.Overall * 100;
            statusLabel.Text = $"{compile.Text}  ·  {progressClock.Elapsed:hh\\:mm\\:ss}";
        }

        if (lines.Count == 0 && live == shownLive)
        {
            return;
        }

        var document = logEditor.Document;
        document.BeginUpdate();

        if (liveStart >= 0)
        {
            document.Remove(liveStart, document.TextLength - liveStart);
            logSpans.RemoveRange(liveSpans, logSpans.Count - liveSpans);
            logEmpty = emptyBeforeLive;
            liveStart = -1;
        }

        var text = new StringBuilder();
        lines.ForEach(Append);

        if (liveLine.Text.Length > 0)
        {
            liveStart = document.TextLength + text.Length;
            liveSpans = logSpans.Count;
            emptyBeforeLive = logEmpty;
            Append(liveLine);
        }

        shownLive = live;
        document.Insert(document.TextLength, text.ToString());
        document.EndUpdate();

        void Append(LogLine line)
        {
            text.Append(logEmpty ? "" : "\n");
            logEmpty = false;

            var start = document.TextLength + text.Length;
            logSpans.AddRange(line.Spans.Select(span => span with { Offset = start + span.Offset }));
            text.Append(line.Text);
        }
    }

    // A tag can arrive in pieces, and shows as text until its end does
    private static string WithoutOpenTag(string html)
    {
        var open = html.LastIndexOf('<');
        return open > html.LastIndexOf('>') ? html[..open] : html;
    }

    // Scrolling moves the log only when it's scrolled, so whether it's at the bottom then says whether to follow it. The text
    // growing or the log resizing leaves it where it is, unless it's following, when it goes to the new bottom. That only comes
    // once the new lines are laid out, so it lands on the real end
    private void OnLogScrolled(object? sender, ScrollChangedEventArgs e)
    {
        var viewer = (ScrollViewer)sender!;
        var bottom = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);

        if (e.OffsetDelta.Y != 0)
        {
            followLog = viewer.Offset.Y >= bottom - 2;
        }
        else if (followLog && viewer.Offset.Y < bottom)
        {
            viewer.Offset = viewer.Offset.WithY(bottom);
        }
    }

    // The Copy button copies the selected text, or the whole log when nothing is selected
    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard && (logEditor.SelectionLength > 0 ? logEditor.SelectedText : logEditor.Text) is { Length: > 0 } text)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void OnCopySelectedLog(object? sender, RoutedEventArgs e)
    {
        logEditor.Copy();
    }

    private void OnCopyAllLog(object? sender, RoutedEventArgs e)
    {
        logEditor.SelectAll();
        logEditor.Copy();
    }

    private void OnSelectAllLog(object? sender, RoutedEventArgs e)
    {
        logEditor.SelectAll();
    }

    private void OnShowLightmapPreview(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            lightmapPreview?.Show();
        }
    }

    private void OnClearLog(object? sender, RoutedEventArgs e)
    {
        logEditor.Document.Text = "";
        logSpans.Clear();
        logEmpty = true;
        liveStart = -1;
        shownLive = "";
        followLog = true;
    }
}
