using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRating.Compatibility;
using Jellyfin.Plugin.LocalRating.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LocalRating.Tests;

public sealed class RatingCompatibilityTests
{
    [Fact]
    public void ConfigurationDefaultsOffAndRoundTripsScopes()
    {
        var config = new PluginConfiguration();
        Assert.False(config.EnableRatingCompatibility);
        Assert.Empty(config.CompatibilityRules);
        config.CompatibilityRules = [new() { UserId = Guid.NewGuid(), LibraryIds = [Guid.NewGuid()] }];
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var restored = (PluginConfiguration)serializer.Deserialize(reader)!;
        Assert.Equal(config.CompatibilityRules[0].LibraryIds, restored.CompatibilityRules[0].LibraryIds);
        Assert.Equal(config.CompatibilityRules[0].UserId, restored.CompatibilityRules[0].UserId);
        Assert.False(restored.EnableRatingCompatibility);
    }

    [Fact]
    public void RegistrationWithoutNativeDtoDoesNotInstallFilter()
    {
        var services = new ServiceCollection();
        Assert.False(CompatibilityRegistration.Register(services));
        Assert.Empty(services);
    }

    [Fact]
    public void RegistrationPreservesNativeServiceWhenNoCompatibilityRequest()
    {
        var services = new ServiceCollection();
        var native = Substitute.For<IDtoService>();
        services.AddSingleton(native);
        Assert.True(CompatibilityRegistration.Register(services));
        using var provider = services.BuildServiceProvider();
        var wrapper = provider.GetRequiredService<IDtoService>();
        var item = new Movie();
        wrapper.GetPrimaryImageAspectRatio(item);
        native.Received(1).GetPrimaryImageAspectRatio(item);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongIdentityOrLibraryFailsBeforeDtoGeneration(bool wrongUser)
    {
        var (wrapper, native, state, context, user, scope) = Fixture();
        var item = new Movie { Id = Guid.NewGuid() };
        scope.BelongsTo(item, state.LibraryId).Returns(wrongUser);
        if (wrongUser) user.Id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => wrapper.GetBaseItemDtos([item], new DtoOptions(), user));
        Assert.Empty(native.ReceivedCalls());
    }

    [Fact]
    public void CancellationStopsBeforeDtoGeneration()
    {
        var (wrapper, native, state, context, user, scope) = Fixture();
        var item = new Movie { Id = Guid.NewGuid() };
        scope.BelongsTo(item, state.LibraryId).Returns(true);
        context.RequestAborted = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => wrapper.GetBaseItemDtos([item], new DtoOptions(), user));
        Assert.Empty(native.ReceivedCalls());
    }

    [Fact]
    public void OnlyFinalPageUsesOriginalDtoOptionsAndSharedEntitiesStayUnchanged()
    {
        var (wrapper, native, state, context, user, scope) = Fixture();
        var items = Enumerable.Range(1, 5).Select(n => new Movie { Id = Guid.NewGuid(), Name = n.ToString(), CommunityRating = 1 }).ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            scope.BelongsTo(items[i], state.LibraryId).Returns(true);
            scope.Rating(user.Id, items[i]).Returns((float)(i + 1));
        }
        IReadOnlyList<BaseItem>? converted = null;
        var options = new DtoOptions { EnableImages = true, EnableUserData = true };
        native.GetBaseItemDtos(Arg.Any<IReadOnlyList<BaseItem>>(), options, user, null, true).Returns(call =>
        {
            converted = call.Arg<IReadOnlyList<BaseItem>>();
            return converted.Select(item => new BaseItemDto { Id = item.Id, CommunityRating = item.CommunityRating }).ToArray();
        });
        var result = wrapper.GetBaseItemDtos(items, options, user, skipVisibilityCheck: true);
        Assert.Equal(new float?[] { 4, 3 }, result.Select(dto => dto.CommunityRating));
        Assert.Equal(2, converted!.Count);
        Assert.Equal(5, state.Total);
        Assert.All(items, item => Assert.Equal(1f, item.CommunityRating));
    }

    private static (CompatibilityDtoService, IDtoService, CompatibilityRequest, DefaultHttpContext, User, IRatingScope) Fixture()
    {
        var context = new DefaultHttpContext();
        var scope = Substitute.For<IRatingScope>();
        var user = new User("fixture", "test", "test") { Id = Guid.NewGuid() };
        var state = new CompatibilityRequest { Scope = scope, UserId = user.Id, LibraryId = Guid.NewGuid(), Start = 1, Limit = 2, PersonalSort = true, Descending = true };
        context.Items[CompatibilityRequest.Key] = state;
        var native = Substitute.For<IDtoService>();
        return (new CompatibilityDtoService(native, new HttpContextAccessor { HttpContext = context }), native, state, context, user, scope);
    }
}
