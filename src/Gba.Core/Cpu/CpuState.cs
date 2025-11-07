using System;

namespace Gba.Core.Cpu;

[Flags]
public enum CpuFlags : uint
{
    Negative = 1u << 31,
    Zero = 1u << 30,
    Carry = 1u << 29,
    Overflow = 1u << 28,
    // bit 5 is Thumb state.
}

public sealed class CpuState
{
    public readonly uint[] R = new uint[16];
    public uint Cpsr = 0x0000001Fu; // start in user mode
    public ulong CycleCount { get; set; }

    public ref uint Pc => ref R[15];

public bool Thumb
{
    get => (Cpsr & (1u << 5)) != 0;
    set => Cpsr = value ? (Cpsr | (1u << 5)) : (Cpsr & ~(1u << 5));
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
}
