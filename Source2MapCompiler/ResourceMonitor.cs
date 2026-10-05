using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Threading;
using Microsoft.Win32;

namespace Source2MapCompiler;

// GPU and video memory are null when Windows has no GPU counters
internal readonly record struct ResourceUsage(float Cpu, float? Gpu, double? VramGigabytes, double? VramTotalGigabytes, double RamGigabytes, double RamTotalGigabytes);

// How busy the whole machine is, from the Windows performance counters Task Manager reads, sampled once a second on a
// timer thread. Windows has had GPU counters since Windows 10's fall 2017 update
[SupportedOSPlatform("windows")]
internal sealed class ResourceMonitor : IDisposable
{
    private const double Gigabyte = 1024d * 1024 * 1024;

    private readonly PerformanceCounter cpu = new("Processor Information", "% Processor Utility", "_Total");
    private readonly PerformanceCounter available = new("Memory", "Available Bytes");
    private readonly PerformanceCounterCategory gpuEngines = new("GPU Engine");
    private readonly PerformanceCounterCategory gpuMemory = new("GPU Adapter Memory");
    private readonly Action<ResourceUsage> sampled;
    private readonly double? vramTotal = VideoMemory();
    private readonly Timer timer;

    // each GPU engine's last sample, as its use is the change since then
    private Dictionary<string, CounterSample> engines = [];

    public ResourceMonitor(Action<ResourceUsage> sampled)
    {
        this.sampled = sampled;
        timer = new Timer(_ => Tick(), null, 0, Timeout.Infinite);
    }

    // Rescheduled after each sample rather than on a fixed period, so a slow one can't overlap the next
    private void Tick()
    {
        try
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var (gpu, vram) = Gpu();
            sampled(new ResourceUsage(Math.Min(cpu.NextValue(), 100), gpu, vram, vramTotal, (total - available.NextValue()) / Gigabyte, total / Gigabyte));
            timer.Change(1000, Timeout.Infinite);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // the counters are missing or turned off, so there's nothing to show
        }
    }

    // Task Manager's figure: each kind of engine's use added up over every process using it, and then the busiest kind. vrad3
    // keeps the compute engine busy where a game would keep the 3D one busy
    private (float? Gpu, double? Vram) Gpu()
    {
        try
        {
            var byType = new Dictionary<string, float>();
            var seen = new Dictionary<string, CounterSample>();

            foreach (InstanceData engine in gpuEngines.ReadCategory()["Utilization Percentage"].Values)
            {
                seen[engine.InstanceName] = engine.Sample;

                if (engines.TryGetValue(engine.InstanceName, out var last))
                {
                    var type = engine.InstanceName[(engine.InstanceName.LastIndexOf("engtype_", StringComparison.Ordinal) + 8)..];
                    byType[type] = byType.GetValueOrDefault(type) + CounterSample.Calculate(last, engine.Sample);
                }
            }

            // engines of processes that have ended drop out
            engines = seen;

            var vram = gpuMemory.ReadCategory()["Dedicated Usage"].Values.Cast<InstanceData>().Sum(adapter => adapter.RawValue) / Gigabyte;
            return (Math.Min(byType.Values.DefaultIfEmpty(0).Max(), 100), vram);
        }
        catch (InvalidOperationException)
        {
            return (null, null);
        }
    }

    // The biggest graphics card's own memory. No counter has it, but Windows keeps each adapter's in the registry, the same
    // figure DXGI gives
    private static double? VideoMemory()
    {
        using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        long largest = 0;

        foreach (var name in adapters?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var adapter = adapters!.OpenSubKey(name);
                largest = Math.Max(largest, adapter?.GetValue("HardwareInformation.qwMemorySize") is long bytes ? bytes : 0);
            }
            catch (SecurityException)
            {
                // the Properties key isn't an adapter, and only the system can open it
            }
        }

        return largest > 0 ? largest / Gigabyte : null;
    }

    public void Dispose()
    {
        timer.Dispose();
        cpu.Dispose();
        available.Dispose();
    }
}
