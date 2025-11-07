namespace Gba.Core.Abstractions;

public interface IVideoSink
{
    /// <summary>
    /// Presents a complete 240x160 RGBA32 frame.
    /// </summary>
    /// <param name="rgba">Pixel buffer length 38400.</param>
    void PresentFrame(ReadOnlySpan<uint> rgba);
}
