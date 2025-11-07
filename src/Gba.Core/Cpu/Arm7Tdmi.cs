using System;
using Gba.Core.Memory;

namespace Gba.Core.Cpu;

public sealed class Arm7Tdmi
{
    private readonly MemoryBus _bus;
    private readonly CpuState _state = new();

    public Arm7Tdmi(MemoryBus bus)
    {
        _bus = bus;
        Reset();
    }

    public CpuState State => _state;

    public void Reset(uint entryPoint = 0x08000000, bool thumb = false)
    {
        Array.Clear(_state.R);
        _state.Cpsr = 0x000000DFu; // System mode, IRQ/FIQ disabled
        _state.SwitchMode(CpuMode.System);
        _state.R[13] = 0x03007F00; // IWRAM top (SP)
        _state.R[14] = 0;

        // Set up stack pointers for IRQ and Supervisor modes
        _state.SwitchMode(CpuMode.IRQ);
        _state.R[13] = 0x03007FA0; // IRQ stack
        _state.SwitchMode(CpuMode.Supervisor);
        _state.R[13] = 0x03007FE0; // Supervisor stack
        _state.SwitchMode(CpuMode.System); // Back to system mode

        _state.Pc = AlignEntryPoint(entryPoint, thumb);
        _state.Thumb = thumb;
        _state.CycleCount = 0;
    }

    private static uint AlignEntryPoint(uint entryPoint, bool thumb)
    {
        return thumb ? entryPoint & ~1u : entryPoint & ~3u;
    }

    public void StepCycles(int cycles)
    {
        ulong target = _state.CycleCount + (ulong)cycles;
        while (_state.CycleCount < target)
        {
            StepInstruction();
        }
    }

    public void StepInstruction()
    {
        // Check for pending interrupts before executing instruction
        if (_bus.HasPendingInterrupt() && !_state.GetFlag(CpuFlags.IRQDisable))
        {
            HandleInterrupt();
        }

        if (_state.Thumb)
        {
            ExecuteThumb();
            _state.CycleCount += 1;
        }
        else
        {
            ExecuteArm();
            _state.CycleCount += 1;
        }
    }

    private void HandleInterrupt()
    {
        // Save current CPSR and PC before mode switch
        uint oldCpsr = _state.Cpsr;
        uint returnAddress = _state.Pc;

        // Switch to IRQ mode
        _state.SwitchMode(CpuMode.IRQ);

        // Save old CPSR to SPSR_irq
        _state.SetSpsr(oldCpsr);

        // Set return address in R14_irq
        _state.R[14] = returnAddress;

        // Set IRQ disable flag and switch to ARM mode
        _state.SetFlag(CpuFlags.IRQDisable, true);
        _state.Thumb = false;

        // Jump to IRQ vector
        _state.Pc = 0x00000018;

        // IRQ handling takes 3 cycles
        _state.CycleCount += 3;
    }

    private void ExecuteArm()
    {
        uint pc = _state.Pc & ~3u;
        uint opcode = _bus.Read32(pc);
        _state.Pc = pc + 4;

        if (!ConditionPassed(opcode >> 28))
        {
            return;
        }

        // Branch / BX
        if ((opcode & 0x0F000000) == 0x0A000000)
        {
            uint offset = opcode & 0x00FFFFFF;
            if ((offset & 0x00800000) != 0)
            {
                offset |= 0xFF000000;
            }
            offset <<= 2;
            if ((opcode & (1u << 24)) != 0)
            {
                _state.R[14] = _state.Pc;
            }
            _state.Pc = unchecked(_state.Pc + offset);
            return;
        }

        if ((opcode & 0x0FFFFFF0) == 0x012FFF10)
        {
            // BX
            int rm = (int)(opcode & 0xF);
            uint target = _state.R[rm];
            _state.Thumb = (target & 1) != 0;
            _state.Pc = target & 0xFFFFFFFEu;
            return;
        }

        uint opClass = (opcode >> 25) & 0x7;
        switch (opClass)
        {
            case 0b000:
            case 0b001:
                if ((opcode & 0x0FC000F0) == 0x00000090)
                {
                    ExecuteArmMultiply(opcode);
                }
                else if ((opcode & 0x0F8000F0) == 0x00800090)
                {
                    ExecuteArmMultiplyLong(opcode);
                }
                else if ((opcode & 0x0E000090) == 0x00000090)
                {
                    ExecuteArmExtraTransfer(opcode);
                }
                else
                {
                    ExecuteArmDataProcessing(opcode);
                }
                break;
            case 0b010:
            case 0b011:
                ExecuteArmSingleDataTransfer(opcode);
                break;
            default:
                // Unimplemented instructions simply no-op for now.
                break;
        }
    }

