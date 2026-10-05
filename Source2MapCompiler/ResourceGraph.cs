using System.Linq;
using ScottPlot.Avalonia;

namespace Source2MapCompiler;

// A minute of a percentage drawn as a filled line, like Task Manager's, by ScottPlot. The plot reads the history array
// itself, so a new sample only shifts it along and redraws
internal sealed class ResourceGraph
{
    private const int Samples = 60;

    private readonly AvaPlot graph;
    private readonly double[] history = new double[Samples];

    public ResourceGraph(AvaPlot graph, string colour)
    {
        this.graph = graph;
        var plot = graph.Plot;
        var line = plot.Add.Scatter(Enumerable.Range(0, Samples).Select(i => (double)i).ToArray(), history, ScottPlot.Color.FromHex(colour));
        line.MarkerSize = 0;
        line.LineWidth = 1.5f;
        line.FillY = true;
        line.FillYColor = line.Color.WithAlpha(0.25);

        plot.Axes.SetLimits(0, Samples - 1, 0, 100);
        plot.HideAxesAndGrid();
        plot.Layout.Frameless();
        plot.FigureBackground.Color = ScottPlot.Colors.Transparent;
        plot.DataBackground.Color = ScottPlot.Colors.Transparent;
        graph.UserInputProcessor.Disable();
    }

    public void Add(double percent)
    {
        Array.Copy(history, 1, history, 0, Samples - 1);
        history[^1] = percent;
        graph.Refresh();
    }
}
