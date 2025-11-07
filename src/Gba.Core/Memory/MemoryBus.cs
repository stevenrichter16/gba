using System;
using Gba.Core.Abstractions;

namespace Gba.Core.Memory;

public enum InterruptType
{
    VBlank = 0,
    HBlank = 1,
    VCount = 2,
    Timer0 = 3,
    Timer1 = 4,
    Timer2 = 5,
    Timer3 = 6,
    Serial = 7,
    Dma0 = 8,
    Dma1 = 9,
    Dma2 = 10,
    Dma3 = 11,
    Keypad = 12,
    GamePak = 13
}

public sealed class MemoryBus
{
    private const uint BiosStart = 0x00000000;
    private const uint EwramStart = 0x02000000;
    private const uint IwramStart = 0x03000000;
    private const uint IoStart = 0x04000000;
    private const uint PramStart = 0x05000000;
    private const uint VramStart = 0x06000000;
    private const uint OamStart = 0x07000000;
    private const uint RomStart = 0x08000000;

    private readonly byte[] _bios = new byte[0x4000];
    private readonly byte[] _ewram = new byte[0x40000];
    private readonly byte[] _iwram = new byte[0x8000];
    private readonly byte[] _pram = new byte[0x400];
    private readonly byte[] _vram = new byte[0x18000];
    private readonly byte[] _oam = new byte[0x400];
    private readonly byte[] _rom;
    private readonly byte[] _sram = new byte[0x10000];

    private uint _openBus;
    private ushort _dispcnt;
    private ushort _dispstat;
    private ushort _vcount;
    private ushort _keyinput = 0x03FF;
    private ushort _bg0cnt;
    private ushort _bg0hofs;
    private ushort _bg0vofs;

    // Interrupt registers
    private ushort _ie;     // Interrupt Enable (0x04000200)
    private ushort _if;     // Interrupt Flags (0x04000202)
    private ushort _ime;    // Interrupt Master Enable (0x04000208)

    public MemoryBus(byte[] rom)
    {
        _rom = rom.Length == 0 ? new byte[4] : rom;
    }

    public IInputSource? InputSource { get; set; }

    public ReadOnlySpan<byte> Vram => _vram;
    public Span<byte> MutableVram => _vram;
    public Span<byte> MutableIwram => _iwram;
    public ushort DisplayControl => _dispcnt;
    public ushort DisplayStatus => _dispstat;
    public ushort VCount => _vcount;
    public ushort Bg0Control => _bg0cnt;
    public ushort Bg0HOffset => (ushort)(_bg0hofs & 0x1FF);
    public ushort Bg0VOffset => (ushort)(_bg0vofs & 0x1FF);

    // Interrupt register accessors
    public ushort InterruptEnable => _ie;
    public ushort InterruptFlags => _if;
    public bool InterruptMasterEnable => (_ime & 1) != 0;

    public void WriteDisplayControl(ushort value) => _dispcnt = (ushort)(value & 0x1FFF);

    public void SetVCount(ushort scanline) => _vcount = scanline;

    public void SetDisplayStatus(ushort value) => _dispstat = value;

    /// <summary>
    /// Requests an interrupt by setting the corresponding bit in IF.
    /// </summary>
    public void RequestInterrupt(InterruptType interrupt)
    {
        _if |= (ushort)(1 << (int)interrupt);
    }

    /// <summary>
    /// Checks if any enabled interrupts are pending.
    /// </summary>
    public bool HasPendingInterrupt()
    {
        if (!InterruptMasterEnable) return false;
        return (_if & _ie) != 0;
    }

    public void LoadBios(ReadOnlySpan<byte> bios)
    {
        bios[..Math.Min(bios.Length, _bios.Length)].CopyTo(_bios);
    }

    public byte Read8(uint address)
    {
        var result = address switch
        {
            >= BiosStart and < BiosStart + 0x4000 => _bios[address & 0x3FFF],
            >= EwramStart and < EwramStart + 0x40000 => _ewram[address & 0x3FFFF],
            >= IwramStart and < IwramStart + 0x8000 => _iwram[address & 0x7FFF],
            >= IoStart and < IoStart + 0x400 => ReadIo8(address),
            >= PramStart and < PramStart + 0x400 => _pram[address & 0x3FF],
            >= VramStart and < VramStart + 0x20000 => _vram[(address & 0x1FFFF) % _vram.Length],
            >= OamStart and < OamStart + 0x400 => _oam[address & 0x3FF],
            >= RomStart and <= 0x0DFFFFFF => ReadRom8(address - RomStart),
            >= 0x0E000000 and < 0x0E010000 => _sram[address & 0xFFFF],
            _ => unchecked((byte)_openBus)
        };

        _openBus = result;
        return result;
    }

