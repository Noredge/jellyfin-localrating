using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.LocalRating.Compatibility;

public sealed class CompatibilityFilter(IRatingScope scope) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var settings = scope.Snapshot;
        var args = context.ActionArguments;
        object? Value(string key) => args.TryGetValue(key, out var value) ? value : null;
        var identity = context.HttpContext.User;
        var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
        if (!settings.Enabled || identity.Identity?.IsAuthenticated != true
            || string.Equals(identity.FindFirstValue("Jellyfin-IsApiKey"), "True", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(identity.FindFirstValue("Jellyfin-UserId"), out var user)
            || !settings.Rules.TryGetValue(user, out var libraries)
            || (Value("userId") is Guid supplied && supplied != user)
            || descriptor?.ControllerTypeInfo.FullName != "Jellyfin.Api.Controllers.ItemsController"
            || descriptor.ControllerTypeInfo.Assembly.GetName().Name != "Jellyfin.Api"
            || descriptor.MethodInfo.Name != "GetItems"
            || context.HttpContext.Request.Method != "GET"
            || Value("parentId") is not Guid library || !libraries.Contains(library)
            || Value("recursive") is not true
            || Value("includeItemTypes") is not BaseItemKind[] types || types.Length != 1 || types[0] != BaseItemKind.Movie
            || Value("collapseBoxSetItems") is true
            || (Value("collapseBoxSetItems") is null && !settings.AllowImplicitCollapse))
        {
            await next();
            return;
        }
        var sorts = Value("sortBy") as ItemSortBy[] ?? [];
        var personalSort = sorts.Contains(ItemSortBy.CommunityRating);
        var start = Value("startIndex") as int? ?? 0;
        var limit = Value("limit") as int?;
        var supportedPersonalSort = sorts.Length == 1 || (sorts.Length == 2 && sorts[0] == ItemSortBy.CommunityRating && sorts[1] == ItemSortBy.SortName);
        if ((personalSort && !supportedPersonalSort) || start < 0 || limit < 0)
        {
            await next();
            return;
        }
        var state = new CompatibilityRequest
        {
            Scope = scope,
            UserId = user,
            LibraryId = library,
            Start = start,
            Limit = limit,
            Minimum = Value("minCommunityRating") as double?,
            PersonalSort = personalSort,
            Descending = (Value("sortOrder") as SortOrder[])?.FirstOrDefault() == SortOrder.Descending,
            SecondaryNameSort = personalSort && sorts.Length == 2,
            NameDescending = (Value("sortOrder") as SortOrder[]) is { Length: > 0 } orders && orders[Math.Min(1, orders.Length - 1)] == SortOrder.Descending
        };
        var includeTotal = Value("enableTotalRecordCount") is not false;
        if (state.WholeSet)
        {
            args["startIndex"] = null;
            args["limit"] = null;
            args["minCommunityRating"] = null;
            args["enableTotalRecordCount"] = false;
        }
        context.HttpContext.Items[CompatibilityRequest.Key] = state;
        try
        {
            var executed = await next();
            if (executed.Exception is not null || executed.Result is not ObjectResult result
                || (result.StatusCode ?? 200) != 200 || result.Value is not QueryResult<BaseItemDto> response) return;
            if (!state.Applied)
            {
                executed.Result = new ObjectResult(new { Error = "Local Rating compatibility hook unavailable." }) { StatusCode = 503 };
                return;
            }
            if (state.WholeSet)
            {
                response.StartIndex = start;
                response.TotalRecordCount = includeTotal ? state.Total : 0;
            }
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers["X-LocalRating-Compatibility"] = "personal";
        }
        finally { context.HttpContext.Items.Remove(CompatibilityRequest.Key); }
    }
}
