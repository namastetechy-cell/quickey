# Launch checklist

## 1. Test on Windows (I couldn't. The code was built and unit-tested on Linux.)

Download `Quickey-win-Setup.exe` from the latest Actions run, install, then check:

- [ ] Drag a PNG in Explorer, tap **Shift**: wheel opens under the cursor; drop on **WebP** → `.webp` appears, original untouched.
- [ ] Mid-drag, tap **Ctrl** then **Alt**: wheel switches in place.
- [ ] Drop in the hub, press **Esc**, or release elsewhere: wheel closes, nothing happens, **no file is moved or deleted**.
- [ ] Same from the desktop; on a second monitor with different scaling (wheel should be centred on the cursor).
- [ ] Rubber-band select + Shift: wheel may appear and must close on release.
- [ ] Select 3 PDFs → right-click → Show more options → *Convert with Quickey*: one wheel for all 3, **Ctrl** → Merge.
- [ ] DOCX → PDF and PPTX → PDF with Office installed; greyed out "Needs Microsoft Word" without it.
- [ ] Alt wheel: Copy path, Terminal, Share (share sheet opens near the cursor), Move to…
- [ ] 11th conversion in a day shows the "Daily limit reached" notification.
- [ ] Settings: change keys, toggle right-click menu and start with Windows, then reboot.
- [ ] Uninstall from Settings → Apps: right-click entry and startup entry are gone.

## 2. Selling (Lemon Squeezy)

1. Create a store and a product "Quickey Pro" with **license keys enabled** (e.g. 2–3 activations per key).
2. Put the store ID, product ID and checkout URL into `src/Quickey.App/Brand.cs`.
3. Test with a test-mode key: activate, deactivate, re-activate on another PC.

## 3. Code signing (important)

Unsigned installers trigger SmartScreen "Windows protected your PC" warnings and some antivirus flags
(low-level input hooks look suspicious to heuristics). Options: **Azure Trusted Signing** (cheapest,
needs a verifiable business identity) or an OV/EV certificate. Add `--signParams` / `--azureTrustedSignFile`
to the `vpk pack` step.

## 4. Updates

Host the `Releases/` folder contents on your site or a bucket (e.g. `https://downloads.<your-domain>/quickey`)
and set that URL as the default `UpdateUrl` in `AppSettings`. Each release: upload the new files next to
the old ones.

## 5. Before you buy a domain

Get a trademark check on the name "Quickey". There is an older keyboard-macro product called *QuicKeys*.
