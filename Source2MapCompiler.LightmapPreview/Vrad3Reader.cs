using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Source2MapCompiler.LightmapPreview;

// Texel x, y is the float at Base + x * Column + y * Row
internal readonly record struct Vrad3Plane(ulong Base, long Column, long Row);

internal readonly record struct Vrad3Lightmap(int Width, int Height, int BlockIndex, ulong TexelInfo, Vrad3Plane Red, Vrad3Plane Green, Vrad3Plane Blue);

// Reads vrad3's lightmap out of its memory. vrad3 is only opened for reading, so this can't change or pause it. vrad3 keeps
// the mean colour of each texel in one plane of floats per colour, each with its own strides, plus a separate array with a
// 12 byte entry per texel whose flag says whether any chart covers it
internal sealed unsafe class Vrad3Reader : IDisposable
{
    // Offsets inside vrad3's lightmap image for the mean channel's colour planes, which are 8 bytes apart, and its colour mask
    private const int PlaneBases = 0x28;
    private const int PlaneColumns = 0x3A8;
    private const int PlaneRows = 0x4A8;
    private const int PlaneMask = 0x8A8;

    private const int TexelInfoSize = 12;

    private readonly nint process;
    private readonly Vrad3Addresses addresses;

    private Vrad3Reader(nint process, Vrad3Addresses addresses)
    {
        this.process = process;
        this.addresses = addresses;
    }

    // Returns null until vrad3.dll is loaded, and throws if the lightmap can't be found in it
    public static Vrad3Reader? TryOpen(Process target)
    {
        var module = FindModule(target, "vrad3.dll");

        if (module == null)
        {
            return null;
        }

        nint handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)target.Id);

        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({target.Id})");
        }

        try
        {
            var image = ReadImage(handle, module.BaseAddress, module.ModuleMemorySize);
            return new Vrad3Reader(handle, Vrad3Locator.Locate(image, (ulong)module.BaseAddress));
        }
        catch
        {
            PInvoke.CloseHandle((HANDLE)handle);
            throw;
        }
    }

    // Returns null until vrad3 has made the lightmap
    public Vrad3Lightmap? TryRead()
    {
        var width = Read<int>(addresses.Image);
        var height = Read<int>(addresses.Image + 4);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var texelInfo = Read<ulong>(addresses.TexelInfo);
        var mask = Read<uint>(addresses.Image + PlaneMask);
        var red = ReadPlane(0);
        var green = ReadPlane(1);
        var blue = ReadPlane(2);

        if (texelInfo == 0 || (mask & 7) != 7 || !IsPlane(red) || !IsPlane(green) || !IsPlane(blue))
        {
            return null;
        }

        return new Vrad3Lightmap(width, height, Read<int>(addresses.BlockIndex), texelInfo, red, green, blue);
    }

    // Texels that no chart covers are left with no colour
    public void ReadBlock(in Vrad3Lightmap lightmap, PixelRegion block, ushort[] red, ushort[] green, ushort[] blue)
    {
        var info = new byte[block.Width * TexelInfoSize];
        var planes = new[] { (lightmap.Red, red), (lightmap.Green, green), (lightmap.Blue, blue) };
        var row = new byte[(block.Width - 1) * planes.Max(p => p.Item1.Column) + 4];

        for (var y = block.Y; y < block.Bottom; y++)
        {
            var start = (y - block.Y) * block.Width;
            ReadTexelInfo(lightmap, block, y, info);

            foreach (var (plane, destination) in planes)
            {
                var span = (int)((block.Width - 1) * plane.Column + 4);
                ReadBytes(plane.Base + (ulong)(block.X * plane.Column + y * plane.Row), row.AsSpan(0, span));

                for (var x = 0; x < block.Width; x++)
                {
                    var baked = (info[x * TexelInfoSize + addresses.TexelInfoFlags] & 1) != 0;
                    destination[start + x] = baked ? BitConverter.HalfToUInt16Bits((Half)BitConverter.ToSingle(row, (int)(x * plane.Column))) : (ushort)0;
                }
            }
        }
    }

    private void ReadTexelInfo(in Vrad3Lightmap lightmap, PixelRegion block, int y, byte[] info)
    {
        ReadBytes(lightmap.TexelInfo + ((ulong)y * (ulong)lightmap.Width + (ulong)block.X) * TexelInfoSize, info);
    }

    private Vrad3Plane ReadPlane(int colour)
    {
        return new Vrad3Plane(
            Read<ulong>(addresses.Image + PlaneBases + (ulong)colour * 8),
            Read<long>(addresses.Image + PlaneColumns + (ulong)colour * 8),
            Read<long>(addresses.Image + PlaneRows + (ulong)colour * 8));
    }

    private static bool IsPlane(Vrad3Plane plane)
    {
        // One or a few floats per texel, and each row at least one texel long
        return plane.Base != 0 && plane.Column >= 4 && plane.Column <= 256 && plane.Row >= plane.Column;
    }

    private static ProcessModule? FindModule(Process target, string name)
    {
        try
        {
            target.Refresh();

            foreach (ProcessModule module in target.Modules)
            {
                if (string.Equals(module.ModuleName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return module;
                }
            }
        }
        catch (Win32Exception)
        {
            // The module list can't be read while the process is still starting up
        }

        return null;
    }

    // Copies vrad3.dll as it sits in memory so the locator can decode it
    private static byte[] ReadImage(nint process, nint address, int size)
    {
        const int Page = 0x1000;
        var image = new byte[size];

        fixed (byte* bytes = image)
        {
            for (var offset = 0; offset < size; offset += Page)
            {
                // Pages that can't be read are left as zeros
                PInvoke.ReadProcessMemory((HANDLE)process, (void*)(address + offset), bytes + offset, (nuint)Math.Min(Page, size - offset), null);
            }
        }

        return image;
    }

    private T Read<T>(ulong address)
        where T : unmanaged
    {
        T value;

        if (!PInvoke.ReadProcessMemory((HANDLE)process, (void*)address, &value, (nuint)sizeof(T), null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory({address:X})");
        }

        return value;
    }

    private void ReadBytes(ulong address, Span<byte> destination)
    {
        fixed (byte* bytes = destination)
        {
            if (!PInvoke.ReadProcessMemory((HANDLE)process, (void*)address, bytes, (nuint)destination.Length, null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory({address:X})");
            }
        }
    }

    public void Dispose()
    {
        PInvoke.CloseHandle((HANDLE)process);
    }
}
