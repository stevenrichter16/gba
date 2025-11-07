# Developing a Game Boy Advance (GBA) Emulator from Scratch

## Introduction
Emulating the Game Boy Advance involves reproducing its hardware entirely in software: the ARM7TDMI CPU, the memory map, the picture processing unit (PPU), the audio processing unit (APU), input, DMA, interrupts, and cartridge interfaces. The goal is to reach correct behavior so that commercial games act like they do on real hardware while balancing ease of development with accuracy and performance. This document lays out the hardware overview, emulator architecture, challenges (especially around the CPU), implementation strategies in C# or Python with an eye toward web deployment, tooling, references, existing emulators, testing practices, and packaging considerations.

## GBA Hardware Architecture Overview

### CPU (ARM7TDMI)
The GBA uses a 32-bit ARM7TDMI RISC core that runs at roughly 16.78 MHz. It executes both 32-bit ARM instructions and 16-bit THUMB instructions and exposes 16 general-purpose 32-bit registers plus the CPSR/SPSR status registers. The CPU has a simple three-stage pipeline (fetch, decode, execute), lacks caches or an MMU, and accesses memory directly. Conditional execution on most instructions via condition codes needs to be modeled closely in an emulator.

### Memory Map
The memory layout is fixed and fully documented (see GBATEK). Key ranges:
- BIOS: 0x00000000-0x00003FFF (16 KB)
- External WRAM (EWRAM): 0x02000000-0x0203FFFF (256 KB, 16-bit bus)
- Internal WRAM (IWRAM): 0x03000000-0x03007FFF (32 KB, 32-bit bus)
- I/O registers: 0x04000000-0x040003FE
- Palette RAM: 0x05000000-0x050003FF (1 KB)
- VRAM: 0x06000000-0x06017FFF (96 KB, 16-bit bus)
- OAM: 0x07000000-0x070003FF (1 KB)
- Game Pak ROM: 0x08000000-0x09FFFFFF (up to 32 MB addressable, 16-bit bus with prefetch buffer)
- Game Pak SRAM/Flash: 0x0E000000-0x0E00FFFF (typically 32 KB)

### Component Mapping
| GBA Hardware | Emulator Module | Description |
| --- | --- | --- |
| ARM7TDMI CPU | CPU core (interpreter or JIT) | Executes ARM/THUMB instructions, manages registers and flags |
| WRAM (IWRAM/EWRAM) | Memory subsystem | Two RAM pools (fast 32 KB internal, slower 256 KB external) |
| VRAM | PPU/graphics | Background tiles, tile maps, bitmap frame buffers |
| Palette RAM | PPU/graphics | 256-color palettes for backgrounds and sprites |
| OAM | PPU/graphics | Sprite attribute table (up to 128 sprites) |
| Video display | PPU renderer | Produces 240x160 LCD output near 60 Hz, blends layers |
| Audio hardware | Audio module (APU) | 4 legacy GB channels + 2 PWM/PCM channels, stereo mixing |
| Game Pak ROM/RAM | Cartridge loader | Provides ROM data, handles save memory (SRAM/Flash/EEPROM) |
| BIOS (16 KB) | BIOS handler | Boot ROM and SWI functions, either real BIOS or HLE |
| I/O registers | Device controllers | Memory-mapped control for PPU, sound, timers, DMA, input |
| Input buttons | Input handler | Maps host keyboard/gamepad events to KEYINPUT bits |
| Timers | Timer module | Four hardware timers, configurable prescalers, IRQ sources |
| DMA | DMA controller | Four DMA channels for VRAM updates, audio streaming, etc. |
| Interrupt circuits | IRQ handler | Manages IME/IE/IF registers, vectors to BIOS handler |

