using System;
using Gba.Core.Abstractions;
using Gba.Core.Cpu;
using Gba.Core.Memory;
using Gba.Core.Ppu;

namespace Gba.Core.Emulation;

public sealed class Emulator
{
    private const int CyclesPerFrame = 280_896; // approx 16.7 MHz / 59.7 Hz
    private const int TotalScanlines = 228; // 160 visible + 68 vblank
    private const int VisibleScanlines = 160;
    private const int CyclesPerScanline = CyclesPerFrame / TotalScanlines; // ~1232 cycles

    private readonly uint[] _rgba = new uint[240 * 160];

    public Emulator(byte[] rom, EmulatorOptions? options = null)
    {
        options ??= new EmulatorOptions();
        Bus = new MemoryBus(rom);
        if (options.Bios is { Length: > 0 })
        {
            Bus.LoadBios(options.Bios);
        }

        Cpu = new Arm7Tdmi(Bus);
        Ppu = new SimplePpu(Bus);

        var entryPoint = options.SkipBios
            ? options.EntryPoint ?? 0x08000000u
            : 0x00000000u;

        Cpu.Reset(entryPoint, options.StartInThumb);
    }

    public MemoryBus Bus { get; }
    public Arm7Tdmi Cpu { get; }
    public SimplePpu Ppu { get; }

    public IInputSource? Input { get; set; }
    public IVideoSink? Video { get; set; }

    public void StepFrame()
    {
        Bus.InputSource = Input;
        Bus.LatchInput();

        for (int scanline = 0; scanline < TotalScanlines; scanline++)
        {
            // Update VCOUNT register
            Bus.SetVCount((ushort)scanline);

            // Update DISPSTAT flags
            ushort dispstat = Bus.DisplayStatus;

            // Set/clear VBLANK flag (bit 0)
            if (scanline >= VisibleScanlines)
            {
                dispstat |= 0x0001; // Set VBLANK flag
            }
            else
            {
                dispstat &= 0xFFFE; // Clear VBLANK flag
            }

            Bus.SetDisplayStatus(dispstat);

            // Trigger VBLANK interrupt when entering VBLANK period
            if (scanline == VisibleScanlines)
            {
                // Check if VBLANK interrupt is enabled in DISPSTAT (bit 3)
                if ((dispstat & 0x0008) != 0)
                {
                    Bus.RequestInterrupt(InterruptType.VBlank);
                }
            }

            // Execute CPU cycles for this scanline
            Cpu.StepCycles(CyclesPerScanline);
        }

        // Render the frame after all scanlines are complete
        Ppu.RenderFrame();
        Ppu.PresentTo(_rgba);
        Video?.PresentFrame(_rgba);
    }

}
