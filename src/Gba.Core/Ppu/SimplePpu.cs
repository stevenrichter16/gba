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

    public void RenderFrame()
    {
        switch (_bus.DisplayControl & 0x7)
        {
            case 0:
                RenderBg0TextMode();
                break;
            case 3:
                RenderMode3();
                break;
            default:
                RenderGradient();
                break;
        }
    }

    private void RenderMode3()
    {
        var vram = _bus.Vram;
        var span = _framebuffer.AsSpan();
        int di = 0;
        for (int si = 0; si + 1 < vram.Length && di < span.Length; si += 2, di++)
        {
            span[di] = (ushort)(vram[si] | (vram[si + 1] << 8));
        }
    }

    private void RenderBg0TextMode()
    {
        var span = _framebuffer.AsSpan();
        span.Clear();

        ushort bgCnt = _bus.Bg0Control;
        bool palette256 = (bgCnt & (1 << 7)) != 0;
        int charBase = ((bgCnt >> 2) & 0x3) * 0x4000;
        int screenBase = ((bgCnt >> 8) & 0x1F) * 0x800;
        int sizeBits = (bgCnt >> 14) & 0x3;
        int mapWidthTiles = sizeBits switch
        {
            0 => 32,
            1 => 64,
            2 => 32,
            3 => 64,
            _ => 32
        };
        int mapHeightTiles = sizeBits switch
        {
            0 => 32,
            1 => 32,
            2 => 64,
            3 => 64,
            _ => 32
        };
        int mapWidthPixels = mapWidthTiles * 8;
        int mapHeightPixels = mapHeightTiles * 8;

        var vram = _bus.Vram;

        for (int y = 0; y < 160; y++)
        {
            int scrolledY = (y + _bus.Bg0VOffset) % mapHeightPixels;
            if (scrolledY < 0) scrolledY += mapHeightPixels;
            int tileY = scrolledY / 8;
            int tilePixelY = scrolledY & 7;

            for (int x = 0; x < 240; x++)
            {
                int scrolledX = (x + _bus.Bg0HOffset) % mapWidthPixels;
                if (scrolledX < 0) scrolledX += mapWidthPixels;
                int tileX = scrolledX / 8;
                int tilePixelX = scrolledX & 7;

                int mapIndex = ((tileY % mapHeightTiles) * mapWidthTiles) + (tileX % mapWidthTiles);
                int mapAddress = screenBase + mapIndex * 2;
                if (mapAddress + 1 >= vram.Length)
                {
                    continue;
                }

                ushort entry = (ushort)(vram[mapAddress] | (vram[mapAddress + 1] << 8));
                int tileIndex = entry & 0x03FF;
                bool hFlip = (entry & (1 << 10)) != 0;
                bool vFlip = (entry & (1 << 11)) != 0;
                int paletteBank = (entry >> 12) & 0xF;

                int tilePixelXEff = hFlip ? 7 - tilePixelX : tilePixelX;
                int tilePixelYEff = vFlip ? 7 - tilePixelY : tilePixelY;

                int tileSize = palette256 ? 64 : 32;
                int tileBase = charBase + tileIndex * tileSize;
                if (tileBase >= vram.Length)
                {
                    continue;
                }

                byte colorIndex;
                if (palette256)
                {
                    int offset = tileBase + tilePixelYEff * 8 + tilePixelXEff;
                    if (offset >= vram.Length)
                    {
                        continue;
                    }
                    colorIndex = vram[offset];
                }
                else
                {
                    int rowOffset = tilePixelYEff * 4;
                    int offset = tileBase + rowOffset + (tilePixelXEff >> 1);
                    if (offset >= vram.Length)
                    {
                        continue;
                    }
                    byte packed = vram[offset];
                    colorIndex = (byte)((tilePixelXEff & 1) == 0 ? (packed & 0x0F) : (packed >> 4));
                    colorIndex = (byte)(colorIndex + paletteBank * 16);
                }

                ushort color = _bus.ReadBgPaletteEntry(colorIndex);
                span[y * 240 + x] = color;
            }
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