    private void ExecuteArmDataProcessing(uint opcode)
    {
        bool immediate = ((opcode >> 25) & 1) != 0;
        bool setFlags = ((opcode >> 20) & 1) != 0;
        uint op = (opcode >> 21) & 0xF;
        int rn = (int)((opcode >> 16) & 0xF);
        int rd = (int)((opcode >> 12) & 0xF);
        var (operand2, carryOut) = immediate ? DecodeImmediateOperand(opcode) : DecodeShiftOperand(opcode);

        uint rnVal = _state.R[rn];
        uint result;

        switch (op)
        {
            case 0x0: // AND
                result = rnVal & operand2;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            case 0x1: // EOR
                result = rnVal ^ operand2;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            case 0x2: // SUB
                result = rnVal - operand2;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetArithFlags(result, rnVal, operand2, subtraction: true);
                break;
            case 0x3: // RSB
                result = operand2 - rnVal;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetArithFlags(result, operand2, rnVal, subtraction: true);
                break;
            case 0x4: // ADD
                result = rnVal + operand2;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetArithFlags(result, rnVal, operand2, subtraction: false);
                break;
            case 0x5: // ADC
                uint carry = _state.GetFlag(CpuFlags.Carry) ? 1u : 0u;
                result = rnVal + operand2 + carry;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetAdcFlags(result, rnVal, operand2, carry);
                break;
            case 0x6: // SBC
                carry = _state.GetFlag(CpuFlags.Carry) ? 1u : 0u;
                result = rnVal - operand2 - (1u - carry);
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetSbcFlags(result, rnVal, operand2, carry);
                break;
            case 0x8: // TST
                result = rnVal & operand2;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            case 0x9: // TEQ
                result = rnVal ^ operand2;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            case 0xA: // CMP
                result = rnVal - operand2;
                if (setFlags) SetArithFlags(result, rnVal, operand2, subtraction: true);
                break;
            case 0xB: // CMN
                result = rnVal + operand2;
                if (setFlags) SetArithFlags(result, rnVal, operand2, subtraction: false);
                break;
            case 0xC: // ORR
                result = rnVal | operand2;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            case 0xD: // MOV
                result = operand2;
                if (rd != 15) _state.R[rd] = result;
                else _state.Pc = result;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            case 0xE: // BIC
                result = rnVal & ~operand2;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            case 0xF: // MVN
                result = ~operand2;
                if (rd != 15) _state.R[rd] = result;
                if (setFlags) SetLogicalFlags(result, carryOut);
                break;
            default:
                break;
        }
    }

    private void ExecuteArmSingleDataTransfer(uint opcode)
    {
        bool load = ((opcode >> 20) & 1) != 0;
        bool writeBack = ((opcode >> 21) & 1) != 0;
        bool byteTransfer = ((opcode >> 22) & 1) != 0;
        bool add = ((opcode >> 23) & 1) != 0;
        bool pre = ((opcode >> 24) & 1) != 0;
        bool immediate = ((opcode >> 25) & 1) == 0;
        int rn = (int)((opcode >> 16) & 0xF);
        int rd = (int)((opcode >> 12) & 0xF);
        uint offset;
        if (immediate)
        {
            offset = opcode & 0xFFF;
        }
        else
        {
            var (val, _) = DecodeShiftOperand(opcode);
            offset = val;
        }
        uint baseAddr = _state.R[rn];
        uint address = pre ? baseAddr : baseAddr;
        if (add)
        {
            address += offset;
        }
        else
        {
            address -= offset;
        }

        if (!pre)
        {
            baseAddr = address;
        }

        if (load)
        {
            uint value = byteTransfer ? _bus.Read8(address) : _bus.Read32(address);
            if (byteTransfer)
            {
                _state.R[rd] = value;
            }
            else
            {
                _state.R[rd] = value;
            }
        }
        else
        {
            uint value = _state.R[rd];
            if (byteTransfer)
            {
                _bus.Write8(address, (byte)value);
            }
            else
            {
                _bus.Write32(address, value);
            }
        }

        if (writeBack)
        {
            _state.R[rn] = address;
        }
    }

    private void ExecuteArmExtraTransfer(uint opcode)
    {
        bool pre = ((opcode >> 24) & 1) != 0;
        bool add = ((opcode >> 23) & 1) != 0;
        bool immediate = ((opcode >> 22) & 1) != 0;
        bool writeBack = ((opcode >> 21) & 1) != 0;
        bool load = ((opcode >> 20) & 1) != 0;
        bool signedTransfer = ((opcode >> 6) & 1) != 0;
        bool halfwordTransfer = ((opcode >> 5) & 1) != 0;

        int rn = (int)((opcode >> 16) & 0xF);
        int rd = (int)((opcode >> 12) & 0xF);

        uint offset;
        if (immediate)
        {
            offset = (uint)(((opcode >> 8) & 0xF) << 4 | (opcode & 0xF));
        }
        else
        {
            offset = _state.R[opcode & 0xF];
        }

        uint address = _state.R[rn];
        uint effective = pre ? (add ? address + offset : address - offset) : address;

        if (load)
        {
            if (!halfwordTransfer && !signedTransfer)
            {
                uint valueLo = _bus.Read32(effective);
                uint valueHi = _bus.Read32(effective + 4);
                _state.R[rd] = valueLo;
                _state.R[(rd + 1) & 0xF] = valueHi;
            }
            else if (halfwordTransfer && !signedTransfer)
            {
                ushort value = _bus.Read16(effective);
                _state.R[rd] = value;
            }
            else if (halfwordTransfer && signedTransfer)
            {
                short value = (short)_bus.Read16(effective);
                _state.R[rd] = (uint)value;
            }
            else
            {
                sbyte value = (sbyte)_bus.Read8(effective);
                _state.R[rd] = (uint)value;
            }
        }
        else
        {
            if (!halfwordTransfer && !signedTransfer)
            {
                _bus.Write32(effective, _state.R[rd]);
                _bus.Write32(effective + 4, _state.R[(rd + 1) & 0xF]);
            }
            else if (halfwordTransfer)
            {
                _bus.Write16(effective, (ushort)(_state.R[rd] & 0xFFFF));
            }
            else
            {
                _bus.Write8(effective, (byte)(_state.R[rd] & 0xFF));
            }
        }

        if (!pre)
        {
            effective = add ? effective + offset : effective - offset;
        }

        if (writeBack || !pre)
        {
            _state.R[rn] = effective;
        }
    }

