using System;
using Gba.Core.Abstractions;
using Gba.Core.Cpu;
using Gba.Core.Memory;
using Gba.Core.Ppu;

namespace Gba.Core.Emulation;

public sealed class Emulator
{
    private const int CyclesPerFrame = 280_896; // approx 16.7 MHz / 59.7 Hz

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
        Cpu.StepCycles(CyclesPerFrame);
        Ppu.RenderFrame();
        Ppu.PresentTo(_rgba);
        Video?.PresentFrame(_rgba);
    }

}
