-- Synthetic data for EXPLAIN (MySQL 8 / MariaDB: recursive CTE)
SET SESSION cte_max_recursion_depth = 2000000;
SET @now = NOW();

INSERT INTO sa_servers (id, address, hostname) VALUES (1, '1.1.1.1:27015', 's1'), (2, '1.1.1.1:27016', 's2'), (3, '1.1.1.1:27017', 's3');

INSERT INTO sa_bans (player_steamid, player_name, player_ip, admin_steamid, admin_name, reason, duration, ends, created, server_id, status)
WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 200000)
SELECT 76561198000000000 + (n * 7919) % 150000, CONCAT('player', n), IF(n % 3 = 0, CONCAT('10.', (n DIV 65536) % 256, '.', (n DIV 256) % 256, '.', n % 256), NULL),
       0, 'Console', 'cheat', IF(n % 4 = 0, 0, 1440),
       @now - INTERVAL (n % 900) DAY + INTERVAL IF(n % 4 = 0, 0, 1440) MINUTE,
       @now - INTERVAL (n % 900) DAY, 1 + n % 3,
       IF(n % 20 = 0, 'ACTIVE', IF(n % 7 = 0, 'UNBANNED', 'EXPIRED'))
FROM seq;

INSERT INTO sa_mutes (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, type, server_id, status, passed)
WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 200000)
SELECT 76561198000000000 + (n * 104729) % 150000, CONCAT('player', n), 0, 'Console', 'spam', IF(n % 5 = 0, 0, 60),
       @now - INTERVAL (n % 900) DAY + INTERVAL 60 MINUTE, @now - INTERVAL (n % 900) DAY,
       ELT(1 + n % 3, 'GAG', 'MUTE', 'SILENCE'), 1 + n % 3, IF(n % 25 = 0, 'ACTIVE', 'EXPIRED'), 0
FROM seq;

INSERT INTO sa_warns (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, server_id, status)
WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 100000)
SELECT 76561198000000000 + (n * 15485863) % 150000, CONCAT('player', n), 0, 'Console', 'warn', 60,
       @now - INTERVAL (n % 900) DAY + INTERVAL 60 MINUTE, @now - INTERVAL (n % 900) DAY, 1 + n % 3,
       IF(n % 30 = 0, 'ACTIVE', 'EXPIRED')
FROM seq;

INSERT IGNORE INTO sa_players_ips (steamid, name, address, used_at)
WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 1000000)
SELECT 76561198000000000 + n % 400000, CONCAT('p', n % 400000), 167772160 + (n * 2654435761) % 700000,
       @now - INTERVAL (n % 400) DAY - INTERVAL (n % 86400) SECOND
FROM seq;

ANALYZE TABLE sa_bans, sa_mutes, sa_warns, sa_players_ips;
