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
