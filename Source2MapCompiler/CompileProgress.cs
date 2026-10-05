using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Source2MapCompiler;

// How far a map build has got, worked out from what resourcecompiler prints, as it reports no progress of its own. Each
// stage starts with a line of its own, and within a stage, its steps say how far it is: the lightmap's blocks and the light
// probe volumes count up to a total, vis and nav number their passes and stages, and the longer steps draw a bar of digits
// and dots, which is read before its line is done as it comes in a few at a time. Stages the options leave out don't
// count, so the bar doesn't jump past them. Each stage's weight is about how many seconds it took in a full build of a
// big map, de_dogtown, where vis took most of it
internal sealed partial class CompileProgress
{
    private sealed record Stage(string Name, Regex Marker, double Weight);

    private const string Visibility = "Building visibility";
    private const string Baking = "Baking the lightmap";
    private const string ProbeVolumes = "Baking light probes";
    private const string Navigation = "Building the nav mesh";

    // where vis's steps end, as shares of the whole of it
    private const double VisClustersBuilt = 0.035;
    private const double VisClustersMerged = 0.09;
    private const double VisCentreRaysDone = 0.12;
    private const double VisBoundaryRaysDone = 0.887;
    private const double VisRaysDone = 0.892;

    private readonly Stage[] stages;
    private readonly double total;
    private int current;
    private double within;
    private string detail = "";

    // vrad3's blocks take about as long as each other, so the one baking moves on by how long the ones before it took
    private int blocksDone;
    private int blockCount;
    private DateTime bakeStarted;
    private DateTime lastBlockDone;
    private bool baking;

    // vrad3's script lists the light probe volumes it bakes after the lightmap, and the log has a line as each is done
    private readonly string? vrad3Folder;
    private int volumesDone;
    private int volumeCount;

    // vis says how many times it merges its clusters before it starts, and has a line as each merge is done
    private int mergesDone;
    private int mergeCount;

    public CompileProgress(OptionValues options, string? vrad3Folder)
    {
        this.vrad3Folder = vrad3Folder;
        var stages = !options.On(CompileOptions.EntitiesOnly.Id);
        var lighting = stages && options.On("lighting");

        this.stages =
        [
            // from the start, before any marker
            new("Preparing", new("(?!)"), 20),
            new("Loading the map", new("^Building map \""), 10),
            .. stages && options.On("visibility") ? [new Stage(Visibility, new("^Building map visibility"), 2400)] : Array.Empty<Stage>(),
            .. lighting
                ? new Stage[]
                {
                    new("Preparing the lighting", new("^Building path trace scene info"), 10),
                    new("Charting the lightmap", new("^Bake Lighting$"), 25),
                    new(Baking, new("^==== Baking"), 80),
                    new(ProbeVolumes, LightProbeVolume(), 90),
                    new("Compressing the lightmap", new("^ProcessReceivedLightingPackets"), 30),
                }
                : [],
            new("Building the world's geometry", new("^Generate Overlay Meshes"), 10),
            .. stages && options.On("physics") ? [new Stage("Building physics", new(@"^\.\.\. Building 'phys'"), 5)] : Array.Empty<Stage>(),
            .. stages && options.On("navigation") ? [new Stage(Navigation, new(@"^\.\.\. Building 'nav'"), 40)] : Array.Empty<Stage>(),
            new("Packing the map", new("Map build finished"), 10),
            new("Finishing", new("^ OK:"), 0),
        ];

        total = this.stages.Sum(stage => stage.Weight);
    }

    // 0 to 1
    public double Overall => (stages.Take(current).Sum(stage => stage.Weight) + stages[current].Weight * Within()) / total;

    public string Text => (StageName, BlockText() ?? detail) switch
    {
        (var stage, { Length: > 0 } step) => $"{stage} – {step}",
        (var stage, _) => stage,
    };

    private string StageName => stages[current].Name;

    // A line resourcecompiler finished printing
    public void Line(string line)
    {
        for (var i = current + 1; i < stages.Length; i++)
        {
            if (stages[i].Marker.IsMatch(line))
            {
                current = i;
                within = 0;
                detail = "";
                baking = false;
                break;
            }
        }

        switch (StageName)
        {
            case Visibility:
                VisLine(line);
                break;
            case Baking:
                BakingLine(line);
                break;
            case ProbeVolumes when LightProbeVolume().IsMatch(line):
                volumeCount = volumeCount > 0 ? volumeCount : CountProbeVolumes();
                volumesDone++;
                detail = volumeCount > 0 ? $"volume {volumesDone}/{volumeCount}" : $"volume {volumesDone}";
                Reach(volumeCount > 0 ? (double)volumesDone / volumeCount : 0);
                break;
            case Navigation:
                NavLine(line);
                break;
        }
    }

