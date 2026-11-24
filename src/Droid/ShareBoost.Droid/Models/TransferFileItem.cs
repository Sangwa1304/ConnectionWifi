using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShareBoost.Droid.Models;

public sealed class TransferFileItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string LocalPath { get; }
    public long Length { get; }
    public string SizeLabel { get; }
    public ImageSource? Thumbnail { get; }
    public string Kind { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TransferFileItem(string name, string localPath, long length, ImageSource? thumbnail, string kind)
    {
        Name = name;
        LocalPath = localPath;
        Length = length;
        Thumbnail = thumbnail;
        Kind = kind;
        SizeLabel = FormatSize(length);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
