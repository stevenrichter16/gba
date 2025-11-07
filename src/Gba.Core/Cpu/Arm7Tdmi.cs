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
        _state.Cpsr = 0x0000001F;
        _state.R[13] = 0x03007F00; // IWRAM top
        _state.R[14] = 0;
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
                ExecuteArmDataProcessing(opcode);
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

    private void ExecuteThumb()
    {
        uint pc = _state.Pc & ~1u;
        ushort opcode = _bus.Read16(pc);
        _state.Pc = pc + 2;

        switch (opcode >> 13)
        {
            case 0b000: // shift/move/add/sub
                ExecuteThumbShiftAddSub(opcode);
                break;
            case 0b001: // immediate add/sub/mov
                ExecuteThumbImmediate(opcode);
                break;
            case 0b010:
            case 0b011:
                ExecuteThumbLoadStore(opcode);
                break;
            case 0b111 when ((opcode & 0xF800) == 0xF000 || (opcode & 0xF800) == 0xE000):
                ExecuteThumbBranch(opcode);
                break;
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

    private void ExecuteThumbLoadStore(ushort opcode)
    {
        int op = (opcode >> 13) & 0x3;
        switch (op)
        {
            case 0b010: // LDR literal or load/store register offset
                if ((opcode & 0x1800) == 0x1800)
                {
                    // LDR literal
                    int rd = (opcode >> 8) & 0x7;
                    uint imm = (uint)((opcode & 0xFF) << 2);
                    uint addr = (_state.Pc & ~3u) + imm;
                    _state.R[rd] = _bus.Read32(addr);
                }
                break;
            case 0b011:
                int rdReg = opcode & 0x7;
                int rb = (opcode >> 3) & 0x7;
                uint offset = (uint)(((opcode >> 6) & 0x1F) << 2);
                if ((opcode & 0x0400) != 0)
                {
                    // LDR word
                    _state.R[rdReg] = _bus.Read32(_state.R[rb] + offset);
                }
                else
                {
                    _bus.Write32(_state.R[rb] + offset, _state.R[rdReg]);
                }
                break;
        }
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
        int shiftType = (int)((opcode >> 5) & 0x3);
        int shiftImm = (int)((opcode >> 7) & 0x1F);
        if (shiftImm == 0 && shiftType is 0 or 1)
        {
            shiftImm = shiftType == 0 ? 0 : 32;
        }
        return shiftType switch
        {
            0 => ShiftLeft(value, shiftImm),
            1 => ShiftRightLogical(value, shiftImm),
            2 => ShiftRightArithmetic(value, shiftImm),
            3 => RotateRight(value, shiftImm == 0 ? 1 : shiftImm),
            _ => (value, _state.GetFlag(CpuFlags.Carry))
        };
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