    private void ExecuteArmMultiply(uint opcode)
    {
        bool accumulate = ((opcode >> 21) & 1) != 0;
        bool setFlags = ((opcode >> 20) & 1) != 0;
        int rd = (int)((opcode >> 16) & 0xF);
        int rn = (int)((opcode >> 12) & 0xF);
        int rs = (int)((opcode >> 8) & 0xF);
        int rm = (int)(opcode & 0xF);

        uint result = _state.R[rm] * _state.R[rs];
        if (accumulate)
        {
            result += _state.R[rn];
            _state.CycleCount += 1;
        }

        _state.R[rd] = result;

        if (setFlags)
        {
            _state.SetFlag(CpuFlags.Zero, result == 0);
            _state.SetFlag(CpuFlags.Negative, (result & 0x80000000) != 0);
        }

        _state.CycleCount += 2;
    }

    private void ExecuteArmMultiplyLong(uint opcode)
    {
        bool signedMul = ((opcode >> 22) & 1) != 0;
        bool accumulate = ((opcode >> 21) & 1) != 0;
        bool setFlags = ((opcode >> 20) & 1) != 0;
        int rdHi = (int)((opcode >> 16) & 0xF);
        int rdLo = (int)((opcode >> 12) & 0xF);
        int rs = (int)((opcode >> 8) & 0xF);
        int rm = (int)(opcode & 0xF);

        ulong product;
        if (signedMul)
        {
            long m = (long)(int)_state.R[rm];
            long s = (long)(int)_state.R[rs];
            product = (ulong)(m * s);
        }
        else
        {
            product = (ulong)_state.R[rm] * _state.R[rs];
        }

        if (accumulate)
        {
            ulong existing = ((ulong)_state.R[rdHi] << 32) | _state.R[rdLo];
            product += existing;
            _state.CycleCount += 1;
        }

        _state.R[rdLo] = (uint)product;
        _state.R[rdHi] = (uint)(product >> 32);

        if (setFlags)
        {
            _state.SetFlag(CpuFlags.Zero, product == 0);
            _state.SetFlag(CpuFlags.Negative, (_state.R[rdHi] & 0x80000000) != 0);
        }

        _state.CycleCount += 3;
    }

    private void ExecuteThumb()
    {
        uint pc = _state.Pc & ~1u;
        ushort opcode = _bus.Read16(pc);
        _state.Pc = pc + 2;

        // Decode instruction format based on opcode bits
        if ((opcode & 0xF800) == 0x1800)
        {
            // Format 2: add/subtract
            ExecuteThumbAddSubtract(opcode);
        }
        else if ((opcode & 0xE000) == 0x0000)
        {
            // Format 1: move shifted register
            ExecuteThumbShiftAddSub(opcode);
        }
        else if ((opcode & 0xE000) == 0x2000)
        {
            // Format 3: move/compare/add/subtract immediate
            ExecuteThumbImmediate(opcode);
        }
        else if ((opcode & 0xFC00) == 0x4000)
        {
            // Format 4: ALU operations
            ExecuteThumbAlu(opcode);
        }
        else if ((opcode & 0xFC00) == 0x4400)
        {
            // Format 5: Hi register operations/branch exchange
            ExecuteThumbHiRegBx(opcode);
        }
        else if ((opcode & 0xF800) == 0x4800)
        {
            // Format 6: PC-relative load
            ExecuteThumbPcRelativeLoad(opcode);
        }
        else if ((opcode & 0xF200) == 0x5000)
        {
            // Format 7: load/store with register offset
            ExecuteThumbLoadStoreRegOffset(opcode);
        }
        else if ((opcode & 0xF200) == 0x5200)
        {
            // Format 8: load/store sign-extended byte/halfword
            ExecuteThumbLoadStoreSignExtended(opcode);
        }
        else if ((opcode & 0xE000) == 0x6000)
        {
            // Format 9: load/store with immediate offset
            ExecuteThumbLoadStoreImmOffset(opcode);
        }
        else if ((opcode & 0xF000) == 0x8000)
        {
            // Format 10: load/store halfword
            ExecuteThumbLoadStoreHalfword(opcode);
        }
        else if ((opcode & 0xF000) == 0x9000)
        {
            // Format 11: SP-relative load/store
            ExecuteThumbSpRelativeLoadStore(opcode);
        }
        else if ((opcode & 0xF000) == 0xA000)
        {
            // Format 12: load address
            ExecuteThumbLoadAddress(opcode);
        }
        else if ((opcode & 0xFF00) == 0xB000)
        {
            // Format 13: add offset to stack pointer
            ExecuteThumbAddOffsetToSp(opcode);
        }
        else if ((opcode & 0xF600) == 0xB400)
        {
            // Format 14: push/pop registers
            ExecuteThumbPushPop(opcode);
        }
        else if ((opcode & 0xF000) == 0xC000)
        {
            // Format 15: multiple load/store
            ExecuteThumbMultipleLoadStore(opcode);
        }
        else if ((opcode & 0xFF00) == 0xDF00)
        {
            // Format 17: software interrupt
            ExecuteThumbSwi(opcode);
        }
        else if ((opcode & 0xF000) == 0xD000)
        {
            // Format 16: conditional branch
            ExecuteThumbConditionalBranch(opcode);
        }
        else if ((opcode & 0xF800) == 0xE000)
        {
            // Format 18: unconditional branch
            ExecuteThumbBranch(opcode);
        }
        else if ((opcode & 0xF000) == 0xF000)
        {
            // Format 19: long branch with link
            ExecuteThumbLongBranchLink(opcode);
        }
    }