    // The line resourcecompiler is still printing, which is where a step's bar shows up, and vrad3's block and volume as it
    // starts them
    public void Partial(string line)
    {
        // the next stage's first line, which moves on to it once it's done
        if (stages.Skip(current + 1).Any(stage => stage.Marker.IsMatch(line)))
        {
            return;
        }

        if (StageName == Baking && Block().Match(line) is { Success: true } block)
        {
            if (!baking && blocksDone == 0)
            {
                bakeStarted = DateTime.UtcNow;
            }

            baking = true;
            blockCount = int.Parse(block.Groups[2].Value, CultureInfo.InvariantCulture);
            return;
        }

        if (StageName == ProbeVolumes && LightProbeVolume().IsMatch(line))
        {
            volumeCount = volumeCount > 0 ? volumeCount : CountProbeVolumes();
            detail = volumeCount > 0 ? $"volume {volumesDone + 1}/{volumeCount}" : $"volume {volumesDone + 1}";
            return;
        }

        if (Bar().Match(line) is { Success: true } bar)
        {
            var step = bar.Groups["step"].Value.Trim().TrimEnd('.');
            var fraction = BarFraction(bar.Groups["bar"].Value);

            if (StageName == Visibility)
            {
                VisStep(step, fraction);
            }
            else
            {
                detail = Counts(step);

                // the charts are packed onto the lightmap in a few passes, which take most of charting it. Other steps' bars
                // only say how far that step is, which could be a moment or most of its stage
                if (ChartPass().Match(step) is { Success: true } pass)
                {
                    var passes = int.Parse(pass.Groups[2].Value, CultureInfo.InvariantCulture);
                    Reach(0.4 + 0.6 * (int.Parse(pass.Groups[1].Value, CultureInfo.InvariantCulture) - 1 + fraction) / passes);
                }
            }
        }
        else if (StageName != Visibility && Step().Match(line) is { Success: true } step)
        {
            detail = Counts(step.Groups[1].Value);
        }
    }

