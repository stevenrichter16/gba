using Gba.Core.Cpu;
using Gba.Core.Memory;
using Xunit;

namespace Gba.Core.Tests;

public sealed class InstructionTests
{
    private static uint EncodeExtraTransfer(bool load, bool signed, bool half, bool writeBack, bool pre,
        bool add, bool immediate, int rn, int rd, int offsetOrRm)
    {
        uint opcode = 0;
        opcode |= 0b1110u << 28; // cond = AL
        opcode |= (pre ? 1u : 0u) << 24;
        opcode |= (add ? 1u : 0u) << 23;
        opcode |= (immediate ? 1u : 0u) << 22;
        opcode |= (writeBack ? 1u : 0u) << 21;
        opcode |= (load ? 1u : 0u) << 20;
        opcode |= (uint)(rn & 0xF) << 16;
        opcode |= (uint)(rd & 0xF) << 12;

        if (immediate)
        {
            uint imm = (uint)offsetOrRm & 0xFF;
            opcode |= (imm & 0xF0) << 4;
            opcode |= (imm & 0x0F);
        }
        else
        {
            opcode |= (uint)(offsetOrRm & 0xF);
        }

        opcode |= 1u << 7;
        opcode |= (signed ? 1u : 0u) << 6;
        opcode |= (half ? 1u : 0u) << 5;
        opcode |= 1u << 4;
        return opcode;
    }
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

    [Fact]
    public void ArmCore_RegisterSpecifiedLsl_ShiftsCorrectly()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;

        WriteWord(0xE3A00001, 0); // MOV R0, #1
        WriteWord(0xE3A01002, 4); // MOV R1, #2
        WriteWord(0xE3A02003, 8); // MOV R2, #3
        WriteWord(0xE0800211, 12); // ADD R0, R0, R1 LSL R2 (register shift)
        WriteWord(0xEAFFFFFE, 16); // loop

        cpu.State.Pc = codeBase;

        for (int i = 0; i < 4; i++)
        {
            cpu.StepInstruction();
        }

        Assert.Equal(17u, cpu.State.R[0]); // 1 + (2 << 3)

        void WriteWord(uint value, int offset) => bus.Write32(codeBase + (uint)offset, value);
    }

    [Fact]
    public void ArmCore_RegisterSpecifiedRrx_UsesCarryFlag()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;

        WriteWord(0xE3A01002, 0); // MOV R1, #2
        WriteWord(0xE1B00061, 4); // MOVS R0, R1 RRX (ROR #0 with flags)
        WriteWord(0xEAFFFFFE, 8);

        cpu.State.Pc = codeBase;
        cpu.State.SetFlag(CpuFlags.Carry, true);

        for (int i = 0; i < 2; i++)
        {
            cpu.StepInstruction();
        }

        Assert.Equal(0x80000001u, cpu.State.R[0]);
        Assert.False(cpu.State.GetFlag(CpuFlags.Carry)); // bit shifted out was 0

        void WriteWord(uint value, int offset) => bus.Write32(codeBase + (uint)offset, value);
    }

    [Fact]
    public void ArmCore_MultiplyAndAccumulate()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;

        WriteWord(0xE3A00005, 0); // MOV R0, #5
        WriteWord(0xE3A01003, 4); // MOV R1, #3
        WriteWord(0xE3A02002, 8); // MOV R2, #2
        WriteWord(0xE0203291, 12); // MLA R0, R1, R2, R3 (R3 default 0)
        WriteWord(0xEAFFFFFE, 16);

        cpu.State.Pc = codeBase;
        cpu.State.R[3] = 7;

        for (int i = 0; i < 5; i++)
        {
            cpu.StepInstruction();
        }

        Assert.Equal(13u, cpu.State.R[0]); // (3*2)+7

        void WriteWord(uint value, int offset) => bus.Write32(codeBase + (uint)offset, value);
    }

    [Fact]
    public void ArmCore_SignedLongMultiplyProduces64BitResult()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;

        WriteWord(0xE3A00002, 0); // MOV R0, #2
        WriteWord(0xE3E01000, 4); // MVN R1, #0 -> -1
        WriteWord(0xE0C32190, 8); // SMULL R3, R2, R1, R0
        WriteWord(0xEAFFFFFE, 12);

        cpu.State.Pc = codeBase;

        for (int i = 0; i < 4; i++)
        {
            cpu.StepInstruction();
        }

        ulong combined = ((ulong)cpu.State.R[3] << 32) | cpu.State.R[2];
        Assert.Equal(unchecked((ulong)(-2)), combined);

        void WriteWord(uint value, int offset) => bus.Write32(codeBase + (uint)offset, value);
    }

    [Fact]
    public void ArmCore_LoadHalfwordAndSignedByte()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;
        const uint dataBase = 0x02000000;

        bus.Write16(dataBase + 4, 0xABCD);
        bus.Write8(dataBase + 8, 0xF6);

        WriteWord(0xE1A00000, 0); // NOP
        WriteWord(EncodeExtraTransfer(load: true, signed: false, half: true,
            writeBack: false, pre: true, add: true, immediate: true, rn: 0, rd: 1, offsetOrRm: 4), 4);
        WriteWord(EncodeExtraTransfer(load: true, signed: true, half: false,
            writeBack: false, pre: true, add: true, immediate: true, rn: 0, rd: 2, offsetOrRm: 8), 8);
        WriteWord(0xEAFFFFFE, 12);

        cpu.State.Pc = codeBase;
        cpu.State.R[0] = dataBase;

        for (int i = 0; i < 4; i++)
        {
            cpu.StepInstruction();
        }

        Assert.Equal(0xABCDu, cpu.State.R[1]);
        Assert.Equal(unchecked((uint)(sbyte)0xF6), cpu.State.R[2]);

        void WriteWord(uint value, int offset) => bus.Write32(codeBase + (uint)offset, value);
    }

    [Fact]
    public void ArmCore_LoadSignedHalfwordWithRegisterOffset()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);
        const uint codeBase = 0x03000000;
        const uint dataBase = 0x02000000;

        bus.Write16(dataBase + 6, 0xFFF0);

        WriteWord(EncodeExtraTransfer(load: true, signed: true, half: true,
            writeBack: false, pre: true, add: true, immediate: false, rn: 0, rd: 4, offsetOrRm: 3), 0); // LDRSH R4, [R0, R3]
        WriteWord(0xEAFFFFFE, 4);

        cpu.State.Pc = codeBase;
        cpu.State.R[0] = dataBase;
        cpu.State.R[3] = 6;

        for (int i = 0; i < 2; i++)
        {
            cpu.StepInstruction();
        }

        Assert.Equal(unchecked((uint)(short)0xFFF0), cpu.State.R[4]);

        void WriteWord(uint value, int offset) => bus.Write32(codeBase + (uint)offset, value);
    }
}
