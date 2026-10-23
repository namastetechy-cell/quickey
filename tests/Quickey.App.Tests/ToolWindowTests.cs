using System.IO;
using ImageMagick;
using Quickey.App.UI;
using Quickey.Core.Images;

namespace Quickey.App.Tests;

/// <summary>Drives the compress, metadata and edit windows (Windows only, runs in CI).</summary>
public class ToolWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("quickey-tools-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Photo(string name = "photo.jpg")
    {
        var path = Path.Combine(_folder, name);
        using var image = new MagickImage("plasma:", 800, 600) { Quality = 97 };
        image.Write(path);
        return path;
    }

    [Fact]
    public void Compress_window_builds_options_and_estimates()
    {
        var photo = Photo();
        Sta.Run(async () =>
        {
            var window = new CompressWindow([photo]);
            window.Show();

            window.SetMode(CompressMode.TargetSize);
            window.SetTarget("60", "KB");
            Assert.Equal(60 * 1024, window.Options()!.TargetBytes);

            await window.UpdateEstimate();
            Assert.Contains("→", window.EstimateText);

            window.SetTarget("not a number", "KB");
            Assert.Null(window.Options());

            window.SetMode(CompressMode.Preset);
            var options = window.Options()!;
            window.Finish(options);
            Assert.Equal(CompressMode.Preset, (await window.Result)!.Mode);
        });
    }

    [Fact]
    public void Metadata_window_shows_tags_and_only_writes_changed_fields()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        MetadataEditor.Write(a, a, new MetadataChange { Make = "Nikon", Artist = "Asha" });
        MetadataEditor.Write(b, b, new MetadataChange { Make = "Nikon", Artist = "Ravi" });

        Sta.Run(async () =>
        {
            var window = new MetadataWindow([a, b]);
            window.Show();

            Assert.Equal("Nikon", window.FieldNamed("Camera make").Box.Text); // shared
            Assert.Equal("", window.FieldNamed("Author").Box.Text);            // differs

            window.FieldNamed("Copyright").Box.Text = "© Studio";
            var change = window.Change()!;
            Assert.Equal("© Studio", change.Copyright);
            Assert.Null(change.Artist); // untouched: each photo keeps its own
            Assert.Null(change.Make);

            window.FieldNamed("Date taken").Box.Text = "whenever";
            Assert.Null(window.Change());

            window.FieldNamed("Date taken").Box.Text = "2024-05-01 14:30";
            window.FieldNamed("Location").Box.Text = "";
            window.Overwrite = true;
            change = window.Change()!;
            Assert.Equal("", change.Location);
            Assert.True(change.OverwriteOriginal);

            window.Finish(change);
            Assert.Same(change, await window.Result);
        });
    }

    [Fact]
    public void Edit_window_tracks_adjustments_and_renders_previews()
    {
        var photo = Photo();
        var preview = ImagePreview.Load(photo, maxSide: 400);
        Sta.Run(async () =>
        {
            var window = new EditWindow([photo], preview);
            window.Show();

            window.SetSlider("Exposure", 40);
            window.SetSlider("Vignette", 30);
            window.SetEffect(PhotoEffect.Sepia);
            await window.RenderNowAsync();

            Assert.Equal(new PhotoAdjustments { Exposure = 40, Vignette = 30, Effect = PhotoEffect.Sepia }, window.Adjustments);

            window.Close();
            Assert.Null(await window.Result); // closing cancels
        });
    }
}
