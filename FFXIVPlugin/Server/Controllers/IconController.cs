using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using XIVDeck.FFXIVPlugin.Server.Helpers;
using XIVDeck.FFXIVPlugin.Utils;

namespace XIVDeck.FFXIVPlugin.Server.Controllers;

[ApiController("/icon")]
public class IconController : WebApiController {
    [Route(HttpVerbs.Get, "/{iconId}")]
    public async Task GetIcon(int iconId) {
        this.HttpContext.Response.ContentType = "image/png";
        await using var stream = this.HttpContext.OpenResponseStream();

        var png = await IconPngCache.GetBytesAsync(iconId);

        if (png == null)
            throw HttpException.NotFound($"Icon {iconId} was not found.");

        await stream.WriteAsync(png);
    }
}


