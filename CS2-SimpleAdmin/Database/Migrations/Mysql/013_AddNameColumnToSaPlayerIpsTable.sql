-- fork: under strict sql_mode INET_ATON() on a non-IPv4 value raises an error instead of
-- returning NULL, which aborted this migration. Copy such rows to sa_migration_backup and
-- drop them with a strict IPv4 pattern first; upstream's DELETE below then never sees one.
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
SELECT '013', 'sa_players_ips', `steamid`, 'address', `address` FROM `sa_players_ips`
WHERE `address` NOT REGEXP '^((25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])[.]){3}(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])$';
DELETE FROM `sa_players_ips` WHERE `address` NOT REGEXP '^((25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])[.]){3}(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])$';

DELETE FROM sa_players_ips WHERE INET_ATON(address) IS NULL AND address IS NOT NULL;
UPDATE `sa_players_ips` SET `address` = INET_ATON(address);
ALTER TABLE `sa_players_ips` CHANGE `address` `address` INT UNSIGNED NOT NULL;
ALTER TABLE `sa_players_ips` ADD INDEX (used_at DESC);
ALTER TABLE `sa_players_ips` ADD `name` VARCHAR(64) NULL DEFAULT NULL AFTER `steamid`;
