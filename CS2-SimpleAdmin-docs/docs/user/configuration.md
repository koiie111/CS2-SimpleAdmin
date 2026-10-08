---
sidebar_position: 3
---

# Configuration

Learn how to configure CS2-SimpleAdmin to suit your server's needs.

## Configuration File Location

The main configuration file is located at:
```
addons/counterstrikesharp/configs/plugins/CS2-SimpleAdmin/CS2-SimpleAdmin.json
```

## Configuration Structure

The configuration file is divided into several sections:

### Database Configuration

Configure your database connection:

```json
"DatabaseConfig": {
  "DatabaseType": "SQLite",
  "SqliteFilePath": "cs2-simpleadmin.sqlite",
  "DatabaseHost": "",
  "DatabasePort": 3306,
  "DatabaseUser": "",
  "DatabasePassword": "",
  "DatabaseName": "",
  "DatabaseSSlMode": "preferred"
}
```

**Database Types:**
- `SQLite` - Local database file (good for single server)
- `MySQL` - MySQL/MariaDB server (required for multi-server setups)

**MySQL Example:**
```json
"DatabaseConfig": {
  "DatabaseType": "MySQL",
  "DatabaseHost": "localhost",
  "DatabasePort": 3306,
  "DatabaseUser": "cs2admin",
  "DatabasePassword": "your_password",
  "DatabaseName": "cs2_simpleadmin",
  "DatabaseSSlMode": "preferred"
}
```

### Other Settings

General plugin settings:

```json
"OtherSettings": {
  "ShowActivityType": 2,
  "TeamSwitchType": 1,
  "KickTime": 5,
  "BanType": 1,
  "TimeMode": 1,
  "DisableDangerousCommands": true,
  "MaxBanDuration": 10080,
  "MaxMuteDuration": 10080,
  "ExpireOldIpBans": 0,
  "ReloadAdminsEveryMapChange": false,
  "DisconnectedPlayersHistoryCount": 10,
  "NotifyPenaltiesToAdminOnConnect": true,
  "ShowBanMenuIfNoTime": true,
  "UserMessageGagChatType": false,
  "CheckMultiAccountsByIp": true,
  "AdditionalCommandsToLog": [],
  "IgnoredIps": []
}
```

**Settings Explained:**

| Setting | Description | Default |
|---------|-------------|---------|
| `ShowActivityType` | How to display admin actions (0=hide, 1=anonymous, 2=show name) | 2 |
| `TeamSwitchType` | Team switch behavior | 1 |
| `KickTime` | Delay before kicking player (seconds) | 5 |
| `BanType` | Legacy: IP addresses never ban (only SteamID64 bans are enforced). `1` still stores the player's IP in new ban rows | 1 |
| `TimeMode` | Time display mode | 1 |
| `DisableDangerousCommands` | Disable potentially dangerous commands | true |
| `MaxBanDuration` | Maximum ban duration in minutes (0=unlimited) | 10080 |
| `MaxMuteDuration` | Maximum mute duration in minutes (0=unlimited) | 10080 |
| `ExpireOldIpBans` | Auto-expire IP bans after X days (0=disabled) | 0 |
| `ReloadAdminsEveryMapChange` | Reload admin permissions on map change | false |
| `DisconnectedPlayersHistoryCount` | Number of disconnected players to track | 10 |
| `NotifyPenaltiesToAdminOnConnect` | Show penalties to admins when they connect | true |
| `ShowBanMenuIfNoTime` | Show ban menu even without time parameter | true |
| `UserMessageGagChatType` | Use UserMessage for gag (alternative chat blocking) | false |
| `CheckMultiAccountsByIp` | Only adds the admin notice about associated accounts; never bans | true |
| `AdditionalCommandsToLog` | Array of additional commands to log | [] |
| `IgnoredIps` | Legacy, not used for bans | [] |

### Metrics and Updates

```json
"EnableMetrics": true,
"EnableUpdateCheck": true
```

