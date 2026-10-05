using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using Source2MapCompiler.LightmapPreview;

namespace Source2MapCompiler;

[SupportedOSPlatform("windows")]
// Keeps one preview window across compiles. Every compile that bakes on the GPU gets a fresh monitor, and the window opens
// on the first update and gets reused after that. The latest update is kept too, so a closed window can be opened again
// with Show, during the compile or after it
internal sealed class LightmapPreviewController(Window owner, Action<string> log) : IDisposable
{
    private LightmapPreviewMonitor? monitor;
    private LightmapPreviewWindow? window;

    // If the window is closed during a compile it stays closed until the next one, or until it's opened again
    private bool closedDuringCompile;

    private LightmapPreviewUpdate? latest;

    // what the window says once the compile has ended
    private string? ended;

    // raised on the first update there is, from when on there's always a preview to show
    public event EventHandler? Available;

    public void Start(int compilerProcessId, string vrad3Folder)
    {
        Stop();

        closedDuringCompile = false;
        ended = null;

        var current = new LightmapPreviewMonitor(compilerProcessId, vrad3Folder);
        current.Message += (_, message) => log(message);
        // Updates that arrive after the compile ended, or from an older compile's monitor, get dropped
        current.Updated += (_, update) => Dispatcher.UIThread.Post(() =>
        {
            if (monitor == current)
            {
                OnUpdated(update);
            }
        });

        monitor = current;
        current.Start();
    }

    public void Stop()
    {
        if (monitor == null)
        {
            return;
        }

        monitor.Dispose();
        monitor = null;

        ended = latest?.Stage switch
        {
            null => null,
            LightmapPreviewStage.ProbeVolume => "The compile has ended. This is the last light probe volume vrad3 baked.",
            LightmapPreviewStage.Processed => "The compile has ended. This is the lightmap vrad3 filtered, before it is compressed.",
            _ => "The compile has ended. This is the lightmap as vrad3 last baked it, before it was filtered and compressed.",
        };

        if (ended != null)
        {
            window?.ShowEnded(ended);
        }
    }

    // Opens the window again, or brings it to the front, showing the latest update
    public void Show()
    {
        if (latest is not { } update)
        {
            return;
        }

        closedDuringCompile = false;

        if (window != null)
        {
            window.Activate();
            return;
        }

        OpenWindow();
        window!.ShowUpdate(update);

        if (ended != null)
        {
            window.ShowEnded(ended);
        }
    }

    private void OnUpdated(LightmapPreviewUpdate update)
    {
        var first = latest == null;
        latest = update;

        if (first)
        {
            Available?.Invoke(this, EventArgs.Empty);
        }

        if (closedDuringCompile)
        {
            return;
        }

        if (window == null)
        {
            OpenWindow();
            log("Showing the lightmap as vrad3 bakes it.");
        }

        window!.ShowUpdate(update);
    }

    private void OpenWindow()
    {
        window = new LightmapPreviewWindow();
        window.Closed += (_, _) =>
        {
            window = null;
            closedDuringCompile = monitor != null;
        };
        window.Show(owner);
    }

    public void Dispose()
    {
        Stop();
    }
}
