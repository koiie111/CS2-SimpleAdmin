-- fork: same indexes as the MySQL migration of the same name (see there for the reasoning)
CREATE INDEX IF NOT EXISTS `idx_sa_bans_steamid` ON `sa_bans` (`player_steamid`);
CREATE INDEX IF NOT EXISTS `idx_sa_bans_status_ends` ON `sa_bans` (`status`, `ends`);
CREATE INDEX IF NOT EXISTS `idx_sa_bans_updated_at` ON `sa_bans` (`updated_at`);
CREATE INDEX IF NOT EXISTS `idx_sa_bans_created` ON `sa_bans` (`created`);
CREATE INDEX IF NOT EXISTS `idx_sa_bans_ip` ON `sa_bans` (`player_ip`);
CREATE INDEX IF NOT EXISTS `idx_sa_warns_steamid` ON `sa_warns` (`player_steamid`);
CREATE INDEX IF NOT EXISTS `idx_sa_warns_status_ends` ON `sa_warns` (`status`, `ends`);
CREATE INDEX IF NOT EXISTS `idx_sa_mutes_status_ends` ON `sa_mutes` (`status`, `ends`);
