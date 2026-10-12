
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
    public InfoMeta[] Metas { get; } = [..metas];
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
    public bool Etat = false;
    public readonly string NameWithExtension;
    private readonly int HachInfoDescription;
    public readonly long Length;

    // Correction : Utilisation sécurisée de Path.GetFileNameWithoutExtension
    private string NameWithoutExtension => Path.GetFileNameWithoutExtension(NameWithExtension);
    public record InfoMetaArgs(InfoMeta[] Meta);

    [JsonIgnore]
    public string Json => JsonConvert.SerializeObject(new
    {
        NameWithExtension,
        Length
    });

    public static InfoMeta? Object(string json)
    {
        try
        {
            var metadata = JsonConvert.DeserializeObject<MetadataPayload>(json);

            if (metadata is null || string.IsNullOrWhiteSpace(metadata.NameWithExtension))
            {
                return null;
            }

            return new InfoMeta(metadata.NameWithExtension, metadata.Length,metadata.HachInfoDescription);
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
        public int HachInfoDescription { get; set; }
    }

    public InfoMeta(string file)
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
        HachInfoDescription = GetHashCode();
    }

    private InfoMeta(string nameWithExtension, long length,int hach)
    {
        NameWithExtension = nameWithExtension;
        Length = length;
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
            infoMetas.Add(new MetadataPayload { NameWithExtension = inf.NameWithExtension, Length = inf.Length, HachInfoDescription = inf.HachInfoDescription});
        }

        return JsonConvert.SerializeObject(infoMetas);
    }

    public static InfoMetaArgs GetObjectMetasTabel(string metaArgs)
    {
        var liste = JsonConvert.DeserializeObject<List<MetadataPayload>>(metaArgs);
        List<InfoMeta> infoMetas = new();
        if (liste != null)
        {
            foreach(var inf in liste)
            {
                infoMetas.Add(new(inf.NameWithExtension, inf.Length,inf.HachInfoDescription));
            }
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

    internal static Dictionary<InfoMeta, string> GetMetas(string[] files)
    {
        var pathlist = new Dictionary<InfoMeta, string>();
        foreach (var file in files)
        {
            try
            {
                var meta = new InfoMeta(file);
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
            if (file.HachInfoDescription == meta.HachInfoDescription)
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
               NameWithExtension == other.NameWithExtension &&
               Length == other.Length;
    }

    public override int GetHashCode() => HashCode.Combine(NameWithExtension, Length);

    public static bool operator ==(InfoMeta? left, InfoMeta? right) => EqualityComparer<InfoMeta>.Default.Equals(left, right);
    public static bool operator !=(InfoMeta? left, InfoMeta? right) => !(left == right);
}
