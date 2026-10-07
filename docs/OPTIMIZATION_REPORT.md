# CS2-SimpleAdmin: оптимизация игрового потока — отчёт

Ветка `perf/game-thread-safety` поверх `f544af7` (HEAD совпадал с коммитом аудита). Ничего не запушено и не опубликовано:
пуш в `main` запускает публикацию релиза. Production-БД не трогалась; все SQL-проверки шли на временных экземплярах
MySQL/MariaDB с отдельными datadir.

**Главное ограничение.** Живого CS2-сервера, реальной БД и целевого «слабого» CPU не было. Ниже есть сборки,
юнит/интеграционные тесты, EXPLAIN и синтетические замеры алгоритмов плагина. Это **не** frame time CS2: гарантий
«не лагает» отчёт не даёт. Для замера на сервере добавлена команда `css_sa_perf` (раздел 9).

## 0. Ревизия после ревью `77d5e1b` (R1–R9)

Ревью проверенного коммита `77d5e1b` нашло девять дефектов, часть которых опровергает «Исправлено» в разделе 3.
Исправления лежат в ветке локально (не запушены, релиз не публиковался, production-БД не использовалась).
Коммиты: код — `cbe6153`; тесты — `a64a1a4`, `93bd2aa`; бенчмарк — `93bd2aa`, `d98f7c1`; CI — `75eaa5f`.
Код R1–R9 лежит одним коммитом `cbe6153`: правки делят `Runtime`, `Initialization`, `PlayerSessions`, и промежуточные
коммиты не собирались бы по отдельности. Разбивка по файлам указана в таблице.

Исходные шесть probes из `Claude-review-reproductions.zip` воспроизведены на `77d5e1b` (все 6 красные по assertion,
плюс пустой MySQLOnly theory), затем перенесены в постоянные тесты под теми же именами. Их assertions не ослаблялись;
менялись только точки подключения (см. «Тестовые швы»).

