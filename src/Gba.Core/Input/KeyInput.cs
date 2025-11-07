namespace Gba.Core.Input;

/// <summary>
/// Tracks GBA button states (active-low KEYINPUT mask).
/// </summary>
public sealed class KeyInput
{
    // Bits: 0=A,1=B,2=Select,3=Start,4=Right,5=Left,6=Up,7=Down,8=R,9=L.
    private ushort _mask = 0x03FF;

    public ushort Mask => _mask;

    public void SetPressed(int bit)
    {
        if ((uint)bit > 9) return;
        _mask = (ushort)(_mask & ~(1 << bit));
    }

    public void SetReleased(int bit)
    {
        if ((uint)bit > 9) return;
        _mask = (ushort)(_mask | (1 << bit));
    }

    public void Reset() => _mask = 0x03FF;
}
