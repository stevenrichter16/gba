using System;
using System.Runtime.CompilerServices;
using Gba.Core.Memory;

namespace Gba.Core.Ppu;

public sealed class SimplePpu
{
    private readonly MemoryBus _bus;
    private readonly ushort[] _framebuffer = new ushort[240 * 160];

    public SimplePpu(MemoryBus bus)
    {
        _bus = bus;
    }

    public ReadOnlySpan<ushort> Framebuffer => _framebuffer;

    public void RenderMode3()
    {
        if ((_bus.DisplayControl & 0x7) != 3)
        {
            RenderGradient();
            return;
        }

        var vram = _bus.Vram;
        var span = _framebuffer.AsSpan();
        int di = 0;
        for (int si = 0; si + 1 < vram.Length && di < span.Length; si += 2, di++)
        {
            span[di] = (ushort)(vram[si] | (vram[si + 1] << 8));
        }
    }

    public void PresentTo(uint[] dest)
    {
        if (dest.Length < _framebuffer.Length) throw new ArgumentException("dest too small");
        for (int i = 0; i < _framebuffer.Length; i++)
        {
            dest[i] = Rgb555ToRgba32(_framebuffer[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Rgb555ToRgba32(ushort color)
    {
        uint r5 = (uint)(color & 0x1F);
        uint g5 = (uint)((color >> 5) & 0x1F);
        uint b5 = (uint)((color >> 10) & 0x1F);
        uint r = (r5 << 3) | (r5 >> 2);
        uint g = (g5 << 3) | (g5 >> 2);
        uint b = (b5 << 3) | (b5 >> 2);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    private void RenderGradient()
    {
        var span = _framebuffer.AsSpan();
        for (int y = 0; y < 160; y++)
        {
            for (int x = 0; x < 240; x++)
            {
                byte r5 = (byte)((x * 31) / 239);
                byte g5 = (byte)((y * 31) / 159);
                byte b5 = (byte)(((x + y) * 31) / (240 + 160 - 2));
                span[y * 240 + x] = (ushort)(r5 | (g5 << 5) | (b5 << 10));
            }
        }
    }
}