    private void ExecuteThumbShiftAddSub(ushort opcode)
    {
        int op = (opcode >> 11) & 0x3;
        int offset = (opcode >> 6) & 0x1F;
        int rs = (opcode >> 3) & 0x7;
        int rd = opcode & 0x7;
        uint value = _state.R[rs];
        uint result;
        switch (op)
        {
            case 0: // LSL
                var (lsl, carry) = ShiftLeft(value, offset == 0 ? 32 : offset);
                result = lsl;
                _state.R[rd] = result;
                _state.SetFlag(CpuFlags.Carry, carry);
                SetLogicalFlags(result, carry);
                break;
            case 1: // LSR
                var (lsr, carryR) = ShiftRightLogical(value, offset == 0 ? 32 : offset);
                result = lsr;
                _state.R[rd] = result;
                SetLogicalFlags(result, carryR);
                break;
            case 2: // ASR
                var (asr, carryA) = ShiftRightArithmetic(value, offset == 0 ? 32 : offset);
                result = asr;
                _state.R[rd] = result;
                SetLogicalFlags(result, carryA);
                break;
            case 3: // ADD/SUB register
                bool sub = ((opcode >> 9) & 1) != 0;
                int rn = (opcode >> 6) & 0x7;
                uint rnVal = _state.R[rn];
                if (sub)
                {
                    result = rnVal - value;
                    _state.R[rd] = result;
                    SetArithFlags(result, rnVal, value, subtraction: true);
                }
                else
                {
                    result = rnVal + value;
                    _state.R[rd] = result;
                    SetArithFlags(result, rnVal, value, subtraction: false);
                }
                break;
        }
    }

    private void ExecuteThumbImmediate(ushort opcode)
    {
        int op = (opcode >> 11) & 0x3;
        int rd = (opcode >> 8) & 0x7;
        uint imm8 = (uint)(opcode & 0xFF);
        uint result;
        switch (op)
        {
            case 0: // MOV
                result = imm8;
                _state.R[rd] = result;
                SetLogicalFlags(result, carry: false);
                break;
            case 1: // CMP
                uint original = _state.R[rd];
                result = original - imm8;
                SetArithFlags(result, original, imm8, subtraction: true);
                break;
            case 2: // ADD
                original = _state.R[rd];
                result = original + imm8;
                _state.R[rd] = result;
                SetArithFlags(result, original, imm8, subtraction: false);
                break;
            case 3: // SUB
                original = _state.R[rd];
                result = original - imm8;
                _state.R[rd] = result;
                SetArithFlags(result, original, imm8, subtraction: true);
                break;
        }
    }

    private void ExecuteThumbAddSubtract(ushort opcode)
    {
        bool immediate = ((opcode >> 10) & 1) != 0;
        bool subtract = ((opcode >> 9) & 1) != 0;
        int rn = (opcode >> 6) & 0x7;
        int rs = (opcode >> 3) & 0x7;
        int rd = opcode & 0x7;

        uint operand = immediate ? (uint)rn : _state.R[rn];
        uint rsVal = _state.R[rs];
        uint result;

        if (subtract)
        {
            result = rsVal - operand;
            _state.R[rd] = result;
            SetArithFlags(result, rsVal, operand, subtraction: true);
        }
        else
        {
            result = rsVal + operand;
            _state.R[rd] = result;
            SetArithFlags(result, rsVal, operand, subtraction: false);
        }
    }

