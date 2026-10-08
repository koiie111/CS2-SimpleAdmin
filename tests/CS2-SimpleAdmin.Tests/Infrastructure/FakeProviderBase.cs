using System.Data.Common;
using CS2_SimpleAdmin.Database;

namespace CS2_SimpleAdmin.Tests;

/// <summary>IDatabaseProvider whose members fail unless overridden.</summary>
internal abstract class FakeProviderBase : IDatabaseProvider
{
    public virtual Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<(bool Success, string? Exception)> CheckConnectionAsync() => Task.FromResult((true, (string?)null));
    public virtual Task DatabaseMigrationAsync() => Task.CompletedTask;
    public virtual string GetIpHistoryQuery() => throw new NotSupportedException();
    public virtual string GetUpsertPlayerIpQuery() => throw new NotSupportedException();
    public virtual string GetAdminsQuery() => throw new NotSupportedException();
    public virtual string GetDeleteAdminQuery(bool globalDelete) => throw new NotSupportedException();
    public virtual string GetAddAdminQuery() => throw new NotSupportedException();
    public virtual string GetAddAdminFlagsQuery() => throw new NotSupportedException();
    public virtual string GetUpdateAdminGroupQuery() => throw new NotSupportedException();
    public virtual string GetGroupsQuery() => throw new NotSupportedException();
    public virtual string GetGroupIdByNameQuery() => throw new NotSupportedException();
    public virtual string GetAddGroupQuery() => throw new NotSupportedException();
    public virtual string GetAddGroupFlagsQuery() => throw new NotSupportedException();
    public virtual string GetAddGroupServerQuery() => throw new NotSupportedException();
    public virtual string GetDeleteGroupQuery() => throw new NotSupportedException();
    public virtual string GetDeleteOldAdminsQuery() => throw new NotSupportedException();
    public virtual string GetAddBanQuery() => throw new NotSupportedException();
    public virtual string GetAddBanBySteamIdQuery() => throw new NotSupportedException();
    public virtual string GetAddBanByIpQuery() => throw new NotSupportedException();
    public virtual string GetUnbanRetrieveBansQuery() => throw new NotSupportedException();
    public virtual string GetUnbanAdminIdQuery() => throw new NotSupportedException();
    public virtual string GetInsertUnbanQuery(bool includeReason) => throw new NotSupportedException();
    public virtual string GetUpdateBanStatusQuery() => throw new NotSupportedException();
    public virtual string GetExpireBansQuery() => throw new NotSupportedException();
    public virtual string GetExpireIpBansQuery() => throw new NotSupportedException();
    public virtual string GetExpireOldPlayerIpsQuery() => throw new NotSupportedException();
    public virtual string GetRenamesQuery() => throw new NotSupportedException();
    public virtual string GetUpsertRenameQuery() => throw new NotSupportedException();
    public virtual string GetDeleteRenameQuery() => throw new NotSupportedException();
    public virtual string GetAddMuteQuery(bool includePlayerName) => throw new NotSupportedException();
    public virtual string GetIsMutedQuery(int timeMode) => throw new NotSupportedException();
    public virtual string GetMuteStatsQuery() => throw new NotSupportedException();
    public virtual string GetRetrieveMutesQuery() => throw new NotSupportedException();
    public virtual string GetUnmuteAdminIdQuery() => throw new NotSupportedException();
    public virtual string GetInsertUnmuteQuery(bool includeReason) => throw new NotSupportedException();
    public virtual string GetUpdateMuteStatusQuery() => throw new NotSupportedException();
    public virtual string GetExpireMutesQuery(int timeMode) => throw new NotSupportedException();
    public virtual string GetAddWarnQuery(bool includePlayerName) => throw new NotSupportedException();
    public virtual string GetPlayerWarnsQuery(bool active) => throw new NotSupportedException();
    public virtual string GetPlayerWarnsCountQuery(bool active) => throw new NotSupportedException();
    public virtual string GetUnwarnByIdQuery() => throw new NotSupportedException();
    public virtual string GetUnwarnLastQuery() => throw new NotSupportedException();
    public virtual string GetExpireWarnsQuery() => throw new NotSupportedException();
    public virtual string GetPenaltyHistoryQuery() => throw new NotSupportedException();
    public virtual string GetPlayerPenaltyStatsQuery() => throw new NotSupportedException();
    public virtual string GetOnlineCreditPlanQuery() => throw new NotSupportedException();
    public virtual string GetApplyOnlineCreditQuery(IReadOnlyList<Managers.OnlineCreditStep> steps) => throw new NotSupportedException();
    public virtual string GetWarnsMenuPageQuery() => throw new NotSupportedException();
    public virtual string GetWarnsMenuCountQuery() => throw new NotSupportedException();
    public virtual string GetExpiredOnlineMutesBatchQuery() => throw new NotSupportedException();
    public virtual string GetPenaltyHistoryPageQuery(string? type) => throw new NotSupportedException();
    public virtual string GetPenaltyHistoryCountQuery(string? type) => throw new NotSupportedException();
    public virtual string GetActiveMutesBatchQuery(int timeMode) => throw new NotSupportedException();
    public virtual string GetActiveSteamBansQuery() => throw new NotSupportedException();
    public virtual string GetActiveSteamBansBatchQuery() => throw new NotSupportedException();
}
