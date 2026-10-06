using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent;

internal static class QbittorrentExistingTorrentLookup
{
    public static async Task<bool> ExistsAsync(
        HttpClient httpClient,
        string baseUrl,
        QbittorrentTorrentAddPlan plan,
        CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(
            $"{baseUrl}/api/v2/torrents/info?hashes={Uri.EscapeDataString(plan.Hash.ToLowerInvariant())}", ct);
        if (!response.IsSuccessStatusCode)
            throw new DownloadClientSubmissionException("Unable to verify existing qBittorrent downloads before submission.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new DownloadClientSubmissionException("qBittorrent returned invalid torrent information.");

        foreach (var torrent in document.RootElement.EnumerateArray())
        {
            if (torrent.ValueKind != JsonValueKind.Object ||
                !torrent.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String ||
                !string.Equals(hash.GetString(), plan.Hash, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!torrent.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !TitleUtils.TitlesExactlyMatch(plan.Title, name.GetString() ?? string.Empty))
                throw new DownloadClientSubmissionException("This torrent already exists in qBittorrent with a different title. Its files have been left unchanged.");

            if (!string.IsNullOrEmpty(plan.Category) &&
                (!torrent.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String ||
                 !string.Equals(category.GetString(), plan.Category, StringComparison.Ordinal)))
                throw new DownloadClientSubmissionException("This torrent already exists in another qBittorrent category. Review it in qBittorrent before retrying.");

            return true;
        }

        return false;
    }
}
