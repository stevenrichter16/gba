namespace Gba.Core.Emulation;

public sealed class EmulatorOptions
{
    /// <summary>
    /// When true, skip running the BIOS and start execution at the Game Pak entry point.
    /// </summary>
    public bool SkipBios { get; init; } = true;

    /// <summary>
    /// Optional BIOS image (16KB). When provided and SkipBios is false, copied into BIOS region.
    /// </summary>
    public byte[]? Bios { get; init; }

    /// <summary>
    /// Optional entry point override when skipping BIOS. Defaults to 0x08000000.
    /// </summary>
    public uint? EntryPoint { get; init; }

    /// <summary>
    /// Start the CPU in THUMB mode (rare; defaults to ARM state).
    /// </summary>
    public bool StartInThumb { get; init; }
}