    private void ExecuteThumbAlu(ushort opcode)
    {
        int op = (opcode >> 6) & 0xF;
        int rs = (opcode >> 3) & 0x7;
        int rd = opcode & 0x7;
        uint rdVal = _state.R[rd];
        uint rsVal = _state.R[rs];
        uint result;

        switch (op)
        {
            case 0x0: // AND
                result = rdVal & rsVal;
                _state.R[rd] = result;
                SetLogicalFlags(result, _state.GetFlag(CpuFlags.Carry));
                break;
            case 0x1: // EOR
                result = rdVal ^ rsVal;
                _state.R[rd] = result;
                SetLogicalFlags(result, _state.GetFlag(CpuFlags.Carry));
                break;
            case 0x2: // LSL
                var (lsl, carryLsl) = ShiftLeft(rdVal, (int)(rsVal & 0xFF));
                _state.R[rd] = lsl;
                SetLogicalFlags(lsl, carryLsl);
                break;
            case 0x3: // LSR
                var (lsr, carryLsr) = ShiftRightLogical(rdVal, (int)(rsVal & 0xFF));
                _state.R[rd] = lsr;
                SetLogicalFlags(lsr, carryLsr);
                break;
            case 0x4: // ASR
                var (asr, carryAsr) = ShiftRightArithmetic(rdVal, (int)(rsVal & 0xFF));
                _state.R[rd] = asr;
                SetLogicalFlags(asr, carryAsr);
                break;
            case 0x5: // ADC
                uint carry = _state.GetFlag(CpuFlags.Carry) ? 1u : 0u;
                result = rdVal + rsVal + carry;
                _state.R[rd] = result;
                SetAdcFlags(result, rdVal, rsVal, carry);
                break;
            case 0x6: // SBC
                carry = _state.GetFlag(CpuFlags.Carry) ? 1u : 0u;
                result = rdVal - rsVal - (1u - carry);
                _state.R[rd] = result;
                SetSbcFlags(result, rdVal, rsVal, carry);
                break;
            case 0x7: // ROR
                uint rorAmount = rsVal & 0xFF;
                if (rorAmount == 0)
                {
                    SetLogicalFlags(rdVal, _state.GetFlag(CpuFlags.Carry));
                }
                else
                {
                    var (ror, carryRor) = RotateRight(rdVal, (int)(rorAmount & 0x1F));
                    _state.R[rd] = ror;
                    SetLogicalFlags(ror, carryRor);
                }
                break;
            case 0x8: // TST
                result = rdVal & rsVal;
                SetLogicalFlags(result, _state.GetFlag(CpuFlags.Carry));
                break;
            case 0x9: // NEG
                result = 0 - rsVal;
                _state.R[rd] = result;
                SetArithFlags(result, 0, rsVal, subtraction: true);
                break;
            case 0xA: // CMP
                result = rdVal - rsVal;
                SetArithFlags(result, rdVal, rsVal, subtraction: true);
                break;
            case 0xB: // CMN
                result = rdVal + rsVal;
                SetArithFlags(result, rdVal, rsVal, subtraction: false);
                break;
            case 0xC: // ORR
                result = rdVal | rsVal;
                _state.R[rd] = result;
                SetLogicalFlags(result, _state.GetFlag(CpuFlags.Carry));
                break;
            case 0xD: // MUL
                result = rdVal * rsVal;
                _state.R[rd] = result;
                SetLogicalFlags(result, false);
                _state.CycleCount += 3; // Multiply takes extra cycles
                break;
            case 0xE: // BIC
                result = rdVal & ~rsVal;
                _state.R[rd] = result;
                SetLogicalFlags(result, _state.GetFlag(CpuFlags.Carry));
                break;
            case 0xF: // MVN
                result = ~rsVal;
                _state.R[rd] = result;
                SetLogicalFlags(result, _state.GetFlag(CpuFlags.Carry));
                break;
        }
    }

    private void ExecuteThumbHiRegBx(ushort opcode)
    {
        int op = (opcode >> 8) & 0x3;
        bool h1 = ((opcode >> 7) & 1) != 0;
        bool h2 = ((opcode >> 6) & 1) != 0;
        int rs = ((opcode >> 3) & 0x7) | (h2 ? 8 : 0);
        int rd = (opcode & 0x7) | (h1 ? 8 : 0);

        switch (op)
        {
            case 0: // ADD
                _state.R[rd] += _state.R[rs];
                if (rd == 15)
                {
                    _state.Pc &= ~1u; // Align PC
                }
                break;
            case 1: // CMP
                uint result = _state.R[rd] - _state.R[rs];
                SetArithFlags(result, _state.R[rd], _state.R[rs], subtraction: true);
                break;
            case 2: // MOV
                _state.R[rd] = _state.R[rs];
                if (rd == 15)
                {
                    _state.Pc &= ~1u; // Align PC
                }
                break;
            case 3: // BX
                uint target = _state.R[rs];
                _state.Thumb = (target & 1) != 0;
                _state.Pc = target & 0xFFFFFFFEu;
                break;
        }
    }

    private void ExecuteThumbPcRelativeLoad(ushort opcode)
    {
        int rd = (opcode >> 8) & 0x7;
        uint imm = (uint)((opcode & 0xFF) << 2);
        uint addr = (_state.Pc & ~3u) + imm;
        _state.R[rd] = _bus.Read32(addr);
    }

    private void ExecuteThumbLoadStoreRegOffset(ushort opcode)
    {
        int ro = (opcode >> 6) & 0x7;
        int rb = (opcode >> 3) & 0x7;
        int rd = opcode & 0x7;
        uint address = _state.R[rb] + _state.R[ro];

        if ((opcode & 0x0800) != 0) // Load
        {
            if ((opcode & 0x0400) != 0) // Byte
            {
                _state.R[rd] = _bus.Read8(address);
            }
            else // Word
            {
                _state.R[rd] = _bus.Read32(address);
            }
        }
        else // Store
        {
            if ((opcode & 0x0400) != 0) // Byte
            {
                _bus.Write8(address, (byte)_state.R[rd]);
            }
            else // Word
            {
                _bus.Write32(address, _state.R[rd]);
            }
        }
    }

