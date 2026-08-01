using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.EntryPoints;

public class SeasonRatingEntryPoint : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ImdbRatingsCacheService _cacheService;
    private readonly ILogger<SeasonRatingEntryPoint> _logger;
    private CancellationTokenSource? _cts;

    public SeasonRatingEntryPoint(
        ILibraryManager libraryManager,
        ImdbRatingsCacheService cacheService,
        ILogger<SeasonRatingEntryPoint> logger)
    {
        _libraryManager = libraryManager;
        _cacheService = cacheService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _libraryManager.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Episode episode
            || !episode.CommunityRating.HasValue
            || string.IsNullOrEmpty(episode.GetProviderId(MetadataProvider.Imdb)))
        {
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.IncludeSeries || !config.IncludeSeasonAverages)
        {
            return;
        }

        if (episode.SeasonId == Guid.Empty)
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => RecalculateSeasonAsync(episode.SeasonId, config.MinimumVotes, token), token);
    }

    private async Task RecalculateSeasonAsync(Guid seasonId, int minimumVotes, CancellationToken cancellationToken)
    {
        try
        {
            var season = _libraryManager.GetItemById(seasonId) as Season;
            if (season is null)
            {
                return;
            }

            var ratings = _cacheService.GetCachedRatings();
            if (ratings is null)
            {
                _logger.LogWarning("Skipping season recalculation — ratings cache not loaded");
                return;
            }

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                HasImdbId = true,
                IsVirtualItem = false,
                Recursive = true
            })
                .OfType<Episode>()
                .Where(ep => ep.SeasonId == seasonId)
                .Select(ep => (SeasonId: ep.SeasonId, ImdbId: ep.GetProviderId(MetadataProvider.Imdb)));

            var averages = SeasonRatingCalculator.CalculateSeasonAverages(episodes, ratings, minimumVotes);
            if (!averages.TryGetValue(seasonId, out var avgRating))
            {
                return;
            }

            if (season.CommunityRating.HasValue && Math.Abs(season.CommunityRating.Value - avgRating) < 0.01f)
            {
                return;
            }

            season.CommunityRating = avgRating;
            var parent = season.GetParent();
            await _libraryManager.UpdateItemAsync(season, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Updated season \"{Name}\" rating to {Rating}", season.Name, avgRating);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recalculate season rating for {SeasonId}", seasonId);
        }
    }
}
