using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Modules.Commands;
using ApiImpl = CS2_SimpleAdmin.Api.CS2_SimpleAdminApi;
using MenuMgr = CS2_SimpleAdmin.Menus.MenuManager;
using Microsoft.Extensions.Localization;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// UnRegisterCommand / UnregisterMenu must not leave the module's callbacks and localizers in the static registries
/// (they would keep an unloaded module alive and grow on every re-registration).
/// </summary>
public class RegistryRetentionTests
{
    private sealed class Owner
    {
        public int Calls;
        public void Handler(CounterStrikeSharp.API.Core.CCSPlayerController? caller, CommandInfo info) => Calls++;
    }

    private sealed class FakeLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private static string Unique() => "css_retention_" + Guid.NewGuid().ToString("N");

    /// <summary>Redirects the in-game RemoveCommand call for the duration of a test and records what was removed.</summary>
    private sealed class RemoverScope : IDisposable
    {
        private readonly Action<string, CommandInfo.CommandCallback> _previous = ApiImpl.CommandRemover;
        public readonly List<string> Removed = [];

        public RemoverScope() => ApiImpl.CommandRemover = (name, _) => Removed.Add(name);

        public void Dispose() => ApiImpl.CommandRemover = _previous;
    }

    // ---- commands ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterThenUnregisterOwned(ApiImpl api, string name)
    {
        var owner = new Owner();
        api.RegisterCommand(name, "d", owner.Handler);
        api.RegisterCommand(name, "d2", owner.Handler); // several handlers under one name
        api.UnRegisterCommand(name);
        return new WeakReference(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsCollected(WeakReference weak)
    {
        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
        }
        return !weak.IsAlive;
    }

    [Fact]
    public void RepeatedRegisterUnregisterCyclesDoNotGrowTheRegistry()
    {
        using var remover = new RemoverScope();
        var api = new ApiImpl();
        var name = Unique();
        var before = CustomCommandRegistry.Definitions.Count;
        var owner = new Owner();

        for (var i = 0; i < 50; i++)
        {
            api.RegisterCommand(name, null, owner.Handler);
            api.RegisterCommand(name, null, owner.Handler);
            api.UnRegisterCommand(name);
        }

        Assert.Equal(before, CustomCommandRegistry.Definitions.Count);
        Assert.False(CustomCommandRegistry.Definitions.ContainsKey(name));
        Assert.Equal(100, remover.Removed.Count); // every handler was still removed from the game, 2 per cycle
    }

    [Fact]
    public void UnregisteredCommandOwnerCanBeCollected()
    {
        using var remover = new RemoverScope();
        var weak = RegisterThenUnregisterOwned(new ApiImpl(), Unique());
        Assert.True(IsCollected(weak), "the command registry still holds the owner after UnRegisterCommand");
    }

    [Fact]
    public void UnregisterIsIdempotentTolerantOfUnknownNamesAndKeepsNeighbours()
    {
        using var remover = new RemoverScope();
        var api = new ApiImpl();
        var gone = Unique();
        var kept = Unique();
        var owner = new Owner();
        api.RegisterCommand(gone, null, owner.Handler);
        api.RegisterCommand(kept, null, owner.Handler);
        api.RegisterCommand(kept, null, owner.Handler);

        try
        {
            api.UnRegisterCommand(gone);
            api.UnRegisterCommand(gone);        // twice
            api.UnRegisterCommand(Unique());     // never registered
            api.UnRegisterCommand("");           // invalid names are ignored, not thrown

            Assert.Equal(1, remover.Removed.Count(n => n == gone)); // the second call did not touch the game again
            Assert.False(CustomCommandRegistry.Definitions.ContainsKey(gone));
            Assert.Equal(2, CustomCommandRegistry.Definitions[kept].Count);
        }
        finally { api.UnRegisterCommand(kept); }
    }

    [Fact]
    public void UnregisterIsCaseInsensitiveLikeTheRegistry()
    {
        using var remover = new RemoverScope();
        var api = new ApiImpl();
        var name = Unique();
        api.RegisterCommand(name, null, new Owner().Handler);
        api.UnRegisterCommand(name.ToUpperInvariant());
        Assert.False(CustomCommandRegistry.Definitions.ContainsKey(name));
    }

    // ---- menus ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterLocalizedMenu(MenuMgr manager, string category, string menuId)
    {
        var localizer = new FakeLocalizer();
        manager.RegisterMenu(category, menuId, "key", static _ => null!, "@css/generic", "css_x", localizer);
        return new WeakReference(localizer);
    }

    [Fact]
    public void UnregisterMenuRemovesLocalizerAndEveryOtherEntry()
    {
        var manager = new MenuMgr();
        var category = Unique();
        var weak = RegisterLocalizedMenu(manager, category, "m1");
        manager.RegisterMenu(category, "m2", "key2", static _ => null!, "@css/generic", "css_y", new FakeLocalizer());
        manager.RegisterMenu(category, "plain", "Plain", static _ => null!, "@css/generic", "css_z");
        var cat = manager.GetMenuCategories()[category];

        manager.UnregisterMenu(category, "m1");

        Assert.DoesNotContain("m1", cat.MenuFactories.Keys);
        Assert.DoesNotContain("m1", cat.MenuNames.Keys);
        Assert.DoesNotContain("m1", cat.MenuPermissions.Keys);
        Assert.DoesNotContain("m1", cat.MenuCommandNames.Keys);
        Assert.DoesNotContain("m1", cat.MenuLocalizers.Keys);
        Assert.True(IsCollected(weak), "the category still holds the module localizer after UnregisterMenu");

        // neighbours are untouched
        Assert.Contains("m2", cat.MenuLocalizers.Keys);
        foreach (var id in new[] { "m2", "plain" })
        {
            Assert.Contains(id, cat.MenuFactories.Keys);
            Assert.Contains(id, cat.MenuNames.Keys);
            Assert.Contains(id, cat.MenuPermissions.Keys);
            Assert.Contains(id, cat.MenuCommandNames.Keys);
        }

        // repeated and unknown removals are harmless
        manager.UnregisterMenu(category, "m1");
        manager.UnregisterMenu(category, "never-registered");
        manager.UnregisterMenu(Unique(), "m1");
        Assert.Equal(2, cat.MenuFactories.Count);
        Assert.Single(cat.MenuLocalizers);
    }
}
