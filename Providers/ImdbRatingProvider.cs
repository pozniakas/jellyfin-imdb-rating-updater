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

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public class ImdbRatingProvider :
    IRemoteMetadataProvider<Movie, MovieInfo>,
    IRemoteMetadataProvider<Series, SeriesInfo>,
    IRemoteMetadataProvider<Episode, EpisodeInfo>,
    IHasOrder
{
    private readonly ImdbRatingsCacheService _cacheService;
    private readonly IHttpClientFactory _httpClientFactory;

    public ImdbRatingProvider(
        ImdbRatingsCacheService cacheService,
        IHttpClientFactory httpClientFactory)
    {
        _cacheService = cacheService;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "IMDb Ratings";

    public int Order => 10;

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>();
        var rating = await LookupRatingAsync(info.GetProviderId(MetadataProvider.Imdb), cancellationToken).ConfigureAwait(false);
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
        var rating = await LookupRatingAsync(info.GetProviderId(MetadataProvider.Imdb), cancellationToken).ConfigureAwait(false);
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
        var rating = await LookupRatingAsync(info.GetProviderId(MetadataProvider.Imdb), cancellationToken).ConfigureAwait(false);
        if (rating.HasValue)
        {
            result.HasMetadata = true;
            result.Item = new Episode { CommunityRating = rating.Value };
        }

        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _httpClientFactory.CreateClient().GetAsync(url, cancellationToken);

    private async Task<float?> LookupRatingAsync(string? imdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(imdbId))
        {
            return null;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var ratings = await _cacheService.GetRatingsAsync(cancellationToken).ConfigureAwait(false);

        if (ratings.TryGetValue(imdbId, out var data) && data.Votes >= config.MinimumVotes)
        {
            return data.Rating;
        }

        return null;
    }
}
