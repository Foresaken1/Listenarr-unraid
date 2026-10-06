/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentAddWorkflow(
        IHttpClientFactory httpClientFactory,
        QbittorrentAuthSession authSession,
        QbittorrentRemovalWorkflow removalWorkflow,
        ILogger<QbittorrentAdapter> logger,
        string clientType)
    {
        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            if (submission is not PreparedTorrentSubmission torrent)
            {
                throw new DownloadClientSubmissionException("qBittorrent requires a prepared torrent submission.");
            }

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            using var httpClient = httpClientFactory.CreateClient(clientType);

            try
            {
                await authSession.LoginAsync(httpClient, client, ct);
            }
            catch (QbittorrentException exception)
            {
                logger.LogError(exception, "qBittorrent authentication failed for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                throw new DownloadClientSubmissionException("qBittorrent authentication failed.", exception);
            }

            var addPlan = QbittorrentTorrentAddPlanner.Create(client, torrent);

            if (await QbittorrentExistingTorrentLookup.ExistsAsync(httpClient, baseUrl, addPlan, ct))
            {
                await ValidatePayloadAndStartAsync(httpClient, baseUrl, client, addPlan, ct, existing: true);
                return new DownloadClientSubmissionResult(addPlan.Hash, addPlan.Hash);
            }

            using var addContent = QbittorrentAddRequestContentBuilder.Build(addPlan);
            using var addResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/add", addContent, ct);

            if (!addResponse.IsSuccessStatusCode)
            {
                // Another request may have added this exact torrent after our lookup.
                if (addResponse.StatusCode == System.Net.HttpStatusCode.Conflict &&
                    await QbittorrentExistingTorrentLookup.ExistsAsync(httpClient, baseUrl, addPlan, ct))
                {
                    await ValidatePayloadAndStartAsync(httpClient, baseUrl, client, addPlan, ct, existing: true);
                    return new DownloadClientSubmissionResult(addPlan.Hash, addPlan.Hash);
                }

                var responseContent = await addResponse.Content.ReadAsStringAsync(ct);
                var redacted = LogRedaction.RedactText(responseContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat([client.Password ?? string.Empty]));

                logger.LogError($"Failed to add torrent to qBittorrent. Status: {addResponse.StatusCode}, Response: {redacted}");
                throw new DownloadClientSubmissionException($"qBittorrent rejected the torrent with HTTP {(int)addResponse.StatusCode}.");
            }

            logger.LogInformation("Successfully sent torrent to qBittorrent");

            await Task.Delay(1000, ct);
            await ValidatePayloadAndStartAsync(httpClient, baseUrl, client, addPlan, ct);

            // qBittorrent can accept a torrent while failing to register private tracker
            // URLs from the file. Keep this explicit fallback in the add workflow so the
            // facade adapter stays thin without hiding this client-specific behavior.
            if (addPlan.TorrentFileData != null)
            {
                try
                {
                    var trackerAnnounces = torrent.TrackerUrls.Where(a =>
                        a.Contains("/announce", StringComparison.OrdinalIgnoreCase) ||
                        a.Contains("/tracker", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (trackerAnnounces.Count > 0)
                    {
                        var trackerUrls = string.Join("\n", trackerAnnounces.Distinct());
                        using var addTrackersData = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("hash", addPlan.Hash),
                            new KeyValuePair<string, string>("urls", trackerUrls)
                        });
                        using var trackersResp = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/addTrackers", addTrackersData, ct);
                        if (trackersResp.IsSuccessStatusCode)
                            logger.LogInformation($"Injected {trackerAnnounces.Count} tracker(s) for torrent {addPlan.Hash} via addTrackers API");
                        else
                            logger.LogDebug($"addTrackers API returned {trackersResp.StatusCode} for torrent {addPlan.Hash} (non-fatal)");
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogDebug(exception, "Non-fatal failure injecting trackers via addTrackers API");
                }
            }

            return new DownloadClientSubmissionResult(addPlan.Hash, addPlan.Hash);
        }

        private async Task ValidatePayloadAndStartAsync(
            HttpClient httpClient,
            string baseUrl,
            DownloadClientConfiguration client,
            QbittorrentTorrentAddPlan addPlan,
            CancellationToken ct,
            bool existing = false)
        {
            var files = await FetchTorrentFilesForInspectionAsync(httpClient, baseUrl, addPlan.Hash, ct);
            if (files.Count == 0)
            {
                if (!existing)
                    await RemoveRejectedTorrentAsync(client, addPlan.Hash, ct);
                throw new DownloadClientSubmissionException("qBittorrent did not expose torrent payload metadata for inspection.");
            }

            if (!files.Any(IsAudioPayloadFile))
            {
                logger.LogInformation(
                    "Rejected qBittorrent torrent {Hash} because its payload did not contain audio files",
                    LogRedaction.SanitizeText(addPlan.Hash));
                if (!existing)
                    await RemoveRejectedTorrentAsync(client, addPlan.Hash, ct);
                throw new DownloadClientSubmissionException("qBittorrent torrent payload does not contain any supported audio files.");
            }

            if (!PayloadMatchesExpectedTitle(files, addPlan.Title))
            {
                logger.LogInformation(
                    "Rejected qBittorrent torrent {Hash} because payload filenames did not match expected title '{Title}'",
                    LogRedaction.SanitizeText(addPlan.Hash),
                    LogRedaction.SanitizeText(addPlan.Title));
                if (!existing)
                    await RemoveRejectedTorrentAsync(client, addPlan.Hash, ct);
                throw new DownloadClientSubmissionException("qBittorrent torrent payload does not match the selected audiobook title.");
            }

            if (!existing)
                await StartTorrentAsync(httpClient, baseUrl, addPlan.Hash, ct);
        }

        private static async Task<List<Dictionary<string, JsonElement>>> FetchTorrentFilesForInspectionAsync(
            HttpClient httpClient,
            string baseUrl,
            string hash,
            CancellationToken ct)
        {
            // Magnet links may need time to fetch metadata from peers before qBittorrent
            // can expose the torrent file list. Wait up to about 60 seconds before
            // treating missing payload metadata as a validation failure.
            for (var attempt = 0; attempt < 60; attempt++)
            {
                using var filesResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/files?hash={Uri.EscapeDataString(hash)}", ct);
                if (!filesResp.IsSuccessStatusCode)
                {
                    return [];
                }

                var filesJson = await filesResp.Content.ReadAsStringAsync(ct);
                var files = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(filesJson) ?? [];
                if (files.Count > 0)
                {
                    return files;
                }

                await Task.Delay(1000, ct);
            }

            return [];
        }

        private static bool IsAudioPayloadFile(Dictionary<string, JsonElement> file)
        {
            if (!file.TryGetValue("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name) || !FileUtils.IsAudioFile(name))
            {
                return false;
            }

            var fileName = Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));
            return !fileName.Contains("sample", StringComparison.OrdinalIgnoreCase)
                && !fileName.Contains("preview", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PayloadMatchesExpectedTitle(
            IReadOnlyCollection<Dictionary<string, JsonElement>> files,
            string expectedTitle)
        {
            var normalizedExpected = TitleUtils.NormalizeTitle(expectedTitle);
            if (normalizedExpected.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            {
                return true;
            }

            var comparablePaths = files
                .Where(IsAudioPayloadFile)
                .Select(GetPayloadName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Replace('\\', '/'))
                .ToList();

            return comparablePaths.Count == 0 ||
                   comparablePaths.Any(path =>
                       TitleUtils.IsMatchingTitle(expectedTitle, path) ||
                       path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                           .Any(segment => TitleUtils.IsMatchingTitle(expectedTitle, Path.GetFileNameWithoutExtension(segment))));
        }

        private static string? GetPayloadName(Dictionary<string, JsonElement> file)
        {
            return file.TryGetValue("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
        }

        private async Task StartTorrentAsync(HttpClient httpClient, string baseUrl, string hash, CancellationToken ct)
        {
            using var resumeContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash)
            });
            using var resumeResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/resume", resumeContent, ct);
            if (resumeResponse.IsSuccessStatusCode)
            {
                return;
            }

            using var startContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash)
            });
            using var startResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/start", startContent, ct);
            if (!startResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "qBittorrent accepted torrent {Hash} but could not start it after payload validation. Status: {Status}",
                    LogRedaction.SanitizeText(hash),
                    startResponse.StatusCode);
                throw new DownloadClientSubmissionException("qBittorrent accepted the torrent but could not start it after payload validation.");
            }
        }

        private async Task RemoveRejectedTorrentAsync(DownloadClientConfiguration client, string hash, CancellationToken ct)
        {
            var removed = await removalWorkflow.RemoveAsync(client, hash, deleteFiles: true, ct);
            if (!removed)
            {
                logger.LogWarning(
                    "Failed to remove rejected qBittorrent torrent {Hash} after payload validation",
                    LogRedaction.SanitizeText(hash));
            }
        }
    }
}
