using System.Net;
using System.Text;
using Jellyfin.Plugin.ThreeCat.ThreeCat;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class DynamicPaginationTests
{
    [Fact]
    public async Task CompleteCatalogueRejectsMissingSecondPage()
    {
        using var http = new HttpClient(new FixtureHandler(
            """{"resposta":{"status":"OK","items":{"item":[{"id":12345,"titol":"Primer","tipologia":"PTVC_PROGRAMA"}]},"paginacio":{"total_pagines":2,"total_items":2}}}""",
            """{"resposta":{"status":"OK","items":{"item":[]}}}"""));
        var client = new ThreeCatClient(http, NullLogger<ThreeCatClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListCompleteProgramsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CompleteCatalogueIncludesNewProgrammeWithoutSelection()
    {
        using var http = new HttpClient(new FixtureHandler(
            """{"resposta":{"status":"OK","items":{"item":[{"id":12345,"titol":"Primer","tipologia":"PTVC_PROGRAMA"}]},"paginacio":{"total_pagines":2,"total_items":2}}}""",
            """{"resposta":{"status":"OK","items":{"item":[{"id":23456,"titol":"Nou","tipologia":"PTVC_PROGRAMA"}]},"paginacio":{"total_pagines":2,"total_items":2}}}"""));
        var client = new ThreeCatClient(http, NullLogger<ThreeCatClient>.Instance);
        var programmes = await client.ListCompleteProgramsAsync(CancellationToken.None);
        Assert.Equal(new[] { "12345", "23456" }, programmes.Select(p => p.Id));
    }

    [Fact]
    public async Task CompleteCatalogueRejectsDuplicateRowsAcrossPages()
    {
        const string response = """{"resposta":{"status":"OK","items":{"item":[{"id":12345,"titol":"Primer","tipologia":"PTVC_PROGRAMA"}]},"paginacio":{"total_pagines":2,"total_items":2}}}""";
        using var http = new HttpClient(new FixtureHandler(response, response));
        var client = new ThreeCatClient(http, NullLogger<ThreeCatClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListCompleteProgramsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StrictEpisodeReadRejectsMissingPage()
    {
        using var http = new HttpClient(new FixtureHandler(
            """{"resposta":{"status":"OK","items":{"item":[{"id":98765,"titol":"T1xC1 - Primer"}]},"paginacio":{"total_pagines":2,"total_items":2}}}""",
            """{"resposta":{"status":"OK","items":{"item":[]}}}"""));
        var client = new ThreeCatClient(http, NullLogger<ThreeCatClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetProgramVideosAsync("12345", 0, CancellationToken.None, requireComplete: true));
    }

    private sealed class FixtureHandler(string first, string second) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = request.RequestUri!.Query;
            var json = query.Split('&').Any(part => part == "pagina=2") ? second : first;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
