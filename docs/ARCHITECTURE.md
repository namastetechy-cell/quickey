# How Quickey works

## The drag-and-key wheel

Windows has no public "a file drag started" event, so Quickey watches input instead:

1. **Low-level hooks** (`Input/DragWatcher.cs`). `WH_MOUSE_LL` notes a left-button press and when the
   pointer has moved past the system drag threshold. `WH_KEYBOARD_LL` watches for Shift/Ctrl/Alt
   being *newly* pressed during that drag. Nothing is swallowed; Explorer still sees every input.
   With "Explorer only" on (default), drags must start in a window of class `CabinetWClass`
   (File Explorer) or `Progman`/`WorkerW` (desktop).
2. **The wheel is a drop target** (`UI/WheelWindow.cs`). A topmost, transparent, non-activating window
   opens centred under the cursor. Because Explorer is already running an OLE drag, the wheel simply
   receives `DragEnter`/`DragOver`/`Drop` like any drop target, and the dropped data carries the file
   paths. This is what makes it reliable: no guessing which files were selected.
3. **Copy, never Move.** The wheel always reports `DragDropEffects.Copy`. A Move effect would make
   Explorer delete the originals after the drop.
4. **Closing.** Dropping onto a wedge runs it. Releasing elsewhere, dropping in the hub, or Esc closes the
   wheel. Releasing the button starts a short timer because the mouse-up hook can arrive before the drop.

The hook can't tell a file drag from a rubber-band selection; in that case the wheel opens with
"Move here to see options" and closes on release. Harmless.

**Integrity levels:** an elevated Quickey can't receive drops from a non-elevated Explorer (UIPI), so the
manifest pins `asInvoker`. Never run Quickey as administrator.

## Right-click menu

`Shell/ShellIntegration.cs` adds a per-user (`HKCU`) verb for files and folders. Explorer launches
`Quickey.exe --files "<path>"` once per selected file; each launch forwards its arguments over a named
pipe to the running instance (`Services/SingleInstance.cs`), which batches them for 300 ms and opens the
wheel in click mode.

## Operations

Everything on a wheel implements `IOperation` (`Quickey.Core/Operations`). An operation declares its
wheel, which selections it applies to, whether it counts toward the free allowance, and optionally why
it's unavailable (shown greyed out). The registry builds each wheel from whatever applies to the
dropped files, so mixed selections just show fewer options.

| Area | Library | Notes |
|------|---------|-------|
| Images | Magick.NET (Apache-2.0) | Reads HEIC/PSD/SVG/TGA but can't write HEIC (HEVC patents). |
| PDF structure | PDFsharp 6.2.4 (MIT) | Merge, split, rotate, recompress. Do not upgrade: 6.5+ is AGPL. |
| PDF rendering | PDFtoImage / PDFium (MIT) | PDF → PNG/JPG at 150 DPI. |
| PDF text | PdfPig (Apache-2.0) | Reading-order text extraction. |
| Office | Installed Microsoft Office via COM | One hidden Office instance per batch, on an STA thread, always quit. |

## Licensing

`Quickey.Core/Licensing` talks to the Lemon Squeezy License API. No API secret ships in the app; those
endpoints take the license key as the credential. Activation is accepted only if the key belongs to the
store and product IDs in `Brand.cs`. The activation is stored DPAPI-encrypted (current user), re-validated
at most daily, and keeps working offline for 14 days. The free allowance is a local counter, which a
determined user could reset. That's an accepted trade-off for a low-friction free tier.

## Updates

Velopack. `Program.Main` runs Velopack's hooks first (and removes the registry entries on uninstall).
When `UpdateUrl` is set in settings, Quickey checks every 6 hours, downloads quietly, and installs on quit.
