# CS2-SimpleAdmin synthetic benchmarks

- Date: 2026-10-07 23:53, .NET 10.0.11, Microsoft Windows 10.0.26200
- CPU: AMD64 Family 25 Model 97 Stepping 2, AuthenticAMD, logical cores: 32
- GC: workstation, concurrent; Release build; tiered JIT; results after warm-up unless stated

## A. Ban check with CheckMultiAccountsByIp (connect / 61 s pass)

Per check of a non-banned player (worst case for the old full scans). µs, allocations per check.

| IP rows | accounts | old p50 | old p99 | old max | old B/op | new p50 | new p99 | new max | new B/op |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 000 | 4 000 | 49,4 | 787,0 | 4098,1 | 31 268 | 0,30 | 0,70 | 87,0 | 112 |
| 100 000 | 40 000 | 604,5 | 762,1 | 775,9 | 272 | 0,30 | 0,80 | 4,2 | 112 |
| 1 000 000 | 400 000 | 11216,2 | 11412,1 | 11412,1 | 272 | 0,10 | 2,00 | 5,5 | 112 |

Incremental updates on the 1 000 000-row snapshot (new code only; the old code mutated shared sets in place):
WithIpHistory 300 rows p50 1,04 ms (787 KB), 2000 rows p50 3,98 ms (1 329 KB); WithBans 10 changes p50 0,51 ms (696 KB, active bans 8 000).


## B. Full cache build (startup / css_reloadbans), background thread

| IP rows | old ms | old alloc MB | old retained MB | new ms | new alloc MB | new retained MB |
|---:|---:|---:|---:|---:|---:|---:|
| 10 000 | 19 | 2 | 1 | 26 | 1 | 1 |
| 100 000 | 19 | 18 | 13 | 40 | 16 | 9 |
| 1 000 000 | 326 | 173 | 124 | 422 | 153 | 85 |

New retained memory includes the IP→accounts reverse index the old code did not have.

## I. 61 s pass: ban check of 64 online players, and GetAccountsByIp

| IP rows | old pass ms | new pass ms | old GetAccountsByIp µs | new GetAccountsByIp µs |
|---:|---:|---:|---:|---:|
| 10 000 | 3,2 | 0,019 | 55 | 0,10 |
| 100 000 | 39,8 | 0,006 | 339 | 0,00 |
| 1 000 000 | 722,1 | 0,006 | 9150 | 0,00 |

## D. Chat gag/silence check (every chat message, game thread)

| case | old p50 ns | old B/op | new p50 ns | new B/op |
|---|---:|---:|---:|---:|
| gagged player | 28 | 72 | 93 | 0 |
| no penalty (fast path) | 5 | 0 | 4 | 0 |

Measured in batches of 1000 calls. The new 'gagged' path includes the timezone conversion of Time.ActualDateTime; the old one used the same clock but also copied the list (ToList) on every message.

## F. css_history: work done in ONE game-thread callback

| player history rows | old: format+print all rows in one callback µs | new: largest callback (20 lines) µs | new rows per page |
|---:|---:|---:|---:|
| 10 | 3 | 0,1 (formatting moved to the DB worker) | 10 |
| 1 000 | 322 | 0,2 (formatting moved to the DB worker) | 50 |
| 10 000 | 4724 | 0,2 (formatting moved to the DB worker) | 50 |

Old: the whole history (and its Dapper dynamic rows) was formatted and printed inside one NextWorldUpdate. New: SQL returns one page (50), formatting happens on the worker, the game thread prints ≤20 lines per dispatcher item.

## E. Game-thread dispatcher: cost per world update

- Idle (no queued work): no pump is scheduled at all; a pump call on an empty queue costs p50 73 ns, 0 B.
- Synthetic item: SpinWait(200) ≈ 4,9 µs.
- 10,000 queued items: drained over 157 world updates, per-update p50 316 µs, max 505 µs (budget 500 µs / 64 items). Old code: 10,000 NextWorldUpdate callbacks, CSS runs up to MaximumFrameTasksExecutedPerTick (core.json) of them per tick.

## G. Rename enforcement (every 5 s and on round start)

| renames | online players | old µs | old B | new µs | new B |
|---:|---:|---:|---:|---:|---:|
| 1,000 | 0 | 35,8 | 224 000 | 0,10 | 0 |
| 1,000 | 16 | 167,8 | 632 000 | 0,30 | 0 |
| 1,000 | 32 | 302,4 | 1 016 000 | 0,10 | 0 |
| 1,000 | 64 | 536,3 | 1 784 000 | 0,10 | 0 |

## H. Stealth CheckTransmit, managed part (one silent admin)

| tracked players = recipients | old µs/call | old B/call | new µs/call | new B/call |
|---:|---:|---:|---:|---:|
| 16 | 4,70 | 664 | 2,60 | 0 |
| 32 | 2,10 | 1 176 | 1,00 | 0 |
| 64 | 6,40 | 2 200 | 4,60 | 0 |

Not included: the old code also resolved every player's ObserverPawn handle (native) on every call and created a controller wrapper per recipient; the new code resolves pawns only after connect/team/spawn/round events (or once per second) and reads the recipient slot directly.

## J. Cost of the mechanisms added by the review fixes (background unless stated)

IP history index with 1 000 000 rows (367 276 accounts, 532 409 addresses); ExpireOldIpBans = 30 days (about 91% of the generated rows are older).

| operation | thread | p50 | max | alloc/op |
|---|---|---:|---:|---:|
| AccountIds() — start of one prune cycle | DB worker | 1,3 ms | 1,4 ms | 5,6 MB |
| Prune of 2 000 accounts, all with stale links (worst case of the catch-up phase) | DB worker, once per 61 s pass | 1,85 ms | 13,23 ms | 1 051 KB |
| Prune of 2 000 accounts, nothing stale (steady state) | DB worker, once per 61 s pass | 24 µs | 64805 µs | 62 KB |
| Checksum(cutoff) for the SQL comparison | DB worker, every 15th pass | 3,0 ms | 3,1 ms | 0 B |

The game thread only reads the published snapshot; none of the above runs on it. They compete with CS2 for CPU and the GC, so the slice size (`IpPruneAccountsPerRefresh`) and cadence are the knobs if a weak CPU shows interference.

Work queue, 50,000 trivial typed jobs: with captured WorkContext 0,38 µs/job (producer-side allocation 405 B/job), without 1,09 µs/job. The context is one small object per accepted job.

Online-time compare-and-set statement for one batch of 64 mutes: text built in p50 7,6 µs (31,0 KB); one UPDATE per batch, same as the unfixed set-based code.

## K. CounterStrikeSharp AdminManager.LoadAdminData / LoadAdminGroups (run on the game thread by the plugin)

CSS 1.0.369 exposes only file-path overloads: read the file, parse the JSON and apply it in one synchronous call, so the dispatcher budget cannot split it. `AdminManager` needs the engine (its type initializer fails in a plain process), so the real call cannot be timed here. Lower bound below: only reading and parsing an equivalent file (N admins, 3 flags + 1 group each); CSS's apply (permission/group dictionaries per admin) comes on top and MUST be measured in the game.

| admins | file KB | read+parse p50 | max | alloc/op |
|---:|---:|---:|---:|---:|
| 10 | 1 | 0,05 ms | 0,12 ms | 19 KB |
| 100 | 12 | 0,23 ms | 0,95 ms | 120 KB |
| 1 000 | 117 | 2,06 ms | 3,00 ms | 1 058 KB |
| 5 000 | 589 | 11,53 ms | 34,68 ms | 5 211 KB |

