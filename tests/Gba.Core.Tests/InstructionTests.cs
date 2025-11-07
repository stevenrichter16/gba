using Gba.Core.Cpu;
using Gba.Core.Memory;
using Xunit;

namespace Gba.Core.Tests;

public sealed class InstructionTests
{
    [Fact]
    public void ArmCore_ExecutesDataProcessingAndStoresToVram()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;
        WriteWord(0xE3A00005, 0); // MOV R0, #5
        WriteWord(0xE3A01003, 4); // MOV R1, #3
        WriteWord(0xE0800001, 8); // ADD R0, R0, R1 => 8
        WriteWord(0xE5820000, 12); // STR R0, [R2]
        WriteWord(0xEAFFFFFE, 16); // Endless loop (branch to self)

        cpu.State.Pc = codeBase;
        cpu.State.R[15] = codeBase;
        cpu.State.R[2] = 0x03004000;

        // Execute MOV/MOV/ADD/STR
        for (int i = 0; i < 4; i++)
        {
            cpu.StepInstruction();
        }

        var stored = bus.Read32(0x03004000);
        Assert.Equal(8u, stored);

        void WriteWord(uint value, int offset) => bus.Write32(codeBase + (uint)offset, value);
    }

    [Fact]
    public void ThumbCore_ExecutesImmediateAndStore()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;
        WriteHalf(0x200A, 0); // MOVS R0, #10
        WriteHalf(0x3005, 2); // ADDS R0, #5 -> 15
        WriteHalf(0x6008, 4); // STR R0, [R1, #0]
        WriteHalf(0xE7FE, 6); // B . (halt)

        cpu.State.Thumb = true;
        cpu.State.Pc = codeBase;
        cpu.State.R[1] = 0x03005000;

        for (int i = 0; i < 3; i++)
        {
            cpu.StepInstruction();
        }

        var result = bus.Read32(0x03005000);
        Assert.Equal(15u, result);

        void WriteHalf(ushort value, int offset) => bus.Write16(codeBase + (uint)offset, value);

    }
}