    private void ExecuteThumbLoadStoreSignExtended(ushort opcode)
    {
        int ro = (opcode >> 6) & 0x7;
        int rb = (opcode >> 3) & 0x7;
        int rd = opcode & 0x7;
        uint address = _state.R[rb] + _state.R[ro];

        if ((opcode & 0x0800) != 0) // Load
        {
            if ((opcode & 0x0400) != 0) // Sign-extended byte
            {
                sbyte value = (sbyte)_bus.Read8(address);
                _state.R[rd] = (uint)(int)value;
            }
            else // Sign-extended halfword
            {
                short value = (short)_bus.Read16(address);
                _state.R[rd] = (uint)(int)value;
            }
        }
        else // Store halfword
        {
            _bus.Write16(address, (ushort)_state.R[rd]);
        }
    }

    private void ExecuteThumbLoadStoreImmOffset(ushort opcode)
    {
        uint offset = (uint)((opcode >> 6) & 0x1F);
        int rb = (opcode >> 3) & 0x7;
        int rd = opcode & 0x7;

        bool load = ((opcode >> 11) & 1) != 0;
        bool byteTransfer = ((opcode >> 12) & 1) != 0;

        if (byteTransfer)
        {
            uint address = _state.R[rb] + offset;
            if (load)
            {
                _state.R[rd] = _bus.Read8(address);
            }
            else
            {
                _bus.Write8(address, (byte)_state.R[rd]);
            }
        }
        else
        {
            uint address = _state.R[rb] + (offset << 2);
            if (load)
            {
                _state.R[rd] = _bus.Read32(address);
            }
            else
            {
                _bus.Write32(address, _state.R[rd]);
            }
        }
    }

    private void ExecuteThumbLoadStoreHalfword(ushort opcode)
    {
        uint offset = (uint)(((opcode >> 6) & 0x1F) << 1);
        int rb = (opcode >> 3) & 0x7;
        int rd = opcode & 0x7;
        uint address = _state.R[rb] + offset;

        if ((opcode & 0x0800) != 0) // Load
        {
            _state.R[rd] = _bus.Read16(address);
        }
        else // Store
        {
            _bus.Write16(address, (ushort)_state.R[rd]);
        }
    }

    private void ExecuteThumbSpRelativeLoadStore(ushort opcode)
    {
        int rd = (opcode >> 8) & 0x7;
        uint offset = (uint)((opcode & 0xFF) << 2);
        uint address = _state.R[13] + offset; // SP is R13

        if ((opcode & 0x0800) != 0) // Load
        {
            _state.R[rd] = _bus.Read32(address);
        }
        else // Store
        {
            _bus.Write32(address, _state.R[rd]);
        }
    }

    private void ExecuteThumbLoadAddress(ushort opcode)
    {
        int rd = (opcode >> 8) & 0x7;
        uint offset = (uint)((opcode & 0xFF) << 2);

        if ((opcode & 0x0800) != 0) // SP
        {
            _state.R[rd] = _state.R[13] + offset;
        }
        else // PC
        {
            _state.R[rd] = (_state.Pc & ~3u) + offset;
        }
    }

    private void ExecuteThumbAddOffsetToSp(ushort opcode)
    {
        uint offset = (uint)((opcode & 0x7F) << 2);
        bool subtract = ((opcode >> 7) & 1) != 0;

        if (subtract)
        {
            _state.R[13] -= offset;
        }
        else
        {
            _state.R[13] += offset;
        }
    }

