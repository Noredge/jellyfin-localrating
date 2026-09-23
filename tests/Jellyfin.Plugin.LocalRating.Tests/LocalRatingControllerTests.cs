using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRating.Controllers;
using Jellyfin.Plugin.LocalRating.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRating.Tests;

public sealed class LocalRatingControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Jellyfin.Plugin.LocalRating.ControllerTests",
        Guid.NewGuid().ToString("N"));
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly IUserDataManager _userDataManager = Substitute.For<IUserDataManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IDtoService _dtoService = Substitute.For<IDtoService>();
    private readonly ILogger<LocalRatingController> _logger = Substitute.For<ILogger<LocalRatingController>>();

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Put_RejectsRatingOutsideSupportedRangeBeforeServiceAccess(int rating)
    {
        var controller = CreateController();

        var result = await controller.Put(
            Guid.NewGuid(),
            new SaveLocalRatingRequest(rating, string.Empty),
            TestContext.Current.CancellationToken);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public async Task Put_RejectsReviewOverFourThousandCharactersBeforeServiceAccess()
    {
        var controller = CreateController();

        var result = await controller.Put(
            Guid.NewGuid(),
            new SaveLocalRatingRequest(8, new string('x', 4001)),
            TestContext.Current.CancellationToken);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public async Task Put_WhenBothStoresSucceed_ReturnsSavedRatingAndReview()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.Path = @"D:\Media\successful-save.mp4";
        item.GetClientTypeName().Returns("Movie");
        item.IsVisible(user, false).Returns(true);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        _userDataManager.GetUserData(user, item).Returns(new UserItemData { Key = itemId.ToString("N") });
        var controller = CreateController(userId);

        var result = await controller.Put(
            itemId,
            new SaveLocalRatingRequest(10, "Saved review"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Value);
        Assert.Equal(itemId, result.Value.ItemId);
        Assert.Equal("Movie", result.Value.ItemType);
        Assert.Equal(10, result.Value.Rating);
        Assert.Equal("Saved review", result.Value.ReviewText);
        _userDataManager.Received(1).SaveUserData(
            user,
            item,
            Arg.Is<UserItemData>(data => data.Rating == 10),
            UserDataSaveReason.UpdateUserRating,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Put_WhenRatingStorageFails_ReturnsRetryableFailureWithoutWritingReview()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.IsVisible(user, false).Returns(true);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        _userDataManager.GetUserData(user, item).Returns(new UserItemData { Key = itemId.ToString("N") });
        _userDataManager
            .When(manager => manager.SaveUserData(
                user,
                item,
                Arg.Any<UserItemData>(),
                UserDataSaveReason.UpdateUserRating,
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Simulated rating failure."));
        var controller = CreateController(userId);

        var result = await controller.Put(
            itemId,
            new SaveLocalRatingRequest(8, "Unsaved review"),
            TestContext.Current.CancellationToken);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        var failure = Assert.IsType<SaveLocalRatingFailureResponse>(response.Value);
        Assert.Equal("rating_save_failed", failure.Code);
        Assert.False(failure.RatingSaved);
        Assert.False(failure.ReviewSaved);
        Assert.False(File.Exists(Path.Combine(_root, "data", "localrating", "localrating.db")));
    }

    [Fact]
    public async Task Put_WhenReviewStorageFails_ReportsThatRatingWasSaved()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.Path = @"D:\Media\partial-save.mp4";
        item.IsVisible(user, false).Returns(true);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        _userDataManager.GetUserData(user, item).Returns(new UserItemData { Key = itemId.ToString("N") });
        await CreateUnsupportedDatabaseAsync();
        var controller = CreateController(userId);

        var result = await controller.Put(
            itemId,
            new SaveLocalRatingRequest(9, "Review that must be retried"),
            TestContext.Current.CancellationToken);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        var failure = Assert.IsType<SaveLocalRatingFailureResponse>(response.Value);
        Assert.Equal("partial_save", failure.Code);
        Assert.True(failure.RatingSaved);
        Assert.False(failure.ReviewSaved);
        _userDataManager.Received(1).SaveUserData(
            user,
            item,
            Arg.Is<UserItemData>(data => data.Rating == 9),
            UserDataSaveReason.UpdateUserRating,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void PutRating_RejectsRatingOutsideSupportedRangeBeforeServiceAccess(int rating)
    {
        var controller = CreateController();

        var result = controller.PutRating(
            Guid.NewGuid(),
            new SaveRatingRequest(rating),
            TestContext.Current.CancellationToken);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public void PutRating_SavesOnlyJellyfinUserRating()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.IsVisible(user, false).Returns(true);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        _userDataManager.GetUserData(user, item).Returns(new UserItemData { Key = itemId.ToString("N") });
        var controller = CreateController(userId);

        var result = controller.PutRating(
            itemId,
            new SaveRatingRequest(7),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Value);
        Assert.Equal(itemId, result.Value.ItemId);
        Assert.Equal(7, result.Value.Rating);
        _userDataManager.Received(1).SaveUserData(
            user,
            item,
            Arg.Is<UserItemData>(data => data.Rating == 7),
            UserDataSaveReason.UpdateUserRating,
            Arg.Any<CancellationToken>());
        Assert.False(File.Exists(Path.Combine(_root, "data", "localrating", "localrating.db")));
    }

    [Fact]
    public void PutRating_WhenStorageFails_ReportsReviewWasNotChanged()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.IsVisible(user, false).Returns(true);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        _userDataManager.GetUserData(user, item).Returns(new UserItemData { Key = itemId.ToString("N") });
        _userDataManager
            .When(manager => manager.SaveUserData(
                user,
                item,
                Arg.Any<UserItemData>(),
                UserDataSaveReason.UpdateUserRating,
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Simulated rating failure."));
        var controller = CreateController(userId);

        var result = controller.PutRating(
            itemId,
            new SaveRatingRequest(6),
            TestContext.Current.CancellationToken);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        var failure = Assert.IsType<SaveLocalRatingFailureResponse>(response.Value);
        Assert.Equal("rating_save_failed", failure.Code);
        Assert.False(failure.RatingSaved);
        Assert.True(failure.ReviewSaved);
    }

    [Fact]
    public async Task PutReview_SavesOnlyPrivateReviewAndCurrentRatingSnapshot()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.Path = @"D:\Media\review-only.mp4";
        item.GetClientTypeName().Returns("Movie");
        item.IsVisible(user, false).Returns(true);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        _userDataManager.GetUserData(user, item).Returns(new UserItemData
        {
            Key = itemId.ToString("N"),
            Rating = 8
        });
        var repository = new LocalRatingRepository(new TestApplicationPaths(_root));
        var controller = CreateController(userId, repository);

        var result = await controller.PutReview(
            itemId,
            new SaveReviewRequest("Review only"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Value);
        Assert.Equal(8, result.Value.Rating);
        Assert.Equal("Review only", result.Value.ReviewText);
        Assert.DoesNotContain(
            _userDataManager.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IUserDataManager.SaveUserData));
        var stored = await repository.GetAsync(userId, itemId, TestContext.Current.CancellationToken);
        Assert.Equal(8, stored?.RatingSnapshot);
        Assert.Equal("Review only", stored?.ReviewText);
    }

    [Fact]
    public async Task PutReview_RejectsOversizedInputBeforeServiceAccess()
    {
        var controller = CreateController();

        var result = await controller.PutReview(
            Guid.NewGuid(),
            new SaveReviewRequest(new string('x', 4001)),
            TestContext.Current.CancellationToken);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public async Task Get_WithoutAuthenticatedJellyfinUserDoesNotExposeItem()
    {
        var controller = CreateController();

        var result = await controller.Get(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public async Task Get_WithUnknownUserDoesNotReadPrivateData()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);
        _userManager.GetUserById(userId).Returns((User?)null);

        var result = await controller.Get(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(_userDataManager.ReceivedCalls());
    }

    [Fact]
    public async Task Get_WithInvisibleItemDoesNotReadPrivateData()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        var controller = CreateController(userId);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        item.IsVisible(user, false).Returns(false);

        var result = await controller.Get(itemId, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(_userDataManager.ReceivedCalls());
    }

    [Fact]
    public void GetRatings_WithoutAuthenticatedJellyfinUserReturnsUnauthorized()
    {
        var controller = CreateController();

        var result = controller.GetRatings(new RatingBatchRequest([Guid.NewGuid()]));

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public void GetRatings_WithUnknownUserReturnsUnauthorized()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);
        _userManager.GetUserById(userId).Returns((User?)null);

        var result = controller.GetRatings(new RatingBatchRequest([Guid.NewGuid()]));

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public void GetRatings_RejectsMoreThanTwoHundredDistinctItemsBeforeLibraryAccess()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);
        _userManager.GetUserById(userId).Returns(CreateUser(userId));
        var itemIds = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();

        var result = controller.GetRatings(new RatingBatchRequest(itemIds));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public void GetRatings_WithNoItemIdsReturnsAnEmptySuccessfulResult()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);
        _userManager.GetUserById(userId).Returns(CreateUser(userId));

        var result = controller.GetRatings(new RatingBatchRequest(null));

        Assert.NotNull(result.Value);
        Assert.Empty(result.Value);
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public void GetRatings_DeduplicatesItemsAndRoundsVisibleUserRating()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var user = CreateUser(userId);
        var item = Substitute.For<BaseItem>();
        var controller = CreateController(userId);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(itemId).Returns(item);
        item.IsVisible(user, false).Returns(true);
        _userDataManager.GetUserData(user, item).Returns(new UserItemData { Key = itemId.ToString("N"), Rating = 7.5 });

        var result = controller.GetRatings(new RatingBatchRequest([itemId, itemId]));

        var rating = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<RatingBatchItem>>(result.Value));
        Assert.Equal(itemId, rating.ItemId);
        Assert.Equal(8, rating.Rating);
        _libraryManager.Received(1).GetItemById(itemId);
    }

    [Fact]
    public void GetRatings_ReturnsOnlyTheAuthenticatedUsersRatingForTheSameItem()
    {
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var firstUser = CreateUser(firstUserId);
        var secondUser = CreateUser(secondUserId);
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.IsVisible(firstUser, false).Returns(true);
        item.IsVisible(secondUser, false).Returns(true);
        _userManager.GetUserById(firstUserId).Returns(firstUser);
        _userManager.GetUserById(secondUserId).Returns(secondUser);
        _libraryManager.GetItemById(itemId).Returns(item);
        _userDataManager.GetUserData(firstUser, item).Returns(new UserItemData { Key = itemId.ToString("N"), Rating = 5 });
        _userDataManager.GetUserData(secondUser, item).Returns(new UserItemData { Key = itemId.ToString("N"), Rating = 8 });

        var firstResult = CreateController(firstUserId).GetRatings(new RatingBatchRequest([itemId]));
        var secondResult = CreateController(secondUserId).GetRatings(new RatingBatchRequest([itemId]));

        Assert.Equal(5, Assert.Single(firstResult.Value!).Rating);
        Assert.Equal(8, Assert.Single(secondResult.Value!).Rating);
    }

    [Fact]
    public void Query_WithoutAuthenticatedJellyfinUserReturnsUnauthorized()
    {
        var controller = CreateController();

        var result = controller.Query(
            new LocalRatingQueryRequest { RatingState = PersonalRatingState.Rated },
            TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
        Assert.Empty(_dtoService.ReceivedCalls());
    }

    [Fact]
    public void Query_WithUnknownUserReturnsUnauthorized()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);
        _userManager.GetUserById(userId).Returns((User?)null);

        var result = controller.Query(
            new LocalRatingQueryRequest { RatingState = PersonalRatingState.Unrated },
            TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
        Assert.Empty(_dtoService.ReceivedCalls());
    }

    [Theory]
    [InlineData(null, 0, 100)]
    [InlineData(999, 0, 100)]
    [InlineData((int)PersonalRatingState.Rated, -1, 100)]
    [InlineData((int)PersonalRatingState.Unrated, 0, 0)]
    [InlineData((int)PersonalRatingState.Unrated, 0, 101)]
    public void Query_RejectsInvalidArgumentsBeforeServiceAccess(int? ratingState, int startIndex, int limit)
    {
        var controller = CreateController();

        var result = controller.Query(
            new LocalRatingQueryRequest
            {
                RatingState = (PersonalRatingState?)ratingState,
                StartIndex = startIndex,
                Limit = limit
            },
            TestContext.Current.CancellationToken);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
        Assert.Empty(_dtoService.ReceivedCalls());
    }

    [Fact]
    public void Query_RejectsUnsupportedItemTypesBeforeServiceAccess()
    {
        var controller = CreateController();

        var result = controller.Query(
            new LocalRatingQueryRequest
            {
                RatingState = PersonalRatingState.Rated,
                IncludeItemTypes = [BaseItemKind.Series]
            },
            TestContext.Current.CancellationToken);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
        Assert.Empty(_dtoService.ReceivedCalls());
    }

    [Theory]
    [InlineData(999, (int)PersonalRatingUnratedPlacement.Last)]
    [InlineData((int)PersonalRatingSortOrder.Ascending, 999)]
    public void Query_RejectsInvalidPersonalRatingSortArgumentsBeforeServiceAccess(
        int sortOrder,
        int unratedPlacement)
    {
        var controller = CreateController();

        var result = controller.Query(
            new LocalRatingQueryRequest
            {
                RatingState = PersonalRatingState.All,
                SortOrder = (PersonalRatingSortOrder)sortOrder,
                UnratedPlacement = (PersonalRatingUnratedPlacement)unratedPlacement
            },
            TestContext.Current.CancellationToken);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_userManager.ReceivedCalls());
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.Empty(_libraryManager.ReceivedCalls());
        Assert.Empty(_dtoService.ReceivedCalls());
    }

    [Fact]
    public void Query_WithInvisibleParentReturnsNotFoundBeforeReadingRatings()
    {
        var userId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var user = CreateUser(userId);
        var parent = Substitute.For<BaseItem>();
        parent.Id = parentId;
        parent.IsVisible(user, false).Returns(false);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(parentId).Returns(parent);
        var controller = CreateController(userId);

        var result = controller.Query(
            new LocalRatingQueryRequest
            {
                RatingState = PersonalRatingState.Rated,
                ParentId = parentId
            },
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(_userDataManager.ReceivedCalls());
        Assert.DoesNotContain(
            _libraryManager.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(ILibraryManager.GetItemList));
        Assert.Empty(_dtoService.ReceivedCalls());
    }

    [Fact]
    public void Query_FiltersBeforePaginationAndExcludesInvisibleItems()
    {
        var userId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var user = CreateUser(userId);
        var parent = CreateVisibleItem(parentId, user);
        parent.GetClientTypeName().Returns("Folder");
        var ratedFirst = CreateVisibleItem(Guid.NewGuid(), user);
        var unrated = CreateVisibleItem(Guid.NewGuid(), user);
        var ratedSecond = CreateVisibleItem(Guid.NewGuid(), user);
        var invisibleRated = Substitute.For<BaseItem>();
        invisibleRated.Id = Guid.NewGuid();
        invisibleRated.IsVisible(user, false).Returns(false);
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemById(parentId).Returns(parent);
        _userDataManager.GetUserData(user, ratedFirst).Returns(new UserItemData
        {
            Key = ratedFirst.Id.ToString("N"),
            Rating = 10
        });
        _userDataManager.GetUserData(user, unrated).Returns(new UserItemData
        {
            Key = unrated.Id.ToString("N")
        });
        _userDataManager.GetUserData(user, ratedSecond).Returns(new UserItemData
        {
            Key = ratedSecond.Id.ToString("N"),
            Rating = 6
        });
        InternalItemsQuery? capturedQuery = null;
        _libraryManager
            .GetItemList(Arg.Do<InternalItemsQuery>(query => capturedQuery = query))
            .Returns([ratedFirst, unrated, ratedSecond, invisibleRated]);
        ConfigureDtoProjection();
        var controller = CreateController(userId);

        var result = controller.Query(
            new LocalRatingQueryRequest
            {
                RatingState = PersonalRatingState.Rated,
                ParentId = parentId,
                Recursive = false,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Movie],
                StartIndex = 1,
                Limit = 1
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.TotalRecordCount);
        Assert.Equal(1, result.Value.StartIndex);
        Assert.Equal(ratedSecond.Id, Assert.Single(result.Value.Items).Id);
        Assert.NotNull(capturedQuery);
        Assert.Same(user, capturedQuery.User);
        Assert.Equal(parentId, capturedQuery.ParentId);
        Assert.False(capturedQuery.Recursive);
        Assert.Equal([BaseItemKind.Movie], capturedQuery.IncludeItemTypes);
        Assert.Null(capturedQuery.StartIndex);
        Assert.Null(capturedQuery.Limit);
        _userDataManager.DidNotReceive().GetUserData(user, invisibleRated);
        _dtoService.Received(1).GetBaseItemDtos(
            Arg.Is<IReadOnlyList<BaseItem>>(items => items.Count == 1 && items[0] == ratedSecond),
            Arg.Is<DtoOptions>(options => options.EnableImages && options.EnableUserData),
            user,
            parent);
        Assert.False(File.Exists(Path.Combine(_root, "data", "localrating", "localrating.db")));
    }

    [Theory]
    [InlineData(null, 6, 5, 6)]
    [InlineData(8, 8, 0, 8)]
    [InlineData(7, 9, 2, 9)]
    [InlineData(9, null, 1, 10)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(10, 10, 0, 10)]
    public void Query_InclusiveBoundsFilterBeforeTotalsAndPaging(int? min, int? max, int start, int expectedScore)
    {
        var userId = Guid.NewGuid();
        var user = CreateUser(userId);
        var other = CreateUser(Guid.NewGuid());
        _userManager.GetUserById(userId).Returns(user);
        var items = Enumerable.Range(0, 11).Select(_ => CreateVisibleItem(Guid.NewGuid(), user)).ToArray();
        for (var score = 0; score <= 10; score++)
        {
            _userDataManager.GetUserData(user, items[score]).Returns(new UserItemData
            {
                Key = items[score].Id.ToString("N"),
                Rating = score == 0 ? null : score
            });
            _userDataManager.GetUserData(other, items[score]).Returns(new UserItemData
            {
                Key = items[score].Id.ToString("N"),
                Rating = 10
            });
        }

        var hidden = Substitute.For<BaseItem>();
        hidden.Id = Guid.NewGuid();
        hidden.IsVisible(user, false).Returns(false);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([hidden, .. items]);
        ConfigureDtoProjection();
        var result = CreateController(userId).Query(new LocalRatingQueryRequest
        {
            RatingState = PersonalRatingState.All,
            MinRating = min,
            MaxRating = max,
            SortOrder = PersonalRatingSortOrder.Ascending,
            StartIndex = start,
            Limit = 1
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Value);
        Assert.Equal((max ?? 10) - (min ?? 1) + 1, result.Value.TotalRecordCount);
        Assert.Equal(items[expectedScore].Id, Assert.Single(result.Value.Items).Id);
        _userDataManager.DidNotReceive().GetUserData(user, hidden);
    }

    [Theory]
    [InlineData(0, null, PersonalRatingState.Rated)]
    [InlineData(null, 11, PersonalRatingState.All)]
    [InlineData(8, 7, PersonalRatingState.Rated)]
    [InlineData(null, 6, PersonalRatingState.Unrated)]
    public void Query_RejectsInvalidRatingBounds(int? min, int? max, PersonalRatingState state)
    {
        var result = CreateController(Guid.NewGuid()).Query(new LocalRatingQueryRequest
        {
            RatingState = state,
            MinRating = min,
            MaxRating = max
        }, TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Empty(_libraryManager.ReceivedCalls());
    }

    [Fact]
    public void Query_UsesOnlyTheAuthenticatedUsersRatingState()
    {
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var firstUser = CreateUser(firstUserId);
        var secondUser = CreateUser(secondUserId);
        var firstItem = Substitute.For<BaseItem>();
        firstItem.Id = Guid.NewGuid();
        firstItem.IsVisible(Arg.Any<User>(), false).Returns(true);
        var secondItem = Substitute.For<BaseItem>();
        secondItem.Id = Guid.NewGuid();
        secondItem.IsVisible(Arg.Any<User>(), false).Returns(true);
        var root = Substitute.For<Folder>();
        _userManager.GetUserById(firstUserId).Returns(firstUser);
        _userManager.GetUserById(secondUserId).Returns(secondUser);
        _libraryManager.GetUserRootFolder().Returns(root);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([firstItem, secondItem]);
        _userDataManager.GetUserData(firstUser, firstItem).Returns(new UserItemData
        {
            Key = firstItem.Id.ToString("N"),
            Rating = 8
        });
        _userDataManager.GetUserData(firstUser, secondItem).Returns(new UserItemData
        {
            Key = secondItem.Id.ToString("N")
        });
        _userDataManager.GetUserData(secondUser, firstItem).Returns(new UserItemData
        {
            Key = firstItem.Id.ToString("N")
        });
        _userDataManager.GetUserData(secondUser, secondItem).Returns(new UserItemData
        {
            Key = secondItem.Id.ToString("N"),
            Rating = 9
        });
        ConfigureDtoProjection();

        var firstResult = CreateController(firstUserId).Query(
            new LocalRatingQueryRequest { RatingState = PersonalRatingState.Rated },
            TestContext.Current.CancellationToken);
        var secondResult = CreateController(secondUserId).Query(
            new LocalRatingQueryRequest { RatingState = PersonalRatingState.Rated },
            TestContext.Current.CancellationToken);
        var firstUnratedResult = CreateController(firstUserId).Query(
            new LocalRatingQueryRequest { RatingState = PersonalRatingState.Unrated },
            TestContext.Current.CancellationToken);

        Assert.Equal(firstItem.Id, Assert.Single(firstResult.Value!.Items).Id);
        Assert.Equal(secondItem.Id, Assert.Single(secondResult.Value!.Items).Id);
        Assert.Equal(secondItem.Id, Assert.Single(firstUnratedResult.Value!.Items).Id);
    }

    [Fact]
    public void Query_SortsAllItemsByPersonalRatingBeforePaginationWithDeterministicTies()
    {
        var userId = Guid.NewGuid();
        var user = CreateUser(userId);
        var unrated = CreateVisibleItem(Guid.Parse("00000000-0000-0000-0000-000000000004"), user);
        unrated.Name = "Unrated";
        var highZeta = CreateVisibleItem(Guid.Parse("00000000-0000-0000-0000-000000000002"), user);
        highZeta.Name = "Zeta";
        var low = CreateVisibleItem(Guid.Parse("00000000-0000-0000-0000-000000000003"), user);
        low.Name = "Low";
        var highAlpha = CreateVisibleItem(Guid.Parse("00000000-0000-0000-0000-000000000001"), user);
        highAlpha.Name = "Alpha";
        _userManager.GetUserById(userId).Returns(user);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([unrated, highZeta, low, highAlpha]);
        _userDataManager.GetUserData(user, unrated).Returns(new UserItemData { Key = unrated.Id.ToString("N") });
        _userDataManager.GetUserData(user, highZeta).Returns(new UserItemData { Key = highZeta.Id.ToString("N"), Rating = 8 });
        _userDataManager.GetUserData(user, low).Returns(new UserItemData { Key = low.Id.ToString("N"), Rating = 3 });
        _userDataManager.GetUserData(user, highAlpha).Returns(new UserItemData { Key = highAlpha.Id.ToString("N"), Rating = 8 });
        ConfigureDtoProjection();
        var controller = CreateController(userId);

        var descending = controller.Query(
            new LocalRatingQueryRequest
            {
                RatingState = PersonalRatingState.All,
                SortOrder = PersonalRatingSortOrder.Descending,
                UnratedPlacement = PersonalRatingUnratedPlacement.Last,
                StartIndex = 1,
                Limit = 2
            },
            TestContext.Current.CancellationToken);
        var ascending = controller.Query(
            new LocalRatingQueryRequest
            {
                RatingState = PersonalRatingState.All,
                SortOrder = PersonalRatingSortOrder.Ascending,
                UnratedPlacement = PersonalRatingUnratedPlacement.First
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(descending.Value);
        Assert.Equal(4, descending.Value.TotalRecordCount);
        Assert.Equal([highZeta.Id, low.Id], descending.Value.Items.Select(item => item.Id));
        Assert.NotNull(ascending.Value);
        Assert.Equal(
            [unrated.Id, low.Id, highAlpha.Id, highZeta.Id],
            ascending.Value.Items.Select(item => item.Id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalRatingController CreateController(
        Guid? userId = null,
        LocalRatingRepository? repository = null)
    {
        var controller = new LocalRatingController(
            _userManager,
            _userDataManager,
            _libraryManager,
            _dtoService,
            repository ?? new LocalRatingRepository(new TestApplicationPaths(_root)),
            _logger);
        var identity = userId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.Value.ToString())], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
        return controller;
    }

    private static User CreateUser(Guid userId) => new("test-user", "test-auth", "test-reset")
    {
        Id = userId
    };

    private static BaseItem CreateVisibleItem(Guid itemId, User user)
    {
        var item = Substitute.For<BaseItem>();
        item.Id = itemId;
        item.IsVisible(user, false).Returns(true);
        return item;
    }

    private void ConfigureDtoProjection()
    {
        _dtoService
            .GetBaseItemDtos(
                Arg.Any<IReadOnlyList<BaseItem>>(),
                Arg.Any<DtoOptions>(),
                Arg.Any<User>(),
                Arg.Any<BaseItem>())
            .Returns(call => ((IReadOnlyList<BaseItem>)call[0]).Select(item => new BaseItemDto
            {
                Id = item.Id
            }).ToArray());
    }

    private async Task CreateUnsupportedDatabaseAsync()
    {
        var databasePath = Path.Combine(_root, "data", "localrating", "localrating.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = 2;";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class TestApplicationPaths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;
        public string WebPath => Path.Combine(root, "web");
        public string ProgramSystemPath => Path.Combine(root, "system");
        public string DataPath => Path.Combine(root, "data");
        public string ImageCachePath => Path.Combine(root, "images");
        public string PluginsPath => Path.Combine(root, "plugins");
        public string PluginConfigurationsPath => Path.Combine(root, "plugin-configurations");
        public string LogDirectoryPath => Path.Combine(root, "logs");
        public string ConfigurationDirectoryPath => Path.Combine(root, "config");
        public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");
        public string CachePath => Path.Combine(root, "cache");
        public string TempDirectory => Path.Combine(root, "temp");
        public string VirtualDataPath => Path.Combine(root, "virtual-data");
        public string TrickplayPath => Path.Combine(root, "trickplay");
        public string BackupPath => Path.Combine(root, "backups");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive)
        {
        }
    }
}
