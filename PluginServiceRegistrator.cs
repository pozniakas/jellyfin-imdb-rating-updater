using Jellyfin.Plugin.ImdbRatings.EntryPoints;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ImdbRatings;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ImdbRatingsCacheService>();
        serviceCollection.AddHostedService<SeasonRatingEntryPoint>();
    }
}