    private void ExecuteThumbPushPop(ushort opcode)
    {
        bool load = ((opcode >> 11) & 1) != 0;
        bool pcLr = ((opcode >> 8) & 1) != 0;
        int rlist = opcode & 0xFF;

        if (load) // POP
        {
            for (int i = 0; i < 8; i++)
            {
                if ((rlist & (1 << i)) != 0)
                {
                    _state.R[i] = _bus.Read32(_state.R[13]);
                    _state.R[13] += 4;
                }
            }
            if (pcLr)
            {
                _state.Pc = _bus.Read32(_state.R[13]);
                _state.R[13] += 4;
            }
        }
        else // PUSH
        {
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((rlist & (1 << i)) != 0) count++;
            }
            if (pcLr) count++;

            // Push in reverse order
            if (pcLr)
            {
                _state.R[13] -= 4;
                _bus.Write32(_state.R[13], _state.R[14]); // Push LR
            }
            for (int i = 7; i >= 0; i--)
            {
                if ((rlist & (1 << i)) != 0)
                {
                    _state.R[13] -= 4;
                    _bus.Write32(_state.R[13], _state.R[i]);
                }
            }
        }
    }

    private void ExecuteThumbMultipleLoadStore(ushort opcode)
    {
        bool load = ((opcode >> 11) & 1) != 0;
        int rb = (opcode >> 8) & 0x7;
        int rlist = opcode & 0xFF;
        uint address = _state.R[rb];

        if (load) // LDMIA
        {
            for (int i = 0; i < 8; i++)
            {
                if ((rlist & (1 << i)) != 0)
                {
                    _state.R[i] = _bus.Read32(address);
                    address += 4;
                }
            }
            _state.R[rb] = address; // Writeback
        }
        else // STMIA
        {
            for (int i = 0; i < 8; i++)
            {
                if ((rlist & (1 << i)) != 0)
                {
                    _bus.Write32(address, _state.R[i]);
                    address += 4;
                }
            }
            _state.R[rb] = address; // Writeback
        }
    }

    private void ExecuteThumbConditionalBranch(ushort opcode)
    {
        int cond = (opcode >> 8) & 0xF;
        int offset = (sbyte)(opcode & 0xFF);
        offset <<= 1;

        if (ConditionPassed((uint)cond))
        {
            _state.Pc = unchecked(_state.Pc + (uint)offset);
        }
    }

    private void ExecuteThumbSwi(ushort opcode)
    {
        // Save current CPSR and PC before mode switch
        uint oldCpsr = _state.Cpsr;
        uint returnAddress = _state.Pc;

        // Switch to Supervisor mode
        _state.SwitchMode(CpuMode.Supervisor);

        // Save old CPSR to SPSR_svc
        _state.SetSpsr(oldCpsr);

        // Set return address in R14_svc
        _state.R[14] = returnAddress;

        // Set IRQ disable flag and switch to ARM mode
        _state.SetFlag(CpuFlags.IRQDisable, true);
        _state.Thumb = false;

        // Jump to SWI vector
        _state.Pc = 0x00000008;
    }

    private void ExecuteThumbBranch(ushort opcode)
    {
        int offset = opcode & 0x07FF;
        if ((offset & 0x0400) != 0)
        {
            offset |= unchecked((int)0xFFFFF800);
        }
        offset <<= 1;
        _state.Pc = unchecked(_state.Pc + (uint)offset);
    }

    private void ExecuteThumbLongBranchLink(ushort opcode)
    {
        bool secondInstruction = ((opcode >> 11) & 1) != 0;
        uint offset = (uint)(opcode & 0x7FF);

        if (!secondInstruction)
        {
            // First instruction: LR = PC + (offset << 12)
            int signedOffset = (int)offset;
            if ((offset & 0x400) != 0)
            {
                signedOffset |= unchecked((int)0xFFFFF800);
            }
            _state.R[14] = unchecked(_state.Pc + (uint)(signedOffset << 12));
        }
        else
        {
            // Second instruction: PC = LR + (offset << 1), LR = PC + 2 | 1
            uint nextInst = _state.Pc - 2;
            _state.Pc = _state.R[14] + (offset << 1);
            _state.R[14] = nextInst | 1; // Set Thumb bit
        }
    }

    private (uint value, bool carry) DecodeImmediateOperand(uint opcode)
    {
        uint imm8 = opcode & 0xFF;
        int rotate = (int)((opcode >> 8) & 0xF) * 2;
        if (rotate == 0)
        {
            return (imm8, _state.GetFlag(CpuFlags.Carry));
        }
        uint value = (imm8 >> rotate) | (imm8 << (32 - rotate));
        return (value, (value & 0x80000000) != 0);
    }

    private (uint value, bool carry) DecodeShiftOperand(uint opcode)
    {
        int rm = (int)(opcode & 0xF);
        uint value = _state.R[rm];
        bool registerShift = ((opcode >> 4) & 1) != 0;
        int shiftType = (int)((opcode >> 5) & 0x3);

        if (!registerShift)
        {
            int shiftImm = (int)((opcode >> 7) & 0x1F);
            if (shiftImm == 0)
            {
                return shiftType switch
                {
                    0 => (value, _state.GetFlag(CpuFlags.Carry)),
                    1 => ShiftRightLogical(value, 32),
                    2 => ShiftRightArithmetic(value, 32),
                    3 => RotateRightExtend(value),
                    _ => (value, _state.GetFlag(CpuFlags.Carry))
                };
            }

            return shiftType switch
            {
                0 => ShiftLeft(value, shiftImm),
                1 => ShiftRightLogical(value, shiftImm),
                2 => ShiftRightArithmetic(value, shiftImm),
                3 => RotateRight(value, shiftImm),
                _ => (value, _state.GetFlag(CpuFlags.Carry))
            };
        }
        else
        {
            int rs = (int)((opcode >> 8) & 0xF);
            uint shiftAmount = _state.R[rs] & 0xFF;
            if (shiftAmount == 0)
            {
                return (value, _state.GetFlag(CpuFlags.Carry));
            }

            return shiftType switch
            {
                0 => RegisterShiftLeft(value, shiftAmount),
                1 => RegisterShiftRightLogical(value, shiftAmount),
                2 => RegisterShiftRightArithmetic(value, shiftAmount),
                3 => RegisterRotateRight(value, shiftAmount),
                _ => (value, _state.GetFlag(CpuFlags.Carry))
            };
        }
    }

    private static (uint value, bool carry) ShiftLeft(uint value, int amount)
    {
        amount &= 0x1F;
        if (amount == 0) return (value, false);
        bool carry = (value & (1u << (32 - amount))) != 0;
        return (value << amount, carry);
    }

    private static (uint value, bool carry) ShiftRightLogical(uint value, int amount)
    {
        amount &= 0x1F;
        if (amount == 0) return (value, (value & 0x80000000) != 0);
        bool carry = ((value >> (amount - 1)) & 1) != 0;
        return (value >> amount, carry);
    }

    private static (uint value, bool carry) ShiftRightArithmetic(uint value, int amount)
    {
        amount &= 0x1F;
        if (amount == 0) amount = 32;
        bool carry = ((value >> (amount - 1)) & 1) != 0;
        uint result = (uint)((int)value >> amount);
        return (result, carry);
    }

    private static (uint value, bool carry) RotateRight(uint value, int amount)
    {
        amount &= 0x1F;
        if (amount == 0) amount = 1;
        uint result = (value >> amount) | (value << (32 - amount));
        bool carry = ((result >> 31) & 1) != 0;
        return (result, carry);
    }

    private static (uint value, bool carry) RegisterShiftLeft(uint value, uint amount)
    {
        if (amount < 32)
        {
            bool carry = (value & (1u << (int)(32 - amount))) != 0;
            return (value << (int)amount, carry);
        }
        if (amount == 32)
        {
            return (0u, (value & 1u) != 0);
        }
        return (0u, false);
    }

    private static (uint value, bool carry) RegisterShiftRightLogical(uint value, uint amount)
    {
        if (amount < 32)
        {
            bool carry = ((value >> (int)(amount - 1)) & 1) != 0;
            return (value >> (int)amount, carry);
        }
        if (amount == 32)
        {
            bool carry = (value & 0x80000000) != 0;
            return (0u, carry);
        }
        return (0u, false);
    }

    private static (uint value, bool carry) RegisterShiftRightArithmetic(uint value, uint amount)
    {
        if (amount < 32)
        {
            bool carry = ((value >> (int)(amount - 1)) & 1) != 0;
            uint result = (uint)((int)value >> (int)amount);
            return (result, carry);
        }
        bool carryOut = (value & 0x80000000) != 0;
        return (carryOut ? 0xFFFFFFFFu : 0u, carryOut);
    }

    private (uint value, bool carry) RegisterRotateRight(uint value, uint amount)
    {
        uint rot = amount & 0x1F;
        if (rot == 0)
        {
            // RRX: rotate with carry
            bool carry = (value & 1u) != 0;
            uint result = (_state.GetFlag(CpuFlags.Carry) ? 0x80000000u : 0u) | (value >> 1);
            return (result, carry);
        }
        return RotateRight(value, (int)rot);
    }

    private (uint value, bool carry) RotateRightExtend(uint value)
    {
        bool carry = (value & 1u) != 0;
        uint result = (_state.GetFlag(CpuFlags.Carry) ? 0x80000000u : 0u) | (value >> 1);
        return (result, carry);
    }

    private bool ConditionPassed(uint condition)
    {
        bool n = _state.GetFlag(CpuFlags.Negative);
        bool z = _state.GetFlag(CpuFlags.Zero);
        bool c = _state.GetFlag(CpuFlags.Carry);
        bool v = _state.GetFlag(CpuFlags.Overflow);
        return condition switch
        {
            0x0 => z,
            0x1 => !z,
            0x2 => c,
            0x3 => !c,
            0x4 => n,
            0x5 => !n,
            0x6 => v,
            0x7 => !v,
            0x8 => c && !z,
            0x9 => !c || z,
            0xA => n == v,
            0xB => n != v,
            0xC => !z && (n == v),
            0xD => z || (n != v),
            0xE => true,
            _ => false
        };
    }

    private void SetLogicalFlags(uint result, bool carry)
    {
        _state.SetFlag(CpuFlags.Zero, result == 0);
        _state.SetFlag(CpuFlags.Negative, (result & 0x80000000) != 0);
        _state.SetFlag(CpuFlags.Carry, carry);
    }

    private void SetArithFlags(uint result, uint left, uint right, bool subtraction)
    {
        _state.SetFlag(CpuFlags.Zero, result == 0);
        _state.SetFlag(CpuFlags.Negative, (result & 0x80000000) != 0);
        if (subtraction)
        {
            _state.SetFlag(CpuFlags.Carry, left >= right);
            bool overflow = ((left ^ right) & (left ^ result) & 0x80000000) != 0;
            _state.SetFlag(CpuFlags.Overflow, overflow);
        }
        else
        {
            ulong wide = (ulong)left + right;
            _state.SetFlag(CpuFlags.Carry, (wide >> 32) != 0);
            bool overflow = ((~(left ^ right) & (left ^ result)) & 0x80000000) != 0;
            _state.SetFlag(CpuFlags.Overflow, overflow);
        }
    }

    private void SetAdcFlags(uint result, uint left, uint right, uint carryIn)
    {
        _state.SetFlag(CpuFlags.Zero, result == 0);
        _state.SetFlag(CpuFlags.Negative, (result & 0x80000000) != 0);
        ulong wide = (ulong)left + right + carryIn;
        _state.SetFlag(CpuFlags.Carry, (wide >> 32) != 0);
        bool overflow = ((~(left ^ right) & (left ^ result)) & 0x80000000) != 0;
        _state.SetFlag(CpuFlags.Overflow, overflow);
    }

    private void SetSbcFlags(uint result, uint left, uint right, uint carryIn)
    {
        _state.SetFlag(CpuFlags.Zero, result == 0);
        _state.SetFlag(CpuFlags.Negative, (result & 0x80000000) != 0);
        long wide = (long)left - right - (1 - carryIn);
        _state.SetFlag(CpuFlags.Carry, wide >= 0);
        bool overflow = (((left ^ right) & (left ^ (uint)wide)) & 0x80000000) != 0;
        _state.SetFlag(CpuFlags.Overflow, overflow);
    }
}
