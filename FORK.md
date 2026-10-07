# Форк CS2-SimpleAdmin (koiie111)

Форк [daffyyyy/CS2-SimpleAdmin](https://github.com/daffyyyy/CS2-SimpleAdmin) для проекта 4ill.ru.

## Отличия от upstream

- **Удалена команда `css_respawn`** вместе с пунктом меню «Respawn» в модуле
  `CS2-SimpleAdmin_FunCommands`. На серверах респаун выдаёт отдельный плагин VIP Respawn,
  и одинаковые команды конфликтовали бы. Ключ `RespawnCommands` из старых конфигов
  просто игнорируется.
- **`Timezone` по умолчанию `Europe/Moscow`** (в upstream `UTC`). Старая 1.5.1a брала локальное
  время игрового сервера, а сайт работает с БД по Москве (`SET time_zone = '+03:00'`). Если оставить UTC,
  `ends`/`created`, истечение наказаний и окно обновления кэша банов (`updated_at >= lastUpdate`)
  сдвинутся на 3 часа. Значение должно совпадать с часовым поясом MySQL (`SELECT NOW()`).
- **В `admin_name` для наказаний из консоли/RCON пишется литерал `Console`**
  (`Managers/BanManager.cs`, `MuteManager.cs`, `WarnManager.cs`). В upstream туда попадает перевод
  `sa_console` («Консоль» при русском языке сервера), а сайт отличает консоль по строке `Console`.
  В чате по-прежнему показывается перевод.
- **Миграции 013 и 015 переписаны так, чтобы проходили на реальных данных.** В upstream при strict `sql_mode`
  (по умолчанию в MySQL 5.7+) они падают: 013 на любом не‑IPv4 адресе в `sa_players_ips`
  (`INET_ATON` выдаёт ошибку вместо NULL), 015 на нечисловом `player_steamid` (`STEAM_X:Y:Z`,
  пустая строка, опечатка, внесённая через сайт). После такого падения плагин прекращает применять миграции.
  В форке перед конвертацией: `STEAM_X:Y:Z` переводится в SteamID64, прочее нечисловое становится
  `NULL` (у игрока) или `0` (у админа, как в upstream), не‑IPv4 адреса удаляются. Каждое изменённое
  или удалённое значение сохраняется в таблице `sa_migration_backup`. Проверено на MySQL 5.7.44,
  8.0.43 и MariaDB 10.11 как на старой БД 1.5.1a, так и на чистой установке.
- **Автосборка:** каждый пуш в `main` собирает плагин, API и модули
  (`.github/workflows/build.yml`) и публикует релиз с тегом `build-<VERSION>-<run_number>`.
  Номер запуска в теге нужен, чтобы два пуша с одной и той же `VERSION` не падали
  на уже существующем теге.

Схема БД не отличается от upstream. Сайт работает и со старой схемой 1.5.x,
и с новой схемой 1.9.x (миграции 010–017).

## Требования

- CounterStrikeSharp ≥ 1.0.369 (.NET 10)

## Обновление с 1.5.1a (build-230)

1. **Сделайте бэкап БД `cs2admin`.** При первом запуске плагин сам применит миграции 010–017, и часть из них необратима:
   - `013`: `sa_players_ips.address` становится `INT UNSIGNED` (`INET_ATON`), строки с адресом не‑IPv4 удаляются;
   - `015`: `player_steamid`/`admin_steamid` становятся `BIGINT`, нечисловые значения (например `Console`) заменяются на `0`.
2. Сначала выложите версию сайта, которая поддерживает обе схемы, и только после неё ставьте плагин.
3. Удалите со серверов старую папку `plugins/CS2-SimpleAdmin` и разложите архив релиза в `addons/counterstrikesharp`.

## Синхронизация с upstream

```bash
git remote add upstream https://github.com/daffyyyy/CS2-SimpleAdmin.git
git fetch upstream
git merge upstream/main
```
Если при мерже вернулся respawn, проверьте `Modules/CS2-SimpleAdmin_FunCommands`.
