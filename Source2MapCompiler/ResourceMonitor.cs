using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security;
using System.Threading;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.Performance;
using Windows.Win32.System.SystemInformation;

namespace Source2MapCompiler;

// GPU and video memory are null when Windows has no GPU counters
internal readonly record struct ResourceUsage(float Cpu, float? Gpu, double? VramGigabytes, double? VramTotalGigabytes, double RamGigabytes, double RamTotalGigabytes);

// How busy the whole machine is, sampled once a second on a timer thread. The CPU and memory come straight from the kernel,
// as .NET's PerformanceCounter parses every counter name in the registry first, and throws on machines where one of them is
// broken. The GPU only has counters, read through PDH, which Windows has had since Windows 10's fall 2017 update
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class ResourceMonitor : IDisposable
{
    private const double Gigabyte = 1024d * 1024 * 1024;

    private readonly Action<ResourceUsage> sampled;
    private readonly Timer timer;
    private readonly double? vramTotal = VideoMemory();

    // Dispose closes the query, so it waits for a sample in progress
    private readonly Lock sampling = new();
    private bool disposed;

    // the times of the last sample, as the CPU's use is the change since then
    private (long Idle, long Total)? cpuTimes;

    // opened on the first sample, as looking the counters up is slow. Null when they're missing, which hides the GPU graph
    private bool gpuOpened;
    private PdhCloseQuerySafeHandle? gpuQuery;
    private PDH_HCOUNTER gpuEngines;
    private PDH_HCOUNTER gpuMemory;

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
            lock (sampling)
            {
                if (disposed)
                {
                    return;
                }

                if (!gpuOpened)
                {
                    gpuOpened = true;
                    OpenGpu();
                }

                var memory = new MEMORYSTATUSEX { dwLength = (uint)Unsafe.SizeOf<MEMORYSTATUSEX>() };

                if (!PInvoke.GlobalMemoryStatusEx(ref memory))
                {
                    throw new Win32Exception();
                }

                var (gpu, vram) = Gpu();
                sampled(new ResourceUsage(Cpu(), gpu, vram, vramTotal, (memory.ullTotalPhys - memory.ullAvailPhys) / Gigabyte, memory.ullTotalPhys / Gigabyte));
                timer.Change(1000, Timeout.Infinite);
            }
        }
        catch (Exception)
        {
            // anything thrown on the timer thread would take the whole app down, and the graphs aren't worth that
        }
    }

    // The share of the time since the last sample the processors weren't idle. The kernel time includes the idle time
    private float Cpu()
    {
        if (!PInvoke.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            throw new Win32Exception();
        }

        var (idle, total) = (Ticks(idleTime), Ticks(kernelTime) + Ticks(userTime));
        var last = cpuTimes;
        cpuTimes = (idle, total);

        if (last is not { } previous || total == previous.Total)
        {
            return 0;
        }

        return (float)Math.Clamp(100d * (1 - (double)(idle - previous.Idle) / (total - previous.Total)), 0, 100);

        static long Ticks(FILETIME time) => ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    }

    private void OpenGpu()
    {
        if (PInvoke.PdhOpenQuery(null, 0, out var query) == 0
            && PInvoke.PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", 0, out gpuEngines) == 0
            && PInvoke.PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out gpuMemory) == 0)
        {
            gpuQuery = query;
        }
        else
        {
            query.Dispose();
        }
    }

    // Task Manager's figure: each kind of engine's use added up over every process using it, and then the busiest kind. vrad3
    // keeps the compute engine busy where a game would keep the 3D one busy
    private (float? Gpu, double? Vram) Gpu()
    {
        if (gpuQuery == null || PInvoke.PdhCollectQueryData((PDH_HQUERY)gpuQuery.DangerousGetHandle()) != 0)
        {
            return (null, null);
        }

        // the engines' use is the change since the last collection, so the first has none
        var byType = new Dictionary<string, double>();

        foreach (var (instance, value) in Read(gpuEngines))
        {
            var type = instance[(instance.LastIndexOf("engtype_", StringComparison.Ordinal) + 8)..];
            byType[type] = byType.GetValueOrDefault(type) + value;
        }

        var vram = Read(gpuMemory).Sum(adapter => adapter.Value) / Gigabyte;
        return ((float)Math.Min(byType.Values.DefaultIfEmpty(0).Max(), 100), vram);
    }

    // Every instance's value from the last collection. Instances that came or went since the one before have no value yet
    private static unsafe List<(string Instance, double Value)> Read(PDH_HCOUNTER counter)
    {
        uint size = 0;
        uint count;

        if (PInvoke.PdhGetFormattedCounterArray(counter, PDH_FMT.PDH_FMT_DOUBLE, &size, &count, null) != PInvoke.PDH_MORE_DATA)
        {
            return [];
        }

        // the instance names are kept in the same buffer, after the items
        var buffer = new byte[size];
        var values = new List<(string, double)>();

        fixed (byte* bytes = buffer)
        {
            var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)bytes;

            if (PInvoke.PdhGetFormattedCounterArray(counter, PDH_FMT.PDH_FMT_DOUBLE, &size, &count, items) != 0)
            {
                return [];
            }

            for (var i = 0; i < count; i++)
            {
                if (items[i].FmtValue.CStatus is PInvoke.PDH_CSTATUS_VALID_DATA or PInvoke.PDH_CSTATUS_NEW_DATA)
                {
                    values.Add((items[i].szName.ToString(), items[i].FmtValue.doubleValue));
                }
            }
        }

        return values;
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
        lock (sampling)
        {
            disposed = true;
            timer.Dispose();
            gpuQuery?.Dispose();
        }
    }
}
