using System.IO;

namespace WindowsWifiWpf.Models;

public class FileI
{
    public string PathFile { get; }
    public string Name { get; }

    public long Size { get; }
    public string Sizee { get => $"{Size} octet"; }
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

            if (Directory.Exists(path)) { Type = TypeFile.Folder; Size = 0; }
            else { Type = TypeFile.File; Size = inf.Length; }

        }
        else throw new ArgumentException($"{path} does'nt exist ");
    }
}
