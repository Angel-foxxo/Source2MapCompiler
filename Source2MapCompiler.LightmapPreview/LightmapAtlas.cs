using System.Threading;
using System.Threading.Tasks;

namespace Source2MapCompiler.LightmapPreview;

public readonly record struct PixelRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public enum LightmapBlockState : byte
{
    Pending,
    Baking,
    Done,
}

// Holds every texel of the lightmap as half floats, the way the view uploads it to the GPU to be exposed and tonemapped
// there, and remembers for each block whether vrad3 has baked it yet so the view can draw the grid. A texel no chart covers
// has no colour and no alpha. The monitor fills it on its own thread while the view reads it on the render thread, which is
// why everything locks
public sealed class LightmapAtlas
{
    // what a texel no chart covers counts as, and what s2v's auto exposure clamps the darkest texels to
    private const float MinLuminance = 0.005f;

    // half float 1.0, the alpha of a covered texel
    private const ushort Opaque = 0x3C00;

    // the texels as red, green, blue and alpha half floats
    private readonly ushort[] rgba;
    private readonly LightmapBlockState[] blocks;
    private readonly Lock sync = new();

    public LightmapAtlas(int width, int height, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        Width = width;
        Height = height;
        BlockSize = blockSize;
        BlocksX = (width + blockSize - 1) / blockSize;
        BlocksY = (height + blockSize - 1) / blockSize;

        blocks = new LightmapBlockState[BlocksX * BlocksY];
        rgba = new ushort[(long)width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public int BlockSize { get; }

    public int BlocksX { get; }

    public int BlocksY { get; }

    public int BlockCount => blocks.Length;

    // goes up whenever texels change, so the view knows to upload them again
    public int Version { get; private set; }

    // the log average luminance of the covered texels, which auto exposure brings to middle grey
    public float AverageLuminance { get; private set; } = 0.18f;

    public LightmapBlockState GetBlockState(int blockX, int blockY)
    {
        return GetBlockState(blockY * BlocksX + blockX);
    }

    public LightmapBlockState GetBlockState(int index)
    {
        lock (sync)
        {
            return blocks[index];
        }
    }

    // Blocks on the right and bottom edges can be smaller than the rest
    public PixelRegion BlockRegion(int index)
    {
        var x = index % BlocksX * BlockSize;
        var y = index / BlocksX * BlockSize;
        return new PixelRegion(x, y, Math.Min(BlockSize, Width - x), Math.Min(BlockSize, Height - y));
    }

    internal void SetBaking(int index)
    {
        lock (sync)
        {
            if (blocks[index] != LightmapBlockState.Done)
            {
                blocks[index] = LightmapBlockState.Baking;
            }
        }
    }

    // Passing no index marks every block as done
    internal void SetDone(int? index = null)
    {
        lock (sync)
        {
            if (index is { } one)
            {
                blocks[one] = LightmapBlockState.Done;
            }
            else
            {
                Array.Fill(blocks, LightmapBlockState.Done);
            }
        }
    }

    // Both the live blocks and the EXRs come in through here, as half floats for each colour along the region's rows
    internal void IngestRows(PixelRegion texels, ReadOnlySpan<ushort> red, ReadOnlySpan<ushort> green, ReadOnlySpan<ushort> blue)
    {
        lock (sync)
        {
            for (var y = 0; y < texels.Height; y++)
            {
                var o = ((long)(texels.Y + y) * Width + texels.X) * 4;

                for (var x = 0; x < texels.Width; x++, o += 4)
                {
                    var i = y * texels.Width + x;
                    rgba[o] = red[i];
                    rgba[o + 1] = green[i];
                    rgba[o + 2] = blue[i];
                    rgba[o + 3] = (red[i] | green[i] | blue[i]) == 0 ? (ushort)0 : Opaque;
                }
            }

            Version++;
        }
    }

    // Works out the average the way s2v's auto exposure does, as the mean of each texel's log2 luminance, from a grid of about
    // a million texels, which is plenty for an average and keeps an 8K lightmap quick. This runs on the monitor's thread
    internal void UpdateAverageLuminance()
    {
        var step = Math.Max(1, (int)Math.Sqrt((double)Width * Height / 1_000_000));
        double sum = 0;
        long count = 0;
        var gather = new Lock();

        lock (sync)
        {
            Parallel.For(0, (Height + step - 1) / step, () => (Sum: 0.0, Count: 0L), (row, _, local) =>
            {
                for (var x = 0; x < Width; x += step)
                {
                    var o = ((long)row * step * Width + x) * 4;

                    if (rgba[o + 3] != 0)
                    {
                        var luminance = 0.2125f * Half(rgba[o]) + 0.7154f * Half(rgba[o + 1]) + 0.0721f * Half(rgba[o + 2]);
                        local.Sum += Math.Log2(Math.Max(luminance, MinLuminance));
                        local.Count++;
                    }
                }

                return local;
            }, local =>
            {
                lock (gather)
                {
                    sum += local.Sum;
                    count += local.Count;
                }
            });
        }

        AverageLuminance = count > 0 ? (float)Math.Pow(2, sum / count) : 0.18f;
    }

    // Hands the texels, rows of red, green, blue and alpha half floats, to the caller while nothing can change them
    public unsafe void ReadPixels(Action<nint, int> read)
    {
        lock (sync)
        {
            fixed (ushort* pixels = rgba)
            {
                read((nint)pixels, Width * 4 * sizeof(ushort));
            }
        }
    }

    private static float Half(ushort bits)
    {
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }
}
