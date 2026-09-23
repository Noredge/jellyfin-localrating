using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.LocalRating.Compatibility;

public sealed class CompatibilityRequest
{
    public static readonly object Key = new();
    public required IRatingScope Scope { get; init; }
    public Guid UserId { get; init; }
    public Guid LibraryId { get; init; }
    public int Start { get; init; }
    public int? Limit { get; init; }
    public double? Minimum { get; init; }
    public bool PersonalSort { get; init; }
    public bool Descending { get; init; }
    public bool SecondaryNameSort { get; init; }
    public bool NameDescending { get; init; }
    public bool WholeSet => PersonalSort || Minimum.HasValue;
    public bool Applied { get; set; }
    public int Total { get; set; }
}

/// <summary>Preserves native queries, but builds full DTOs only for the selected page.</summary>
public sealed class CompatibilityDtoService(IDtoService inner, IHttpContextAccessor accessor) : IDtoService
{
    public double? GetPrimaryImageAspectRatio(BaseItem item) => inner.GetPrimaryImageAspectRatio(item);
    public BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User? user = null, BaseItem? owner = null) => inner.GetBaseItemDto(item, options, user, owner);
    public BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem>? taggedItems, User? user = null) => inner.GetItemByNameDto(item, options, taggedItems, user);

    public IReadOnlyList<BaseItemDto> GetBaseItemDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User? user = null, BaseItem? owner = null, bool skipVisibilityCheck = false)
    {
        var context = accessor.HttpContext;
        if (context?.Items.TryGetValue(CompatibilityRequest.Key, out var value) != true || value is not CompatibilityRequest state || state.Applied)
            return inner.GetBaseItemDtos(items, options, user, owner, skipVisibilityCheck);
        // Fail closed if the controller supplies an unexpected identity or scope.
        if (user?.Id != state.UserId || (owner is not null && owner.Id != state.LibraryId) || items.Any(item => !state.Scope.BelongsTo(item, state.LibraryId)))
            throw new InvalidOperationException("Local Rating compatibility scope mismatch.");
        state.Applied = true;
        var candidates = new List<(BaseItem Item, float? Score)>(items.Count);
        foreach (var item in items)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            candidates.Add((item, state.Scope.Rating(state.UserId, item)));
        }
        IEnumerable<(BaseItem Item, float? Score)> selected = candidates;
        if (state.Minimum.HasValue) selected = selected.Where(x => x.Score >= state.Minimum.Value);
        if (state.PersonalSort)
        {
            var rated = selected.OrderBy(x => x.Score.HasValue ? 0 : 1);
            var byScore = state.Descending ? rated.ThenByDescending(x => x.Score) : rated.ThenBy(x => x.Score);
            selected = (state.SecondaryNameSort
                ? state.NameDescending ? byScore.ThenByDescending(x => x.Item.SortName, StringComparer.OrdinalIgnoreCase) : byScore.ThenBy(x => x.Item.SortName, StringComparer.OrdinalIgnoreCase)
                : byScore.ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)).ThenBy(x => x.Item.Id);
        }
        var ordered = selected.ToArray();
        state.Total = ordered.Length;
        if (state.WholeSet)
        {
            selected = ordered.Skip(state.Start);
            if (state.Limit.HasValue) selected = selected.Take(state.Limit.Value);
        }
        var page = selected.ToArray();
        var scores = page.ToDictionary(x => x.Item.Id, x => x.Score);
        var result = inner.GetBaseItemDtos(page.Select(x => x.Item).ToArray(), options, user, owner, skipVisibilityCheck);
        foreach (var dto in result) dto.CommunityRating = scores[dto.Id];
        return result;
    }
}