| # | Корневая причина | Исправление (основные файлы) | Тесты | Остаточное ограничение |
|---|---|---|---|---|
| R1 | `LoadQueued=true` ставился при постановке и снимался только при отказе очереди; сбой принятой работы оставлял сессию навсегда «в очереди» | Явные состояния `Pending/InFlight/Loaded/RetryWait` в `PlayerSession`, backoff 2→5→15→30→60 с (+до 25 % по SteamID), переходы только на игровом потоке для текущей сессии и номера попытки; очередь заполнена — попытка не тратится, повтор через 2 с; контроллер недоступен при применении — повтор, а не потеря; применение помечает `Loaded` до побочных эффектов (без дублей) (`PlayerSessions.cs`, `PlayerManager.cs`) | `R1_ConnectLoadRetryTests` (сбой stats, сбой чтения mutes, queue-full, дубли connect/full-connect, disconnect, переиспользованный слот, поздний результат старой попытки, таймер старого поколения) | Повторы не ограничены по числу (чтобы не потерять наказание), но ограничены по частоте (≤ 1 раз в ~60–75 с на игрока). При длительной недоступности БД игрок остаётся без загруженных мутов, пока БД не вернётся; бан-проверка по кэшу работает независимо |
| R2 | `IpHistoryIndex.With` только объединял; удалённые SQL-связи жили в обратном индексе; `FindBannedOtherAccount` не смотрел `UsedAt` владельца | (а) поиск учитывает давность связи **владельца** (`<= cutoff` игнорируется, как будто задание истечения уже отработало) — совпадает с rebuild сразу; (б) ограниченный sweep `Prune` (2000 аккаунтов за проход) удаляет устаревшие связи из обоих направлений; (в) COUNT/SUM-контрольная сумма `sa_players_ips` каждые 15 проходов, при **повторном** расхождении — перестроение индекса из SQL (внешний DELETE) (`IpHistoryIndex.cs`, `BanCacheSnapshot.cs`, `CacheManager.cs`) | `R2_IpHistoryConsistencyTests` (refresh = rebuild после expiry/renewal/удаления/внешнего DELETE, граница cutoff, expiry=0, счётчики, рост без ложных перестроений; все СУБД) | Внешний DELETE замечается через ≈ 16–30 проходов (≈ 15–30 мин); до этого связь живёт (лечится `css_reloadbans`). Перестроение — полный фоновый проход по таблице (~150 МБ аллокаций на 1M строк). Правило «связь другого аккаунта старше N дней не действует» теперь применяется и на пути «текущий IP» (ранее — только для собственных IP) |
| R3 | `ReportWriteFailureAsync` читал `caller.Slot/SteamID` в DB-worker; ещё шесть мест читали `player.SteamID` в worker (unmute-пути, варн) | `CallerRef` (slot+SteamID+userid) снимается на игровом потоке в `TryQueuePenaltyWork` и едет в `WorkContext`; `ReportWriteFailureAsync(string)` не принимает контроллер; доставка — игровой callback, проверка той же связки slot+steam+userid, иначе консоль; native-чтения из worker заменены на заранее снятые значения (`CallerRef.cs`, `Initialization.cs`, `basebans.cs`, `basecomms.cs`) | `R3_CallerIdentityTests` (отложенный сбой + disconnect / переиспользование слота / тот же аккаунт с новым userid / консоль; ни один native-вызов не на потоке worker) и **контрактный тест-сканер исходников** `NoDatabaseJobReadsAControllerOutsideAGameThreadCallback` | Сканер ищет по именам переменных (`caller/player/…`), это эвристика. Модуль Redis имеет собственный насос на игровой поток; живой проверки не было |
| R4 | `EnsureDatabaseReady` принимал `DatabaseReady` при `ServerId=null`; менеджеры читали изменяемый глобальный `ServerId` в момент исполнения | `Runtime.IsDatabaseConnected` ≠ `IsOperationReady` (`Ready` **и** server id); `OperationScope.Server/Global`; `WorkContext.ServerId` снимается при постановке и подменяет `CS2_SimpleAdmin.ServerId` внутри задачи; глобальные записи (`rename-save`, глобальные admin/group, `group-delete`, reload админов) помечены `Global` явно (`Runtime.cs`, `Initialization.cs`, `Variables.cs`) | `R4_OperationReadinessTests` (в т.ч. реальная SQLite-запись: `server_id` = захваченный 7 при смене глобального на 8; в `DatabaseReady` строки нет) | Если сервер не смог определить IP (fallback без server row), серверные наказания **отклоняются** с понятным сообщением (раньше записывались с NULL). Это изменение контракта; глобальные команды работают |
| R5 | UPDATE `passed += N` без транзакции и checkpoint: после частичного успеха повтор начислял те же минуты | Идемпотентный протокол `OnlineCredit`: один раз фиксируется план (pre/post-image каждого мута), запись — **compare-and-set** `UPDATE … SET passed=target WHERE id=… AND passed=expected` одним оператором на батч в транзакции; тот же объект повторяется до `Applied`, затем сворачивается в `CreditedTicks` на игровом потоке; нового окна не считается, пока прежнее не свёрнуто; мут получает минуты только после своего `created`; шаг online идёт до `expire-mutes` и при сбое блокирует его (`OnlineCredit.cs`, `MuteManager.cs`, `PeriodicMaintenance.cs`, `SharedQueries.cs`) | `R5_OnlineCreditTests` (сбой после UPDATE до SELECT, между plan и apply, до commit, **потеря ack после commit**, 3 батча со сбоем посередине, отмена после commit, потерянный apply на игровом потоке, новый мут, внешнее изменение, число UPDATE = ceil(N/64); все СУБД) | Недокредит безопасен и возможен: при внешнем изменении `passed`, при перезапуске плагина (сессия обнуляется, как и раньше) и до 59 с остатка при реконнекте. Двойного начисления нет по построению (CAS) |
| R6 | `TaskCompletionSource` типизированной задачи завершался только из `Work`; отброшенная до старта задача зависала | `Job` хранит `Discard`; `Complete()`, выход worker и гонка «enqueue после остановки» завершают принятые задачи отменой (ровно один раз); отмена на задачу (`CancellationToken`) до dequeue / во время работы / во время SQL без токена; `Unload` по-прежнему не блокирует (`BoundedWorkQueue.cs`) | `R6_QueueCompletionTests` (включая два стресс-теста гонок по 100–150 раундов) | Работа, уже начавшаяся в SQL без токена, завершится сама; ожидающий получает отмену сразу |
| R7 | `Runtime.OnGameThread` брал **текущие** dispatcher/token в момент вызова | `RuntimeContext` (lifetime+dispatcher) и `WorkContext` (+server id) захватываются при **принятии** задачи и передаются через `AsyncLocal`; `OnGameThread` бьёт в свой контекст и повторно проверяет его на игровом потоке; `TrySetState/TrySetServerId/TryMarkDatabaseReady`, startup и admins-reload привязаны к своему контексту; `Restart` сбрасывает server id (`Runtime.cs`, `ServerManager.cs`, `Initialization.cs`) | `R7_LifetimeBindingTests`: **same-assembly restart** (probe, отложенный action, server id, State, очередь) отдельно от **AssemblyLoadContext** (`SeparateAssemblyLoadContextHasIndependentRuntimeStatics` — доказывает только изоляцию статиков) | Живой hot reload CSS (события, таймеры, кэши самого CSS) не проверялся; код вне очередей (таймеры) должен брать `Runtime.Context` явно |
| R8 | `css_warns` читал всю историю и строил все `AddMenuOption` в одном callback | SQL-страница фиксированного размера (8) со стабильным `ORDER BY` (активные, затем `id DESC`), reason режется в SQL до 80 символов, навигация Next/Previous; рядом: `css_adminhelp` читает управляемую копию (≤ 60 строк × 240), фоновое чтение по mtime; `removeid` чанками по 20; счётчик `overBudgetItems` в `css_sa_perf` (`WarnManager.cs`, `basecommands.cs`, `AdminHelpCache.cs`) | `R8_BoundedGameThreadWorkTests` (10 / 10 000 / 100 000 варнов: ≤ 8 строк; 1000 строк по страницам без повторов и пропусков на всех СУБД; admin help; dispatcher над бюджетом) | Бюджет 0,5 мс по-прежнему проверяется **между** элементами. Прочие apply-циклы: `ApplyAdminCache` (O(админов) вызовов CSS) и загрузка renames (O(строк `sa_renames`)) на игровом потоке не разбиты |
| R9 | `catch` в цикле reload делал `TrySetResult()` как при успехе; `MarkReady` шёл дальше | `AdminReloadResult {Success, Failed, Canceled}` (`AdminReloadCoordinator`), coalescing сохранён, busy освобождается на любом исходе (в т.ч. если упал сам лог); startup: до 2 повторов (3 с, 10 с), затем `Failed`, а не `Ready`; ручная `css_reladmin` сообщает итог; файлы `groups.json`/`admins.json` заменяются только после успешного чтения **обеих** таблиц (temp-файлы, затем rename); права CSS при сбое не трогаются (`AdminReloadCoordinator.cs`, `PermissionManager.cs`, `ServerManager.cs`) | `R9_AdminReloadTests` (исходы, coalescing, падение логгера, устаревший контекст, retry/give-up/cancel старта, сбой чтения admins/groups и записи файла оставляют оригиналы) | Применение `LoadAdminData`/`LoadAdminGroups` в CSS остаётся синхронным (см. ниже) |

