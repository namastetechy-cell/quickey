using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Quickey.App.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace Quickey.App.Actions;

/// <summary>Opens the Windows share sheet (Nearby Sharing, Mail, Teams...) for files, near the cursor.</summary>
internal static class ShareSheet
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private static Window? _anchor;

    /// <summary>
    /// The share sheet needs a visible owner window. The wheel is already hidden by the time an action
    /// runs, so a 1×1 transparent tool window at the cursor stands in.
    /// </summary>
    private static IntPtr Anchor()
    {
        _anchor ??= new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
        };
        var handle = new WindowInteropHelper(_anchor).EnsureHandle();
        _anchor.Show();
        Win32.GetCursorPos(out var cursor);
        Win32.CenterOn(handle, cursor);
        return handle;
    }

    public static void Show(IReadOnlyList<string> files)
    {
        var window = Anchor();
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var manager = DataTransferManager.FromAbi(interop.GetForWindow(window, ref iid));

        void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            sender.DataRequested -= OnDataRequested;
            var deferral = args.Request.GetDeferral();
            args.Request.Data.Properties.Title = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Count} files";
            _ = Task.Run(async () =>
            {
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var file in files)
                        items.Add(await StorageFile.GetFileFromPathAsync(file));
                    args.Request.Data.SetStorageItems(items);
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }

        manager.DataRequested += OnDataRequested;
        interop.ShowShareUIForWindow(window);
    }
}
