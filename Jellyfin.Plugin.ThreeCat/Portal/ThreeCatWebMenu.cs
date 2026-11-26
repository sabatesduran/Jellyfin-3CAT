using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ThreeCat.Portal;

/// <summary>Opt-in augmentation of the public Jellyfin Web configuration. Never writes the installed web files.</summary>
public sealed class ThreeCatWebMenu : IStartupFilter
{
    private const string ConfigSuffix = "/web/config.json";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, downstream) =>
            {
                if (Plugin.Instance?.Configuration.WebPortalMenu != true
                    || !HttpMethods.IsGet(context.Request.Method)
                    || context.Request.Path.Value is not { } path
                    || !path.EndsWith(ConfigSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    await downstream(context).ConfigureAwait(false);
                    return;
                }

                string? augmented = null;
                try
                {
                    var paths = context.RequestServices.GetRequiredService<IApplicationPaths>();
                    var file = Path.Combine(paths.WebPath, "config.json");
                    if (File.Exists(file) && new FileInfo(file).Length <= 1024 * 1024)
                    {
                        var prefix = context.Request.PathBase.ToString() + path[..^ConfigSuffix.Length];
                        if (!prefix.StartsWith("//", StringComparison.Ordinal) && !prefix.Contains('\\'))
                        {
                            augmented = AddLink(await File.ReadAllTextAsync(file, context.RequestAborted).ConfigureAwait(false),
                                prefix.TrimEnd('/') + "/ThreeCat/Explore/");
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    // Fail open: the normal web client configuration is always available.
                }

                if (augmented is null)
                {
                    await downstream(context).ConfigureAwait(false);
                    return;
                }

                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-store";
                await context.Response.WriteAsync(augmented, context.RequestAborted).ConfigureAwait(false);
            });
            next(app);
        };
    }

    internal static string? AddLink(string source, string url)
    {
        if (JsonNode.Parse(source) is not JsonObject config) return null;
        if (config["menuLinks"] is null) config["menuLinks"] = new JsonArray();
        if (config["menuLinks"] is not JsonArray links) return null;
        if (links.OfType<JsonObject>().Any(link =>
            string.Equals(link["url"]?.ToString(), url, StringComparison.OrdinalIgnoreCase))) return source;
        links.Add(new JsonObject { ["name"] = "3Cat", ["url"] = url, ["icon"] = "live_tv" });
        return config.ToJsonString();
    }
}
