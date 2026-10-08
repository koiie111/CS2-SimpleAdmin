using System.Data.Common;

namespace CS2_SimpleAdmin.Database;

public interface IDatabaseProvider
{
    Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
    Task<(bool Success, string? Exception)> CheckConnectionAsync();
    Task DatabaseMigrationAsync();
    
    // CacheManager
    string GetIpHistoryQuery();

    // PlayerManager
    string GetUpsertPlayerIpQuery();
    
    // PermissionManager
    string GetAdminsQuery();
    string GetDeleteAdminQuery(bool globalDelete);
    string GetAddAdminQuery();
    string GetAddAdminFlagsQuery();
    string GetUpdateAdminGroupQuery();
    string GetGroupsQuery();
    string GetGroupIdByNameQuery();
    string GetAddGroupQuery();
    string GetAddGroupFlagsQuery();
    string GetAddGroupServerQuery();
    string GetDeleteGroupQuery();
    string GetDeleteOldAdminsQuery();
    
    // BanManager
    string GetAddBanQuery();
    string GetAddBanBySteamIdQuery();
    string GetAddBanByIpQuery();
    string GetUnbanRetrieveBansQuery();
    string GetUnbanAdminIdQuery();
    string GetInsertUnbanQuery(bool includeReason);
    string GetUpdateBanStatusQuery();
    string GetExpireBansQuery();
    string GetExpireIpBansQuery();
    string GetExpireOldPlayerIpsQuery();

    // Renames (css_prename)
    string GetRenamesQuery();
    string GetUpsertRenameQuery();
    string GetDeleteRenameQuery();

    // MuteManager
    string GetAddMuteQuery(bool includePlayerName);
    string GetIsMutedQuery(int timeMode);

    /// <summary>Typed active mutes (id, owner, type, ends, duration, created, passed) of <c>player_steamid IN @ids</c>.</summary>
    string GetActiveMutesBatchQuery(int timeMode);

    /// <summary>Authoritative connect-time ban lookup: active, unexpired bans of <c>@PlayerSteamID</c>.</summary>
    string GetActiveSteamBansQuery();

    /// <summary>SteamID64s among <c>@ids</c> that have an active, unexpired ban (periodic check of online players).</summary>
    string GetActiveSteamBansBatchQuery();
    string GetMuteStatsQuery();
    string GetRetrieveMutesQuery();
    string GetUnmuteAdminIdQuery();
    string GetInsertUnmuteQuery(bool includeReason);
    string GetUpdateMuteStatusQuery();
    string GetExpireMutesQuery(int timeMode);
    
    // WarnManager
    string GetAddWarnQuery(bool includePlayerName);
    string GetPlayerWarnsQuery(bool active);
    string GetPlayerWarnsCountQuery(bool active);
    string GetUnwarnByIdQuery();
    string GetUnwarnLastQuery();
    string GetExpireWarnsQuery();

    // Penalty history (css_history)
    string GetPenaltyHistoryQuery();

    // Connect load: all totals in one round trip
    string GetPlayerPenaltyStatsQuery();

    // TimeMode 0 (online time), set-based over the online players
    string GetWarnsMenuPageQuery();
    string GetWarnsMenuCountQuery();
    string GetOnlineCreditPlanQuery();
    string GetApplyOnlineCreditQuery(IReadOnlyList<Managers.OnlineCreditStep> steps);
    string GetExpiredOnlineMutesBatchQuery();

    // Penalty history (css_history): filtered + paged in SQL
    string GetPenaltyHistoryPageQuery(string? type);
    string GetPenaltyHistoryCountQuery(string? type);

}