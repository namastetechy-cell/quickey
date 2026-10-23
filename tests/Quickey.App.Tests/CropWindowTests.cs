using System.IO;
using System.Runtime.ExceptionServices;
using ImageMagick;
using Quickey.App.UI;
using Quickey.Core.Images;

namespace Quickey.App.Tests;

// The window completes Result synchronously inside Finish/Close, so reading it can't block or deadlock.
#pragma warning disable xUnit1031

/// <summary>Drives the real crop window (Windows only, runs in CI).</summary>
public class CropWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("quickey-crop-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private ImagePreview Preview(uint width, uint height)
    {
        var path = Path.Combine(_folder, "photo.png");
        using (var image = new MagickImage("gradient:red-blue", width, height))
            image.Write(path);
        return ImagePreview.Load(path);
    }

    private static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void Presets_and_drags_produce_the_expected_crop()
    {
        var preview = Preview(1920, 1080);
        OnSta(() =>
        {
            var window = new CropWindow(preview, "photo.png", 0, 1);
            window.Show();

            window.SelectPreset("9:16");
            Assert.Equal(9.0 / 16, window.Selection.Width / window.Selection.Height, 6);
            Assert.Equal(1080, window.Selection.Height, 6);

            window.SelectPreset("1:1");
            window.SimulateDrag(CropHandle.TopLeft, 100, 100);
            Assert.Equal(1, window.Selection.Width / window.Selection.Height, 6);
            Assert.True(window.Selection.Width < 1080);

            window.SelectPreset("Free");
            window.SimulateDrag(CropHandle.Right, -50, 0);
            Assert.True(window.Selection.Width < 1920);
            Assert.Equal(1080, window.Selection.Height, 6);

            var expected = window.Selection;
            window.Finish(CropChoice.Crop);
            var decision = window.Result.GetAwaiter().GetResult();
            Assert.Equal(CropChoice.Crop, decision.Choice);
            Assert.Equal(expected, decision.Rect);
        });
    }

    [Fact]
    public void Closing_the_window_cancels()
    {
        var preview = Preview(800, 600);
        OnSta(() =>
        {
            var window = new CropWindow(preview, "photo.png", 0, 3);
            window.Show();
            window.Close();
            Assert.Equal(CropChoice.CancelAll, window.Result.GetAwaiter().GetResult().Choice);
        });
    }
}