### Тестовые швы (что и зачем менялось в тестах)
- `QueueLoad` принимает штамп времени и работает с состоянием, а не с флагом; `ApplyLoadResult` получает номер попытки; вызовы native вынесены в швы `ControllerAvailable/NativeEffects/KickBanned`, таймеры — в `RetryScheduler`.
- `CheckOnlineModeMutesAsync` принимает `OnlineCredit` (объекты переживают повтор, как в продакшене) вместо кортежей; сценарий probe (сбой SELECT после UPDATE → повтор) сохранён через `FlakyQueriesProvider`. Для сбоев в других точках — `MuteManager.FaultHook`.
- `ServerId` присваивается через `GlobalServerId`; тесты инициализации создают свежий runtime (`ResetForTests`), потому что состояние теперь привязано к контексту.
- `BanCacheSnapshotTests.ReverseIndexMatchesFullScanReference`: эталон теперь тоже применяет давность связи **другого** аккаунта (это и есть R2). Остальные проверки эталона не менялись.

### Что именно измерено и чем
| Что | Результат | Чем получено |
|---|---|---|
| Unit + SQLite | 141 passed, 2 skipped (MySQL-теории, явный skip) | `dotnet test tests/CS2-SimpleAdmin.Tests -c Release` без СУБД |
| SQLite + MySQL 5.7 / 8.0 + MariaDB 10.11 | **232/232** | `SA_REQUIRE_MYSQL=1 dotnet test …` (временные экземпляры из бинарников OSPanel, отдельные datadir, порты 33057/33080/33110) |
| 6 серверов | **322/322** | то же с `SA_TEST_MYSQL`: MySQL 5.7, 8.0, 8.4; MariaDB 10.4, 10.11, 11.4. Идемпотентность миграций — `MigrationsApplyAndAreIdempotent` на каждом |
| Release-сборки | main, API, FunCommands, Stealth, Redis, bench: 0 ошибок, 0 предупреждений (убран и CS8618 FunCommands) | `dotnet build -c Release` |
| Синтетика | `docs/perf/bench_results_review.md` (раздел J — цена новых механизмов; K — нижняя граница загрузчика админов CSS) | `dotnet run --project bench/CS2-SimpleAdmin.Bench -c Release -- <out.md>` |
| **Frame time CS2 / целевой CPU** | **не измерялось** (нет живого сервера и целевой машины) | — |

Тесты без MySQL больше не падают с «No data found»: theory получает строку-заглушку и **явно пропускается** с текстом
«SQLite-only run does NOT verify MySQL/MariaDB». В CI добавлены два задания: `tests` (SQLite, MySQL-теории пропускаются)
и `tests-mysql` (службы MySQL 5.7/8.0, MariaDB 10.11 и `SA_REQUIRE_MYSQL=1`: недоступный сервер — ошибка).
Задание `publish` не менялось: привязка релиза к тестам — решение владельца. Задание в CI **не запускалось** (нет GitHub Actions).

