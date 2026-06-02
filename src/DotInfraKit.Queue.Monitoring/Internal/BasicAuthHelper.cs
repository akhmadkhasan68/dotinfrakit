using System.Text;
using Microsoft.AspNetCore.Http;

namespace DotInfraKit.Queue.Monitoring.Internal;

internal static class BasicAuthHelper
{
    internal static bool Validate(HttpContext ctx, BasicAuthOptions auth)
    {
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (header?.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) != true)
            return false;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            var colon = decoded.IndexOf(':');
            if (colon < 0) return false;
            return decoded[..colon] == auth.Username && decoded[(colon + 1)..] == auth.Password;
        }
        catch
        {
            return false;
        }
    }
}
