# GBA Emulator Implementation Plan

## CPU & Timing
- Complete ARM/Thumb instruction sets: data processing variants (full barrel shifter support with register-specified shifts and RRX, correct carry-out), multiplies (MUL/MLA/UMULL/SMULL), halfword/byte/dual transfers (LDRH/LDRSH/LDRSB/STRH/LDRD/STRD), swap instructions (SWP), block transfers (LDM/STM with all addressing modes), and high-register Thumb ops.
- Implement data-processing writes to R15 (pipeline flush, CPSR/SPSR restore when `S` bit set) and mode switching semantics for exception returns.
- Add coprocessor op stubs (CDP/MRC/MCR/LDC/STC) and undefined instruction handling (vector 0x04).
- Implement SWI (vector 0x08) and exception entry/exit (stacking LR/SPSR, mode change), with BIOS fallback when missing.
- Add barrel-shifter register (`Rs`) paths: support LSL/LSR/ASR/ROR with register-specified shift amounts (mask to lower 8 bits, handle 0 vs ≥32 cases, produce correct carry), and implement RRX when ROR shift amount is zero. Ensure Thumb equivalents match behavior.
- Add cycle scheduling with instruction timing + memory wait states to drive timers, DMA, and PPU events.
- Wire SWI/IRQ dispatch through BIOS vector table; provide high-level BIOS fallbacks when a BIOS isn’t supplied.

## Memory & DMA
- Fill out the memory map (SRAM/Flash/EEPROM protocols, ROM prefetch wait states, mirrors, open-bus behavior).
- Implement DMA channels 0–3 with immediate, VBlank/HBlank, FIFO triggers, and CPU stall timing.
- Detect save types (SRAM, Flash, EEPROM) and persist data via browser storage keyed by ROM hash.

## PPU Rendering
- Build a per-scanline renderer (262 lines/frame) to capture mid-frame register writes and blending.
- Support BG modes 0–2 (text/affine backgrounds), modes 3–5 (bitmaps), windows, mosaic, alpha blending, brightness control.
- Implement sprite (OBJ) rendering with priority, affine transforms, OBJ window, and blending.
- Respect VRAM layout nuances (char/screen blocks, affine mapping, extended palettes) and optimize (tile caches, WebGL if needed).

## Audio / APU
- Emulate PSG channels (square, wave, noise) with envelopes, sweep, and length counters.
- Implement Direct Sound A/B FIFOs, timer-driven sample output, and DMA feeding.
- Mix stereo PCM, resample for Web Audio, and stream via AudioWorklet; provide mute/low-quality fallbacks.

## Timers & Interrupts
- Implement timers 0–3 with prescalers, cascading, reload, and IRQ generation.
- Build a full interrupt controller: IE/IF/IME, BIOS IRQ trampoline, event priorities.
- Trigger IRQs for VBlank/HBlank, VCount match, DMA completion, timers, keypad, serial (stub).

## Input & Peripherals
- Support keyboard/on-screen/gamepad input plus KEYCNT button IRQ behavior.
- Add save states (serialize CPU/PPU/APU/memory) and optional cartridge peripherals (rumble, sensors) as stubs.

## Testing & Validation
- Automate gba-suite, Tonc tests, DMA/timer ROMs, and compare snapshots with known-good emulators.
- Provide instruction tracing/logging toggles and integrate regression tests into CI.

## Performance & Deployment
- Optimize interpreter (decoded opcode tables, fast memory paths) and consider a lightweight dynarec if necessary.
- Offer accuracy/performance toggles (disable blending, lower audio quality, frame skip).
- Ship Blazor WebAssembly AoT builds, PWA packaging, drag-and-drop ROM/BIOS uploads, save persistence, and a compatibility UI.

Following this plan—CPU completeness, DMA/timers, full PPU/APU coverage, and comprehensive testing—will lead to a browser-based GBA emulator capable of running commercial games accurately.
