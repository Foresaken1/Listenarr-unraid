/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Downloads.Submission;

internal static class DownloadMismatchRetirement
{
    public static async Task RetireAsync(
        int audiobookId,
        TrustedDownloadCandidate candidate,
        IConfigurationService configurationService,
        IDownloadRepository downloadRepository,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var activeDownloads = await DownloadDuplicateGuard.GetActiveDownloadsAsync(
            audiobookId,
            configurationService,
            downloadRepository);

        foreach (var activeDownload in activeDownloads)
        {
            if (DownloadDuplicateGuard.IsSameDownloadTitle(activeDownload.Title, candidate.Title))
            {
                continue;
            }

            var message = $"Superseded by a different selected result: {candidate.Title}";
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                logger,
                "Retiring mismatched active download {DownloadId} for audiobook {AudiobookId}. Existing title: '{ExistingTitle}', selected title: '{SelectedTitle}'",
                LogRedaction.SanitizeText(activeDownload.Id),
                audiobookId,
                LogRedaction.SanitizeText(activeDownload.Title),
                LogRedaction.SanitizeText(candidate.Title));

            activeDownload.Failed(message);
            await downloadRepository.UpdateAsync(activeDownload);
        }
    }
}
