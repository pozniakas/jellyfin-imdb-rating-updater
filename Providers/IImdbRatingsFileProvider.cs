using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public interface IImdbRatingsFileProvider
{
    string CachePath { get; }

    bool HasCacheFile { get; }

    Task<string> GetRatingsFilePathAsync(CancellationToken cancellationToken);
}
