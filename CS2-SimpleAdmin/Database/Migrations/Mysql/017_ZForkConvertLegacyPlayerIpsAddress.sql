-- fork: the first 013 (upstream 676a18d, 1.7.7-alpha, May 2025) only added `name`; the
-- INET_ATON conversion was added to the same file later, so databases that applied the early
-- 013 keep a VARCHAR address that 1.9 cannot read (IpHistoryRow.Address is uint). Convert it
-- here when the column is still VARCHAR; a no-op otherwise. Named 017_Z... so it runs right
-- after 017 under any comparer and never shadows a future upstream 018.
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
SELECT '017_Z', 'sa_players_ips', `steamid`, 'address', `address` FROM `sa_players_ips`
WHERE `address` NOT REGEXP '^((25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])[.]){3}(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])$'
  AND (SELECT `DATA_TYPE` FROM information_schema.`COLUMNS` WHERE `TABLE_SCHEMA` = DATABASE() AND `TABLE_NAME` = 'sa_players_ips' AND `COLUMN_NAME` = 'address') LIKE '%char%';
DELETE FROM `sa_players_ips`
WHERE `address` NOT REGEXP '^((25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])[.]){3}(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])$'
  AND (SELECT `DATA_TYPE` FROM information_schema.`COLUMNS` WHERE `TABLE_SCHEMA` = DATABASE() AND `TABLE_NAME` = 'sa_players_ips' AND `COLUMN_NAME` = 'address') LIKE '%char%';
UPDATE `sa_players_ips` SET `address` = INET_ATON(`address`)
WHERE (SELECT `DATA_TYPE` FROM information_schema.`COLUMNS` WHERE `TABLE_SCHEMA` = DATABASE() AND `TABLE_NAME` = 'sa_players_ips' AND `COLUMN_NAME` = 'address') LIKE '%char%';
ALTER TABLE `sa_players_ips` CHANGE `address` `address` INT UNSIGNED NOT NULL;
