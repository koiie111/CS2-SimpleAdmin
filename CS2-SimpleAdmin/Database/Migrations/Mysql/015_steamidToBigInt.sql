-- fork: rows the BIGINT conversion cannot take (STEAM_X:Y:Z, empty strings, typos edited in
-- through the web panel) made the ALTERs below fail under strict sql_mode. STEAM_X:Y:Z is
-- converted to SteamID64, anything else non-numeric becomes NULL (player) / 0 (admin, as
-- upstream does); every changed value is kept in sa_migration_backup. 'Console' is expected.
CREATE TABLE IF NOT EXISTS `sa_migration_backup` (
                                  `id` int(11) NOT NULL AUTO_INCREMENT,
                                  `migration` varchar(64) NOT NULL,
                                  `table_name` varchar(64) NOT NULL,
                                  `row_id` varchar(64) NULL,
                                  `column_name` varchar(64) NOT NULL,
                                  `old_value` varchar(255) NULL,
                                  `created` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
                                  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

INSERT INTO `sa_migration_backup` (`migration`, `table_name`, `row_id`, `column_name`, `old_value`)
SELECT '015', 'sa_bans', `id`, 'player_steamid', `player_steamid` FROM `sa_bans`
WHERE `player_steamid` IS NOT NULL AND `player_steamid` NOT REGEXP '^[0-9]{1,18}$';
UPDATE `sa_bans` SET `player_steamid` = 76561197960265728 + CAST(SUBSTRING_INDEX(`player_steamid`, ':', -1) AS UNSIGNED) * 2 + CAST(SUBSTRING(`player_steamid`, 9, 1) AS UNSIGNED)
WHERE `player_steamid` REGEXP '^STEAM_[0-5]:[01]:[0-9]{1,10}$';
UPDATE `sa_bans` SET `player_steamid` = NULL WHERE `player_steamid` NOT REGEXP '^[0-9]{1,18}$';
INSERT INTO `sa_migration_backup` (`migration`, `table_name`, `row_id`, `column_name`, `old_value`)
SELECT '015', 'sa_bans', `id`, 'admin_steamid', `admin_steamid` FROM `sa_bans`
WHERE `admin_steamid` NOT REGEXP '^[0-9]{1,18}$' AND CAST(`admin_steamid` AS CHAR) <> 'Console';
UPDATE `sa_bans` SET `admin_steamid` = 76561197960265728 + CAST(SUBSTRING_INDEX(`admin_steamid`, ':', -1) AS UNSIGNED) * 2 + CAST(SUBSTRING(`admin_steamid`, 9, 1) AS UNSIGNED)
WHERE `admin_steamid` REGEXP '^STEAM_[0-5]:[01]:[0-9]{1,10}$';

INSERT INTO `sa_migration_backup` (`migration`, `table_name`, `row_id`, `column_name`, `old_value`)
SELECT '015', 'sa_mutes', `id`, 'player_steamid', `player_steamid` FROM `sa_mutes`
WHERE `player_steamid` IS NOT NULL AND `player_steamid` NOT REGEXP '^[0-9]{1,18}$';
UPDATE `sa_mutes` SET `player_steamid` = 76561197960265728 + CAST(SUBSTRING_INDEX(`player_steamid`, ':', -1) AS UNSIGNED) * 2 + CAST(SUBSTRING(`player_steamid`, 9, 1) AS UNSIGNED)
WHERE `player_steamid` REGEXP '^STEAM_[0-5]:[01]:[0-9]{1,10}$';
UPDATE `sa_mutes` SET `player_steamid` = NULL WHERE `player_steamid` NOT REGEXP '^[0-9]{1,18}$';
INSERT INTO `sa_migration_backup` (`migration`, `table_name`, `row_id`, `column_name`, `old_value`)
SELECT '015', 'sa_mutes', `id`, 'admin_steamid', `admin_steamid` FROM `sa_mutes`
WHERE `admin_steamid` NOT REGEXP '^[0-9]{1,18}$' AND CAST(`admin_steamid` AS CHAR) <> 'Console';
UPDATE `sa_mutes` SET `admin_steamid` = 76561197960265728 + CAST(SUBSTRING_INDEX(`admin_steamid`, ':', -1) AS UNSIGNED) * 2 + CAST(SUBSTRING(`admin_steamid`, 9, 1) AS UNSIGNED)
WHERE `admin_steamid` REGEXP '^STEAM_[0-5]:[01]:[0-9]{1,10}$';

INSERT INTO `sa_migration_backup` (`migration`, `table_name`, `row_id`, `column_name`, `old_value`)
SELECT '015', 'sa_warns', `id`, 'player_steamid', `player_steamid` FROM `sa_warns`
WHERE `player_steamid` IS NOT NULL AND `player_steamid` NOT REGEXP '^[0-9]{1,18}$';
UPDATE `sa_warns` SET `player_steamid` = 76561197960265728 + CAST(SUBSTRING_INDEX(`player_steamid`, ':', -1) AS UNSIGNED) * 2 + CAST(SUBSTRING(`player_steamid`, 9, 1) AS UNSIGNED)
WHERE `player_steamid` REGEXP '^STEAM_[0-5]:[01]:[0-9]{1,10}$';
UPDATE `sa_warns` SET `player_steamid` = NULL WHERE `player_steamid` NOT REGEXP '^[0-9]{1,18}$';
INSERT INTO `sa_migration_backup` (`migration`, `table_name`, `row_id`, `column_name`, `old_value`)
SELECT '015', 'sa_warns', `id`, 'admin_steamid', `admin_steamid` FROM `sa_warns`
WHERE `admin_steamid` NOT REGEXP '^[0-9]{1,18}$' AND CAST(`admin_steamid` AS CHAR) <> 'Console';
UPDATE `sa_warns` SET `admin_steamid` = 76561197960265728 + CAST(SUBSTRING_INDEX(`admin_steamid`, ':', -1) AS UNSIGNED) * 2 + CAST(SUBSTRING(`admin_steamid`, 9, 1) AS UNSIGNED)
WHERE `admin_steamid` REGEXP '^STEAM_[0-5]:[01]:[0-9]{1,10}$';

INSERT INTO `sa_migration_backup` (`migration`, `table_name`, `row_id`, `column_name`, `old_value`)
SELECT '015', 'sa_admins', `id`, 'player_steamid', `player_steamid` FROM `sa_admins`
WHERE `player_steamid` NOT REGEXP '^[0-9]{1,18}$' AND CAST(`player_steamid` AS CHAR) <> 'Console';
UPDATE `sa_admins` SET `player_steamid` = 76561197960265728 + CAST(SUBSTRING_INDEX(`player_steamid`, ':', -1) AS UNSIGNED) * 2 + CAST(SUBSTRING(`player_steamid`, 9, 1) AS UNSIGNED)
WHERE `player_steamid` REGEXP '^STEAM_[0-5]:[01]:[0-9]{1,10}$';

ALTER TABLE `sa_bans` CHANGE `player_steamid` `player_steamid` BIGINT NULL DEFAULT NULL;
UPDATE `sa_bans`
SET admin_steamid = '0'
WHERE admin_steamid NOT REGEXP '^[0-9]{1,18}$';
ALTER TABLE `sa_bans` CHANGE `admin_steamid` `admin_steamid` BIGINT NOT NULL;

ALTER TABLE `sa_mutes` CHANGE `player_steamid` `player_steamid` BIGINT NULL DEFAULT NULL;
UPDATE `sa_mutes`
SET admin_steamid = '0'
WHERE admin_steamid NOT REGEXP '^[0-9]{1,18}$';
ALTER TABLE `sa_mutes` CHANGE `admin_steamid` `admin_steamid` BIGINT NOT NULL;

ALTER TABLE `sa_warns` CHANGE `player_steamid` `player_steamid` BIGINT NULL DEFAULT NULL;
UPDATE `sa_warns`
SET admin_steamid = '0'
WHERE admin_steamid NOT REGEXP '^[0-9]{1,18}$';
ALTER TABLE `sa_warns` CHANGE `admin_steamid` `admin_steamid` BIGINT NOT NULL;

UPDATE `sa_admins`
SET player_steamid = '0'
WHERE player_steamid NOT REGEXP '^[0-9]{1,18}$';
ALTER TABLE `sa_admins` CHANGE `player_steamid` `player_steamid` BIGINT NULL DEFAULT NULL;
