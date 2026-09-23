using Jellyfin.Plugin.LocalRating.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.LocalRating.Compatibility;

public sealed record ScopeSettings(bool Enabled, IReadOnlyDictionary<Guid, Guid[]> Rules, bool AllowImplicitCollapse = false);

public interface IRatingScope
{
    ScopeSettings Snapshot { get; }
    bool BelongsTo(BaseItem item, Guid library);
    float? Rating(Guid user, BaseItem item);
}

public sealed class RatingScope(ILibraryManager libraryManager, IUserManager users, IUserDataManager userData, IServerConfigurationManager serverConfiguration) : IRatingScope
{
    public ScopeSettings Snapshot
    {
        get
        {
            var config = Plugin.Instance?.Configuration;
            var rules = (config?.CompatibilityRules ?? [])
                .Where(r => r is not null && r.UserId != Guid.Empty)
                .GroupBy(r => r.UserId)
                .ToDictionary(g => g.Key, g => g.SelectMany(r => r.LibraryIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray());
            return new(config?.EnableRatingCompatibility == true, rules, !serverConfiguration.Configuration.EnableGroupingMoviesIntoCollections);
        }
    }

    public bool BelongsTo(BaseItem item, Guid library) => libraryManager.GetCollectionFolders(item).Any(folder => folder.Id == library);

    public float? Rating(Guid user, BaseItem item)
    {
        var owner = users.GetUserById(user);
        if (owner is null || !item.IsVisible(owner, false)) return null;
        var value = userData.GetUserData(owner, item)?.Rating;
        return value is >= 1 and <= 10 && value == Math.Truncate(value.Value) ? (float)value.Value : null;
    }
}
