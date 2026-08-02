using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ImdbRatings;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ImdbFlatFileDownloader>(sp =>
            new ImdbFlatFileDownloader(
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ImdbFlatFileDownloader>>(),
                sp.GetRequiredService<IApplicationPaths>().DataPath));
        serviceCollection.AddSingleton<IImdbRatingsFileProvider>(sp =>
            sp.GetRequiredService<ImdbFlatFileDownloader>());
    }
}
