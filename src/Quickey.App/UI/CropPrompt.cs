using System.IO;
using System.Windows;
using Quickey.Core.Images;

namespace Quickey.App.UI;

/// <summary>Shows the crop window for each image the Crop wedge runs on.</summary>
public sealed class CropPrompt : ICropPrompt
{
    public async Task<CropDecision> AskAsync(string file, int index, int count, CancellationToken cancellationToken)
    {
        // Decoding a large photo takes a moment; keep it off the UI thread.
        var preview = await Task.Run(() => ImagePreview.Load(file), cancellationToken);
        var window = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var crop = new CropWindow(preview, Path.GetFileName(file), index, count);
            crop.Show();
            return crop;
        });
        return await window.Result;
    }
}