    private byte ReadIo8(uint address)
    {
        uint offset = address & 0xFFFFFF;

        // Handle registers above 0x04000400 separately
        if (offset >= 0x200)
        {
            return offset switch
            {
                0x200 => (byte)(_ie & 0xFF),
                0x201 => (byte)(_ie >> 8),
                0x202 => (byte)(_if & 0xFF),
                0x203 => (byte)(_if >> 8),
                0x208 => (byte)(_ime & 0xFF),
                0x209 => (byte)(_ime >> 8),
                _ => unchecked((byte)_openBus)
            };
        }

        return (address & 0x3FF) switch
        {
            0x000 => (byte)(_dispcnt & 0xFF),
            0x001 => (byte)(_dispcnt >> 8),
            0x004 => (byte)(_dispstat & 0xFF),
            0x005 => (byte)(_dispstat >> 8),
            0x006 => (byte)(_vcount & 0xFF),
            0x007 => (byte)(_vcount >> 8),
            0x008 => (byte)(_bg0cnt & 0xFF),
            0x009 => (byte)(_bg0cnt >> 8),
            0x010 => (byte)(_bg0hofs & 0xFF),
            0x011 => (byte)(_bg0hofs >> 8),
            0x012 => (byte)(_bg0vofs & 0xFF),
            0x013 => (byte)(_bg0vofs >> 8),
            0x130 => (byte)(_keyinput & 0xFF),
            0x131 => (byte)(_keyinput >> 8),
            _ => unchecked((byte)_openBus)
        };
    }

    private byte ReadRom8(uint offset)
    {
        offset %= (uint)_rom.Length;
        return _rom[offset];
    }

    public ushort Read16(uint address)
    {
        address &= 0xFFFFFFFE;
        ushort lo = Read8(address);
        ushort hi = Read8(address + 1);
        return (ushort)(lo | (hi << 8));
    }

    public uint Read32(uint address)
    {
        address &= 0xFFFFFFFC;
        uint b0 = Read8(address);
        uint b1 = Read8(address + 1);
        uint b2 = Read8(address + 2);
        uint b3 = Read8(address + 3);
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    public void Write8(uint address, byte value)
    {
        switch (address)
        {
            case >= EwramStart and < EwramStart + 0x40000:
                _ewram[address & 0x3FFFF] = value;
                break;
            case >= IwramStart and < IwramStart + 0x8000:
                _iwram[address & 0x7FFF] = value;
                break;
            case >= IoStart and < IoStart + 0x400:
                WriteIo8(address, value);
                break;
            case >= PramStart and < PramStart + 0x400:
                _pram[address & 0x3FF] = value;
                break;
            case >= VramStart and < VramStart + 0x20000:
                _vram[(address & 0x1FFFF) % _vram.Length] = value;
                break;
            case >= OamStart and < OamStart + 0x400:
                _oam[address & 0x3FF] = value;
                break;
            case >= 0x0E000000 and < 0x0E010000:
                _sram[address & 0xFFFF] = value;
                break;
        }
    }

    private void WriteIo8(uint address, byte value)
    {
        uint offset = address & 0xFFFFFF;

        // Handle registers above 0x04000400 separately
        if (offset >= 0x200)
        {
            switch (offset)
            {
                case 0x200:
                    _ie = (ushort)((_ie & 0xFF00) | value);
                    break;
                case 0x201:
                    _ie = (ushort)((_ie & 0x00FF) | (value << 8));
                    break;
                case 0x202:
                    // Write 1 to acknowledge/clear interrupt
                    _if = (ushort)(_if & ~value);
                    break;
                case 0x203:
                    _if = (ushort)(_if & ~(value << 8));
                    break;
                case 0x208:
                    _ime = (ushort)((_ime & 0xFF00) | value);
                    break;
                case 0x209:
                    _ime = (ushort)((_ime & 0x00FF) | (value << 8));
                    break;
            }
            return;
        }

        switch (address & 0x3FF)
        {
            case 0x000:
                _dispcnt = (ushort)((_dispcnt & 0xFF00) | value);
                break;
            case 0x001:
                _dispcnt = (ushort)((_dispcnt & 0x00FF) | (value << 8));
                break;
            case 0x004:
                _dispstat = (ushort)((_dispstat & 0xFF00) | value);
                break;
            case 0x005:
                _dispstat = (ushort)((_dispstat & 0x00FF) | (value << 8));
                break;
            case 0x008:
                _bg0cnt = (ushort)((_bg0cnt & 0xFF00) | value);
                break;
            case 0x009:
                _bg0cnt = (ushort)((_bg0cnt & 0x00FF) | (value << 8));
                break;
            case 0x010:
                _bg0hofs = (ushort)((_bg0hofs & 0xFF00) | value);
                break;
            case 0x011:
                _bg0hofs = (ushort)((_bg0hofs & 0x00FF) | (value << 8));
                break;
            case 0x012:
                _bg0vofs = (ushort)((_bg0vofs & 0xFF00) | value);
                break;
            case 0x013:
                _bg0vofs = (ushort)((_bg0vofs & 0x00FF) | (value << 8));
                break;
        }
    }

    public void Write16(uint address, ushort value)
    {
        address &= 0xFFFFFFFE;
        Write8(address, (byte)(value & 0xFF));
        Write8(address + 1, (byte)(value >> 8));
    }

    public void Write32(uint address, uint value)
    {
        address &= 0xFFFFFFFC;
        Write8(address, (byte)(value & 0xFF));
        Write8(address + 1, (byte)((value >> 8) & 0xFF));
        Write8(address + 2, (byte)((value >> 16) & 0xFF));
        Write8(address + 3, (byte)((value >> 24) & 0xFF));
    }

    public void LatchInput()
    {
        if (InputSource is null)
        {
            _keyinput = 0x03FF;
        }
        else
        {
            _keyinput = (ushort)(InputSource.ReadKeys() | 0xFC00);
        }
    }

    public ushort ReadBgPaletteEntry(int index)
    {
        index &= 0xFF;
        int offset = index * 2;
        if (offset + 1 >= _pram.Length)
        {
            return 0;
        }

        return (ushort)(_pram[offset] | (_pram[offset + 1] << 8));
    }
}
