namespace Gba.Core.Abstractions;

public interface IInputSource
{
    /// <summary>
    /// Returns the current KEYINPUT mask (active low).
    /// </summary>
    ushort ReadKeys();
}