### Остаточная работа на игровом потоке (честно)
- **`AdminManager.LoadAdminData/LoadAdminGroups`**: в CSS 1.0.369 публичны только перегрузки по пути файла (чтение + разбор + применение одним синхронным вызовом); безопасного ограниченного применения нет, reflection по приватным членам как «незаметную замену контракта» не использовал. Три элемента диспетчера **не** доказывают ограничение 0,5 мс. В процессе без движка `AdminManager` не инициализируется, поэтому реальный вызов не измерен; нижняя граница (чтение + JSON-разбор эквивалентного файла): 100 админов ≈ 0,2 мс, 1000 ≈ 2 мс, 5000 ≈ 12 мс (макс. 35 мс); применение CSS добавляется сверху. Reload редкий (старт, смена карты при `ReloadAdminsEveryMapChange`, команда); при тысячах админов отключайте перезагрузку на каждой карте и измерьте `game.dispatcher_item`/`overBudgetItems` в игре.
- **Параллелизм не повышался**: 4 worker'а MySQL / 1 SQLite как были. Новые фоновые механизмы (prune ≈ 1,9 мс и ~1 МБ на проход при догоне, контрольная сумма ≈ 3 мс на 1M строк, `AccountIds` ≈ 1,3 мс / 5,6 МБ на цикл) конкурируют с CS2 за CPU и GC; ручки — `IpPruneAccountsPerRefresh`, `IpChecksumEveryRefreshes`.
- Добавлены метрики: `overBudgetItems`, `connectLoadRetries`, `discarded` очереди, `ipRebuilds/ipPrunePasses`.
- **Stealth** (сверка с CSS 1.0.369): указательная схема `FastPath` соответствует CSS; добавлены проверки границ (`count`, `infos`, нулевой указатель) и очистка отслеживаемых контроллеров на `OnMapStart` (после смены карты старые wrapper'ы указывали на освобождённые сущности). **В игре не проверено**: disconnect, смена pawn/оружия, переиспользование entity, смена карты, hot reload.

### Пересмотр прежних «Исправлено»
- **F17 lifecycle/Ready**: «Исправлено» было неверно — `DatabaseReady` считался достаточным (R4), startup объявлял Ready при провале admins (R9), задачи старого поколения могли менять состояние нового (R7).
- **F13 online-mode**: «без двойного учёта при retry» было неверно (R5); заменено протоколом CAS, число UPDATE по-прежнему ceil(N/64).
- **F08/F10 IP**: «refresh = rebuild» было неверно после expiry/удаления (R2).
- **F11 ограниченные очереди**: «ограничено» не гарантировало завершение принятых задач (R6).
- **F15 reload admins**: остаётся «Частично»; сбой reload больше не выглядит успехом (R9).
- **F05 connect-load**: сбой принятой загрузки навсегда блокировал повтор (R1).

### Чек-лист непроверенного (нужен живой CS2 / целевой слабый CPU)
1. Frame time p50/p95/p99/max: без плагина / `f544af7` / `77d5e1b` / эта ветка при одинаковых карте, числе игроков и конфиге; повторить с прогревом JIT; фиксировать CPU, частоты, версии БД и ping до БД.
2. Нагрузки: connect burst 32/64; активный чат с gag; `css_warns`/`css_history` на игроке с 10 000+ записями; maintenance со 100 000+ банами; `css_reladmin` и смена карты при 100/1000/5000 админов (**время `LoadAdminData`/`LoadAdminGroups`**); недоступная и медленная БД (повторы R1); переполнение очередей; unload и hot reload CSS во время SQL (R7 вживую); Redis disconnect/reconnect.
3. Снять `css_sa_perf` (`game.dispatcher_item` max, `overBudgetItems`, `connectLoadRetries`, очереди pending/discarded, ipRebuilds, GC pause/alloc).
4. Stealth: обычный зритель, silent-админ, HLTV, disconnect, смена pawn/оружия, переиспользование entity, смена карты, hot reload.
5. FunCommands `resize` после ухода игрока; Redis с двумя серверами; первая миграция на копии реальной БД.
6. «Почти нулевое влияние» и жёсткая граница 0,5 мс без этих замеров не заявляются.

### Изменения контрактов (отдельно)
- Серверные записи (бан/мут/варн/unban…) отклоняются, пока нет `Ready` **и** server id (раньше хватало `DatabaseReady`). Глобальные команды (`css_prename`, глобальные админ/группа, `css_reladmin`) работают при подключённой БД.
- `css_reladmin` сообщает исход reload. `css_warns` — меню страницами по 8 (Next/Previous).
- Связь другого аккаунта с IP старше `ExpireOldIpBans` дней больше не даёт бан и на пути «текущий IP».
- Миграций БД нет; конфиг не менялся; `IDatabaseProvider` (public интерфейс) получил четыре метода и потерял `GetUpdateMutePassedBatchQuery`.

## 1. Коммиты

| Коммит | Содержание |
|---|---|
| `4542abd` | Этапы 1–3 основного плагина: асинхронный init, сессии, ограниченные очереди, диспетчер игрового потока, иммутабельные наказания и кэш, протокол refresh, SQL-исправления, история, reload админов, Discord, `css_sa_perf` |
| `b020baa` | Тесты; убран статический фиктивный экземпляр плагина (`Instance = new()`) |
| `909ff69` | Миграция индексов `017_ZZForkPerformanceIndexes`, блокировка миграций `GET_LOCK`, EXPLAIN/тайминги в `docs/perf` |
| `73d13a1` | Модули: Stealth, FunCommands, Redis |
| `324ea48` | IP-индекс «база + overlay», бенчмарки |
| `4884be9` | Явные ошибки записи наказаний, проверка готовности БД перед записью, убраны «немые» catch |

## 2. Карта потоков и владения состоянием

| Состояние / работа | Владелец | Кто читает | Как передаётся |
|---|---|---|---|
| Контроллеры, pawn, ConVar, `AdminManager`, меню, чат, kick | игровой поток | игровой поток | фон получает строки/числа (снимок) |
| `PlayerSessions` (слот → сессия: id, SteamID, userid, имя, IP, время входа) | игровой поток | любой поток (атомарные ссылки) | фон держит объект сессии, применение проверяет `IsCurrent` |
| `PlayerPenaltyManager` (gag/mute/silence по слотам) | игровой поток (CAS-замена неизменяемого объекта слота) | любой поток без блокировок | API отдаёт копии |
| `PlayersInfo` | игровой поток (`ApplyLoadResult`, команды, disconnect) | игровой поток и API | — |
| Кэш банов `BanCacheSnapshot` | один писатель (`SemaphoreSlim` в `CacheManager`, только фоновые потоки) | любой поток, одна ссылка на операцию, без ожидания | публикация одной `Volatile.Write` |
| SQL | очередь `Runtime.Db` | — | результат → `Runtime.OnGameThread` |
| Discord HTTP | очередь `Runtime.Http` | — | payload собирается на игровом потоке, сериализуется и отправляется в фоне |
| Redis | поток StackExchange.Redis → своя ограниченная очередь → `NextWorldUpdate` | — | — |
| Silent-слоты для API | игровой поток (`PublishSilentSnapshot`) | любой | копия заменяется целиком при изменении |

**Очереди и лимиты**

| Очередь | Ёмкость | Параллелизм | При переполнении |
|---|---|---|---|
| `Runtime.Db` | 512 задач | 4 исполнителя (MySQL), 1 последовательный (SQLite) | бан/мут/варн/админ **не применяется**, админ получает «NOT saved»; connect-load повторяется в следующем проходе 61 с; periodic пропускается |
| `Runtime.Http` (Discord, метрики, update-check) | 64 | 2 | уведомление отбрасывается и считается (`http rejected`) — документированная деградация |
| `GameDispatcher` | 4096 | 1 насос за world update, ≤ 64 элементов и ≤ 0,5 мс | фоновые продюсеры ждут места асинхронно (back-pressure); `TryPost` с игрового потока возвращает false |
| Redis входящие | 64 | ≤ 4 сообщения за кадр | сообщение отбрасывается и считается |
| Redis исходящие | 32 в полёте | — | отбрасывается и считается |
| Admin reload | 1 выполняется + 1 отложенный | — | N запросов объединяются максимум в 2 прохода |
| `css_admins_reload` listener | 1 ожидающий таймер | — | повторные вызовы только считаются |

**Отмена и поколения.** `Unload` отменяет `PluginLifetime` (CancellationToken), останавливает диспетчер (ожидающие задачи
отменяются, ничего не применяется), закрывает очереди и очищает сессии — **ничего не ожидая** на игровом потоке.
Disconnect завершает сессию слота, смена карты очищает все сессии: результаты для старых подключений отбрасываются
(`staleSession`). Таймауты: MySQL `ConnectionTimeout=10`, `DefaultCommandTimeout=30` (миграции — 600 с), SQLite
`Default Timeout=15`, HTTP — 15 с на запрос.

**Retry.**
- Подключение при старте — 5 попыток (задержки 2/5/10/20 с), затем состояние Failed.
- Запись сервера и сборка кэша — по 3 попытки.
- Скрипт миграции — 1 повтор.
- Discord — до 3 попыток с учётом 429 `Retry-After`.
- После Failed повтор идёт при следующей смене карты.

## 3. Аудит F01–F20

| # | Статус | Где в коде | Тест | Остаточный риск |
|---|---|---|---|---|
| F01 блокирующий CheckConnection | **Исправлено** | `Initialization.cs`, `CS2-SimpleAdmin.cs` (`OnConfigParsed` не ждёт) | `InitializationTests` (retry, недоступная БД, падение миграции) | `AdminManager.LoadAdminData`/`LoadAdminGroups` читают JSON синхронно на игровом потоке (API CSS), см. F15 |
| F02 история в одном callback | **Исправлено** | `SharedQueries.PenaltyHistoryPage/Count`, `QueueHistoryPrint`, `BasicMenu.OpenHistoryPage` | `HistoryPagesAreStableWithTiedTimestampsAndFilteredInSql` (4 СУБД) | Подписи столбцов UNION проверены на 4 движках; OFFSET-пагинация дорожает на очень глубоких страницах (≥10k строк у одного игрока — сотни строк чтения) |
| F03 вложенные коллекции | **Исправлено** | `PlayerPenaltyManager.cs` | `PenaltyManagerTests` (8 писателей × 2000 + читатели, 0 потерь) | — |
| F04 изменяемый кэш | **Исправлено** | `BanCacheSnapshot.cs`, `IpHistoryIndex.cs`, `CacheManager.cs` | `PublishedSnapshotsAreNotChangedByLaterGenerations`, `IncrementalEqualsFullRebuild` | — |
| F05 нет проверки сессии | **Исправлено** | `PlayerSessions.cs`, `PlayerManager.ResolveController`, периодический kick, penalties/warns/history/reload-bans | `PlayerSessionsTests` | Команды, применяемые сразу на игровом потоке, не меняли семантику (контроллер ещё валиден) |
| F06 перекрытие 61 с, гонка expire/refresh | **Исправлено** | `PeriodicMaintenance.cs` (single-flight, порядок), `BanRecord.IsEffectivelyActive` | `TimedBanPastItsEndIsNotActiveEvenBeforeTheSqlExpiry` | — |
| F07 Stealth `CheckTransmit` | **Исправлено** | `CS2-SimpleAdmin_StealthModule.cs` | синтетика H; живой проверки нет | Быстрый путь читает слот получателя по gamedata-offset `CheckTransmitPlayerSlot`, как это делает CSS 1.0.369; если CSS изменит раскладку списка, нужен пересмотр (fallback — индексатор CSS) |
| F08 скан IP-истории | **Исправлено** (согласованность после expiry — R2) | обратный индекс IP → аккаунты | `ReverseIndexMatchesFullScanReference` (200 случайных наборов против эталона полного скана) | — |
| F09 история в кэше | **Исправлено** | кэш хранит только ACTIVE, totals — SQL | `ConnectStatsCountHistoryNotActiveCacheEntries` | IP-история остаётся в памяти (нужна multiaccount-проверке), но компактнее исходной |
| F10 потеря изменений refresh | **Исправлено** | `CacheManager.RefreshCacheAsync` | `FullBuildThenRefresh…`, `StatusChangeInvisibleToUpdatedAt…`, `ThousandsOfBansChangedAtOnceArePaged`, `MoreThan300IpChanges…` (4 СУБД) | Смена IP/SteamID, сделанная внешним писателем на **SQLite** без обновления `updated_at`, не видна до `css_reloadbans` (смена статуса ловится контрольной суммой) |
| F11 неограниченные задачи | **Исправлено** | `BoundedWorkQueue`, `Runtime`, coalescing reload, listener | `BoundedWorkQueueTests` | — |
| F12 двойной SET NAMES, неверный stats | **Исправлено** | `MysqlDatabaseProvider.CreateConnectionAsync`, `MuteManager.GetPlayerMutes`, `GetPlayerPenaltyStatsAsync` | `CyrillicRoundTripsAndCollationSurvivesPoolReset`, `ConnectStats…` | `SET NAMES` остался один (нужен после сброса пула); `time_zone` не трогался |
| F13 online-mode N+N запросов | **Исправлено** (повторный учёт при retry найден ревью, R5) | `MuteManager.CheckOnlineModeMutesAsync`, `PeriodicMaintenance.ComputeCredits` | `OnlineTimeMutesAreCreditedSetBased` (100 игроков: 2 UPDATE по `Com_update` против 100) | Учёт по реальному онлайну: при переподключении теряется до 59 с неначисленного остатка |
| F14 индексы 016 выключены | **Исправлено** | `017_ZZForkPerformanceIndexes.sql` (MySQL, SQLite) | `MigrationTests` (создание, частичное наличие, 2 сервера одновременно) | См. раздел 6 |
| F15 reload admins в игре | **Частично** | `ReloadAdminsAsync` (coalescing, атомарная запись, без повторного чтения файлов с диска, 3 отдельных элемента диспетчера) | — | `AdminManager.LoadAdminData`/`LoadAdminGroups` по-прежнему читают и парсят файл на игровом потоке (публичный API CSS 1.0.369 принимает только путь). Второй `LoadAdminData` оставлен: CSS сливает флаги групп только в существующие домены, без живого теста это не убрать |
| F16 SQLite «Async» синхронный | **Подтверждено и учтено** | 1 последовательный исполнитель; ни одного вызова провайдера из игрового callback | `SqliteProviderAsyncIsSynchronous` (рефлексия: методы не переопределены) | WAL не включался (файл может лежать на сетевом хранилище) |
| F17 lifecycle/Ready | ~~Исправлено~~ **пересмотрено, см. R4/R7/R9 (раздел 0)** | `Initialization.MarkReady`, `ServerManager.LoadAsync`, `Unload` | `InitializationTests` | Ready один раз за загрузку плагина; FunCommands и Example и так регистрируют меню повторно-безопасно |
| F18 устаревший `_config` | **Исправлено** | поле удалено; операции берут снимок `Config`; `CurrentConfig` | `ConfigSnapshotTests` | — |
| F19 Discord/Redis | **Исправлено** | `DiscordSender.cs`, модуль Redis | — (Redis-модуль собран, но не тестировался с живым Redis) | По умолчанию каналы Redis объединены (`cs2-simpleadmin_events`); при наличии внешнего relay со старыми каналами их можно вернуть в конфиге |
| F20.1 renames | **Исправлено** | `PlayerManager.EnforceRenamesOnline` | синтетика G | — |
| F20.2 command listener | **Исправлено** | `CommandListenerCore`, `ChatTriggers`, `CommandLogFilter` | — | Логирование произвольных команд сохранено; сравнение теперь регистронезависимое |
| F20.3 чат: ToList/очистка | **Исправлено** | `HasAnyPenalty` + чтение без мутаций | `ChatFastPathSeesNoPenaltyWithoutAllocating` | — |
| F20.4 Time/timezone | **Исправлено** | `Time.Configure` (один раз на конфиг, ошибка логируется один раз) | `PenaltyRulesFollowTheCurrentConfig` | Семантика не менялась: SQLite → UTC, иначе `Timezone` |
| F20.5 FunCommands 0,12 с | **Исправлено** | запись только при отличии | — | Таймер оставлен: движок может сбрасывать значения, а проверить это без живого сервера нельзя |

**Дополнительно (раздел 5 аудита и найденное попутно)**

- `BanRecord.Created` и `ends`/`duration` теперь читаются; `ExpireOldIpBans` работает (тест `ExpireOldIpBansUsesTheBanCreationDate`).
- `TotalBans` — исторический счёт из БД.
- Пустые типы и слоты удаляются; API наказаний и silent-слотов отдаёт копии.
- Успех бана, мута и варна больше не скрывает ошибку записи: админ получает «could NOT be saved». `BanPlayer` создаёт соединение внутри `try`.
- Новый бан сразу попадает в кэш, переподключение отклоняется до refresh.
- **Найдено:** статический `Instance = new()` создавал второй объект плагина, конструктор `BasePlugin` регистрировал listener в CSS. Исправлено в основном плагине и в Redis-модуле.
- **Найдено:** два сервера, стартующие на одной не до конца мигрированной БД, ломали друг другу неидемпотентные upstream-скрипты (например, `005`). Теперь миграции идут под `GET_LOCK` (тест `TwoServersMigratingTheSameDatabaseAtOnceBothSucceed`).
- **Найдено:** `RemovePenaltiesByDateTime` сравнивал `ends` с точностью до тиков, а БД хранит секунды. Наказания, выданные в текущей сессии, в TimeMode 0 не помечались отыгранными. Исправлено (допуск 1 с).
- Таймеры `css_hide` проверяют `IsValid` контроллера.
- Example-модуль не собирается и на исходном коммите (устаревший `CS2-SimpleAdminApi.dll` в папке модуля: `TotalKicks`, `CanTarget`). Не исправлялось.

## 4. Изменения поведения и контракты

| Изменение | Почему | Совместимость |
|---|---|---|
| При недоступной БД/ошибке миграции плагин не выгружается сам, а остаётся в состоянии Failed и повторяет попытку при смене карты; команды отвечают статусом | Нельзя ждать БД на игровом потоке | Ошибка миграции теперь **блокирует** готовность (раньше молча продолжал) — проверьте лог при первом старте |
| Записи наказаний при переполнении очереди или до готовности БД отклоняются явно | Не терять баны молча | Новое сообщение администратору |
| `TotalBans` = все баны игрока (с учётом `MultiServerMode`) | Исправление F12/§5 | Иное число в `css_who`/уведомлениях |
| TimeMode 0 начисляет реальные минуты онлайна | Нет дрейфа/дублей | Раньше: +1 мин за каждый проход 61 с любому, кто онлайн в этот момент |
| В кэше IP «забыт» после `ExpireOldIpBans` дней по `ends` (как это делает SQL-джоб) | Согласованность с БД | Совпадает с поведением после перезапуска |
| `css_history <цель> [тип] [страница]`, по 50 строк | F02 | Старый синтаксис работает, выводится первая страница и подсказка следующей |
| Меню истории — 20 записей на страницу + «»» / «« » | F02 | — |
| В уведомлении «associated accounts» показывается ≤ 25 аккаунтов + «ещё N» | Ограниченный вывод | — |
| Redis: `ConfigVersion 2`, ключи `PublishChannel`/`SubscribeChannel` | F19 | Сообщения совместимы с v1 (добавлено поле `Id`) |
| `ListSilentAdminsSlots()` возвращает разделяемый снимок | Нет аллокаций, нельзя повредить состояние плагина | Тип тот же (`HashSet<int>`); вызывающим нельзя его менять |
| `GetPlayerInfo` до окончания загрузки возвращает снимок вместо исключения | — | — |

Не менялось: команды и права/immunity, `BanType`, `TimeMode`, `MultiServerMode`, литерал `Console` в `admin_name`,
timezone по умолчанию и работа с временем, legacy-миграция конфига, обработка `Commands.json`, номер сборки форка,
`css_respawn` не возвращён.

## 5. Сборки и тесты

Windows 11, .NET SDK 10.0.400. Сборка той же последовательностью, что в `.github/workflows/build.yml` (из корня репозитория):

| Проект | Результат |
|---|---|
| CS2-SimpleAdmin | 0 ошибок, 33 предупреждения (IL2026/2072/2075 trimming и `dynamic`/Dapper; на исходном коммите было 53) |
| CS2-SimpleAdminApi | 0/0 |
| FunCommands | 0 ошибок, 1 предупреждение (CS8618, было и до изменений) |
| Stealth | 0/0 |
| Redis (не входит в CI) | 0/0 |
| AntiDLL, BanSound (не входят в CI) | 0 ошибок |
| Example (не входит в CI) | 4 ошибки, **как и на `f544af7`** |

`global.json` модулей требует SDK 8, поэтому модули собираются из корня, как в CI. Trimming: `PublishTrimmed` действует
только на `dotnet publish`, а CI собирает и выкладывает результат `dotnet build`, поэтому поставляемый артефакт не
обрезается. Предупреждения не подавлялись глобально; новые Discord-payload'ы используют source-generated JSON.

**Тесты** (`tests/CS2-SimpleAdmin.Tests`, xUnit): исторически 86/86; после ревью см. раздел 0 (141 без СУБД, 232 с 3 СУБД, 322 с 6 СУБД).
- Интеграционные тесты идут на SQLite, MySQL 5.7.44, MySQL 8.0.43 и MariaDB 10.11.14. Это временные экземпляры из
  бинарников OSPanel, порты 33057/33080/33110; адреса можно задать через `SA_TEST_MYSQL`.
- Тесты, писавшиеся под дефекты (gag-гонки, `Created`, LIMIT 300, «немое» удаление, неверный stats-запрос,
  одновременная миграция), падают на поведении исходного кода. Часть из них прямо демонстрирует старый дефект: например,
  вызов старого запроса из `GetPlayerMutes` бросает исключение.

```bash
dotnet test tests/CS2-SimpleAdmin.Tests
```

Не покрыто тестами из-за отсутствия CSS runtime: код, вызывающий native API (kick, `VoiceFlags`, меню, `AdminManager`,
`CheckTransmit`). Ограничения такого кода обеспечиваются архитектурой (диспетчер, сессии), но в игре не проверялись.

## 6. SQL и миграция

`017_ZZForkPerformanceIndexes` (имя сортируется после `017_ZFork…` и до будущих upstream `018+`, по соглашению форка):

- `sa_bans`: `player_steamid`, `(status, ends)`, `updated_at`, `created`, `player_ip`;
- `sa_warns`: `player_steamid`, `(status, ends)`;
- `sa_mutes`: `(status, ends)`.

Идемпотентна: проверка через `information_schema` + `PREPARE` (MySQL 5.7 не умеет `CREATE INDEX IF NOT EXISTS`),
на SQLite — `IF NOT EXISTS`. Скрипты миграций выполняются на отдельном соединении с `AllowUserVariables`, под
`GET_LOCK('cs2_simpleadmin_migrations')`. InnoDB строит индексы online.

Синтетика: 200k банов (5 % ACTIVE), 200k мутов, 100k варнов, 1M IP. Время запроса p50 в мс, без индексов → с индексами
(`docs/perf/timing_*.txt`):

| Запрос | MySQL 5.7 | MySQL 8.0 | MariaDB 10.11 |
|---|---|---|---|
| delta refresh банов | 55,6 → 0,10 | 63,1 → 0,13 | 42,4 → 0,12 |
| ACTIVE checksum | 22,2 → 1,12 | 26,8 → 1,27 | 17,2 → 1,19 |
| expire bans* | 24,4 → 6,4 | 30,0 → 8,1 | 19,2 → 12,1 |
| expire mutes* | 24,8 → 5,1 | 29,9 → 6,5 | 19,2 → 10,1 |
| expire warns* | 12,4 → 2,3 | 15,0 → 4,1 | 9,5 → 3,0 |
| статистика при подключении | 48,5 → 5,4 | 40,4 → 1,1 | 22,5 → 1,1 |

\* В синтетике тысячи ACTIVE-строк уже с прошедшим `ends`, поэтому индекс читает их все. В реальной БД, где
expire-джоб работает постоянно, таких строк единицы.

EXPLAIN до/после: `docs/perf/explain_*.txt`.
- До: full scan `sa_bans`/`sa_warns` во всех запросах прохода 61 с.
- После: `ref`/`range`/`index_merge`.
- Запрос дельты с `OR` даёт `index_merge sort_union` на всех трёх движках, если параметры передаются литералами. Именно
  так их отправляет MySqlConnector (клиентская подстановка); с серверными user-переменными 5.7 и MariaDB выбирают PK.
- `DELETE FROM sa_players_ips WHERE used_at <= …` остаётся full scan: индекс 013 есть, но удаляется большая доля строк.

Нагрузка записи: на `sa_bans` добавлено 5 вторичных индексов. Записей там мало (наказания), сайт пишет их же — на
синтетике `ALTER` занял ~1,5 с на 200k строк.

**Rollout**
1. Бэкап БД.
2. Выложить сборку на один сервер — миграция применится под блокировкой; на больших таблицах индексы строятся online.
3. Проверить лог (`Migration "017_ZZ…" successfully applied`) и `css_sa_perf` (state=Ready).
4. Затем остальные серверы.

Сайт не затрагивается: схема та же плюс индексы, литерал `Console` и формат времени без изменений.

**Rollback**
1. Вернуть предыдущую сборку плагина — индексы ей не мешают.
2. При необходимости удалить индексы:
   `ALTER TABLE sa_bans DROP INDEX idx_sa_bans_steamid, DROP INDEX idx_sa_bans_status_ends, DROP INDEX idx_sa_bans_updated_at, DROP INDEX idx_sa_bans_created, DROP INDEX idx_sa_bans_ip; ALTER TABLE sa_warns DROP INDEX idx_sa_warns_steamid, DROP INDEX idx_sa_warns_status_ends; ALTER TABLE sa_mutes DROP INDEX idx_sa_mutes_status_ends; DELETE FROM sa_migrations WHERE version = '017_ZZForkPerformanceIndexes';`
3. Конфиг Redis v2 читается и старой версией (лишние ключи игнорируются).

## 7. Бенчмарки (синтетические)

Методика: `bench/CS2-SimpleAdmin.Bench` сравнивает **точные копии алгоритмов `f544af7`** (`Baseline.cs`, без
CSS/БД) с новым кодом на одних данных и одной машине.
- Release, .NET 10.0.11, workstation concurrent GC; замеры после прогрева.
- **AMD Zen 4 (Family 25 Model 97), 32 потока, Windows 11 — это не целевой слабый CPU.** На слабом CPU абсолютные
  числа будут выше, отношения сопоставимы.
- Сырые данные: `docs/perf/bench_results.md`.

| Сценарий | Было | Стало |
|---|---|---|
| Multiaccount-проверка 1 игрока, 1M IP-записей (p50) | 13,7 мс | 0,1 мкс |
| То же, 100k / 10k | 0,9 мс / 55 мкс | 0,4 мкс |
| Проход 61 с: 64 игрока, 1M IP | 888 мс CPU | 0,006 мс |
| `GetAccountsByIp`, 1M | 11,3 мс | < 0,1 мкс |
| Полная сборка кэша, 1M IP (фон) | 378 мс, 173 МБ alloc, 124 МБ удерживается | 500 мс, 153 МБ, 85 МБ (с обратным индексом) |
| Инкрементальный refresh 300 / 2000 IP-строк на 1M | (мутация общих структур) | 1,1 / 5,6 мс фона |
| Чат: проверка gag, игрок с gag | 48 нс, 72 Б | 93 нс, 0 Б (включает конвертацию времени) |
| Чат: игрок без наказаний | 5 нс | 5 нс, 0 Б |
| Один игровой callback истории 10 / 1000 / 10000 строк | 3 / 341 / 4901 мкс | ≤ 20 строк печати на callback, форматирование в фоне |
| Renames: 1000 записей, 64 игрока (раз в 5 с) | 564 мкс, 1,7 МБ | 0,2 мкс, 0 Б |
| Stealth managed-часть, 64 получателя, 1 silent | 7 мкс, 2,2 КБ/вызов | 5 мкс, 0 Б/вызов; native-резолв pawn убран из каждого вызова — не измерено |
| Диспетчер: 10 000 элементов по ~8 мкс | 10 000 callback'ов через лимит CSS на тик | 158 обновлений по ≤ 504 мкс |
| Online-mode, 100 игроков | 100 UPDATE + 100 SELECT | 2 UPDATE + 2 SELECT (реальная БД) |

Синтетика не измеряла: frame time CS2, native-вызовы, стоимость `AdminManager.LoadAdminData` (файл и JSON на
игровом потоке, растёт с числом админов), паузы GC в процессе сервера.

## 8. Что теперь ограничено

- **Игровой поток:**
  - нет ожидания БД/HTTP/файлов, кроме `LoadAdminData`/`LoadAdminGroups` CSS;
  - применение результатов ≤ 64 элементов и ≤ 0,5 мс за world update (бюджет не прерывает одиночный элемент, native-вызов и GC);
  - вывод истории ≤ 20 строк за элемент;
  - периодический callback — только снимок сессий и upkeep наказаний (≤ 64 слота).
- **Фон:**
  - очереди ограничены, проход 61 с не перекрывается;
  - refresh ограничен 50k изменёнными банами (дальше полная пересборка) и 50k IP-строк за проход (дальше продолжение со следующего прохода);
  - DB-запросы с таймаутами.
- **Память:** кэш хранит только ACTIVE-баны; IP-история компактнее исходной; при повторных reload и disconnect ничего не копится (сессии и очереди очищаются).

**Что ещё может давать пики**
1. `AdminManager.LoadAdminData`/`LoadAdminGroups` при reload админов (синхронный файл + JSON, вызов CSS).
2. Полная пересборка кэша при старте и `css_reloadbans` на большой IP-истории: фон, но ~150 МБ аллокаций за раз → GC-паузы процесса.
3. Массовый одновременный вход 64 игроков: 64 connect-load через 4 исполнителя; игровая часть на игрока мала.
4. Native-операции движка (kick, смена команды, меню) — по своей природе.
5. Сам `css_sa_perf` печатает ~20 строк.

## 9. Что требует живого сервера

Измерить на целевом CPU и конфиге три варианта (без плагина / `f544af7` / эта ветка): 0/16/32/64 игрока; массовые
connect/disconnect и смена карты; 20–50 hot reload; медленная/недоступная БД; Discord 429; Redis burst; Stealth с
silent-админом; FunCommands 0/1/32/64.

- `css_sa_perf` после прогрева показывает p50/p95/p99/max игровых обработчиков (`game.*`), насоса диспетчера и
  задержки применения, очереди и отказы, refresh/full build, GC.
- `css_sa_perf reset` обнуляет гистограммы перед замером.

Общий frame time сервера снимать отдельно (серверная телеметрия CS2, `dotnet-counters`).

Также нужна проверка в игре:
- Stealth: обычный зритель, silent-админ, HLTV, переподключение;
- FunCommands: скорость и гравитация после урона, спавна и смены раунда;
- Redis: два сервера, отсутствие эха;
- первая миграция на копии реальной БД 4ill.ru.
