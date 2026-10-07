# CS2-SimpleAdmin synthetic benchmarks

- Date: 2026-10-07 20:37, .NET 10.0.11, Microsoft Windows 10.0.26200
- CPU: AMD64 Family 25 Model 97 Stepping 2, AuthenticAMD, logical cores: 32
- GC: workstation, concurrent; Release build; tiered JIT; results after warm-up unless stated

## A. Ban check with CheckMultiAccountsByIp (connect / 61 s pass)

Per check of a non-banned player (worst case for the old full scans). µs, allocations per check.

| IP rows | accounts | old p50 | old p99 | old max | old B/op | new p50 | new p99 | new max | new B/op |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 000 | 4 000 | 54,7 | 843,5 | 4915,7 | 19 807 | 0,40 | 0,90 | 83,2 | 112 |
| 100 000 | 40 000 | 897,2 | 1360,3 | 1491,9 | 272 | 0,40 | 1,00 | 107,5 | 112 |
| 1 000 000 | 400 000 | 13727,1 | 15482,6 | 15482,6 | 272 | 0,10 | 2,10 | 12,0 | 112 |

Incremental updates on the 1 000 000-row snapshot (new code only; the old code mutated shared sets in place):
WithIpHistory 300 rows p50 1,07 ms (788 KB), 2000 rows p50 5,55 ms (1 329 KB); WithBans 10 changes p50 0,60 ms (696 KB, active bans 8 000).


## B. Full cache build (startup / css_reloadbans), background thread

| IP rows | old ms | old alloc MB | old retained MB | new ms | new alloc MB | new retained MB |
|---:|---:|---:|---:|---:|---:|---:|
| 10 000 | 23 | 2 | 1 | 34 | 1 | 1 |
| 100 000 | 29 | 18 | 13 | 50 | 16 | 9 |
| 1 000 000 | 378 | 173 | 124 | 500 | 153 | 85 |

New retained memory includes the IP→accounts reverse index the old code did not have.

## I. 61 s pass: ban check of 64 online players, and GetAccountsByIp

| IP rows | old pass ms | new pass ms | old GetAccountsByIp µs | new GetAccountsByIp µs |
|---:|---:|---:|---:|---:|
| 10 000 | 3,5 | 0,020 | 61 | 0,10 |
| 100 000 | 55,3 | 0,006 | 406 | 0,10 |
| 1 000 000 | 887,6 | 0,006 | 11312 | 0,00 |

## D. Chat gag/silence check (every chat message, game thread)

| case | old p50 ns | old B/op | new p50 ns | new B/op |
|---|---:|---:|---:|---:|
| gagged player | 37 | 72 | 91 | 0 |
| no penalty (fast path) | 5 | 0 | 4 | 0 |

Measured in batches of 1000 calls. The new 'gagged' path includes the timezone conversion of Time.ActualDateTime; the old one used the same clock but also copied the list (ToList) on every message.

## F. css_history: work done in ONE game-thread callback

| player history rows | old: format+print all rows in one callback µs | new: largest callback (20 lines) µs | new rows per page |
|---:|---:|---:|---:|
| 10 | 3 | 0,1 (formatting moved to the DB worker) | 10 |
| 1 000 | 358 | 0,2 (formatting moved to the DB worker) | 50 |
| 10 000 | 5038 | 0,2 (formatting moved to the DB worker) | 50 |

Old: the whole history (and its Dapper dynamic rows) was formatted and printed inside one NextWorldUpdate. New: SQL returns one page (50), formatting happens on the worker, the game thread prints ≤20 lines per dispatcher item.

## E. Game-thread dispatcher: cost per world update

- Idle (no queued work): no pump is scheduled at all; a pump call on an empty queue costs p50 78 ns, 0 B.
- Synthetic item: SpinWait(200) ≈ 7,8 µs.
- 10,000 queued items: drained over 157 world updates, per-update p50 502 µs, max 512 µs (budget 500 µs / 64 items). Old code: 10,000 NextWorldUpdate callbacks, CSS runs up to MaximumFrameTasksExecutedPerTick (core.json) of them per tick.

## G. Rename enforcement (every 5 s and on round start)

| renames | online players | old µs | old B | new µs | new B |
|---:|---:|---:|---:|---:|---:|
| 1,000 | 0 | 26,9 | 224 000 | 0,10 | 0 |
| 1,000 | 16 | 180,6 | 632 000 | 0,40 | 0 |
| 1,000 | 32 | 314,9 | 1 016 000 | 0,10 | 0 |
| 1,000 | 64 | 579,3 | 1 784 000 | 0,20 | 0 |

## H. Stealth CheckTransmit, managed part (one silent admin)

| tracked players = recipients | old µs/call | old B/call | new µs/call | new B/call |
|---:|---:|---:|---:|---:|
| 16 | 6,20 | 664 | 2,00 | 0 |
| 32 | 2,20 | 1 176 | 1,10 | 0 |
| 64 | 6,90 | 2 200 | 5,10 | 0 |

Not included: the old code also resolved every player's ObserverPawn handle (native) on every call and created a controller wrapper per recipient; the new code resolves pawns only after connect/team/spawn/round events (or once per second) and reads the recipient slot directly.