    private void VisLine(string line)
    {
        if (line.StartsWith("Generated clusters for", StringComparison.Ordinal))
        {
            Reach(VisClustersBuilt);
            detail = "merging regions";
        }
        else if (line.StartsWith("Merged cluster lists", StringComparison.Ordinal))
        {
            Reach(VisClustersMerged);
        }
        else if (line.StartsWith("ClusterCenterRayGenerator complete", StringComparison.Ordinal))
        {
            Reach(VisCentreRaysDone);
        }
        else if (line.StartsWith("CBoundaryPointsRayGenerator complete", StringComparison.Ordinal))
        {
            Reach(VisBoundaryRaysDone);
        }
        else if (line.StartsWith("LOS Scan tried", StringComparison.Ordinal))
        {
            Reach(VisRaysDone);
        }
        else if (MergeSteps().Match(line) is { Success: true } steps)
        {
            mergeCount = int.Parse(steps.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else if (line.StartsWith("Merged to ", StringComparison.Ordinal) && mergeCount > 0)
        {
            mergesDone++;
            Reach(VisRaysDone + (1 - VisRaysDone) * mergesDone / mergeCount);
            detail = $"merging clusters {Math.Min(mergesDone + 1, mergeCount)}/{mergeCount}";
        }
        else if (RayPass().Match(line) is { Success: true } pass)
        {
            VisStep($"{pass.Groups[1].Value} pass {pass.Groups[2].Value}", 1);
        }
    }

    // A vis step, with how far its bar has got. The ray passes have no count, so each one goes most of the way through what's
    // left of its generator's share, and only the generator finishing gets to the end of it
    private void VisStep(string step, double fraction)
    {
        if (RayPass().Match(step) is { Success: true } pass)
        {
            var (from, to, name) = pass.Groups[1].Value switch
            {
                "ClusterCenterRayGenerator" => (VisClustersMerged, VisCentreRaysDone, "cluster centre rays"),
                "CBoundaryPointsRayGenerator" => (VisCentreRaysDone, VisBoundaryRaysDone, "boundary point rays"),
                _ => (VisBoundaryRaysDone, VisRaysDone, "large cluster rays"),
            };

            var passes = int.Parse(pass.Groups[2].Value, CultureInfo.InvariantCulture) - 1 + fraction;
            Reach(from + (to - from) * (1 - Math.Pow(0.85, passes)));
            detail = $"{name}, pass {pass.Groups[2].Value}";
        }
        else if (step.StartsWith("Merge vis clusters", StringComparison.Ordinal) && mergeCount > 0)
        {
            Reach(VisRaysDone + (1 - VisRaysDone) * (mergesDone + fraction) / mergeCount);
            detail = $"merging clusters {Math.Min(mergesDone + 1, mergeCount)}/{mergeCount}";
        }
    }

    // The blocks take most of the bake, the filtering and welding after them the rest
    private void BakingLine(string line)
    {
        if (Block().Match(line) is { Success: true } block)
        {
            blocksDone = int.Parse(block.Groups[1].Value, CultureInfo.InvariantCulture);
            blockCount = int.Parse(block.Groups[2].Value, CultureInfo.InvariantCulture);
            lastBlockDone = DateTime.UtcNow;
            baking = false;
            detail = "";
            Reach(0.75 * blocksDone / blockCount);
        }
        else if (line.TrimStart().StartsWith("Welding seams", StringComparison.Ordinal))
        {
            Reach(0.95);
        }
    }

    // NAVGEN's stages are about a third of the nav, the AI data and the bomb damage after them the rest
    private void NavLine(string line)
    {
        if (NavStage().Match(line) is { Success: true } stage)
        {
            var done = int.Parse(stage.Groups[1].Value, CultureInfo.InvariantCulture);
            var count = int.Parse(stage.Groups[2].Value, CultureInfo.InvariantCulture);

            // NAVGEN runs a last stage on its own afterwards, which isn't worth going back for
            if (count > 1)
            {
                Reach(0.3 * done / count);
                detail = $"stage {Math.Min(done + 1, count)}/{count}";
            }
        }
        else if (line.StartsWith("Baking CS2 Bomb Damage", StringComparison.Ordinal))
        {
            Reach(0.9);
            detail = "bomb damage";
        }
    }

    private void Reach(double fraction)
    {
        within = Math.Clamp(Math.Max(within, fraction), 0, 1);
    }

    private double Within()
    {
        return baking && blockCount > 0 ? 0.75 * (blocksDone + BlockFraction()) / blockCount : within;
    }

    // How far the block baking is likely to be, short of done until it says so. Nothing's known before the first one ends
    private double BlockFraction()
    {
        return blocksDone > 0 ? Math.Min((DateTime.UtcNow - lastBlockDone) / AverageBlock(), 0.95) : 0;
    }

    private TimeSpan AverageBlock()
    {
        return (lastBlockDone - bakeStarted) / blocksDone;
    }

    private string? BlockText()
    {
        if (!baking)
        {
            return null;
        }

        var text = $"block {blocksDone + 1}/{blockCount}";
        return blocksDone == 0 ? text : $"{text}, about {AverageBlock() * (blockCount - blocksDone - BlockFraction()):m\\:ss} left";
    }

    // Read once the volumes start, by when resourcecompiler has long written the script. Zero when it can't be read, which
    // leaves the stage counting without a total
    private int CountProbeVolumes()
    {
        try
        {
            return vrad3Folder == null ? 0 : File.ReadLines(Path.Combine(vrad3Folder, "script-gpu.vrad3")).Count(ScriptProbeVolume().IsMatch);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // A bar's last number, out of ten, and the dots after it, as a share of the dots between its numbers
    private static double BarFraction(string bar)
    {
        var numbers = Regex.Matches(bar, @"\d+");
        var last = int.Parse(numbers[^1].Value, CultureInfo.InvariantCulture);
        var dotsAfter = bar.Length - (numbers[^1].Index + numbers[^1].Length);
        var dotsBetween = numbers.Count > 1 ? numbers[1].Index - (numbers[0].Index + numbers[0].Length) : 4;
        return Math.Min((last + (double)dotsAfter / dotsBetween) / 10, 1);
    }

    // Pass 2 of 4 reads as pass 2/4
    private static string Counts(string step)
    {
        return OfCount().Replace(step, "$1/$2");
    }

    // a step's name, then its bar so far, which counts from 0 to 10: Preprocessing Lights [0....1....2, Merge vis clusters:0...1...2,
    // Pass 2 of 4 ->[0
    [GeneratedRegex(@"^\s*(?<step>.*?)\s*(?:->)?[\[:](?<bar>0(?:\.{2,}\d{1,2})*\.*)$")]
    private static partial Regex Bar();

    [GeneratedRegex(@"Compute block (\d+)/(\d+)")]
    private static partial Regex Block();

    // a light probe volume vrad3 finished, Compute 68x68x37 LPV_599420511 [...] Done in 2.56 seconds
    [GeneratedRegex(@"^\s*Compute \S+ LPV_")]
    private static partial Regex LightProbeVolume();

    [GeneratedRegex(@"^\s*run compute_lpv_")]
    private static partial Regex ScriptProbeVolume();

    // CBoundaryPointsRayGenerator pass 8
    [GeneratedRegex(@"^(\w+RayGenerator) pass (\d+)")]
    private static partial Regex RayPass();

    // 37110 clusters, 5 steps
    [GeneratedRegex(@"^\d+ clusters, (\d+) steps")]
    private static partial Regex MergeSteps();

    // >>NAVGEN: stage 06 / 31: Recast took 4.644 sec
    [GeneratedRegex(@"NAVGEN: stage (\d+) / (\d+)")]
    private static partial Regex NavStage();

    [GeneratedRegex(@"(\d+) of (\d+)")]
    private static partial Regex OfCount();

    // Pass 2 of 4, packing the lightmap's charts
    [GeneratedRegex(@"^Pass (\d+) of (\d+)$")]
    private static partial Regex ChartPass();

    // a step without a bar, like vrad3 filtering and welding the lightmap after its last block, Median Filter (7x7)...
    // Not the rest of a bar that something else printed in the middle of, which starts with a number
    [GeneratedRegex(@"^[\s>-]*([A-Za-z][^\[]*?)\.{3,}\s*$")]
    private static partial Regex Step();
}
