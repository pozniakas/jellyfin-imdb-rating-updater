using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public class ImdbRatingsCacheService
{
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static Dictionary<string, (float Rating, int Votes)>? _ratingsCache;
    private static DateTime _cacheLoadedAt;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(23);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImdbRatingsCacheService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataPath;

    public ImdbRatingsCacheService(
        IHttpClientFactory httpClientFactory,
        ILogger<ImdbRatingsCacheService> logger,
        ILoggerFactory loggerFactory,
        IApplicationPaths applicationPaths)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _dataPath = applicationPaths.DataPath;
    }

    public IReadOnlyDictionary<string, (float Rating, int Votes)>? GetCachedRatings()
        => _ratingsCache is not null && DateTime.UtcNow - _cacheLoadedAt < CacheLifetime ? _ratingsCache : null;

    public async Task<IReadOnlyDictionary<string, (float Rating, int Votes)>> GetRatingsAsync(CancellationToken cancellationToken)
    {
        if (_ratingsCache is not null && DateTime.UtcNow - _cacheLoadedAt < CacheLifetime)
        {
            return _ratingsCache;
        }

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ratingsCache is not null && DateTime.UtcNow - _cacheLoadedAt < CacheLifetime)
            {
                return _ratingsCache;
            }

            _logger.LogInformation("Loading IMDb ratings into memory cache");

            var downloader = new ImdbFlatFileDownloader(
                _httpClientFactory,
                _loggerFactory.CreateLogger<ImdbFlatFileDownloader>(),
                _dataPath);

            var parser = new ImdbRatingsParser(_loggerFactory.CreateLogger<ImdbRatingsParser>());

            _ratingsCache = await DownloadAndParseAsync(downloader, parser, cancellationToken).ConfigureAwait(false);
            _cacheLoadedAt = DateTime.UtcNow;

            _logger.LogInformation("Cached {Count} IMDb ratings", _ratingsCache.Count);
            return _ratingsCache;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<Dictionary<string, (float Rating, int Votes)>> DownloadAndParseAsync(
        ImdbFlatFileDownloader downloader,
        ImdbRatingsParser parser,
        CancellationToken cancellationToken)
    {
        try
        {
            var filePath = await GetFilePathWithRetryAsync(downloader, cancellationToken).ConfigureAwait(false);
            return await parser.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "IMDb ratings data failed validation; invalidating cache and retrying");
            downloader.InvalidateCache();

            var filePath = await downloader.GetRatingsFilePathAsync(cancellationToken).ConfigureAwait(false);
            return await parser.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetFilePathWithRetryAsync(
        ImdbFlatFileDownloader downloader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await downloader.GetRatingsFilePathAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransientNetworkError(ex))
        {
            _logger.LogWarning(ex, "Transient network error downloading IMDb ratings; retrying once");
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            try
            {
                return await downloader.GetRatingsFilePathAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryEx) when (IsTransientNetworkError(retryEx))
            {
                if (!downloader.HasCacheFile)
                {
                    _logger.LogError(retryEx, "Download failed after retry and no cached ratings file exists");
                    throw;
                }

                _logger.LogWarning(retryEx, "Download failed after retry; falling back to stale cache at {Path}", downloader.CachePath);
                return downloader.CachePath;
            }
        }
    }

    private static bool IsTransientNetworkError(Exception ex)
    {
        return ex is HttpRequestException
            || (ex is IOException && ex is not InvalidDataException);
    }
}