### Graphics (PPU)
The PPU renders a 240x160 LCD at about 59.7 Hz. It combines multiple background layers (three text layers plus affine layers) and sprites. Modes 0-2 are tile based, modes 3-5 offer bitmap framebuffers. The PPU is tile and palette driven; palette entries are 15-bit BGR values (32768 colors). Rendering often proceeds scanline by scanline: combine enabled backgrounds with their scroll/affine attributes, overlay sprites with priority and transparency, then apply windowing or blending. Initial implementations can render whole frames at a time for simplicity, but eventually per-scanline timing is needed for mid-frame effects. VBlank interrupts (~60 Hz) are critical because games synchronize graphics updates there.

### Sound (APU)
The sound unit consists of the Game Boy PSG (two square channels, one wave channel, one noise channel) plus two 8-bit PCM FIFO channels (Direct Sound A/B) that usually run near 32 kHz using DMA to feed data. The emulator must model channel envelopes, sweep, frequency, FIFO refill via DMA, and mix stereo audio for the host API. Due to cost, many developers defer accurate audio until CPU/PPU are stable, but streaming PCM (used by many games) should eventually be handled for a complete experience.

### Input
The GBA controller has an eight-direction D-pad plus A, B, L, R, Start, and Select. Button state is reflected in the KEYINPUT register (bit cleared when pressed). Some accessories (link cable, sensors) can be skipped or stubbed initially. Input must be polled frequently so games respond instantly, and KEYCNT can trigger interrupts on button combos if implemented.

