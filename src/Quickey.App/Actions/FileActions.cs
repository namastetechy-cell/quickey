using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using Quickey.Core.Operations;

namespace Quickey.App.Actions;

/// <summary>
/// The Actions wheel: everyday file chores. Works on any file or folder and never uses up the
/// free tier's conversions.
/// </summary>
public static class FileActions
{
    public static IEnumerable<IOperation> All()
    {
        yield return new FileAction("action.copy.path", "Copy path", Glyphs.Link, _ => true, files =>
            OnUi(() => Clipboard.SetText(string.Join(Environment.NewLine, files.Select(f => files.Count > 1 ? $"\"{f}\"" : f)))));

        yield return new FileAction("action.copy.name", "Copy name", Glyphs.Text, _ => true, files =>
            OnUi(() => Clipboard.SetText(string.Join(Environment.NewLine, files.Select(Path.GetFileName)))));

        yield return new FileAction("action.copy.image", "Copy image", Glyphs.Photo,
            files => files.Count == 1 && FileKinds.Detect(files[0]) == FileKind.Image,
            files => OnUi(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(files[0]);
                bitmap.EndInit();
                Clipboard.SetImage(bitmap);
            }));

        yield return new FileAction("action.copy.to", "Copy to…", Glyphs.Copy, _ => true, files =>
            OnUi(() => Transfer(files, move: false)));

        yield return new FileAction("action.move.to", "Move to…", Glyphs.Move, _ => true, files =>
            OnUi(() => Transfer(files, move: true)));

        yield return new FileAction("action.terminal", "Terminal", Glyphs.Terminal, files => files.Count == 1, files =>
            OpenTerminal(Directory.Exists(files[0]) ? files[0] : Path.GetDirectoryName(files[0])!));

        yield return new FileAction("action.share", "Share", Glyphs.Share, files => files.All(File.Exists), files =>
            OnUi(() => ShareSheet.Show(files)));
    }

    private static void OnUi(Action action) => Application.Current.Dispatcher.Invoke(action);

    /// <summary>Copies or moves with Explorer's own progress, conflict and undo-friendly dialogs.</summary>
    private static void Transfer(IReadOnlyList<string> files, bool move)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = move ? "Move to" : "Copy to" };
        if (dialog.ShowDialog() != true)
            return;

        foreach (var source in files)
        {
            var destination = Path.Combine(dialog.FolderName, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
            if (Directory.Exists(source))
            {
                if (move) FileSystem.MoveDirectory(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
                else FileSystem.CopyDirectory(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
            }
            else
            {
                if (move) FileSystem.MoveFile(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
                else FileSystem.CopyFile(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
            }
        }
    }

    /// <summary>Windows Terminal if it's installed, otherwise PowerShell.</summary>
    private static void OpenTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder }, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
    }

    private sealed class FileAction(string id, string label, string glyph, Func<IReadOnlyList<string>, bool> appliesTo, Action<IReadOnlyList<string>> run) : IOperation
    {
        public string Id => id;
        public string Label => label;
        public string Glyph => glyph;
        public OperationCategory Category => OperationCategory.Actions;
        public bool CountsTowardLimit => false;

        public bool AppliesTo(IReadOnlyList<string> files) => files.Count > 0 && appliesTo(files);

        public Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken)
        {
            var result = new OperationResult(Label);
            try
            {
                run(files);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new OperationError(files[0], ex.Message));
            }
            return Task.FromResult(result);
        }
    }
}
