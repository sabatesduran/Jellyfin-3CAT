using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ThreeCat.Api;

/// <summary>Serves only the static portal shell. All personal catalogue and playback data use Jellyfin authentication.</summary>
[ApiController]
[AllowAnonymous]
[Route("ThreeCat/Explore")]
public sealed class ThreeCatPortalController : ControllerBase
{
    [HttpGet]
    public IActionResult Index()
    {
        if (Request.Path.Value?.EndsWith("/", StringComparison.Ordinal) != true) return Redirect("Explore/");
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        return Resource("index.html", "text/html; charset=utf-8");
    }

    [HttpGet("{asset}")]
    public IActionResult Asset(string asset) => asset switch
    {
        "portal.js" => Resource(asset, "text/javascript; charset=utf-8"),
        "portal.css" => Resource(asset, "text/css; charset=utf-8"),
        _ => NotFound()
    };

    private IActionResult Resource(string name, string type)
    {
        var stream = typeof(ThreeCatPortalController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.ThreeCat.Portal." + name);
        if (stream is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return File(stream, type);
    }
}
