using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalRating.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRating.Controllers;

[ApiController]
[Authorize]
[Route("LocalRating/Items")]
public sealed class LocalRatingController : ControllerBase
{
    private const int MaxReviewLength = 4000;
    private const int MaxBatchSize = 200;
    private const int MaxQueryPageSize = 100;
    private static readonly BaseItemKind[] DefaultQueryItemTypes =
    [
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.MusicVideo,
        BaseItemKind.Trailer
    ];
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly LocalRatingRepository _repository;
    private readonly ILogger<LocalRatingController> _logger;

    public LocalRatingController(
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        LocalRatingRepository repository,
        ILogger<LocalRatingController> logger)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("{itemId:guid}")]
    [ProducesResponseType<LocalRatingResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LocalRatingResponse>> Get(Guid itemId, CancellationToken cancellationToken)
    {
        var access = ResolveItem(itemId);
        if (access is null)
        {
            return NotFound();
        }

        var (userId, user, item) = access.Value;
        var userData = _userDataManager.GetUserData(user, item);
        var review = await _repository.GetAsync(userId, itemId, cancellationToken).ConfigureAwait(false);
        return ToResponse(item, ToIntegerRating(userData?.Rating), review);
    }

    [HttpPut("{itemId:guid}")]
    [ProducesResponseType<LocalRatingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<SaveLocalRatingFailureResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LocalRatingResponse>> Put(
        Guid itemId,
        [FromBody] SaveLocalRatingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Rating is < 1 or > 10)
        {
            return ValidationProblem("Rating must be null or an integer from 1 through 10.");
        }

        var reviewText = request.ReviewText ?? string.Empty;
        if (reviewText.Length > MaxReviewLength)
        {
            return ValidationProblem($"ReviewText must not exceed {MaxReviewLength} characters.");
        }

        var access = ResolveItem(itemId);
        if (access is null)
        {
            return NotFound();
        }

        var (userId, user, item) = access.Value;
        var userData = _userDataManager.GetUserData(user, item) ?? new UserItemData
        {
            Key = item.GetUserDataKeys().FirstOrDefault() ?? item.Id.ToString("N")
        };
        userData.Rating = request.Rating;
        try
        {
            _userDataManager.SaveUserData(
                user,
                item,
                userData,
                UserDataSaveReason.UpdateUserRating,
                cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableSaveFailure(exception))
        {
            _logger.LogError(exception, "Failed to save Local Rating score for user {UserId} and item {ItemId}.", userId, itemId);
            return SaveFailure(
                "rating_save_failed",
                ratingSaved: false,
                reviewSaved: false,
                "The rating and review could not be saved. Your input may be retried.");
        }

        ReviewRecord review;
        try
        {
            review = await _repository.UpsertAsync(
                userId,
                itemId,
                item.Path,
                request.Rating,
                reviewText,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableSaveFailure(exception))
        {
            _logger.LogError(exception, "Rating saved but review failed for user {UserId} and item {ItemId}.", userId, itemId);
            return SaveFailure(
                "partial_save",
                ratingSaved: true,
                reviewSaved: false,
                "The rating was saved, but the review was not. Your input may be retried.");
        }

        return ToResponse(item, request.Rating, review);
    }

    [HttpPut("{itemId:guid}/rating")]
    [ProducesResponseType<SaveRatingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<SaveLocalRatingFailureResponse>(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SaveRatingResponse> PutRating(
        Guid itemId,
        [FromBody] SaveRatingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Rating is < 1 or > 10)
        {
            return ValidationProblem("Rating must be null or an integer from 1 through 10.");
        }

        var access = ResolveItem(itemId);
        if (access is null)
        {
            return NotFound();
        }

        var (userId, user, item) = access.Value;
        var userData = _userDataManager.GetUserData(user, item) ?? new UserItemData
        {
            Key = item.GetUserDataKeys().FirstOrDefault() ?? item.Id.ToString("N")
        };
        userData.Rating = request.Rating;
        try
        {
            _userDataManager.SaveUserData(
                user,
                item,
                userData,
                UserDataSaveReason.UpdateUserRating,
                cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableSaveFailure(exception))
        {
            _logger.LogError(exception, "Failed to save Local Rating score for user {UserId} and item {ItemId}.", userId, itemId);
            return SaveFailure(
                "rating_save_failed",
                ratingSaved: false,
                reviewSaved: true,
                "The rating could not be saved. The review was not changed.");
        }

        return new SaveRatingResponse(item.Id, request.Rating);
    }

    [HttpPut("{itemId:guid}/review")]
    [ProducesResponseType<LocalRatingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<SaveLocalRatingFailureResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LocalRatingResponse>> PutReview(
        Guid itemId,
        [FromBody] SaveReviewRequest request,
        CancellationToken cancellationToken)
    {
        var reviewText = request.ReviewText ?? string.Empty;
        if (reviewText.Length > MaxReviewLength)
        {
            return ValidationProblem($"ReviewText must not exceed {MaxReviewLength} characters.");
        }

        var access = ResolveItem(itemId);
        if (access is null)
        {
            return NotFound();
        }

        var (userId, user, item) = access.Value;
        var rating = ToIntegerRating(_userDataManager.GetUserData(user, item)?.Rating);
        try
        {
            var review = await _repository.UpsertAsync(
                userId,
                itemId,
                item.Path,
                rating,
                reviewText,
                cancellationToken).ConfigureAwait(false);
            return ToResponse(item, rating, review);
        }
        catch (Exception exception) when (IsRecoverableSaveFailure(exception))
        {
            _logger.LogError(exception, "Failed to save Local Rating review for user {UserId} and item {ItemId}.", userId, itemId);
            return SaveFailure(
                "review_save_failed",
                ratingSaved: true,
                reviewSaved: false,
                "The review could not be saved. The rating was not changed.");
        }
    }

    [HttpPost("ratings")]
    [ProducesResponseType<IReadOnlyList<RatingBatchItem>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RatingBatchItem>> GetRatings([FromBody] RatingBatchRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return Unauthorized();
        }

        var itemIds = request.ItemIds?.Distinct().Take(MaxBatchSize + 1).ToArray() ?? [];
        if (itemIds.Length > MaxBatchSize)
        {
            return BadRequest($"At most {MaxBatchSize} item ids may be requested at once.");
        }

        var results = new List<RatingBatchItem>();
        foreach (var itemId in itemIds)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null || !item.IsVisible(user, false))
            {
                continue;
            }

            var rating = ToIntegerRating(_userDataManager.GetUserData(user, item)?.Rating);
            if (rating is not null)
            {
                results.Add(new RatingBatchItem(itemId, rating.Value));
            }
        }

        return results;
    }

    [HttpGet("query")]
    [ProducesResponseType<LocalRatingQueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<LocalRatingQueryResponse> Query(
        [FromQuery] LocalRatingQueryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RatingState is null || !Enum.IsDefined(request.RatingState.Value))
        {
            return ValidationProblem("RatingState must be Rated, Unrated, or All.");
        }

        if (request.MinRating is < 1 or > 10 || request.MaxRating is < 1 or > 10
            || request.MinRating > request.MaxRating
            || (request.RatingState == PersonalRatingState.Unrated
                && (request.MinRating.HasValue || request.MaxRating.HasValue)))
        {
            return ValidationProblem(
                detail: "Rating bounds must be from 1 through 10, ordered, and cannot be combined with Unrated.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.SortOrder is PersonalRatingSortOrder sortOrder && !Enum.IsDefined(sortOrder))
        {
            return ValidationProblem("SortOrder must be Ascending or Descending.");
        }

        if (!Enum.IsDefined(request.UnratedPlacement))
        {
            return ValidationProblem("UnratedPlacement must be First or Last.");
        }

        if (request.StartIndex < 0)
        {
            return ValidationProblem("StartIndex must be zero or greater.");
        }

        if (request.Limit is < 1 or > MaxQueryPageSize)
        {
            return ValidationProblem($"Limit must be from 1 through {MaxQueryPageSize}.");
        }

        var includeItemTypes = request.IncludeItemTypes is { Length: > 0 }
            ? request.IncludeItemTypes.Distinct().ToArray()
            : DefaultQueryItemTypes;
        if (includeItemTypes.Any(itemType => !DefaultQueryItemTypes.Contains(itemType)))
        {
            return ValidationProblem("IncludeItemTypes contains an unsupported item type.");
        }

        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return Unauthorized();
        }

        BaseItem? parent = null;
        if (request.ParentId is Guid parentId)
        {
            parent = _libraryManager.GetItemById(parentId);
            if (parent is null || !parent.IsVisible(user, false))
            {
                return NotFound();
            }
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = request.Recursive,
            IncludeItemTypes = includeItemTypes,
            EnableTotalRecordCount = false
        };
        if (parent is not null)
        {
            query.Parent = parent;
            query.ParentId = parent.Id;
        }

        var candidates = _libraryManager.GetItemList(query);
        var matchingItems = new List<PersonalRatingCandidate>(candidates.Count);
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.IsVisible(user, false))
            {
                continue;
            }

            var rating = ToIntegerRating(_userDataManager.GetUserData(user, item)?.Rating);
            var hasRating = rating is not null;
            if ((request.MinRating.HasValue || request.MaxRating.HasValue)
                && (!hasRating || rating < request.MinRating || rating > request.MaxRating))
            {
                continue;
            }

            if (request.RatingState == PersonalRatingState.All
                || (request.RatingState == PersonalRatingState.Rated && hasRating)
                || (request.RatingState == PersonalRatingState.Unrated && !hasRating))
            {
                matchingItems.Add(new PersonalRatingCandidate(item, rating));
            }
        }

        var orderedItems = request.SortOrder is PersonalRatingSortOrder requestedSortOrder
            ? OrderByPersonalRating(matchingItems, requestedSortOrder, request.UnratedPlacement)
            : matchingItems;
        var pageItems = orderedItems
            .Skip(request.StartIndex)
            .Take(request.Limit)
            .Select(candidate => candidate.Item)
            .ToArray();
        var dtoOptions = new DtoOptions
        {
            EnableImages = true,
            EnableUserData = true
        };
        var owner = parent ?? _libraryManager.GetUserRootFolder();
        var items = _dtoService.GetBaseItemDtos(pageItems, dtoOptions, user, owner);

        return new LocalRatingQueryResponse(items, matchingItems.Count, request.StartIndex);
    }

    private static IEnumerable<PersonalRatingCandidate> OrderByPersonalRating(
        IEnumerable<PersonalRatingCandidate> candidates,
        PersonalRatingSortOrder sortOrder,
        PersonalRatingUnratedPlacement unratedPlacement)
    {
        var ordered = candidates.OrderBy(candidate => GetUnratedRank(candidate, unratedPlacement));
        var byRating = sortOrder == PersonalRatingSortOrder.Ascending
            ? ordered.ThenBy(candidate => candidate.Rating)
            : ordered.ThenByDescending(candidate => candidate.Rating);

        return byRating
            .ThenBy(candidate => candidate.Item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Item.Id);
    }

    private static int GetUnratedRank(
        PersonalRatingCandidate candidate,
        PersonalRatingUnratedPlacement unratedPlacement) =>
        candidate.Rating is null
            ? unratedPlacement == PersonalRatingUnratedPlacement.First ? 0 : 1
            : unratedPlacement == PersonalRatingUnratedPlacement.First ? 1 : 0;

    private (Guid UserId, Jellyfin.Database.Implementations.Entities.User User, BaseItem Item)? ResolveItem(Guid itemId)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return null;
        }

        var user = _userManager.GetUserById(userId.Value);
        var item = _libraryManager.GetItemById(itemId);
        return user is null || item is null || !item.IsVisible(user, false)
            ? null
            : (userId.Value, user, item);
    }

    private Guid? GetCurrentUserId()
    {
        var value = User.FindFirstValue("Jellyfin-UserId");
        return Guid.TryParse(value, out var userId) ? userId : null;
    }

    private static int? ToIntegerRating(double? rating)
    {
        if (rating is null)
        {
            return null;
        }

        var rounded = (int)Math.Round(rating.Value, MidpointRounding.AwayFromZero);
        return rounded is >= 1 and <= 10 ? rounded : null;
    }

    private static bool IsRecoverableSaveFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;

    private ObjectResult SaveFailure(string code, bool ratingSaved, bool reviewSaved, string message) =>
        StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new SaveLocalRatingFailureResponse(code, ratingSaved, reviewSaved, message));

    private static LocalRatingResponse ToResponse(BaseItem item, int? rating, ReviewRecord? review) =>
        new(
            item.Id,
            item.GetClientTypeName(),
            rating,
            review?.ReviewText ?? string.Empty,
            review?.CreatedAt,
            review?.UpdatedAt);

    private sealed record PersonalRatingCandidate(BaseItem Item, int? Rating);
}
