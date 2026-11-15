using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LibraryMI.Models;

public class FileI : IEquatable<FileI?>
{
    public bool IsPlayer => FileIs == Types.Music || FileIs == Types.Video;
    public bool IsMusic => FileIs == Types.Music;
    public bool IsVideo => FileIs == Types.Video;
    public readonly Types FileIs = Types.Others;
    public string PathFile { get; }
    public string Name { get; }
    public long Size { get; }
    public string Sizee => Type == TypeFile.Folder ? "Dossier" : FormatBytes(Size);
    public TypeFile Type { get; }

    public static IEnumerable<string> AsEnumerable(IEnumerable<FileI> Enums)
    {
        return from r in Enums select r.PathFile;
    }

    public FileI(string path)
    {
        if (Path.Exists(path))
        {
            var inf = new FileInfo(path);
            PathFile = path;
            Name = inf.Name;
            if(musicExtention.Contains(Path.GetExtension(path)))
            {
                FileIs = Types.Music;
            }
            else if (VideoExtention.Contains(Path.GetExtension(path)))
            {
                FileIs = Types.Video;
            }
            if (Directory.Exists(path)) 
            { 
                Type = TypeFile.Folder; 
                Size = 0; 
            }
            else 
            { 
                Type = TypeFile.File; 
                Size = inf.Length; 
            }
        }
        else 
        {
            throw new ArgumentException($"{path} n'existe pas ou est introuvable.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} octets";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.##} Ko";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.##} Mo";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} Go";
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as FileI);
    }

    public bool Equals(FileI? other)
    {
        return other is not null &&
               PathFile == other.PathFile &&
               Name == other.Name &&
               Size == other.Size;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(PathFile, Name, Size);
    }
    public string[] musicExtention =
        [
        ".mp3"
        ];
    public string[] VideoExtention =
        [
        ".mp4"
        ];
    public static bool operator ==(FileI? left, FileI? right)
    {
        return EqualityComparer<FileI>.Default.Equals(left, right);
    }

    public static bool operator !=(FileI? left, FileI? right)
    {
        return !(left == right);
    }
}

public enum Types
{
    Image,
    Video,
    Music,
    Documents,
    Others,
}
 
