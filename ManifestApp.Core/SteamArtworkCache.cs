using System.Globalization;

namespace ManifestApp.Core;

public sealed class SteamArtworkCache(HttpClient http)
{
    /// <returns>Absolute path on disk inside <see cref="AppPaths.ImageCacheDir"/>.</returns>
    public async Task<string?> GetCapsule184PathAsync(uint appId, CancellationToken cancellationToken)
    {
        AppPaths.EnsureLayout();
        var file = Path.Combine(AppPaths.ImageCacheDir, $"{appId}.jpg");

        var url =
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId.ToString(CultureInfo.InvariantCulture)}/capsule_184x69.jpg";

        if (File.Exists(file))
            return file;

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentType?.MediaType?.Contains("image", StringComparison.OrdinalIgnoreCase)
                != true)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var tempPath = Path.Combine(AppPaths.ImageCacheDir, $"{appId}.tmp");
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           bufferSize: 81920, FileOptions.SequentialScan | FileOptions.Asynchronous))
                await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);

            File.Move(tempPath, file, overwrite: true);
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try
            {
                var tmp = Path.Combine(AppPaths.ImageCacheDir, $"{appId}.tmp");
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // ignored
            }

            return null;
        }
    }
}
