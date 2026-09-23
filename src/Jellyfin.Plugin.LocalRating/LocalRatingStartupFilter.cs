using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.LocalRating;

/// <summary>Adds the response-only web asset injector without modifying jellyfin-web on disk.</summary>
public sealed class LocalRatingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<LocalRatingWebInjectionMiddleware>();
        next(app);
    };
}

public sealed class LocalRatingWebInjectionMiddleware
{
    private const string Marker = "data-jlr-assets=\"0.6.1\"";
    private static readonly string Injection = LoadInjection();
    private readonly RequestDelegate _next;

    public LocalRatingWebInjectionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldInspect(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        var originalAcceptEncoding = context.Request.Headers.AcceptEncoding.ToString();
        var originalIfNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        var originalIfModifiedSince = context.Request.Headers.IfModifiedSince.ToString();
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        context.Request.Headers.AcceptEncoding = string.Empty;
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        try
        {
            await _next(context).ConfigureAwait(false);
            context.Response.Body = originalBody;

            buffer.Position = 0;
            if (context.Response.StatusCode == StatusCodes.Status200OK
                && context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                var html = await new StreamReader(buffer, Encoding.UTF8, true, leaveOpen: true)
                    .ReadToEndAsync(context.RequestAborted)
                    .ConfigureAwait(false);
                if (!html.Contains(Marker, StringComparison.Ordinal))
                {
                    html = Inject(html);
                }

                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.ContentLength = bytes.Length;
                context.Response.Headers.Remove("Content-Encoding");
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
                await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                context.Response.ContentLength = buffer.Length;
                await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
            if (string.IsNullOrEmpty(originalAcceptEncoding))
            {
                context.Request.Headers.Remove("Accept-Encoding");
            }
            else
            {
                context.Request.Headers.AcceptEncoding = originalAcceptEncoding;
            }

            RestoreRequestHeader(context, "If-None-Match", originalIfNoneMatch);
            RestoreRequestHeader(context, "If-Modified-Since", originalIfModifiedSince);
        }
    }

    private static bool ShouldInspect(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Inject(string html)
    {
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyEnd >= 0
            ? html.Insert(bodyEnd, Injection)
            : html + Injection;
    }

    private static string LoadInjection()
    {
        var assembly = typeof(LocalRatingWebInjectionMiddleware).Assembly;
        var css = ReadResource(assembly, "Jellyfin.Plugin.LocalRating.Web.localrating.css");
        var js = ReadResource(assembly, "Jellyfin.Plugin.LocalRating.Web.localrating.js");
        return $"<style {Marker}>{css}</style><script {Marker}>{js}</script>";
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void RestoreRequestHeader(HttpContext context, string name, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            context.Request.Headers.Remove(name);
        }
        else
        {
            context.Request.Headers[name] = value;
        }
    }
}
