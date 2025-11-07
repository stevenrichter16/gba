using Gba.Core.Cpu;
using Gba.Core.Memory;
using Gba.Core.Emulation;
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

    [Fact]
    public void InterruptSystem_VBlankInterruptTriggersIrqHandler()
    {
        var bus = new MemoryBus(Array.Empty<byte>());
        var cpu = new Arm7Tdmi(bus);

        // Set up IRQ vector at 0x00000018 to set a flag in memory
        const uint irqVector = 0x00000018;
        const uint flagAddress = 0x03000100;

        // IRQ handler: Write 0x12345678 to flagAddress, then return
        // MOV R0, #0x12000000
        bus.Write32(irqVector + 0, 0xE3A00612); // MOV R0, #0x12000000 (using rotated immediate)
        // ORR R0, R0, #0x340000
        bus.Write32(irqVector + 4, 0xE3800D34); // ORR R0, R0, #0x340000
        // ORR R0, R0, #0x5000
        bus.Write32(irqVector + 8, 0xE3800A05); // ORR R0, R0, #0x5000
        // ORR R0, R0, #0x678
        bus.Write32(irqVector + 12, 0xE3800C67); // ORR R0, R0, #0x678
        bus.Write32(irqVector + 16, 0xE3800078); // ORR R0, R0, #0x78
        // MOV R1, #flagAddress
        bus.Write32(irqVector + 20, 0xE3A01503); // MOV R1, #0x03000000 | (1 << 8)
        // STR R0, [R1, #0x100]
        bus.Write32(irqVector + 24, 0xE5810100); // STR R0, [R1, #0x100]
        // Acknowledge interrupt: Write to IF register
        bus.Write32(irqVector + 28, 0xE3A02000); // MOV R2, #0
        bus.Write32(irqVector + 32, 0xE3A03004); // MOV R3, #4 (base address for I/O)
        bus.Write32(irqVector + 36, 0xE3833C02); // ORR R3, R3, #0x200
        bus.Write32(irqVector + 40, 0xE3A02001); // MOV R2, #1 (VBlank bit)
        bus.Write32(irqVector + 44, 0xE1C320B2); // STRH R2, [R3, #2] (write to IF at 0x04000202)
        // Return from interrupt: SUBS PC, R14, #4
        bus.Write32(irqVector + 48, 0xE25EF004); // SUBS PC, R14, #4

        // Main code: Enable interrupts and loop
        const uint mainCode = 0x03000000;
        // Enable VBLANK interrupt in DISPSTAT
        bus.Write32(mainCode + 0, 0xE3A00008); // MOV R0, #8 (VBLANK IRQ enable)
        bus.Write32(mainCode + 4, 0xE3A01004); // MOV R1, #4 (I/O base)
        bus.Write32(mainCode + 8, 0xE1C100B4); // STRH R0, [R1, #4] (write to DISPSTAT)
        // Enable VBLANK interrupt in IE
        bus.Write32(mainCode + 12, 0xE3A00001); // MOV R0, #1 (VBLANK bit)
        bus.Write32(mainCode + 16, 0xE3A01004); // MOV R1, #4
        bus.Write32(mainCode + 20, 0xE3811C02); // ORR R1, R1, #0x200
        bus.Write32(mainCode + 24, 0xE1C100B0); // STRH R0, [R1] (write to IE at 0x04000200)
        // Set IME (Interrupt Master Enable)
        bus.Write32(mainCode + 28, 0xE3A00001); // MOV R0, #1
        bus.Write32(mainCode + 32, 0xE3A01004); // MOV R1, #4
        bus.Write32(mainCode + 36, 0xE3811C02); // ORR R1, R1, #0x200
        bus.Write32(mainCode + 40, 0xE5810008); // STR R0, [R1, #8] (write to IME at 0x04000208)
        // Clear IRQ disable flag in CPSR to allow interrupts
        bus.Write32(mainCode + 44, 0xE321F0DF); // MSR CPSR_c, #0xDF (System mode, interrupts enabled)
        // Infinite loop
        bus.Write32(mainCode + 48, 0xEAFFFFFE); // B . (loop forever)

        cpu.State.Pc = mainCode;

        // Execute setup code (12 instructions to set up interrupts)
        for (int i = 0; i < 12; i++)
        {
            cpu.StepInstruction();
        }

        // Verify interrupts are enabled
        Assert.True(bus.InterruptMasterEnable);
        Assert.Equal(1, bus.InterruptEnable & 1); // VBLANK enabled

        // Manually trigger VBLANK interrupt
        bus.RequestInterrupt(InterruptType.VBlank);

        // Execute one more instruction - this should trigger the interrupt handler
        cpu.StepInstruction();

        // Verify we're now in IRQ mode
        Assert.Equal(CpuMode.IRQ, cpu.State.Mode);
        Assert.Equal(irqVector, cpu.State.Pc & ~3u);

        // Execute the IRQ handler (13 instructions)
        for (int i = 0; i < 13; i++)
        {
            cpu.StepInstruction();
        }

        // Verify the flag was set by the IRQ handler
        uint flagValue = bus.Read32(flagAddress);
        Assert.Equal(0x12345678u, flagValue);

        // Verify we've returned from the interrupt
        Assert.NotEqual(CpuMode.IRQ, cpu.State.Mode);
    }
}
