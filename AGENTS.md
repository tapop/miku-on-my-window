# Working on Miku on My Window

This is a lightweight Windows x64 desktop pet, implemented with C#/.NET Framework 4.x, WinForms, and Win32 layered windows. There is no browser runtime, package manager, or network dependency in the application.

Read `HANDOFF.md`, `README.md`, and `사용법.txt` before changing behavior. The current implementation is `source/MikuDesktop.cs`. The embedded sprite atlas, icon, manifest, and build/verification scripts are in `source/`.

## Build and validation

- Build on Windows using `source/build.ps1`. This uses the Windows .NET Framework C# compiler and produces `MikuDesktop.exe` in the repository root. `-OutputPath` can select another destination.
- For validation, close the app on the testing desktop and run `source/verify.ps1`. It invokes behavior checks, native-window checks, and a short performance sample.
- Keep generated executables, portable ZIPs, local position/settings files, and temporary test outputs out of Git.
- An already-running executable is locked on Windows. Build to a separate path, close only this application's instance normally, then replace it and restart when applying changes.

## Behavior and performance

- Typing uses only the original focus animation (human list item 8; zero-based atlas row 7). Maintain the 1.5-second default after the last eligible key press.
- Only jumping completes its animation cycle after its trigger ends. Dragging can interrupt jumping immediately. Other animations switch when their triggers expire.
- Original idle column 0 is intentionally excluded. The five remaining columns are indexed through `Atlas.Index`; the blink cycle is approximately 5.3 seconds.
- There are no idle/focus interpolation frames. The runtime caches 48 used original frames.
- A cursor held over Miku repeats jumping. Dragging shows the direction of movement and suppresses jumping.
- Menus open from the tray only; right-clicking Miku does not open a menu.
- At 100%, the actual window is 192×208 physical pixels regardless of Windows DPI. Preserve transparent hit-testing, no focus stealing, no taskbar/Alt+Tab button, and monitor/work-area clamping based on visible sprite pixels.
- Reuse cached native bitmaps. Do not introduce file reads, image decoding, or managed bitmap creation per rendered frame. Hidden/paused states unregister Raw Input and stop animation timers.
- Keyboard events are timing-only: do not convert, save, or transmit typed text.

## Continuing on another PC

This repository carries source and project context, not the previous Codex chat or account credentials. Use the cloned folder as the Codex project. Do not hard-code the original user's paths or assume a specific monitor layout.

Wheel/pinch zoom and switching sprite sets above a zoom threshold have only been discussed; they are not implemented or authorized for implementation yet. See `HANDOFF.md` for the proposed design.
