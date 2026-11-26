using System.Globalization;

namespace Jellyfin.Plugin.ThreeCat.ThreeCat;

public sealed partial class ThreeCatClient
{
    // The search facet is documented in the upstream ROADMAP (PUFF_FILM).
    internal const string FilmsUrl = "https://api.3cat.cat/cercador/tot?_format=json&text=&format_contingut=PUFF_FILM&ordre=-data_publicacio&items_pagina=100&pagina={0}&sdom=img&version=2.0&https=true&master=no";

    public async Task<IReadOnlyList<ThreeCatVideo>> ListFilmsAsync(CancellationToken ct)
    {
        var items = new Dictionary<string, ThreeCatVideo>(StringComparer.Ordinal);
        var pages = 1;
        for (var page = 1; page <= pages; page++)
        {
            var url = string.Format(CultureInfo.InvariantCulture, FilmsUrl, page);
            var response = (await GetJsonAsync<ThreeCatEnvelope>(url, ct).ConfigureAwait(false))?.Response;
            if (response?.Items?.Items is not { Count: > 0 } batch || !string.Equals(response.Status, "OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("3Cat: catàleg de cinema buit o incomplet.");
            pages = Math.Max(1, response.Pagination?.TotalPages ?? page);
            if (pages > MaxPages) throw new InvalidOperationException("3Cat: massa pàgines de cinema.");
            foreach (var video in batch)
                if (video.Id is { Length: > 0 } id) items[id] = video;
            if (page == pages && response.Pagination?.TotalItems is { } count && items.Count < count)
                throw new InvalidOperationException("3Cat: falten pel·lícules al catàleg.");
        }

        // The live film facet contains both genuine shorts (including 5-minute films) and
        // explicitly titled trailers. Runtime is not a valid discriminator for short films.
        return items.Values.Where(v => DynamicFilmPolicy.IsFilm(v)
            && (v.Expires is null || v.Expires > DateTimeOffset.UtcNow)).ToArray();
    }
}
