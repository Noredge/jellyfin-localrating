using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.LocalRating.Models;

public sealed record LocalRatingResponse(
    Guid ItemId,
    string ItemType,
    int? Rating,
    string ReviewText,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record SaveLocalRatingRequest(int? Rating, string? ReviewText);

public sealed record SaveRatingRequest(int? Rating);

public sealed record SaveRatingResponse(Guid ItemId, int? Rating);

public sealed record SaveReviewRequest(string? ReviewText);

public sealed record SaveLocalRatingFailureResponse(
    string Code,
    bool RatingSaved,
    bool ReviewSaved,
    string Message);

public sealed record RatingBatchRequest(IReadOnlyList<Guid>? ItemIds);

public sealed record RatingBatchItem(Guid ItemId, int Rating);

public enum PersonalRatingState
{
    Rated,
    Unrated,
    All
}

public enum PersonalRatingSortOrder
{
    Ascending,
    Descending
}

public enum PersonalRatingUnratedPlacement
{
    First,
    Last
}

public sealed class LocalRatingQueryRequest
{
    public PersonalRatingState? RatingState { get; set; }

    public int? MinRating { get; set; }

    public int? MaxRating { get; set; }

    public Guid? ParentId { get; set; }

    public bool Recursive { get; set; } = true;

    public BaseItemKind[]? IncludeItemTypes { get; set; }

    public PersonalRatingSortOrder? SortOrder { get; set; }

    public PersonalRatingUnratedPlacement UnratedPlacement { get; set; } = PersonalRatingUnratedPlacement.Last;

    public int StartIndex { get; set; }

    public int Limit { get; set; } = 100;
}

public sealed record LocalRatingQueryResponse(
    IReadOnlyList<BaseItemDto> Items,
    int TotalRecordCount,
    int StartIndex);

internal sealed record ReviewRecord(
    Guid UserId,
    Guid ItemId,
    string? MediaPath,
    int? RatingSnapshot,
    string ReviewText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
