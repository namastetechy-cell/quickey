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

## 2. Selling (Dodo Payments)

Lemon Squeezy stops taking payments on 31 March 2027, so licenses are sold through Dodo Payments
(merchant of record, supports Indian sellers and UPI).

1. Two products with **license keys on, activation limit 2, no expiry, tax-inclusive price**:
   "Quickey Pro" ($) and "Quickey Pro (India)" (₹699).
2. Put both product IDs into `Brand.LicenseServer` in `src/Quickey.App/Brand.cs`, and the checkout links
   (`https://checkout.dodopayments.com/buy/<product id>`) into the website's pricing section.
3. Test mode uses `LicenseServer.Test` and `test.checkout.dodopayments.com`; test-mode product IDs differ
   from live ones. At go-live switch to `LicenseServer.Live` with the live IDs and publish a release.
4. Test with a test-mode key: activate, deactivate, re-activate on another PC.

## 3. Code signing (important)

Unsigned installers trigger SmartScreen "Windows protected your PC" warnings and some antivirus flags
(low-level input hooks look suspicious to heuristics). Options: **Azure Trusted Signing** (cheapest,
needs a verifiable business identity) or an OV/EV certificate. Add `--signParams` / `--azureTrustedSignFile`
to the `vpk pack` step.

## 4. Updates

Releases are published to the public `namastetechy-cell/quickey-releases` repo (the `RELEASES_TOKEN`
secret lets CI write there), which is also the update feed (`Brand.UpdateFeed`) and hosts the website on
GitHub Pages.

## 5. Before you buy a domain

Get a trademark check on the name "Quickey". There is an older keyboard-macro product called *QuicKeys*.
