using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ImdbRatings.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public class ImdbRatingProvider :
    IRemoteMetadataProvider<Movie, MovieInfo>,
    IRemoteMetadataProvider<Series, SeriesInfo>,
    IRemoteMetadataProvider<Episode, EpisodeInfo>
{
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static Dictionary<string, (float Rating, int Votes)>? _cache;
    private static Timer? _evictionTimer;

    private readonly IImdbRatingsFileProvider _fileProvider;
    private readonly ImdbRatingsParser _parser;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImdbRatingProvider> _logger;

    public ImdbRatingProvider(
        IImdbRatingsFileProvider fileProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<ImdbRatingProvider> logger,
        ILogger<ImdbRatingsParser> parserLogger)
    {
        _fileProvider = fileProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _parser = new ImdbRatingsParser(parserLogger);
    }

    internal ImdbRatingProvider(
        IImdbRatingsFileProvider fileProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<ImdbRatingProvider> logger,
        ImdbRatingsParser parser)
    {
        _fileProvider = fileProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _parser = parser;
    }

    public string Name => "IMDb Rating";

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>();
        var rating = await LookupRatingAsync(info, cancellationToken).ConfigureAwait(false);
        if (rating.HasValue)
        {
            result.HasMetadata = true;
            result.Item = new Movie { CommunityRating = rating.Value };
        }

        return result;
    }

    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>();
        var rating = await LookupRatingAsync(info, cancellationToken).ConfigureAwait(false);
        if (rating.HasValue)
        {
            result.HasMetadata = true;
            result.Item = new Series { CommunityRating = rating.Value };
        }

        return result;
    }

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();
        var rating = await LookupRatingAsync(info, cancellationToken).ConfigureAwait(false);
        if (rating.HasValue)
        {
            result.HasMetadata = true;
            result.Item = new Episode { CommunityRating = rating.Value };
        }

        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("ImdbRatings");
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    private async Task<float?> LookupRatingAsync(ItemLookupInfo info, CancellationToken cancellationToken)
    {
        var imdbId = info.GetProviderId(MetadataProvider.Imdb);
        if (string.IsNullOrEmpty(imdbId))
        {
            return null;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var cache = await GetOrLoadCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cache is null)
        {
            return null;
        }

        if (!cache.TryGetValue(imdbId, out var ratingData))
        {
            return null;
        }

        if (ratingData.Votes < config.MinimumVotes)
        {
            _logger.LogDebug("IMDb rating for {ImdbId} skipped — {Votes} votes below minimum {MinVotes}", imdbId, ratingData.Votes, config.MinimumVotes);
            return null;
        }

        return ratingData.Rating;
    }

    private async Task<Dictionary<string, (float Rating, int Votes)>?> GetOrLoadCacheAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
            {
                return _cache;
            }

            string filePath;
            try
            {
                filePath = await _fileProvider.GetRatingsFilePathAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_fileProvider.HasCacheFile)
                {
                    _logger.LogWarning(ex, "IMDb ratings file not available for provider lookup");
                    return null;
                }

                filePath = _fileProvider.CachePath;
            }

            _logger.LogInformation("Loading IMDb ratings into memory cache from {Path}", filePath);
            var ratings = await _parser.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            _cache = ratings;
            _logger.LogInformation("IMDb ratings cache loaded: {Count} entries", ratings.Count);

            _evictionTimer?.Dispose();
            _evictionTimer = new Timer(_ => { _cache = null; }, null, TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan);

            return _cache;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    internal static void ResetCacheForTesting()
    {
        _evictionTimer?.Dispose();
        _evictionTimer = null;
        _cache = null;
    }
}
