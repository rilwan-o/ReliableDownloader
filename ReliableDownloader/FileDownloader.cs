using Microsoft.Extensions.Logging;
using NLog;
using Polly;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReliableDownloader;

public class FileDownloader : IFileDownloader
{
    private readonly IWebSystemCalls _webSystemCalls;
    private readonly ILogger<FileDownloader> _logger;
    private const int BUFFER_SIZE = 8192;
    private const int MAX_RETRIES = 3;

    public FileDownloader(IWebSystemCalls webSystemCalls, ILogger<FileDownloader> logger)
    {
        _webSystemCalls = webSystemCalls;
        _logger = logger;
    }

    public async Task<bool> TryDownloadFile(
         string contentFileUrl,
         string localFilePath,
         Action<FileProgress> onProgressChanged,
         CancellationToken cancellationToken)
    {
        try
        {
            var success = await RetryOnFailure(async () =>
            {
                using var response = await _webSystemCalls.GetHeadersAsync(contentFileUrl, cancellationToken);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (CDNSupportsPartialContent(response))
                    {
                        return await TryPartialDownloadFile(contentFileUrl, response, localFilePath, onProgressChanged, cancellationToken);
                    }
                    else
                    {
                        // The CDN does not support partial content. Download the complete file.
                        return await TryCompleteDownloadFile(contentFileUrl, response, localFilePath, onProgressChanged, cancellationToken);
                    }
                }
                else
                {
                    _logger.LogError($"{response.StatusCode} for {nameof(_webSystemCalls.GetHeadersAsync)}");

                    return false;
                }
            });

            return success;

        }
        catch (Exception ex)
        {
            // Handle exceptions, e.g., network issues.
            _logger.LogError(ex.Message, ex);
            return false;
        }
    }

    private async Task<bool> TryPartialDownloadFile(
        string contentFileUrl,
        HttpResponseMessage response,
        string localFilePath,
        Action<FileProgress> onProgressChanged,
        CancellationToken cancellationToken)
    {
        try
        {
            long? totalFileSize = response.Content.Headers.ContentLength;
            long totalBytesDownloaded = 0;
            bool success = false;
            var cancelRequested = false;

            using (var fileStream = File.Create(localFilePath))
            using (var md5 = MD5.Create())
            {
                var buffer = new byte[BUFFER_SIZE];
                var rangeStart = 0L;

                do
                {
                    // Calculate the range for partial download
                    long rangeEnd = rangeStart + BUFFER_SIZE - 1;
                    if (totalFileSize.HasValue && rangeEnd >= totalFileSize.Value)
                    {
                        rangeEnd = totalFileSize.Value - 1;
                    }

                    using (var responseStream = await _webSystemCalls.DownloadPartialContentAsync(contentFileUrl, rangeStart, rangeEnd, cancellationToken))
                    {
                        var stream = await responseStream.Content.ReadAsStreamAsync();
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                cancelRequested = true;
                                break;
                            }
                            fileStream.Write(buffer, 0, bytesRead);
                            md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                            totalBytesDownloaded += bytesRead;

                            var progress = new FileProgress(totalFileSize, totalBytesDownloaded,
                                CalculateProgress(totalBytesDownloaded, totalFileSize), null);
                            onProgressChanged(progress);
                        }
                    }

                    rangeStart = rangeEnd + 1;
                } while (rangeStart < totalFileSize);

                md5.TransformFinalBlock(buffer, 0, 0);

                if (!cancelRequested)
                {
                    var md5Hash = md5.Hash;
                    var contentMd5 = response.Content.Headers.ContentMD5;
                    if (contentMd5 != null && !StructuralComparisons.StructuralEqualityComparer.Equals(md5Hash, contentMd5))
                    {
                        File.Delete(localFilePath);
                    }
                    else
                    {
                        success = true;
                    }
                }
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex.Message, ex);

            File.Delete(localFilePath);
            return false;
        }
    }

    private async Task<bool> TryCompleteDownloadFile(
        string contentFileUrl,
        HttpResponseMessage response,
        string localFilePath,
        Action<FileProgress> onProgressChanged,
        CancellationToken cancellationToken)
    {
        try
        {
            long? totalFileSize = response.Content.Headers.ContentLength;
            long totalBytesDownloaded = 0;
            bool success = false;
            var cancelRequested = false;

            using (var fileStream = File.Create(localFilePath))
            using (var md5 = MD5.Create())
            {
                var buffer = new byte[BUFFER_SIZE];

                using (var responseStream = await _webSystemCalls.DownloadContentAsync(contentFileUrl, cancellationToken))
                {
                    var stream = await responseStream.Content.ReadAsStreamAsync();
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelRequested = true;
                            break;
                        }
                        fileStream.Write(buffer, 0, bytesRead);
                        md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                        totalBytesDownloaded += bytesRead;

                        var progress = new FileProgress(totalFileSize, totalBytesDownloaded,
                            CalculateProgress(totalBytesDownloaded, totalFileSize), null);
                        onProgressChanged(progress);
                    }
                }

                md5.TransformFinalBlock(buffer, 0, 0);
                if (!cancelRequested)
                {
                    var md5Hash = md5.Hash;
                    var contentMd5 = response.Content.Headers.ContentMD5;
                    if (contentMd5 != null && !StructuralComparisons.StructuralEqualityComparer.Equals(md5Hash, contentMd5))
                    {
                        File.Delete(localFilePath);
                    }
                    else
                    {
                        success = true;
                    }
                }
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex.Message, ex);

            File.Delete(localFilePath);
            return false;
        }
    }

    private static bool CDNSupportsPartialContent(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Accept-Ranges", out var acceptRanges) && acceptRanges.Contains("bytes");
    }

    private double? CalculateProgress(long downloaded, long? total)
    {
        if (total.HasValue && total > 0)
        {
            return (double)downloaded / total;
        }
        return null;
    }

    public async Task<bool> RetryOnFailure(Func<Task<bool>> action, int maxRetries = MAX_RETRIES)
    {
        var retryPolicy = Policy
            .Handle<Exception>()
            .RetryAsync(maxRetries); // Maximum number of retries

        return await retryPolicy.ExecuteAsync(async () => await action());
    }
}