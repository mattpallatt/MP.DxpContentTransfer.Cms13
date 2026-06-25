using Microsoft.AspNetCore.Http;
using System.Text;

namespace DxpContentTransfer.Cms13.Middleware;

public class DxpAdminScriptMiddleware(RequestDelegate next)
{
    private const string AdminPath = "/EPiServer/EPiServer.Cms.UI.Admin";
    private const string ScriptTag = "<script src=\"/EPiServer/DxpContentTransfer.Cms13/ClientResources/Scripts/AdminInit.js\"></script>";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAdminPageRequest(context))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var contentType = context.Response.ContentType ?? string.Empty;

        if (!contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await buffer.CopyToAsync(originalBody);
            return;
        }

        var html = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();
        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        if (bodyClose < 0)
        {
            var raw = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength = raw.Length;
            await originalBody.WriteAsync(raw);
            return;
        }

        var modified = html[..bodyClose] + ScriptTag + html[bodyClose..];
        var bytes = Encoding.UTF8.GetBytes(modified);
        context.Response.ContentLength = bytes.Length;
        await originalBody.WriteAsync(bytes);
    }

    private static bool IsAdminPageRequest(HttpContext context)
    {
        return HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments(AdminPath, StringComparison.OrdinalIgnoreCase);
    }
}
