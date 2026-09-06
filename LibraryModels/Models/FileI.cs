using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LibraryMI.Models;

public class FileI
{
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
}
