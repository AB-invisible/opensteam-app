using System.Text.Json;
using ManifestApp.Core.Models;

namespace ManifestApp.Core;

public sealed class InstalledManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public InstalledManifestRecordsFile Load()
    {
        AppPaths.EnsureLayout();
        if (!File.Exists(AppPaths.InstalledRecordsPath))
            return new InstalledManifestRecordsFile();

        try
        {
            var json = File.ReadAllText(AppPaths.InstalledRecordsPath);
            var f = JsonSerializer.Deserialize<InstalledManifestRecordsFile>(json, JsonOptions);
            return f ?? new InstalledManifestRecordsFile();
        }
        catch
        {
            return new InstalledManifestRecordsFile();
        }
    }

    public void Save(InstalledManifestRecordsFile file)
    {
        AppPaths.EnsureLayout();
        File.WriteAllText(
            AppPaths.InstalledRecordsPath,
            JsonSerializer.Serialize(file, JsonOptions));
    }

    public InstalledManifestRecord? FindByAppId(uint appId)
    {
        return Load().Records.FirstOrDefault(r => r.AppId == appId);
    }

    public void Upsert(InstalledManifestRecord record)
    {
        var file = Load();
        file.Records.RemoveAll(r => r.AppId == record.AppId);
        file.Records.Add(record);
        Save(file);
    }

    public void Remove(uint appId)
    {
        var file = Load();
        file.Records.RemoveAll(r => r.AppId == appId);
        Save(file);
    }
}
