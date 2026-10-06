using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TinyEXR;
using TinyEXR.IO;

namespace Source2MapCompiler.LightmapPreview;

public enum LightmapPreviewStage
{
    Waiting, // the script has been read and the blocks are laid out
    Baking,
    BlockDone,
    Processed, // showing vrad3's filtered, dilated and welded lightmap
    ProbeVolume, // showing a light probe volume vrad3 just baked
}

// Index and Count are the block and how many there are, or for a light probe volume, the volume and how many there are
public sealed record LightmapPreviewUpdate(LightmapAtlas Atlas, bool IsNewAtlas, PixelRegion Pixels, int Index, int Count, LightmapPreviewStage Stage);

// Follows a compile's lightmap bake by polling the addon's _vrad3 folder and vrad3 itself. resourcecompiler writes
// script-gpu.vrad3 there first, which gives the lightmap size and how many blocks there are. While vrad3 bakes, each block is
// read out of its memory as soon as it moves on to the next one. fireflies.exr is the first file vrad3 writes after the last
// block, so that's how we know the bake is over, and then irradiance.exr (sh2_dc.exr for SH2 maps) replaces the raw blocks
// with the filtered lightmap. After that vrad3 bakes the light probe volumes, and each is shown once it's written. If reading
// vrad3 fails, the grid and the filtered lightmap still show up. Events come in on the monitor's thread
public sealed class LightmapPreviewMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // vrad3 writes this right after the last block
    private const string BakedFileName = "fireflies.exr";

    // How many rows of an EXR to read at a time
    private const int ExrRows = 64;

    private readonly int compilerProcessId;
    private readonly string vrad3Folder;
    private readonly Thread thread;
    private readonly ManualResetEventSlim stopping = new();
    private readonly HashSet<int> ignored = [];

    // resourcecompiler clears out _vrad3 before vrad3 starts, but until then the folder still has the last compile's files,
    // so anything older than this gets ignored
    private DateTime startedUtc;

    private Vrad3ScriptReader.Vrad3Script? script;
    private Vrad3Reader? reader;
    private Process? vrad3;

    private ushort[] red = [], green = [], blue = [];
    private bool newAtlas;

    private int current = -1;
    private bool baked;
    private bool processed;

    // vrad3 writes the filtered lightmap all at once, so it's only read after its size and time stay the same for two polls
    private (long Length, DateTime Time) processedFile;

    private bool anyRead;

    private readonly HashSet<string> shownProbeVolumes = [];

    public LightmapPreviewMonitor(int compilerProcessId, string vrad3Folder)
    {
        this.compilerProcessId = compilerProcessId;
        this.vrad3Folder = vrad3Folder;
        thread = new Thread(Run) { IsBackground = true, Name = "Lightmap preview" };
    }

    public event EventHandler<string>? Message;

    public event EventHandler<LightmapPreviewUpdate>? Updated;

    public LightmapAtlas? Atlas { get; private set; }

    public void Start()
    {
        startedUtc = DateTime.UtcNow;
        thread.Start();
    }

    private void Run()
    {
        while (!stopping.IsSet)
        {
            try
            {
                if (script == null)
                {
                    TryReadScript();
                }
                else if (!processed)
                {
                    if (!baked)
                    {
                        ReadBlocks();
                    }

                    if (!baked && IsFromThisCompile(BakedFileName))
                    {
                        FinishBlocks();
                    }

                    TryReadProcessed();
                }
                else
                {
                    ReadProbeVolumes();
                }
            }
            catch (Exception exception)
            {
                Message?.Invoke(this, $"Lightmap preview stopped: {exception.Message}");
                processed = true;
                Close();
            }

            stopping.Wait(PollInterval);
        }

        Close();
    }

    private bool IsFromThisCompile(string name)
    {
        var file = new FileInfo(Path.Combine(vrad3Folder, name));
        return file.Exists && file.LastWriteTimeUtc >= startedUtc;
    }

    private void TryReadScript()
    {
        if (!IsFromThisCompile(Vrad3ScriptReader.FileName) || Vrad3ScriptReader.TryRead(Path.Combine(vrad3Folder, Vrad3ScriptReader.FileName)) is not { } read)
        {
            return;
        }

        script = read;
        var atlas = new LightmapAtlas(read.Width, read.Height, read.BlockSize);
        Atlas = atlas;
        red = new ushort[read.BlockSize * read.BlockSize];
        green = new ushort[red.Length];
        blue = new ushort[red.Length];
        newAtlas = true;
        Raise(atlas, default, -1, LightmapPreviewStage.Waiting);
    }

    // If this fails it only stops the live blocks, the rest of the preview keeps going
    private void ReadBlocks()
    {
        try
        {
            if (reader == null)
            {
                TryOpen();
                return;
            }

            if (vrad3!.HasExited)
            {
                Close();
                return;
            }

            if (TryReadLightmap() is not { } lightmap)
            {
                return;
            }

            var index = lightmap.BlockIndex;

            if ((uint)index >= (uint)Atlas!.BlockCount || index == current)
            {
                return;
            }

            // vrad3 bakes blocks in order, so everything before the current block is finished
            ReadBlocksBefore(lightmap, index);
            current = index;
            Atlas.SetBaking(index);
            Raise(Atlas, default, index, LightmapPreviewStage.Baking);
        }
        catch (Exception exception)
        {
            StopReading(exception);
        }
    }

    // The last block has no block after it, so it gets read once the bake is over
    private void FinishBlocks()
    {
        baked = true;

        try
        {
            if (reader != null && TryReadLightmap() is { } lightmap)
            {
                ReadBlocksBefore(lightmap, Atlas!.BlockCount);
            }
        }
        catch (Exception exception)
        {
            StopReading(exception);
        }

        Close();
    }

    private Vrad3Lightmap? TryReadLightmap()
    {
        if (reader!.TryRead() is not { } lightmap)
        {
            return null;
        }

        var expected = script!.Value;

        if (lightmap.Width != expected.Width || lightmap.Height != expected.Height)
        {
            throw new NotSupportedException($"vrad3's lightmap is {lightmap.Width}x{lightmap.Height}, not the {expected.Width}x{expected.Height} its script says.");
        }

        return lightmap;
    }

    private void ReadBlocksBefore(in Vrad3Lightmap lightmap, int end)
    {
        var atlas = Atlas!;

        for (var index = 0; index < end; index++)
        {
            if (atlas.GetBlockState(index) == LightmapBlockState.Done)
            {
                continue;
            }

            var block = atlas.BlockRegion(index);
            reader!.ReadBlock(lightmap, block, red, green, blue);
            atlas.IngestRows(block, red, green, blue);
            atlas.UpdateAverageLuminance();
            atlas.SetDone(index);
            anyRead = true;
            Raise(atlas, block, index, LightmapPreviewStage.BlockDone);
        }
    }

    private void StopReading(Exception exception)
    {
        // Reads start failing once vrad3 frees the lightmap or exits, which is normal and not worth reporting
        if (!anyRead && vrad3 is { HasExited: false })
        {
            Message?.Invoke(this, $"Lightmap preview can not show the blocks as they bake: {exception.Message}");
        }

        if (vrad3 != null)
        {
            ignored.Add(vrad3.Id);
        }

        Close();
    }

    // vrad3 is found by name, then checked against this compile through its parent processes
    private void TryOpen()
    {
        foreach (var process in Process.GetProcessesByName("vrad3"))
        {
            if (reader != null || ignored.Contains(process.Id))
            {
                process.Dispose();
                continue;
            }

            if (!ProcessTree.IsDescendantOf(process.Id, compilerProcessId))
            {
                // Belongs to another compile
                ignored.Add(process.Id);
                process.Dispose();
                continue;
            }

            try
            {
                reader = Vrad3Reader.TryOpen(process);
            }
            catch (Exception exception)
            {
                ignored.Add(process.Id);
                Message?.Invoke(this, $"Lightmap preview can not show the blocks as they bake: {exception.Message}");
            }

            if (reader != null)
            {
                vrad3 = process;
            }
            else
            {
                process.Dispose();
            }
        }
    }

    private void Close()
    {
        reader?.Dispose();
        reader = null;
        vrad3?.Dispose();
        vrad3 = null;
    }

    private void TryReadProcessed()
    {
        var file = new FileInfo(Path.Combine(vrad3Folder, script!.Value.ProcessedFileName));

        if (!file.Exists || file.LastWriteTimeUtc < startedUtc)
        {
            return;
        }

        var seen = processedFile;
        processedFile = (file.Length, file.LastWriteTimeUtc);

        if (seen != processedFile)
        {
            return;
        }

        var atlas = Atlas!;

        try
        {
            ReadExr(file.Name, (width, height) => width == atlas.Width && height == atlas.Height
                ? atlas
                : throw new InvalidDataException($"{file.Name} is {width}x{height}, not the {atlas.Width}x{atlas.Height} its script says."));
        }
        catch (IOException)
        {
            // vrad3 is still writing it
            return;
        }

        processed = true;
        atlas.SetDone();
        Raise(atlas, new PixelRegion(0, 0, atlas.Width, atlas.Height), -1, LightmapPreviewStage.Processed);
    }

    // vrad3 writes each volume's dlshd.exr right after its ambientcube.exr, so once that's there the ambient cube is whole.
    // An ambient cube is the light coming from each of six directions, and its EXR lays the volume's depth slices side by side
    // with a row of them for each direction, then a last row that's a mask rather than light, which is left out
    private void ReadProbeVolumes()
    {
        var volumes = script!.Value.ProbeVolumes;

        for (var index = 0; index < volumes.Length; index++)
        {
            var id = volumes[index];

            if (shownProbeVolumes.Contains(id) || !IsFromThisCompile($"lpv_{id}_dlshd.exr"))
            {
                continue;
            }

            // added first so a volume that can't be read is only reported once
            shownProbeVolumes.Add(id);
            var atlas = ReadExr($"lpv_{id}_ambientcube.exr", (width, height) => new LightmapAtlas(width, height / 7 * 6, Math.Max(width, height)));
            atlas.SetDone();
            newAtlas = true;
            Raise(atlas, new PixelRegion(0, 0, atlas.Width, atlas.Height), index, LightmapPreviewStage.ProbeVolume, volumes.Length);
        }
    }

    // Reads the half float colour of an EXR vrad3 wrote into the atlas made for its size, which can have fewer rows than it
    private LightmapAtlas ReadExr(string name, Func<int, int, LightmapAtlas> atlasFor)
    {
        using var stream = new FileStream(Path.Combine(vrad3Folder, name), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dataSource = new StreamDataSource(stream, true);
        using var exr = ExrReader.OpenSource(dataSource, null!);

        Check(exr.ParseHeader());

        var header = exr.GetHeader(0);
        var window = header.DataWindow;

        foreach (var channel in new[] { "R", "G", "B" })
        {
            if (!header.Channels.Any(c => c.Name == channel && c.PixelType == PixelType.Half))
            {
                throw new InvalidDataException($"{name} has no half float {channel} channel.");
            }
        }

        var atlas = atlasFor((int)window.Width, (int)window.Height);

        for (var y = 0; y < atlas.Height; y += ExrRows)
        {
            var rows = Math.Min(ExrRows, atlas.Height - y);
            var read = exr.ReadScanlines(0, window.MinY + y, rows);
            Check(read.Operation);
            var level = read.Value!.Levels[0];

            atlas.IngestRows(
                new PixelRegion(0, y, atlas.Width, rows),
                MemoryMarshal.Cast<byte, ushort>(level.GetChannel("R").Data),
                MemoryMarshal.Cast<byte, ushort>(level.GetChannel("G").Data),
                MemoryMarshal.Cast<byte, ushort>(level.GetChannel("B").Data));
        }

        atlas.UpdateAverageLuminance();
        return atlas;

        void Check(ReaderResult result)
        {
            if (!result.IsSuccess)
            {
                throw new InvalidDataException($"{name} could not be read: {result.Error?.Message ?? result.Status.ToString()}");
            }
        }
    }

    private void Raise(LightmapAtlas atlas, PixelRegion pixels, int index, LightmapPreviewStage stage, int? count = null)
    {
        var isNew = newAtlas;
        newAtlas = false;
        Updated?.Invoke(this, new LightmapPreviewUpdate(atlas, isNew, pixels, index, count ?? atlas.BlockCount, stage));
    }

    public void Dispose()
    {
        stopping.Set();

        if (thread.IsAlive)
        {
            thread.Join();
        }

        // In case the monitor was never started
        Close();
        stopping.Dispose();
    }
}
