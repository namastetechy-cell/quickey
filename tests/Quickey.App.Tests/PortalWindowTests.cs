using System.IO;
using ImageMagick;
using Quickey.App.UI;
using Quickey.Core.Images;

namespace Quickey.App.Tests;

/// <summary>Drives the Portal photo window (Windows only, runs in CI).</summary>
public class PortalWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("quickey-portal-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Photo()
    {
        var path = Path.Combine(_folder, "selfie.jpg");
        using var image = new MagickImage("plasma:", 1200, 1600) { Quality = 95 };
        image.Write(path);
        return path;
    }

    [Fact]
    public void Presets_lock_the_crop_shape_and_estimate_the_real_size()
    {
        var photo = Photo();
        var preview = ImagePreview.Load(photo, 800);
        Sta.Run(async () =>
        {
            var window = new PortalWindow(photo, preview, 0, 1);
            window.Show();

            window.SelectPreset(PortalPreset.Passport);
            await window.UpdateEstimate();
            Assert.StartsWith("✓ Result: 413 × 531 px", window.ResultText);

            window.SelectPreset(PortalPreset.Signature);
            Assert.Equal(PortalPreset.Signature, window.Preset);

            window.Finish(new PortalChoice(PortalAction.Save, window.Preset, CropGeometry.Initial(1200, 1600, window.Preset!.Ratio)));
            var choice = (await window.Result)!;
            Assert.Equal(PortalAction.Save, choice.Action);
            Assert.Equal(140.0 / 60, choice.Crop.Width / choice.Crop.Height, 2);
        });
    }

    [Fact]
    public void Custom_sizes_in_cm_or_px_and_bad_input_is_explained()
    {
        var photo = Photo();
        var preview = ImagePreview.Load(photo, 800);
        Sta.Run(async () =>
        {
            var window = new PortalWindow(photo, preview, 0, 1);
            window.Show();

            window.SelectCustom("2", "2.5", "cm", "300", "5", "30");
            Assert.Equal((236, 295, 30), (window.Preset!.WidthPx, window.Preset.HeightPx, window.Preset.MaxKb));

            window.SelectCustom("300", "400", "px", "200", "", "80");
            Assert.Equal((300, 400, 0), (window.Preset!.WidthPx, window.Preset.HeightPx, window.Preset.MinKb));

            window.SelectCustom("300", "abc", "px", "200", "", "80");
            Assert.Null(window.Preset);
            Assert.Contains("numbers", window.ResultText);

            window.Close();
            Assert.Null(await window.Result); // closing cancels
        });
    }
}
