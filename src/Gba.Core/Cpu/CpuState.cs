using System;

namespace Gba.Core.Cpu;

[Flags]
public enum CpuFlags : uint
{
    Negative = 1u << 31,
    Zero = 1u << 30,
    Carry = 1u << 29,
    Overflow = 1u << 28,
    IRQDisable = 1u << 7,
    FIQDisable = 1u << 6,
    // bit 5 is Thumb state.
}

public enum CpuMode : byte
{
    User = 0x10,
    FIQ = 0x11,
    IRQ = 0x12,
    Supervisor = 0x13,
    Abort = 0x17,
    Undefined = 0x1B,
    System = 0x1F
}

public sealed class CpuState
{
    public readonly uint[] R = new uint[16];
    public uint Cpsr = 0x000000DFu; // Start in System mode with IRQ/FIQ disabled
    public ulong CycleCount { get; set; }

    // Saved Program Status Registers for each privileged mode
    private uint _spsrFiq;
    private uint _spsrIrq;
    private uint _spsrSvc;
    private uint _spsrAbt;
    private uint _spsrUnd;

    // Banked registers (R13_mode, R14_mode) for each mode
    private uint _r13User, _r14User;
    private uint _r13Fiq, _r14Fiq;
    private uint _r13Irq, _r14Irq;
    private uint _r13Svc, _r14Svc;
    private uint _r13Abt, _r14Abt;
    private uint _r13Und, _r14Und;

    public ref uint Pc => ref R[15];

    public bool Thumb
    {
        get => (Cpsr & (1u << 5)) != 0;
        set => Cpsr = value ? (Cpsr | (1u << 5)) : (Cpsr & ~(1u << 5));
    }

    public CpuMode Mode
    {
        get => (CpuMode)(Cpsr & 0x1F);
        set => Cpsr = (Cpsr & ~0x1Fu) | (uint)value;
    }

    public bool GetFlag(CpuFlags flag) => (Cpsr & (uint)flag) != 0;

    public void SetFlag(CpuFlags flag, bool value)
    {
        if (value)
        {
            Cpsr |= (uint)flag;
        }
        else
        {
            Cpsr &= ~(uint)flag;
        }
    }

    public uint GetSpsr()
    {
        return Mode switch
        {
            CpuMode.FIQ => _spsrFiq,
            CpuMode.IRQ => _spsrIrq,
            CpuMode.Supervisor => _spsrSvc,
            CpuMode.Abort => _spsrAbt,
            CpuMode.Undefined => _spsrUnd,
            _ => Cpsr // User/System don't have SPSR
        };
    }

    public void SetSpsr(uint value)
    {
        switch (Mode)
        {
            case CpuMode.FIQ:
                _spsrFiq = value;
                break;
            case CpuMode.IRQ:
                _spsrIrq = value;
                break;
            case CpuMode.Supervisor:
                _spsrSvc = value;
                break;
            case CpuMode.Abort:
                _spsrAbt = value;
                break;
            case CpuMode.Undefined:
                _spsrUnd = value;
                break;
        }
    }

    /// <summary>
    /// Switches CPU mode, banking/restoring R13 and R14.
    /// </summary>
    public void SwitchMode(CpuMode newMode)
    {
        if (Mode == newMode) return;

        // Save current R13 and R14 to their banked locations
        switch (Mode)
        {
            case CpuMode.User:
            case CpuMode.System:
                _r13User = R[13];
                _r14User = R[14];
                break;
            case CpuMode.FIQ:
                _r13Fiq = R[13];
                _r14Fiq = R[14];
                break;
            case CpuMode.IRQ:
                _r13Irq = R[13];
                _r14Irq = R[14];
                break;
            case CpuMode.Supervisor:
                _r13Svc = R[13];
                _r14Svc = R[14];
                break;
            case CpuMode.Abort:
                _r13Abt = R[13];
                _r14Abt = R[14];
                break;
            case CpuMode.Undefined:
                _r13Und = R[13];
                _r14Und = R[14];
                break;
        }

        // Restore R13 and R14 from new mode's banked registers
        switch (newMode)
        {
            case CpuMode.User:
            case CpuMode.System:
                R[13] = _r13User;
                R[14] = _r14User;
                break;
            case CpuMode.FIQ:
                R[13] = _r13Fiq;
                R[14] = _r14Fiq;
                break;
            case CpuMode.IRQ:
                R[13] = _r13Irq;
                R[14] = _r14Irq;
                break;
            case CpuMode.Supervisor:
                R[13] = _r13Svc;
                R[14] = _r14Svc;
                break;
            case CpuMode.Abort:
                R[13] = _r13Abt;
                R[14] = _r14Abt;
                break;
            case CpuMode.Undefined:
                R[13] = _r13Und;
                R[14] = _r14Und;
                break;
        }

        Mode = newMode;
    }
}
