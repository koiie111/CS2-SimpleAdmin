using System.Data.Common;
using CS2_SimpleAdmin.Database;

namespace CS2_SimpleAdmin.Tests;

/// <summary>IDatabaseProvider whose members fail unless overridden.</summary>
internal abstract class FakeProviderBase : IDatabaseProvider
{
    public virtual Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<(bool Success, string? Exception)> CheckConnectionAsync() => Task.FromResult((true, (string?)null));
    public virtual Task DatabaseMigrationAsync() => Task.CompletedTask;
    public virtual string GetBanSelectQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetIpHistoryQuery() => throw new NotSupportedException();
    public virtual string GetBanUpdateQuery(bool multiServer) => throw new NotSupportedException();
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
    public virtual string GetUnbanRetrieveBansQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetUnbanAdminIdQuery() => throw new NotSupportedException();
    public virtual string GetInsertUnbanQuery(bool includeReason) => throw new NotSupportedException();
    public virtual string GetUpdateBanStatusQuery() => throw new NotSupportedException();
    public virtual string GetExpireBansQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetExpireIpBansQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetExpireOldPlayerIpsQuery() => throw new NotSupportedException();
    public virtual string GetRenamesQuery() => throw new NotSupportedException();
    public virtual string GetUpsertRenameQuery() => throw new NotSupportedException();
    public virtual string GetDeleteRenameQuery() => throw new NotSupportedException();
    public virtual string GetAddMuteQuery(bool includePlayerName) => throw new NotSupportedException();
    public virtual string GetIsMutedQuery(bool multiServer, int timeMode) => throw new NotSupportedException();
    public virtual string GetMuteStatsQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetUpdateMutePassedQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetCheckExpiredMutesQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetRetrieveMutesQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetUnmuteAdminIdQuery() => throw new NotSupportedException();
    public virtual string GetInsertUnmuteQuery(bool includeReason) => throw new NotSupportedException();
    public virtual string GetUpdateMuteStatusQuery() => throw new NotSupportedException();
    public virtual string GetExpireMutesQuery(bool multiServer, int timeMode) => throw new NotSupportedException();
    public virtual string GetAddWarnQuery(bool includePlayerName) => throw new NotSupportedException();
    public virtual string GetPlayerWarnsQuery(bool multiServer, bool active) => throw new NotSupportedException();
    public virtual string GetPlayerWarnsCountQuery(bool multiServer, bool active) => throw new NotSupportedException();
    public virtual string GetUnwarnByIdQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetUnwarnLastQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetExpireWarnsQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetPenaltyHistoryQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetPlayerPenaltyStatsQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetOnlineCreditPlanQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetApplyOnlineCreditQuery(IReadOnlyList<Managers.OnlineCreditStep> steps) => throw new NotSupportedException();
    public virtual string GetWarnsMenuPageQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetWarnsMenuCountQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetExpiredOnlineMutesBatchQuery(bool multiServer) => throw new NotSupportedException();
    public virtual string GetPenaltyHistoryPageQuery(bool multiServer, string? type) => throw new NotSupportedException();
    public virtual string GetPenaltyHistoryCountQuery(bool multiServer, string? type) => throw new NotSupportedException();
}