## Key Components and Modules in an Emulator
- **CPU emulation (ARM7TDMI core):** Fetch, decode, and execute ARM/THUMB opcodes, maintain registers/CPSR, support mode switching, vector resets/interrupts, and integrate with timers and DMA. Interpreters are common; dynarecs are advanced.
- **Memory management unit (bus):** Map 32-bit addresses onto BIOS, RAM, VRAM, palette, OAM, cartridge ROM/RAM, and I/O registers. Provide handlers for each region, and special logic for I/O accesses that have side effects. Handle mirroring and wait states if desired.
- **Graphics (PPU):** Maintain VRAM, tile maps, OAM, palette, and produce frames or scanlines by compositing backgrounds and sprites according to priority, windowing, and blending. Provide VBlank/HBlank timing hooks.
- **Audio (APU):** Simulate PSG channels plus Direct Sound A/B FED via FIFOs and DMA. Mix to stereo PCM at a fixed sample rate and output through the host (Web Audio, etc.).
- **BIOS:** Either load the official 16 KB BIOS (user supplied or open replacement like Normatt's) or implement high-level emulation of SWI calls. Using the real BIOS is simpler and more accurate.
- **Timers and DMA:** Emulate four timers with different prescalers and interrupt generation plus four DMA channels that can trigger immediately or on VBlank/HBlank/sound FIFO events.
- **Interrupts:** Manage IME, IE, and IF registers, assert interrupts for VBlank, HBlank, timers, DMA, input, etc., and vector the CPU to the BIOS handler when conditions are met.

## Challenges in Emulating the ARM7TDMI CPU
- **Dual instruction set:** Must support both 32-bit ARM and 16-bit THUMB opcodes plus seamless switching via the CPSR T flag, effectively doubling decode logic.
- **Instruction decoding and conditional execution:** ARM opcodes include condition codes so the emulator must check flags and skip instructions when needed while still accounting for timing. Many opcodes (data processing, barrel shifter, loads/stores, multiply, swap, system ops) have tricky edge cases.
- **Pipeline and timing:** The three-stage pipeline introduces fetch/decode/execute overlap and penalties on taken branches or interrupts. Simplified emulators often run instruction-accurate rather than cycle-accurate but still must handle extra cycles for multi-load/store or multiply instructions if timing-sensitive software is considered.
- **Performance in high-level languages:** Achieving millions of instructions per second is challenging, especially in Python. C# can reach full speed with optimized interpreters (array-backed registers, minimized bounds checks) and ahead-of-time (AOT) compilation to WebAssembly. Python generally requires PyPy for acceptable speed, careful avoidance of per-instruction allocations, and possibly C extensions for hotspots.
- **Accuracy versus simplicity:** Handling processor modes (USR, FIQ, IRQ, SVC, etc.) with banked registers, PC behavior (ARM PC reads as current address + 8, THUMB + 4), sign extension rules, and CPSR flag updates demands precision. Testing with known ROMs (ARMWrestler, gba-suite) is essential.

## Implementation Strategy for Major Subsystems

### CPU Implementation Strategy (Interpreter Core)
**C# approach:** Use an interpreter loop with a uint[] for registers, dedicated fields for CPSR flags, and methods for Read32/Write32 memory operations. Fetch the opcode from memory at PC, increment PC appropriately (ARM: PC += 4, THUMB: PC += 2), decode via bit masks or lookup tables, and dispatch to handler methods or a large switch. Implement arithmetic, logic, memory, branch, multiply, status register transfers, and SWI. Keep the core allocation-free inside the loop, leverage inlining, and consider partial decoding caches if needed. Add THUMB support once ARM base works. Integrate interrupt checks after each instruction and respect the BIOS vector table.

**Python approach:** Similar interpreter loop but use lists or bytearray/memoryview objects. Maintain registers as list elements, keep frequently accessed values in locals, and mask to 32 bits to simulate wrapping. Use if/elif decoding or dictionary dispatch, and run under PyPy for speed. Avoid heavy Python function calls per instruction; inline logic as much as possible. Consider optional acceleration via Cython or native extensions if targeting full-speed play. Implement SWI by either running BIOS code (if present) or intercepting and emulating routines.

### Memory and MMU Strategy
Allocate arrays per region (bios[0x4000], ewram[0x40000], iwram[0x8000], vram[0x18000], pram[0x400], oam[0x400], rom[size], save[size]). Implement Read8/16/32 and Write variants that inspect the address range, map to the correct buffer with offsets, and handle mirroring or unaligned access. Provide specialized handlers for I/O space that read/write device register structures (PPU state, timer state, DMA control). Consider fast paths for common regions and optionally emulate wait states or prefetch effects later. Load BIOS and game ROM binaries during initialization.

### PPU/Graphics Implementation Strategy
Start with a simple mode (e.g., Mode 3 bitmap where VRAM is a 240x160 framebuffer) to get visible output. Then implement Mode 0 tile backgrounds: parse BG control registers (REG_BGxCNT) for tile base, map base, size, and color depth; fetch tile indices and pixels; apply palette lookups; and composite per scanline. Add sprites by iterating OAM entries, determining whether they overlap the current scanline, fetching tile data, applying flips/affine transforms, and blending according to priority. Implement affine backgrounds (Modes 1-2) with matrix math for rotated/scaled layers. Add windowing and alpha blending as needed. Simulate 228 scanlines per frame (160 visible, ~68 VBlank), trigger VBlank interrupts, and optionally HBlank events for DMA. For web output, convert 15-bit colors to RGBA and blit to an HTML canvas using ImageData or WebGL.

### APU/Sound Implementation Strategy
Choose an output sample rate (32,768 Hz or 44.1 kHz). Determine cycles per sample (~512 cycles for 32 kHz) and accumulate CPU cycles until the next sample is due, then advance each channel's state. For the PSG channels, implement duty cycles, frequency counters, envelopes, and sweep/length timers. For the wave channel, step through the 32-byte waveform. For the noise channel, implement a 15-bit LFSR. For Direct Sound A/B, maintain FIFO buffers, emulate DMA refills when below threshold, and pull 8-bit signed samples at the rate driven by Timer 0 or 1. Mix all active channels into left/right outputs based on SOUNDCNT settings, clamp values, and store samples into a buffer. On the web, pass the PCM buffer to Web Audio (e.g., via an AudioWorklet) to avoid glitches. Start with basic audio (even just Direct Sound) and iterate toward full fidelity.

### Input Handling Strategy
Map keyboard or gamepad events to the 16-bit KEYINPUT register (bit cleared when pressed). Update this register in response to browser events (keydown/keyup or Gamepad API polling) and expose it to the CPU reads at 0x04000130. Optionally handle KEYCNT for button-triggered interrupts. Provide customizable key bindings and consider on-screen controls for touch devices.

## C# vs Python for Web Deployment
**C# + WebAssembly (Blazor):** Modern .NET (7/8) can compile to WebAssembly via Blazor WebAssembly with optional AOT for critical code. The emulator core stays in C#, UI is built with Blazor components, and JavaScript interop handles canvas drawing and Web Audio output. Performance is strong (OptimeGBA reports 250 FPS for Pokemon Emerald on desktop) so even with browser overhead, 60 FPS is feasible. You can limit download size by AOT-compiling only hot paths. For desktop builds, the same core can target SDL or OpenTK frontends.

**Python + Pyodide/PyScript:** Pyodide brings CPython to WebAssembly (~5 MB download) and allows Python code in the browser. This is great for rapid prototyping and educational demos, but performance is limited; CPython without JIT struggles to keep up with a 16 MHz system. Vectorized helpers (NumPy) can accelerate some tasks, yet a full-speed emulator is unlikely unless heavy parts move to compiled extensions. PyScript simplifies DOM/canvas interaction and event handling. Audio must still route through Web Audio via JS interop. Python shines for readability and experimentation, but for a user-facing emulator, C# (or another compiled language) is the practical option.

## Resources and Documentation for Emulator Developers
- **GBATEK:** Definitive GBA hardware reference covering memory maps, registers, timing, and peripherals.
- **ARM7TDMI and ARM architecture manuals:** Official documentation on instruction semantics, pipeline behavior, and processor modes.
- **CowBite Spec:** Tom Happ's easier-to-digest virtual hardware spec for GBA.
- **Tonc tutorial:** J. Vijn's guide for GBA homebrew developers, invaluable for understanding how games use backgrounds, sprites, affine layers, and more.
- **Open-source emulators:** mGBA (C), NanoBoyAdvance, Higan's GBA core, RustBoyAdvance-NG, and VisualBoyAdvance provide reference implementations. OptimeGBA (C#) and PyBoyAdvance (Python) are especially relevant. PyBoy (GB/GBC) and other .NET Game Boy projects illustrate emulator structure in these languages.
- **Testing ROMs:** ARMWrestler and gba-suite by jsmolka validate CPU correctness. Additional homebrew covers timers, DMA, PPU, and sound behaviors.
- **Community hubs:** gbdev Discord, EmuDev subreddit, old EmuTalk threads, and similar forums discuss quirks, share logs, and offer debugging help.

## Examples of GBA Emulators in C# and Python
- **Optime GBA (C#):** .NET Core emulator with OpenTK/SDL frontends, passes ARMwrestler and gba-suite, implements timers, DMA, audio, sprites, alpha blending, windowing, and requires a real BIOS. Desktop builds exceed 250 FPS on some games; a web frontend streams frames. Demonstrates that C# is performant enough for full-speed emulation.
- **PyBoy Advance (Python):** Pure Python project prioritizing readability and education. Works best under PyPy due to CPython slowness, supports Normatt's open BIOS, and references other emulators for correctness. Not as fast as C/C#/Rust emulators but a valuable learning resource.
- **Other inspirations:** Numerous C# Game Boy Color emulators (CoreBoy, GBdotNET), Unity-based emu experiments, and Python Game Boy emulators (PyBoy) showcase common architecture patterns transferable to the GBA.

## Testing and Validation of the Emulator
- **CPU instruction tests:** Run ARMWrestler and gba-suite to verify arithmetic, logic, branches, and edge cases. Investigate mismatches via logging and compare with known-good emulator traces.
- **Unit tests:** Write targeted tests for tricky opcodes by setting up register state, executing a single instruction, and asserting outputs and flags.
- **BIOS checks:** Running the real BIOS ensures SWI behavior and VBlank timing. Successful Nintendo logo display indicates BIOS, interrupts, and VRAM writes work.
- **Graphics validation:** Use homebrew test ROMs or known commercial titles (Mario Advance, Pokemon, Sonic Advance, Golden Sun) to confirm backgrounds, sprites, affine effects, windowing, blending, and line scrolling. Compare with captures from real hardware or mature emulators.
- **Audio validation:** Listen for channel correctness, run ROMs that isolate each PSG channel, and ensure Direct Sound DMA-fed music plays without underruns.
- **Timing/performance:** Ensure the emulator throttles to 59.7 FPS for usability. Some ROMs measure timer accuracy; use them if aiming for tight timing.
- **Save/load:** Test SRAM/Flash/EEPROM emulation with games that save (Pokemon, Zelda). Persist save data to disk or browser storage.
- **Edge cases:** Handle open-bus reads, mid-scanline register writes, DMA conflicts, and other quirks. Use logging to capture all register writes for debugging. If discrepancies arise, compare CPU instruction traces with another emulator.
- **Community feedback:** Share builds to gather compatibility reports and uncover regressions.

## Packaging and Deploying the Emulator for Web Use
1. **Web app setup:** Build a simple HTML page or Blazor WebAssembly app with a canvas for video, controls for loading ROM/BIOS files, and optional on-screen buttons.
2. **Loading games:** Provide a file picker for .gba and BIOS files, read binaries via the File API, and feed them into the emulator's memory arrays. Respect legal constraints by requiring user-supplied ROMs.
3. **Initialization:** Reset emulator state, load BIOS and ROM into their regions, clear RAM, set default register values, and either run the BIOS startup sequence or optionally skip it by mimicking its initialization.
4. **Main emulation loop:** Tie CPU execution to the browser clock (requestAnimationFrame or Worker loop). Run enough CPU cycles for one frame (~280,896 cycles per 1/60 s), render the resulting frame into a pixel buffer, and send it to the canvas via ImageData or WebGL. Mix the audio samples generated during that window and enqueue them to Web Audio (ScriptProcessor or AudioWorklet) to keep playback continuous. Repeat each frame while polling input and processing events.
5. **Audio handling:** Accumulate PCM samples per frame (or every N frames) and push them to the browser via Web Audio. A quick approach is to build an `AudioBuffer`, feed it through an `AudioBufferSourceNode`, and schedule playback back-to-back, accepting minor drift. For smoother playback, implement an `AudioWorklet` that reads from a `SharedArrayBuffer` the emulator fills so audio pulls rather than pushes. Blazor can call a small JS helper such as `playAudio(frameSamples)` through `IJSRuntime`; PyScript can invoke the same JS glue. If the audio pipeline proves unstable, temporarily disable audio or emit a placeholder tone so gameplay work can continue in parallel.
6. **Optimization for web:** 
   - Enable Ahead-of-Time (AOT) compilation for Blazor release builds even though it increases download size; the tighter CPU loop often needs the extra throughput. 
   - If running under Pyodide, consider moving hot loops to separately compiled WebAssembly modules (for example, a C extension that exposes the CPU step function) and call them from Python. 
   - Ship release builds with debug logging stripped or behind a verbosity flag, because frequent console output stalls browsers. 
   - Keep allocations bounded; a full GBA setup with ROM, RAM, frame buffers, and audio history fits comfortably within tens of megabytes, so avoid accidental large Python objects or unmanaged arrays. 
   - Offer optional frame skipping or dynamic throttling so users on slower hardware can trade smoothness for playability.
7. **Testing on web:** Exercise the emulator across Chromium, Firefox, and Safari, because each handles WebAssembly optimization, audio clocks, and input focus slightly differently. Validate both desktop and mobile browsers; mobile often enforces user-gesture requirements before audio can start and might struggle with CPU throughput, so consider adaptive quality settings and on-screen controls for touch devices.
8. **Packaging and distribution:** Host the generated static assets (HTML, JS, WASM, CSS, optional BIOS blob) on a static site provider such as GitHub Pages or Netlify. PyScript deployments only need a single HTML file plus your `.py` modules, but ensure Pyodide caches are warmed via service workers to reduce reload time. Require users to upload commercial ROMs themselves; if you want a demo, bundle only public-domain or homebrew titles. Use JavaScript `FileReader` to ingest BIOS/ROM bytes without full page reloads, and keep state in memory so the emulator survives minor UI interactions.
9. **Save data persistence:** Mirror SRAM or Flash writes into browser storage (IndexedDB or `localStorage`) keyed by a hash of the ROM header. Flush periodically or when the user exits to avoid data loss, and provide a UI button for exporting/importing save files. Blazor can leverage existing local storage packages; PyScript can call JavaScript directly to access storage APIs.
10. **UI/UX considerations:** Document the control scheme ("click the canvas and use keyboard/gamepad"), add focus management so key events are not lost, and expose remapping options. For mobile, draw a virtual D-pad and buttons overlayed on the canvas or use absolute-positioned HTML elements with transparency. Consider Gamepad API support for hardware controllers and provide quality-of-life toggles (mute audio, fast-forward, frame step) for power users.

## Conclusion
Building a GBA emulator from scratch is demanding but tractable when broken into CPU, memory, graphics, audio, and platform layers. C# provides a performant path to WebAssembly through Blazor with AOT, making it a strong default for a web-targeted emulator, while Python (especially via Pyodide) favors readability and rapid experimentation when raw speed is less critical. Lean on canonical documentation such as GBATEK, ARM7TDMI manuals, CowBite, and Tonc, and study mature open-source cores like mGBA, OptimeGBA, RustBoyAdvance-NG, and PyBoyAdvance to validate design decisions. Combine community test ROMs with in-browser QA across multiple devices to catch regressions early, and incrementally expand from BIOS execution to full graphics, audio, and save functionality. With steady iteration and disciplined testing, you can deliver a browser-ready GBA emulator that doubles as both a learning resource and a practical way to enjoy classic titles.

## Implementation Roadmap (C# .NET Core + Blazor WebAssembly)
Building a browser-based GBA emulator in C# is complex, so the work is organized into phases that prioritize getting graphics on screen quickly, allow ROMs such as Pokemon Fire Red to run without a BIOS, and ensure the Blazor UI works on desktop and mobile browsers.

### Phase 1: Planning and Project Setup
- Research the hardware (GBATEK, Copetti, ARM docs) and capture requirements: Mode 3 video first, BIOS optional, Blazor WASM front end.
- Design modules (CPU, MemoryBus, Cartridge, PPU, InputController, Emulator orchestrator) and decide on interfaces between the core library and UI.
- Create a .NET solution with a reusable core class library plus a Blazor WASM project that hosts a canvas and file input controls for ROM upload.
- Milestone: Solution scaffolded, stub classes exist, and the Blazor page accepts a ROM file (bytes stored for later use).

### Phase 2: Core Loop and CPU Interpreter
- Implement ARM7TDMI state (R0-R15, CPSR/SPSR) and an interpreted fetch-decode-execute loop that supports core ARM instructions, then add THUMB.
- Provide a minimal memory interface so the CPU can fetch instructions from ROM and use a scratch RAM block.
- Run CPU unit tests and community ROMs such as gba-suite or ARMWrestler to validate instructions; temporarily skip SWI/BIOS by jumping straight to cartridge entry.
- Milestone: CPU executes small programs deterministically without crashing.

### Phase 3: Memory Bus and ROM Loading
- Map the 32-bit address space (BIOS, EWRAM, IWRAM, I/O, palettes, VRAM, OAM, cartridge ROM/SRAM) in the MemoryBus and handle open bus behavior gracefully.
- Implement a Cartridge class that loads `.gba` data from the Blazor file picker and exposes it at 0x08000000; treat save RAM (0x0E000000) as a stub for now.
- Define reset behavior: initialize registers to post-BIOS defaults so Fire Red or other commercial ROMs run without requiring the proprietary BIOS (optionally allow user-supplied BIOS later).
- Milestone: Real game ROM code executes and advances PC through expected addresses (visible via logging) even though there is no video yet.

### Phase 4: Graphics Output (Mode 3 First)
- Implement enough of the PPU to support bitmap Mode 3 (VRAM as 240x160 RGB555 framebuffer) so homebrew/test ROMs can draw directly.
- Expose VRAM, palette RAM, DISPCNT, and related registers through the memory map; start with Mode 3 and force games into Mode 3 until more modes land.
- Use Blazor JS interop to blit the framebuffer to an HTML canvas every frame via `putImageData` or an `ImageBitmap`, sending the entire pixel buffer in a single call.
- Add a simple frame-step where the CPU runs a fixed number of cycles (approx 280k) per frame, then triggers a render, approximating VBlank.
- Milestone: A ROM draws recognizable graphics inside the browser canvas on both desktop and mobile (even if only Mode 3 content renders).

### Phase 5: Input and Controls
- Map keyboard events (arrows or WASD, Z/X, Enter, Right Shift, etc.) to the KEYINPUT register (active-low bits) and ensure the CPU reads real-time states.
- Build a responsive on-screen controller for touch devices (HTML divs or SVG overlays) with multitouch support so multiple buttons can be pressed simultaneously.
- Optional: plan for future Gamepad API integration by abstracting input sources.
- Milestone: Games respond to both keyboard and on-screen controls.

### Phase 6: Blazor Integration and Main Loop
- Drive the emulator via `requestAnimationFrame` or an async loop that steps one frame, renders, then yields to keep the UI responsive.
- Separate the emulator core from UI glue: core exposes `StepFrame()` and framebuffer accessors, Blazor handles canvas drawing, HUD, and input translation.
- Provide basic UI controls (Load ROM, Reset, Pause/Resume, FPS indicator) sized for touch and desktop.
- Milestone: End-to-end gameplay loop works - load ROM, run at ~60 FPS, control the game from desktop and mobile browsers.

### Phase 7: Performance Optimization
- Profile hotspots (CPU interpreter, memory reads, canvas blits) using browser dev tools.
- Publish Blazor with WebAssembly AOT enabled to boost CPU-bound code; ensure Release builds strip debug logging and reuse buffers.
- Optimize interpreter (opcode tables, `Span`/unsafe memory reads), consider frame skipping or throttling toggles, and test across Chrome, Firefox, Safari, and representative Android/iOS devices.
- Milestone: Representative games (e.g., Pokemon Fire Red intro) reach full speed on desktop and acceptable speed on mid-range mobile hardware.

### Phase 8: Deployment Strategy
- Configure the Blazor app as a PWA so it can be "installed" and run offline; verify service worker caching and full-screen behavior.
- Publish static assets to GitHub Pages, Netlify, or Azure Static Web Apps with HTTPS; support ROM drag-and-drop and file input on both desktop and mobile.
- Set up CI to build the core library, run CPU test ROMs, and produce deployable artifacts automatically.
- Milestone: Public URL available where users can load their own ROMs and play in-browser; PWA install works on mobile.

### Future Enhancements
- Audio emulation feeding samples into Web Audio (start with Direct Sound FIFOs, later add PSG channels and AudioWorklet streaming).
- Save states and persistent cartridge SRAM/Flash via IndexedDB or `localStorage` keyed by ROM hash.
- Full PPU feature set (tile modes, sprites with affine transforms, windowing, blending, Modes 4/5), timers, DMA, interrupt timing, and better cycle accounting.
- Gamepad API integration, custom key mapping UI, fullscreen toggle, FPS/debug overlays, and potential link cable support via WebRTC for multiplayer.

### Reference Projects and Resources
- Michel Heily's "Hello, GBA!" series (Medium) for step-by-step emulator insights.
- OSDev's Game Boy Advance Barebones article for Mode 3 framebuffers.
- Qkmaxware's BlazorBoy (Game Boy) and gb-net for examples of separating core logic and Blazor UI plus using .NET AOT in WebAssembly.
- Stack Overflow threads on Blazor canvas blitting via JS interop.
- Microsoft docs on Blazor WebAssembly AOT compilation and deployment best practices.