- `EnableMetrics` - Send anonymous usage statistics
- `EnableUpdateCheck` - Check for plugin updates on load

### Timezone

Set your server's timezone for accurate timestamps:

```json
"Timezone": "UTC"
```

See the [list of timezones](#timezone-list) below.

### Warning Thresholds

Configure automatic actions when players reach warning thresholds:

```json
"WarnThreshold": {
  "998": "css_addban STEAMID64 60 \"3/4 Warn\"",
  "999": "css_ban #USERID 120 \"4/4 Warn\""
}
```

**Example:** Automatically ban a player for 60 minutes when they receive their 3rd warning.

### Multi-Server Mode (deprecated)

```json
"MultiServerMode": true
```

Penalties (bans, mutes, gags, silences, warns) are **always** shared across every server that uses the same database,
whatever this setting says; an old config with `false` behaves like `true`. `server_id` is only stored as information about
where a penalty was issued. Admin permissions and groups keep their own per-server scope. See
[GLOBAL_PENALTIES.md](https://github.com/koiie111/CS2-SimpleAdmin/blob/main/docs/GLOBAL_PENALTIES.md).

Related `OtherSettings`:
- `UnverifiedConnectionTimeoutSeconds` (default `45`, 10-600): a connection whose ban/mute state cannot be read in time is disconnected; nothing is written for it.
- `GagAllowedChatCommands` (default `[]`): names of commands of **other plugins** a gagged/silenced player may still run with a chat trigger (`!rank`). This plugin's own commands are decided by a fixed list of verified keys (names listed here are ignored for them).

### Discord Integration

Send notifications to Discord webhooks:

```json
"Discord": {
  "DiscordLogWebhook": "https://discord.com/api/webhooks/...",
  "DiscordPenaltyBanSettings": [...],
  "DiscordPenaltyMuteSettings": [...],
  "DiscordPenaltyGagSettings": [...],
  "DiscordPenaltySilenceSettings": [...],
  "DiscordPenaltyWarnSettings": [...],
  "DiscordAssociatedAccountsSettings": [...]
}
```

**Webhook Settings:**
Each penalty type can have its own webhook configuration:

```json
"DiscordPenaltyBanSettings": [
  {
    "name": "Color",
    "value": "#FF0000"
  },
  {
    "name": "Webhook",
    "value": "https://discord.com/api/webhooks/YOUR_WEBHOOK_HERE"
  },
  {
    "name": "ThumbnailUrl",
    "value": "https://example.com/ban-icon.png"
  },
  {
    "name": "ImageUrl",
    "value": ""
  },
  {
    "name": "Footer",
    "value": "CS2-SimpleAdmin"
  },
  {
    "name": "Time",
    "value": "{relative}"
  }
]
```

**Available Placeholders:**
- `{relative}` - Relative timestamp
- `{fixed}` - Fixed timestamp

### Map Configuration

Configure default maps and workshop maps:

```json
"DefaultMaps": [
  "de_dust2",
  "de_mirage",
  "de_inferno"
],
"WorkshopMaps": {
  "aim_map": "123456789",
  "surf_map": "987654321"
}
```

### Custom Server Commands

Add custom commands to the admin menu:

```json
"CustomServerCommands": [
  {
    "Flag": "@css/root",
    "DisplayName": "Reload Admins",
    "Command": "css_reloadadmins"
  },
  {
    "Flag": "@css/cheats",
    "DisplayName": "Enable sv_cheats",
    "Command": "sv_cheats 1"
  }
]
```

### Menu Configuration

Configure menu appearance and options:

```json
"MenuConfig": {
  "MenuType": "selectable",
  "Durations": [
    { "name": "1 minute", "duration": 1 },
    { "name": "5 minutes", "duration": 5 },
    { "name": "15 minutes", "duration": 15 },
    { "name": "1 hour", "duration": 60 },
    { "name": "1 day", "duration": 1440 },
    { "name": "7 days", "duration": 10080 },
    { "name": "14 days", "duration": 20160 },
    { "name": "30 days", "duration": 43200 },
    { "name": "Permanent", "duration": 0 }
  ],
  "BanReasons": [
    "Hacking",
    "Voice Abuse",
    "Chat Abuse",
    "Admin disrespect",
    "Other"
  ],
  "KickReasons": [
    "Voice Abuse",
    "Chat Abuse",
    "Admin disrespect",
    "Other"
  ],
  "WarnReasons": [
    "Voice Abuse",
    "Chat Abuse",
    "Admin disrespect",
    "Other"
  ],
  "MuteReasons": [
    "Advertising",
    "Spamming",
    "Spectator camera abuse",
    "Hate",
    "Admin disrespect",
    "Other"
  ],
  "AdminFlags": [
    { "name": "Generic", "flag": "@css/generic" },
    { "name": "Chat", "flag": "@css/chat" },
    { "name": "Change Map", "flag": "@css/changemap" },
    { "name": "Slay", "flag": "@css/slay" },
    { "name": "Kick", "flag": "@css/kick" },
    { "name": "Ban", "flag": "@css/ban" },
    { "name": "Perm Ban", "flag": "@css/permban" },
    { "name": "Unban", "flag": "@css/unban" },
    { "name": "Show IP", "flag": "@css/showip" },
    { "name": "Cvar", "flag": "@css/cvar" },
    { "name": "Rcon", "flag": "@css/rcon" },
    { "name": "Root (all flags)", "flag": "@css/root" }
  ]
}
```

## Timezone List

<details>
<summary>Click to expand timezone list</summary>

```
UTC
America/New_York
America/Chicago
America/Denver
America/Los_Angeles
Europe/London
Europe/Paris
Europe/Berlin
Europe/Warsaw
Europe/Moscow
Asia/Tokyo
Asia/Shanghai
Asia/Dubai
Australia/Sydney
Pacific/Auckland
... (and many more)
```

For a complete list, see the info.txt file in the documentation folder.

</details>

## Commands Configuration

You can customize command aliases in:
```
addons/counterstrikesharp/configs/plugins/CS2-SimpleAdmin/Commands.json
```

Example:
```json
{
  "Commands": {
    "css_ban": {
      "Aliases": [
        "css_ban",
        "css_ban2"
      ]
    }
  }
}
```

This allows you to:
- **Disable commands** - Remove all aliases from the array
- **Add aliases** - Add multiple command variations
- **Rename commands** - Change the command name while keeping functionality

## Best Practices

### Security

1. **Use MySQL in production** - SQLite is not suitable for multi-server setups
2. **Set MaxBanDuration** - Prevent accidental permanent bans
3. **Enable DisableDangerousCommands** - Protect against accidental server crashes
4. **Use strong database passwords** - If using MySQL

### Performance

1. **Set ReloadAdminsEveryMapChange to false** - Unless you frequently modify admin permissions
2. **Limit DisconnectedPlayersHistoryCount** - Reduce memory usage
3. **Use database indices** - Migrations create these automatically

### Multi-Server Setup

1. **Share one database** - penalties are network-wide automatically (`MultiServerMode` is ignored)
2. **Use MySQL** - Required for multi-server
3. **Configure server IDs** - Each server gets a unique ID automatically
4. **Test penalties** - Ensure bans work across all servers

## Troubleshooting

### Changes not taking effect

**Solution:** Reload the plugin or restart the server:
```
css_plugins reload CS2-SimpleAdmin
```

### Discord webhooks not working

**Solution:**
- Verify webhook URL is correct
- Check that the webhook is not deleted in Discord
- Ensure server has internet access

### TimeMode issues

**Solution:** Set your timezone correctly in the configuration

## Next Steps

- **[Learn admin commands](commands/basebans)** - Browse available commands
- **[Set up admins](#)** - Add your admin team
- **[Configure modules](../modules/intro)** - Extend functionality
