using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.LocalRating.Tests;

public sealed class WebInjectionMiddlewareTests
{
    private const string Marker = "data-jlr-assets=\"0.6.1\"";

    [Fact]
    public async Task InvokeAsync_InjectsEmbeddedAssetsIntoWebShell()
    {
        var (context, body) = await InvokeAsync(
            "/web/index.html",
            "text/html; charset=utf-8",
            "<html><body><main>Jellyfin</main></body></html>");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(2, CountOccurrences(body, Marker));
        Assert.Contains("<style", body, StringComparison.Ordinal);
        Assert.Contains("<script", body, StringComparison.Ordinal);
        Assert.Contains(".jlr-panel", body, StringComparison.Ordinal);
        Assert.Contains("window.__jlrLoaded", body, StringComparison.Ordinal);
        Assert.Contains("client.getCurrentUserId()", body, StringComparison.Ordinal);
        Assert.Contains("state.ratings.clear()", body, StringComparison.Ordinal);
        Assert.Contains("state.pendingIds.clear()", body, StringComparison.Ordinal);
        Assert.Contains("delete card.dataset.jlrItemId", body, StringComparison.Ordinal);
        Assert.Contains("state.userGeneration !== requestGeneration", body, StringComparison.Ordinal);
        Assert.Contains("currentUserKey() !== requestUserKey", body, StringComparison.Ordinal);
        Assert.Contains("LocalRating/Items/${itemId}/rating", body, StringComparison.Ordinal);
        Assert.Contains("LocalRating/Items/${itemId}/review", body, StringComparison.Ordinal);
        Assert.Contains("保存评价", body, StringComparison.Ordinal);
        Assert.Contains("jlr-current-rating-value", body, StringComparison.Ordinal);
        Assert.Contains("当前评分", body, StringComparison.Ordinal);
        Assert.Contains("★ ${selectedRating} / 10", body, StringComparison.Ordinal);
        Assert.Contains("jlr-review-times", body, StringComparison.Ordinal);
        Assert.Contains("sessionStorage.setItem", body, StringComparison.Ordinal);
        Assert.DoesNotContain("编辑评价", body, StringComparison.Ordinal);
        Assert.DoesNotContain("textarea.focus(", body, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", body, StringComparison.Ordinal);
        Assert.Contains("function placeDetailPanel(host, panel)", body, StringComparison.Ordinal);
        Assert.Contains("window.matchMedia('(max-width: 42rem)').matches", body, StringComparison.Ordinal);
        Assert.Contains("panel.classList.toggle('jlr-panel-narrow', narrowSidePoster)", body, StringComparison.Ordinal);
        Assert.Contains("--jlr-narrow-clearance", body, StringComparison.Ordinal);
        Assert.Contains("Math.ceil(posterBottom - hostTop)", body, StringComparison.Ordinal);
        Assert.Contains("primaryContent.prepend(panel)", body, StringComparison.Ordinal);
        Assert.Contains("setTimeout(refreshPlacement, 500)", body, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('resize'", body, StringComparison.Ordinal);
        Assert.Contains("jlr-undo-button", body, StringComparison.Ordinal);
        Assert.DoesNotContain("再次点击放弃修改", body, StringComparison.Ordinal);
        Assert.Contains("<main>Jellyfin</main>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_ForcesFreshHtmlAndRestoresConditionalRequestHeaders()
    {
        var sawIfNoneMatch = true;
        var sawIfModifiedSince = true;
        var context = CreateContext("/web/index.html");
        context.Request.Headers.IfNoneMatch = "\"old-shell\"";
        context.Request.Headers.IfModifiedSince = "Mon, 31 Aug 2026 12:00:00 GMT";

        var middleware = new LocalRatingWebInjectionMiddleware(async httpContext =>
        {
            sawIfNoneMatch = !StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfNoneMatch);
            sawIfModifiedSince = !StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfModifiedSince);
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "text/html";
            httpContext.Response.Headers.ETag = "\"new-shell\"";
            httpContext.Response.Headers.LastModified = "Tue, 01 Sep 2026 12:00:00 GMT";
            await httpContext.Response.WriteAsync("<html><body>Fresh</body></html>");
        });

        await middleware.InvokeAsync(context);

        Assert.False(sawIfNoneMatch);
        Assert.False(sawIfModifiedSince);
        Assert.Equal("\"old-shell\"", context.Request.Headers.IfNoneMatch.ToString());
        Assert.Equal("Mon, 31 Aug 2026 12:00:00 GMT", context.Request.Headers.IfModifiedSince.ToString());
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", context.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());
        Assert.Equal("0", context.Response.Headers.Expires.ToString());
    }

    [Fact]
    public async Task InvokeAsync_InjectsCompleteLibraryRatingView()
    {
        var (_, body) = await InvokeAsync(
            "/web/index.html",
            "text/html; charset=utf-8",
            "<html><body><main>Jellyfin</main></body></html>");

        Assert.Contains("jlr-library-controls", body, StringComparison.Ordinal);
        Assert.Contains("我的评分筛选与排序", body, StringComparison.Ordinal);
        Assert.Contains("LocalRating/Items/query?${parameters}", body, StringComparison.Ordinal);
        Assert.Contains("const libraryPageSize = 100", body, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes: 'Movie'", body, StringComparison.Ordinal);
        Assert.Contains("unratedPlacement', 'Last'", body, StringComparison.Ordinal);
        Assert.Contains("state.userGeneration !== requestGeneration", body, StringComparison.Ordinal);
        Assert.Contains(".jlr-library-mode > .itemsContainer", body, StringComparison.Ordinal);
        Assert.Contains(".jlr-library-mode > .focuscontainer-x:not(.jlr-library-native-toolbar)", body, StringComparison.Ordinal);
        Assert.Contains(".jlr-library-placeholder[hidden]", body, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(auto-fill", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_InjectsCompactMobileLibraryToolbar()
    {
        var (_, body) = await InvokeAsync(
            "/web/index.html",
            "text/html; charset=utf-8",
            "<html><body><main>Jellyfin</main></body></html>");

        Assert.Contains("我的评分筛选与排序", body, StringComparison.Ordinal);
        Assert.Contains("heading.textContent = '我的评分'", body, StringComparison.Ordinal);
        Assert.Contains("reset.textContent = '重置筛选'", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Jellyfin 默认视图", body, StringComparison.Ordinal);
        Assert.Contains("ui.reset.hidden = true", body, StringComparison.Ordinal);
        Assert.Contains("const hasMultiplePages = total > libraryPageSize", body, StringComparison.Ordinal);
        Assert.Contains("`共 ${total} 部`", body, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr))", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_InjectsConciseRatingHintAndRoundedDesktopPicker()
    {
        var (_, body) = await InvokeAsync(
            "/web/index.html",
            "text/html; charset=utf-8",
            "<html><body><main>Jellyfin</main></body></html>");

        Assert.Contains("选择数字即可自动保存", body, StringComparison.Ordinal);
        Assert.DoesNotContain("选择下方数字即可自动保存", body, StringComparison.Ordinal);
        Assert.Contains("@supports (appearance: base-select)", body, StringComparison.Ordinal);
        Assert.Contains(".jlr-library-select::picker(select)", body, StringComparison.Ordinal);
        Assert.Contains("border-radius: 0.5rem", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotDuplicateExistingAssets()
    {
        var html = $"<html><body><style {Marker}></style></body></html>";
        var (_, body) = await InvokeAsync("/web/index.html", "text/html", html);

        Assert.Equal(html, body);
        Assert.Equal(1, CountOccurrences(body, Marker));
    }

    [Fact]
    public async Task InvokeAsync_PassesThroughNonHtmlResponse()
    {
        const string json = "{\"status\":\"ok\"}";
        var (_, body) = await InvokeAsync("/web/index.html", "application/json", json);

        Assert.Equal(json, body);
        Assert.DoesNotContain(Marker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotInspectNonWebRoute()
    {
        var context = CreateContext("/LocalRating/Items/ratings");
        context.Request.Headers.IfNoneMatch = "\"api\"";
        var downstreamHeader = string.Empty;
        var middleware = new LocalRatingWebInjectionMiddleware(async httpContext =>
        {
            downstreamHeader = httpContext.Request.Headers.IfNoneMatch.ToString();
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsync("[]");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal("\"api\"", downstreamHeader);
        Assert.Equal("[]", ReadBody(context));
    }

    private static async Task<(DefaultHttpContext Context, string Body)> InvokeAsync(
        string path,
        string contentType,
        string responseBody)
    {
        var context = CreateContext(path);
        var middleware = new LocalRatingWebInjectionMiddleware(async httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = contentType;
            await httpContext.Response.WriteAsync(responseBody);
        });

        await middleware.InvokeAsync(context);
        return (context, ReadBody(context));
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }
}
