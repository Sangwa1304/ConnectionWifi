using Newtonsoft.Json;
using System.Security.Cryptography;

namespace ServiceTcpChared;

public class InfoMetas(IEnumerable<InfoMeta> metas)
{
    public InfoMeta[] Metas { get; } = [.. metas];
    public long Length => Count();

    private long Count()
    {
        long i = 0;
        foreach(var r in Metas)
        {
            i += r?.Length?? 0;
        }
        return i;
    }
}

public class InfoMeta : IEquatable<InfoMeta?>
{
    internal const int MaxFileNameLength = 255;
    internal const int MaxFilesPerTransfer = 10_000;
    internal const long MaxFileLength = 1L * 1024 * 1024 * 1024 * 1024;

    public bool Etat = false;
    public readonly string NameWithExtension;
    public readonly string SessionId;
    public readonly string FileHash;
    private readonly int HachInfoDescription;
    public readonly long Length;

    // Correction : Utilisation sécurisée de Path.GetFileNameWithoutExtension
    private string NameWithoutExtension => Path.GetFileNameWithoutExtension(NameWithExtension);
    public record InfoMetaArgs(InfoMeta[] Meta);

    [JsonIgnore]
    public string Json => JsonConvert.SerializeObject(new
    {
        NameWithExtension,
        Length,
        SessionId,
        FileHash,
        HachInfoDescription
    });

    public static InfoMeta? Object(string json)
    {
        try
        {
            var metadata = JsonConvert.DeserializeObject<MetadataPayload>(json);

            if (metadata is null || !IsValidMetadata(metadata.NameWithExtension, metadata.Length) ||
                !IsValidHash(metadata.FileHash) ||
                string.IsNullOrWhiteSpace(metadata.SessionId) || !Guid.TryParseExact(metadata.SessionId, "N", out _))
            {
                return null;
            }

            return new InfoMeta(metadata.NameWithExtension, metadata.Length, metadata.HachInfoDescription, metadata.SessionId, metadata.FileHash);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur de désérialisation des métadonnées : {ex.Message}");
            return null;
        }
    }

    public static int GetInfoJson(string json)
    {
        var meta = Object(json);
        if(meta != null)
        {
            return meta.HachInfoDescription;
        }
        return 0;
    }

    sealed class MetadataPayload
    {
        public string? NameWithExtension { get; set; }
        public long Length { get; set; }
        public string? SessionId { get; set; }
        public string? FileHash { get; set; }
        public int HachInfoDescription { get; set; }
    }

    public InfoMeta(string file, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("Le chemin du fichier est obligatoire.", nameof(file));
        }

        var inf = new FileInfo(file);
        if (!inf.Exists)
        {
            throw new FileNotFoundException("Le fichier source est introuvable.", file);
        }

