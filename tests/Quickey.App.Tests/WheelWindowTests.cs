using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using Quickey.App.Actions;
using Quickey.App.Native;
using Quickey.App.Office;
using Quickey.App.UI;
using Quickey.Core.Operations;
using Quickey.Core.Settings;

namespace Quickey.App.Tests;

/// <summary>
/// Drives the real wheel window (Windows only, runs in CI). Its job is to catch WPF mistakes that
/// only show up at runtime, like the one that crashed the app on the wheel's second redraw.
/// </summary>
public class WheelWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("quickey-ui-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "");
        return path;
    }

    private static readonly Win32.POINT Cursor = new() { X = 400, Y = 400 };

    private static WheelWindow NewWheel()
    {
        var dialogs = new Quickey.Core.Images.ImageDialogs(new CropPrompt(), new CompressPrompt(), new MetadataPrompt(), new EditPrompt());
        var registry = OperationRegistry.CreateDefault(dialogs).AddRange(OfficeOperations.All()).AddRange(FileActions.All());
        var settings = new AppSettings();
        return new WheelWindow(registry, () => settings);
    }

    /// <summary>WPF windows need an STA thread; rethrow anything that happens there.</summary>
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
    public void Survives_repeated_drags_and_category_switches()
    {
        var png = Touch("photo.png");
        var pdf = Touch("doc.pdf");
        OnSta(() =>
        {
            var wheel = NewWheel();
            for (var round = 0; round < 3; round++)
            {
                wheel.ShowForDrag(OperationCategory.Convert, Cursor);
                wheel.SimulateDragEnter([png]);
                Assert.Contains(wheel.Wedges, o => o.Id == "image.to.webp");

                wheel.ShowForDrag(OperationCategory.Tools, Cursor); // modifier tapped mid-drag
                Assert.Contains(wheel.Wedges, o => o.Id == "image.compress");
                Assert.Contains(wheel.Wedges, o => o.Id == "image.crop");
                Assert.Contains(wheel.Wedges, o => o.Id == "image.edit");
                Assert.Contains(wheel.Wedges, o => o.Id == "image.metadata");

                wheel.SimulateDragEnter([pdf]);
                Assert.Contains(wheel.Wedges, o => o.Id == "pdf.split");

                wheel.HideWheel();
                Assert.False(wheel.IsOpen);
            }
            wheel.Close();
        });
    }

    [Fact]
    public void Right_click_mode_shows_the_files_options()
    {
        var files = new[] { Touch("a.pdf"), Touch("b.pdf") };
        OnSta(() =>
        {
            var wheel = NewWheel();
            wheel.ShowForFiles(files, OperationCategory.Tools, Cursor);
            Assert.Contains(wheel.Wedges, o => o.Id == "pdf.merge");
            wheel.ShowForFiles(files, OperationCategory.Actions, Cursor);
            Assert.Contains(wheel.Wedges, o => o.Id == "action.copy.path");
            wheel.HideWheel();
            wheel.Close();
        });
    }

    [Fact]
    public void Shows_a_hint_when_nothing_applies()
    {
        var unknown = Touch("notes.xyz");
        OnSta(() =>
        {
            var wheel = NewWheel();
            wheel.ShowForDrag(OperationCategory.Convert, Cursor);
            Assert.Equal("Move here to see options", wheel.HintText);
            wheel.SimulateDragEnter([unknown]);
            Assert.Empty(wheel.Wedges);
            Assert.StartsWith("Nothing to convert", wheel.HintText);
            wheel.Close();
        });
    }

    [Fact]
    public void Hit_test_maps_directions_to_wedges()
    {
        var png = Touch("photo.png");
        OnSta(() =>
        {
            var wheel = NewWheel();
            wheel.ShowForDrag(OperationCategory.Convert, Cursor);
            wheel.SimulateDragEnter([png]);
            var count = wheel.Wedges.Count;
            const double c = 190; // wheel centre in canvas units

            Assert.Equal(-1, wheel.HitTest(new Point(c, c)));             // hub cancels
            Assert.Equal(-1, wheel.HitTest(new Point(c, c - 300)));       // far outside
            Assert.Equal(0, wheel.HitTest(new Point(c, c - 120)));        // 12 o'clock is the first wedge
            Assert.Equal(count / 2, wheel.HitTest(new Point(c, c + 120))); // 6 o'clock is halfway round
            wheel.Close();
        });
    }
}
