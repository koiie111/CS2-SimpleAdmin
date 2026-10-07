-- fork: indexes for the queries the plugin runs every 61 s on every server and on every connect.
-- Chosen from EXPLAIN on MySQL 8.0 / 5.7 / MariaDB 10.11 with 200k bans, 200k mutes, 100k warns, 1M IPs
-- (before: full scans of sa_bans/sa_warns; see docs in the fork report):
--   sa_bans(player_steamid)       connect stats, css_history, ban back-fill
--   sa_bans(status, ends)         ban expiry, ExpireOldIpBans, ACTIVE checksum/page of the cache
--   sa_bans(updated_at), (created) incremental cache refresh (index_merge sort_union for the OR)
--   sa_bans(player_ip)            ban back-fill "(player_steamid = ? OR player_ip = ?)" (index_merge union)
--   sa_warns(player_steamid)      connect stats, css_history, css_warns
--   sa_warns(status, ends)        warn expiry
--   sa_mutes(status, ends)        mute expiry (014 indexes all start with player_steamid)
-- sa_bans/sa_warns get few writes (penalties), so the extra index maintenance is negligible.
--
-- Idempotent and safe when two servers migrate at the same time: every index is created only if missing
-- (MySQL 5.7 has no CREATE INDEX IF NOT EXISTS, hence the information_schema check + prepared statement).
-- InnoDB builds secondary indexes online (reads and writes continue).

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_bans' AND INDEX_NAME = 'idx_sa_bans_steamid') = 0,
    'ALTER TABLE `sa_bans` ADD INDEX `idx_sa_bans_steamid` (`player_steamid`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_bans' AND INDEX_NAME = 'idx_sa_bans_status_ends') = 0,
    'ALTER TABLE `sa_bans` ADD INDEX `idx_sa_bans_status_ends` (`status`, `ends`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_bans' AND INDEX_NAME = 'idx_sa_bans_updated_at') = 0,
    'ALTER TABLE `sa_bans` ADD INDEX `idx_sa_bans_updated_at` (`updated_at`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_bans' AND INDEX_NAME = 'idx_sa_bans_created') = 0,
    'ALTER TABLE `sa_bans` ADD INDEX `idx_sa_bans_created` (`created`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_bans' AND INDEX_NAME = 'idx_sa_bans_ip') = 0,
    'ALTER TABLE `sa_bans` ADD INDEX `idx_sa_bans_ip` (`player_ip`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_warns' AND INDEX_NAME = 'idx_sa_warns_steamid') = 0,
    'ALTER TABLE `sa_warns` ADD INDEX `idx_sa_warns_steamid` (`player_steamid`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_warns' AND INDEX_NAME = 'idx_sa_warns_status_ends') = 0,
    'ALTER TABLE `sa_warns` ADD INDEX `idx_sa_warns_status_ends` (`status`, `ends`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;

SET @sa_idx = IF((SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sa_mutes' AND INDEX_NAME = 'idx_sa_mutes_status_ends') = 0,
    'ALTER TABLE `sa_mutes` ADD INDEX `idx_sa_mutes_status_ends` (`status`, `ends`)', 'DO 0');
PREPARE sa_stmt FROM @sa_idx; EXECUTE sa_stmt; DEALLOCATE PREPARE sa_stmt;
