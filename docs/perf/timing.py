import subprocess, sys, time, re
port = sys.argv[1]
db = sys.argv[2] if len(sys.argv) > 2 else 'sa_explain'
exe = r'E:\OSPanel\modules\MySQL-8.0\bin\mysql.exe'
NOIDX_B = 'IGNORE INDEX (idx_sa_bans_steamid, idx_sa_bans_status_ends, idx_sa_bans_updated_at, idx_sa_bans_created, idx_sa_bans_ip)'
NOIDX_W = 'IGNORE INDEX (idx_sa_warns_steamid, idx_sa_warns_status_ends)'
NOIDX_M = 'IGNORE INDEX (idx_sa_mutes_status_ends)'
queries = {
 'refresh delta (bans changed in 90s)': "SELECT id, player_steamid, player_ip, status, created, ends, duration FROM sa_bans {B} WHERE (updated_at >= '__T__' OR created >= '__T__') AND id > 0 ORDER BY id LIMIT 1000",
 'active checksum': "SELECT COUNT(*), COALESCE(SUM(id),0) FROM sa_bans {B} WHERE status = 'ACTIVE'",
 'expire bans (as SELECT)': "SELECT COUNT(*) FROM sa_bans {B} WHERE status = 'ACTIVE' AND duration > 0 AND ends <= NOW()",
 'expire mutes (as SELECT)': "SELECT COUNT(*) FROM sa_mutes {M} WHERE status = 'ACTIVE' AND duration > 0 AND ends <= NOW()",
 'expire warns (as SELECT)': "SELECT COUNT(*) FROM sa_warns {W} WHERE status = 'ACTIVE' AND duration > 0 AND ends <= NOW()",
 'player stats (connect)': "SELECT (SELECT COUNT(*) FROM sa_bans {B} WHERE player_steamid = 76561198000012345), (SELECT COUNT(*) FROM sa_mutes WHERE player_steamid = 76561198000012345 AND type='GAG'), (SELECT COUNT(*) FROM sa_warns {W} WHERE player_steamid = 76561198000012345)",
}
def run(sql, reps=15):
    script = 'SET profiling=1; SET profiling_history_size=100;\n' + (sql + ';\n') * reps + 'SHOW PROFILES;'
    out = subprocess.run([exe, '-h127.0.0.1', f'-P{port}', '-uroot', db, '-N', '-e', script], capture_output=True, text=True).stdout
    durs = sorted(float(l.split('\t')[1]) * 1000 for l in out.splitlines() if re.match(r'^\d+\t[0-9.]+\t', l))
    durs = durs[:-1] if len(durs) > 3 else durs
    return durs[len(durs)//2], durs[-1]
print(f'{"query":<38} {"without idx p50/max ms":>24} {"with idx p50/max ms":>22}')
T = subprocess.run([exe, '-h127.0.0.1', f'-P{port}', '-uroot', '-N', '-e', "SELECT DATE_FORMAT(NOW() - INTERVAL 90 SECOND, '%Y-%m-%d %H:%i:%s')"], capture_output=True, text=True).stdout.strip()
for name, q in queries.items():
    q = q.replace('__T__', T)
    a = run(q.format(B=NOIDX_B, W=NOIDX_W, M=NOIDX_M))
    b = run(q.format(B='', W='', M=''))
    print(f'{name:<38} {a[0]:>12.2f} / {a[1]:>8.2f} {b[0]:>12.2f} / {b[1]:>8.2f}')