        Length = inf.Length;
        NameWithExtension = inf.Name;
        SessionId = NormalizeSessionId(sessionId);
        FileHash = ComputeFileHash(file);
        HachInfoDescription = GetHashCode();
    }

    private InfoMeta(string nameWithExtension, long length, int hach, string sessionId, string fileHash)
    {
        if (!IsValidMetadata(nameWithExtension, length) ||
            !Guid.TryParseExact(sessionId, "N", out _) ||
            !IsValidHash(fileHash))
        {
            throw new ArgumentException("Les métadonnées du fichier sont invalides.");
        }

        NameWithExtension = nameWithExtension;
        Length = length;
        SessionId = sessionId;
        FileHash = fileHash;
        HachInfoDescription = hach;
    }
    public static InfoMetaArgs GetInfoMetaArgs(Dictionary<InfoMeta, string> metasDictionnarys)
    {
        InfoMeta[] metas = new InfoMeta[metasDictionnarys.Count];
        metasDictionnarys.Keys.CopyTo(metas, 0);
        return new InfoMetaArgs(metas);
    }

    public static string GetJsonMetasTabel(InfoMetaArgs metaArgs)
    {
        List<MetadataPayload> infoMetas = new();

        foreach(var inf in metaArgs.Meta)
        {
            infoMetas.Add(new MetadataPayload
            {
                NameWithExtension = inf.NameWithExtension,
                Length = inf.Length,
                SessionId = inf.SessionId,
                FileHash = inf.FileHash,
                HachInfoDescription = inf.HachInfoDescription
            });
        }

        return JsonConvert.SerializeObject(infoMetas);
    }

    public static InfoMetaArgs GetObjectMetasTabel(string metaArgs)
    {
        var liste = JsonConvert.DeserializeObject<List<MetadataPayload>>(metaArgs)
            ?? throw new JsonException("La liste de métadonnées est vide ou invalide.");

        if (liste.Count == 0 || liste.Count > MaxFilesPerTransfer)
        {
            throw new JsonException("Le nombre de fichiers de la session est invalide.");
        }

        string? sessionId = null;
        List<InfoMeta> infoMetas = new();
        foreach (var inf in liste)
        {
            if (inf.SessionId is null || !Guid.TryParseExact(inf.SessionId, "N", out _))
            {
                throw new JsonException("La session de métadonnées est invalide.");
            }

            sessionId ??= inf.SessionId;
            if (!string.Equals(sessionId, inf.SessionId, StringComparison.Ordinal) ||
                !IsValidMetadata(inf.NameWithExtension, inf.Length))
            {
                throw new JsonException("Les métadonnées de la session sont incohérentes.");
            }

            if (!IsValidHash(inf.FileHash))
            {
                throw new JsonException("L'empreinte du fichier est invalide.");
            }

            infoMetas.Add(new(inf.NameWithExtension!, inf.Length, inf.HachInfoDescription, inf.SessionId, inf.FileHash));
        }

        return new([.. infoMetas]);
    }

    public string GetDirectFile(string downloadPath)
    {
        string root = Path.GetFullPath(downloadPath);
        string direct = Path.Combine(root, NameWithExtension);
        if (!IsPathInDirectory(root, direct))
        {
            throw new InvalidDataException("Le chemin de destination est invalide.");
        }

        if (!File.Exists(direct))
        {
            return direct;
        }

        return ReadapteFileName(direct);
    }

    private static string ReadapteFileName(string direct)
    {
        string directory = Path.GetDirectoryName(direct)
            ?? throw new InvalidDataException("Le dossier de destination est invalide.");
        string name = Path.GetFileName(direct);

        for (int attempt = 1; attempt <= 100_000; attempt++)
        {
            string candidate = Path.Combine(directory, $"Again_({attempt}){name}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Impossible de déterminer un nom de destination disponible.");
    }

    public string GetTempFile(string downloadPath, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith("."))
        {
            extension = ".san";
        }
        string root = Path.GetFullPath(downloadPath);
        string temporary = Path.Combine(root, $".{SessionId}.{FileHash}{extension}");
        if (!IsPathInDirectory(root, temporary))
        {
            throw new InvalidDataException("Le chemin temporaire est invalide.");
        }

        return temporary;
    }

    internal static Dictionary<InfoMeta, string> GetMetas(string[] files, string? sessionId = null)
    {
        var pathlist = new Dictionary<InfoMeta, string>();
        foreach (var file in files)
        {
            try
            {
                var meta = new InfoMeta(file, sessionId);
                pathlist[meta] = file;
            }
            catch (Exception)
            {
                continue;
            }
        }
        return pathlist;
    }

    internal static bool GetInstanceDirectory(InfoMeta meta, Dictionary<InfoMeta, string> files, out string path)
    {
        foreach (var file in files.Keys)
        {
            if (file.SessionId == meta.SessionId && file.HachInfoDescription == meta.HachInfoDescription)
            {
                path = files[file];
                return true;
            }
            if (file == meta)
            {
                path = files[file];
                return true;
            }
        }
        
        path = "";
        return false;
    }

    public override bool Equals(object? obj) => Equals(obj as InfoMeta);

    public bool Equals(InfoMeta? other)
    {
        return other is not null &&
               SessionId == other.SessionId &&
               NameWithExtension == other.NameWithExtension &&
               Length == other.Length &&
               FileHash == other.FileHash;
    }

    public override int GetHashCode() => HashCode.Combine(SessionId, NameWithExtension, Length, FileHash);

    internal static string ComputeFileHash(string file)
    {
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string NormalizeSessionId(string? sessionId) =>
        Guid.TryParseExact(sessionId, "N", out Guid value)
            ? value.ToString("N")
            : Guid.NewGuid().ToString("N");

    private static bool IsValidMetadata(string? name, long length)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFileNameLength ||
            length < 0 || length > MaxFileLength || Path.GetFileName(name) != name ||
            Path.IsPathRooted(name) || name is "." or "..")
        {
            return false;
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               name.All(character => !char.IsControl(character));
    }

    private static bool IsValidHash(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) && hash.Length == 64 &&
        hash.All(Uri.IsHexDigit);

    private static bool IsPathInDirectory(string directory, string path)
    {
        string normalizedDirectory = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public static bool operator ==(InfoMeta? left, InfoMeta? right) => EqualityComparer<InfoMeta>.Default.Equals(left, right);
    public static bool operator !=(InfoMeta? left, InfoMeta? right) => !(left == right);
}
