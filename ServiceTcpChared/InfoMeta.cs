
/*
 * 
 * Protocole d'excution des trnasferts :
 * 1. L'appareil démarre et écoute les connexions entrantes (il demarre le serveur TCP et le client TCP)
 * 2. l'apareil 1 lancer le trnasfert via BLE en partageant les metadonnees, le port tcp, puis on initilise un canal de trnasmission(Wifidirect)
 * 3. l'appareil 2 se connecte à l'appareil 2 via le client TCP et l'address ip fournis via le canal de transmission ce qui arrete le serveur cote appareil 2 et demarre le serveur cote appareil 1, le client pareil.
 * 4. le client cote appareil 2 envoie la metadonnee du fichier à l'appareil 1, ce qui permet à l'appareil 1 de savoir quel fichier envoyer.
 * 5. l'appareil 1 envoie le fichier via le serveur TCP vers le client TCP de l'appareil 2.
 * 6. l'appareil 2 recoit le fichier et le sauvegarde dans le dossier de téléchargement.
 * 7. si le tableau de metadonnees est vide, le trnasfert est terminé, sinon on recommence à l'étape 4 pour un autre fichier.
 * 8. si un fichier est corrompu ou que le trnasfert est interrompu, l'appareil 2 peut renvoyer la metadonnee du fichier à l'appareil 1 pour recommencer le trnasfert du fichier.
*/

using Newtonsoft.Json;

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
        HachInfoDescription
    });

    public static InfoMeta? Object(string json)
    {
        try
        {
            var metadata = JsonConvert.DeserializeObject<MetadataPayload>(json);

            if (metadata is null || !IsValidMetadata(metadata.NameWithExtension, metadata.Length) ||
                string.IsNullOrWhiteSpace(metadata.SessionId) || !Guid.TryParseExact(metadata.SessionId, "N", out _))
            {
                return null;
            }

            return new InfoMeta(metadata.NameWithExtension, metadata.Length, metadata.HachInfoDescription, metadata.SessionId);
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

    private sealed class MetadataPayload
    {
        public string? NameWithExtension { get; set; }
        public long Length { get; set; }
        public string? SessionId { get; set; }
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
        HachInfoDescription = GetHashCode();
    }

    private InfoMeta(string nameWithExtension, long length, int hach, string sessionId)
    {
        if (!IsValidMetadata(nameWithExtension, length) ||
            !Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("Les métadonnées du fichier sont invalides.");
        }

        NameWithExtension = nameWithExtension;
        Length = length;
        SessionId = sessionId;
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

            infoMetas.Add(new(inf.NameWithExtension!, inf.Length, inf.HachInfoDescription, inf.SessionId));
        }

        return new([.. infoMetas]);
    }

    public string GetDirectFile(string downloadPath)
    {
        var direct = Path.Combine(downloadPath, NameWithExtension);
        if (File.Exists(direct))
        {
           return ReadapteFileName(direct);
        }
        else
        {
            return direct;
        }  
    }

    private string ReadapteFileName( string direct)
    {
        if(File.Exists(direct))
        {
            var n = Path.GetFileName(direct);
            if (n.StartsWith("Again_("))
            {
                var nb = n.Split('(')[1].Split(')')[0];
                _ = int.TryParse(nb, out int h);
                
                h++;
                var newnb = "Again_(" + $"{h}" + ")";
                try
                {
                    n = n.Replace("Again_(" + $"{nb}" + ")", newnb);
                    
                }
                catch(Exception)
                {
                    n = "s_" + n;
                }
                
            }
            else n = "Again_(1)" + n;

            string direc = direct.Replace(Path.GetFileName(direct), n);
            return ReadapteFileName(direc);
        }
        else
        {
            return direct;
        }
    }

    public string GetTempFile(string downloadPath, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith("."))
        {
            extension = ".san";
        }
        return Path.Combine(downloadPath, $"{NameWithoutExtension}{extension}");
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
               Length == other.Length;
    }

    public override int GetHashCode() => HashCode.Combine(SessionId, NameWithExtension, Length);

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

    public static bool operator ==(InfoMeta? left, InfoMeta? right) => EqualityComparer<InfoMeta>.Default.Equals(left, right);
    public static bool operator !=(InfoMeta? left, InfoMeta? right) => !(left == right);
}
