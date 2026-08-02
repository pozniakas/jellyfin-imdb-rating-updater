using System.Net.Http;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// Tests run sequentially within this collection because
/// ImdbRatingProvider uses static cache state.
/// </summary>
[Collection("ImdbRatingProvider")]
public class ImdbRatingProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tsvPath;

    public ImdbRatingProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "imdb-provider-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _tsvPath = Path.Combine(_tempDir, "title.ratings.tsv");

        // Always start with clean cache state.
        ImdbRatingProvider.ResetCacheForTesting();
    }

    public void Dispose()
    {
        ImdbRatingProvider.ResetCacheForTesting();

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ─── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_Movie_ReturnsRatingWhenImdbIdExists()
    {
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0111161\t9.3\t2800000\n");
        var provider = CreateProvider();

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0111161");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal(9.3f, result.Item!.CommunityRating);
    }

    [Fact]
    public async Task GetMetadata_Series_ReturnsRatingWhenImdbIdExists()
    {
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0903747\t8.5\t500000\n");
        var provider = CreateProvider();

        var info = new SeriesInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0903747");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal(8.5f, result.Item!.CommunityRating);
    }

    [Fact]
    public async Task GetMetadata_Episode_ReturnsRatingWhenImdbIdExists()
    {
        WriteTsv("tconst\taverageRating\tnumVotes\ntt4283088\t9.9\t200000\n");
        var provider = CreateProvider();

        var info = new EpisodeInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt4283088");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal(9.9f, result.Item!.CommunityRating);
    }

    // ─── No IMDb ID ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_NoImdbId_ReturnsNoMetadata()
    {
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0111161\t9.3\t2800000\n");
        var provider = CreateProvider();

        var info = new MovieInfo(); // no IMDb ID set

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Null(result.Item);
    }

    // ─── IMDb ID not found in dataset ───────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_ImdbIdNotInDataset_ReturnsNoMetadata()
    {
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0111161\t9.3\t2800000\n");
        var provider = CreateProvider();

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt9999999");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Null(result.Item);
    }

    // ─── MinimumVotes filtering ─────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_VotesBelowMinimum_ReturnsNoMetadata()
    {
        // Default PluginConfiguration has MinimumVotes=1, but let's have an entry
        // with 0 votes (edge case — shouldn't exist in real data but tests the logic).
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0000001\t5.0\t0\n");
        var provider = CreateProvider();

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0000001");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        // Default MinimumVotes=1, entry has 0 votes → skipped.
        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task GetMetadata_VotesExactlyAtMinimum_ReturnsRating()
    {
        // Default MinimumVotes=1, entry has 1 vote → passes.
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0000001\t7.2\t1\n");
        var provider = CreateProvider();

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0000001");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal(7.2f, result.Item!.CommunityRating);
    }

    // ─── Cache file unavailable ─────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_DownloadFailsAndNoCacheFile_ReturnsNoMetadata()
    {
        // Don't create any TSV file — simulate first-run with no data.
        var fakeProvider = new FakeImdbRatingsFileProvider(
            cachePath: _tsvPath,
            hasCacheFile: false,
            throwOnGetPath: new IOException("Network error"));

        var provider = CreateProvider(fakeProvider);

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0111161");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    // ─── Fallback to stale cache ────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_DownloadFailsButCacheExists_UsesStaleCacheFile()
    {
        // Cache file exists on disk but download throws.
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0111161\t8.0\t100000\n");

        var fakeProvider = new FakeImdbRatingsFileProvider(
            cachePath: _tsvPath,
            hasCacheFile: true,
            throwOnGetPath: new HttpRequestException("timeout"));

        var provider = CreateProvider(fakeProvider);

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0111161");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal(8.0f, result.Item!.CommunityRating);
    }

    // ─── Cancellation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_CancellationRequested_ThrowsOperationCanceled()
    {
        WriteTsv("tconst\taverageRating\tnumVotes\ntt0111161\t9.3\t2800000\n");
        var provider = CreateProvider();

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0111161");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetMetadata(info, cts.Token));
    }

    // ─── Multiple items share cache (only one parse) ────────────────────────────

    [Fact]
    public async Task GetMetadata_MultipleLookups_ReusesSameCache()
    {
        WriteTsv(
            "tconst\taverageRating\tnumVotes\n" +
            "tt0111161\t9.3\t2800000\n" +
            "tt0068646\t9.2\t1900000\n");
        var countingProvider = new CountingImdbRatingsFileProvider(_tsvPath);
        var provider = CreateProvider(countingProvider);

        var info1 = new MovieInfo();
        info1.SetProviderId(MetadataProvider.Imdb, "tt0111161");
        var info2 = new MovieInfo();
        info2.SetProviderId(MetadataProvider.Imdb, "tt0068646");

        var result1 = await provider.GetMetadata(info1, CancellationToken.None);
        var result2 = await provider.GetMetadata(info2, CancellationToken.None);

        Assert.True(result1.HasMetadata);
        Assert.True(result2.HasMetadata);
        Assert.Equal(9.3f, result1.Item!.CommunityRating);
        Assert.Equal(9.2f, result2.Item!.CommunityRating);

        // GetRatingsFilePathAsync should only be called once (cache reused).
        Assert.Equal(1, countingProvider.GetPathCallCount);
    }

    // ─── Concurrent access ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_ConcurrentCalls_OnlyParsesOnce()
    {
        WriteTsv(
            "tconst\taverageRating\tnumVotes\n" +
            "tt0111161\t9.3\t2800000\n" +
            "tt0068646\t9.2\t1900000\n" +
            "tt0071562\t9.0\t800000\n");
        var countingProvider = new CountingImdbRatingsFileProvider(_tsvPath);
        var provider = CreateProvider(countingProvider);

        var tasks = new List<Task<MediaBrowser.Controller.Providers.MetadataResult<MediaBrowser.Controller.Entities.Movies.Movie>>>();
        for (int i = 0; i < 10; i++)
        {
            var info = new MovieInfo();
            info.SetProviderId(MetadataProvider.Imdb, "tt0111161");
            tasks.Add(provider.GetMetadata(info, CancellationToken.None));
        }

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.True(r.HasMetadata);
            Assert.Equal(9.3f, r.Item!.CommunityRating);
        });

        // Despite 10 concurrent calls, file should only be loaded once.
        Assert.Equal(1, countingProvider.GetPathCallCount);
    }

    // ─── GetSearchResults returns empty ─────────────────────────────────────────

    [Fact]
    public async Task GetSearchResults_Movie_ReturnsEmpty()
    {
        var provider = CreateProvider();
        var results = await provider.GetSearchResults(new MovieInfo(), CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSearchResults_Series_ReturnsEmpty()
    {
        var provider = CreateProvider();
        var results = await provider.GetSearchResults(new SeriesInfo(), CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSearchResults_Episode_ReturnsEmpty()
    {
        var provider = CreateProvider();
        var results = await provider.GetSearchResults(new EpisodeInfo(), CancellationToken.None);
        Assert.Empty(results);
    }

    // ─── Provider name ──────────────────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsImdbRating()
    {
        var provider = CreateProvider();
        Assert.Equal("IMDb Rating", provider.Name);
    }

    // ─── Multiple entries: correct one selected ─────────────────────────────────

    [Fact]
    public async Task GetMetadata_MultipleEntries_ReturnsCorrectRating()
    {
        WriteTsv(
            "tconst\taverageRating\tnumVotes\n" +
            "tt0111161\t9.3\t2800000\n" +
            "tt0068646\t9.2\t1900000\n" +
            "tt0071562\t9.0\t800000\n");
        var provider = CreateProvider();

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0068646");

        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal(9.2f, result.Item!.CommunityRating);
    }

    // ─── OperationCanceledException propagates from download ────────────────────

    [Fact]
    public async Task GetMetadata_DownloadThrowsOperationCanceled_PropagatesException()
    {
        var fakeProvider = new FakeImdbRatingsFileProvider(
            cachePath: _tsvPath,
            hasCacheFile: false,
            throwOnGetPath: new OperationCanceledException());

        var provider = CreateProvider(fakeProvider);

        var info = new MovieInfo();
        info.SetProviderId(MetadataProvider.Imdb, "tt0111161");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.GetMetadata(info, CancellationToken.None));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private void WriteTsv(string content)
    {
        File.WriteAllText(_tsvPath, content);
    }

    private ImdbRatingProvider CreateProvider(IImdbRatingsFileProvider? fileProvider = null)
    {
        fileProvider ??= new FakeImdbRatingsFileProvider(_tsvPath, hasCacheFile: true);
        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance, minExpectedRows: 0);

        return new ImdbRatingProvider(
            fileProvider,
            new FakeHttpClientFactory(),
            NullLogger<ImdbRatingProvider>.Instance,
            parser);
    }

    // ─── Test doubles ───────────────────────────────────────────────────────────

    private sealed class FakeImdbRatingsFileProvider : IImdbRatingsFileProvider
    {
        private readonly Exception? _throwOnGetPath;

        public FakeImdbRatingsFileProvider(string cachePath, bool hasCacheFile, Exception? throwOnGetPath = null)
        {
            CachePath = cachePath;
            HasCacheFile = hasCacheFile;
            _throwOnGetPath = throwOnGetPath;
        }

        public string CachePath { get; }

        public bool HasCacheFile { get; }

        public Task<string> GetRatingsFilePathAsync(CancellationToken cancellationToken)
        {
            if (_throwOnGetPath is not null)
            {
                throw _throwOnGetPath;
            }

            return Task.FromResult(CachePath);
        }
    }

    private sealed class CountingImdbRatingsFileProvider : IImdbRatingsFileProvider
    {
        private int _getPathCallCount;

        public CountingImdbRatingsFileProvider(string cachePath)
        {
            CachePath = cachePath;
        }

        public string CachePath { get; }

        public bool HasCacheFile => true;

        public int GetPathCallCount => _getPathCallCount;

        public Task<string> GetRatingsFilePathAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _getPathCallCount);
            return Task.FromResult(CachePath);
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
