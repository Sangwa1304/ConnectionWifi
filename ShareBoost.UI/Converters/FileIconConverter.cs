using LibraryMI.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Core;

namespace ShareBoost.UI;

public class FileMiIcon
{
    static List<(FileI, Action<FileI>)> concurrents = new();

    static Task Icona = Task.CompletedTask;
    public static Task Add(FileI file, Action<FileI> retour, Func<DispatcherQueue> disp)
    {
        concurrents.Add((file, retour));
        if (Icona.Status != TaskStatus.Running)
        {
            concurrents.Clear();
            concurrents.Add((file, retour));

            Icona = Task.Run(async()=>
            {
                ModernThumbnailConverter conv = new(disp);
                while (concurrents.Count != 0)
                {
                    var bitmap = await conv.GetBitmap(concurrents[0].Item1.PathFile);
                    concurrents[0].Item1.IconeMemory = bitmap;
                    concurrents[0].Item2.Invoke(concurrents[0].Item1);
                    concurrents.RemoveAt(0);
                }
                return Task.CompletedTask;
            });
            return Icona;
        }
        return Task.CompletedTask;
    }

}
public partial class ModernThumbnailConverter : IValueConverter
{
    private const uint SIIGBF_RESIZETOFIT = 0x00;
    private const uint SIIGBF_BIGGERSIZEOK = 0x01;
    static Func<DispatcherQueue> Dispatcher;
    public ModernThumbnailConverter(Func<DispatcherQueue> queue)
    {
        Dispatcher = queue;
    }
    public async Task<object> Convert(object value, Type targetType, object parameter, string language)
    {
        return await GetBitmap(value);
    }
    public async Task<BitmapImage> GetBitmap(object value)
    {

        if (value is not string filePath ||
            string.IsNullOrWhiteSpace(filePath) ||
            !File.Exists(filePath))
        {
            return await GetDefaultIcon();
        }

        // 1. Miniature Windows Shell
        try
        {
            BitmapImage thumbnail = await
                 GetShellThumbnail(filePath, 128, 128);

            if (thumbnail != null)
                return thumbnail;
        }
        catch
        {
            // Fallback
        }

        // 2. Icône associée
        try
        {
            using (Icon icon =
                Icon.ExtractAssociatedIcon(filePath))
            {
                if (icon != null)
                    return await ConvertIconToImageSource(icon);
            }
        }
        catch
        {
            // Fallback
        }

        // 3. Icône générique
        return await GetDefaultIcon();
    }
    // -----------------------------------------
    // WINDOWS SHELL THUMBNAIL
    // -----------------------------------------

    private static async Task<BitmapImage> GetShellThumbnail(
        string filePath,
        int width,
        int height)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;

        IShellItemImageFactory factory = null;
        IntPtr hBitmap = IntPtr.Zero;

        try
        {
            int hr = SHCreateItemFromParsingName(
                filePath,
                IntPtr.Zero,
                ref iid,
                out factory);

            Marshal.ThrowExceptionForHR(hr);

            if (factory == null)
                return null;

            hr = factory.GetImage(
                new SIZE(width, height),
                SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK,
                out hBitmap);

            Marshal.ThrowExceptionForHR(hr);

            if (hBitmap == IntPtr.Zero)
                return null;

            using (Bitmap bitmap =
                System.Drawing.Image.FromHbitmap(hBitmap))
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;


                return await GetBit(stream.AsRandomAccessStream());
            }
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);

            if (factory != null &&
                Marshal.IsComObject(factory))
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }

    // -----------------------------------------
    // ICON CONVERSION
    // -----------------------------------------

    private async static Task<BitmapImage> ConvertIconToImageSource(
        Icon icon)
    {
        using (Bitmap bitmap = icon.ToBitmap())
        using (MemoryStream stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            return await GetBit(stream.AsRandomAccessStream());
        }
    }

    private static async Task<BitmapImage> GetBit(IRandomAccessStream randomAccessStream)
    {
        var tcs = new TaskCompletionSource<BitmapImage>();
        Dispatcher.Invoke()?.TryEnqueue(async () =>
        {
            var map = new BitmapImage();
            await map.SetSourceAsync(randomAccessStream);
            tcs.SetResult(map);
        });
        return await tcs.Task;
    }

    // -----------------------------------------
    // DEFAULT ICON
    // -----------------------------------------

    private static async Task<BitmapImage> GetDefaultIcon()
    {
        try
        {
            using (Icon icon =
                (Icon)SystemIcons.Application.Clone())
            {
                return await ConvertIconToImageSource(icon);
            }
        }
        catch
        {
            return null;
        }
    }

    // -----------------------------------------
    // COM INTEROP
    // -----------------------------------------

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In] SIZE size,
            [In] uint flags,
            out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int width, int height)
        {
            cx = width;
            cy = height;
        }
    }

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)]
        out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }

    object IValueConverter.Convert(object value, Type targetType, object parameter, string language)
    {
        return Convert(value, targetType, parameter, language).GetAwaiter().GetResult();
    }
}