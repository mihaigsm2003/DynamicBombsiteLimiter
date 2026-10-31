# Dynamic Bombsite Limiter

<img width="895" height="606" alt="image" src="https://github.com/user-attachments/assets/5ee9e5d4-1c13-441e-ac8d-62f75c5c8205" />


Dynamic Bombsite Limiter is a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for Counter-Strike 2 servers.

It allows server administrators to define custom 3D barriers around bombsites or other areas of a map. At the start of each round, the plugin checks the number of alive players on each team. Barriers are enabled for the whole round if either team is below the configured minimum, making it possible to limit the playable bombsite area on low-population servers.

## Features

- Per-map 3D barrier configuration.
- Automatic barrier activation based on each team's alive player count at round start.
- Optional bot counting.
- Round state is fixed for the rest of the round; player-count changes do not enable or disable barriers mid-round.
- A configurable round-start delay, followed by a center-screen HUD message and a chat announcement.
- Optional persistent wireframe rendering of configured barriers.
- Five barrier interaction modes:
  - `Wall` - pushes the player back to their last valid position.
  - `Teleport` - sends the player back to their spawn position.
  - `Bounce` - pushes the player back with configurable velocity.
  - `Hurt` - periodically deals configurable damage.
  - `Kill` - eliminates the player.
- In-game barrier creation using weapon bullet impacts.
- Wireframe preview for configured barriers.
- JSON configuration files that can be edited manually or managed in-game.

## Requirements

- A Counter-Strike 2 dedicated server.
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) installed and working.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building from source.

## Installation

1. Download the latest release, or build the plugin from source:

   ```powershell
  dotnet build .\DynamicBombsiteLimiter.csproj -c Release
   ```

2. Copy `bin/Release/net10.0/DynamicBombsiteLimiter.dll` to:

   ```text
   <csgo>/addons/counterstrikesharp/plugins/DynamicBombsiteLimiter/
   ```

3. Restart the server or reload the plugin through CounterStrikeSharp.

The plugin creates its configuration directory automatically:

```text
<csgo>/addons/counterstrikesharp/configs/plugins/DynamicBombsiteLimiter/
```

## Configuration

The global configuration is stored in `config.json`. If the file does not exist, it is generated with the default values:

```json
{
  "Enabled": true,
  "BarrierCommandPermission": "@css/root",
  "MinPlayersPerTeam": 5,
  "BlockedBombsite": "B",
  "CountBots": false,
  "ShowBarriersPermanently": true,
  "BarrierAction": "Wall",
  "HurtDamage": 10,
  "BounceSpeed": 300,
  "BounceUpwardVelocity": 100,
  "BarrierMessage": "Nu poti trece de aceasta bariera. Bombsite-ul {bombsite} este inchis.",
  "RoundStartHudDelaySeconds": 5,
  "RoundStartHudDurationSeconds": 5,
  "RoundStartMessage": "Nu se joaca pe toata mapa. Bombsite-ul {bombsite} este inchis in aceasta runda.",
  "RoundStartOpenMessage": "Se joaca pe toata mapa. Ambele bombsite-uri sunt deschise in aceasta runda."
}
```

### Configuration options

| Option | Description |
| --- | --- |
| `Enabled` | Enables or disables the plugin. |
| `BarrierCommandPermission` | CounterStrikeSharp permission required for barrier creation, removal, saving, and reloading. Defaults to `@css/root`. Server console commands are allowed. |
| `MinPlayersPerTeam` | Minimum alive players required on both T and CT at round start. Barriers activate if either team is below this value. |
| `BlockedBombsite` | Bombsite name used in messages, for example `A` or `B`. |
| `CountBots` | Includes alive bots in the per-team counts when enabled. |
| `ShowBarriersPermanently` | Draws configured barrier wireframes after map configuration loads and at round start. |
| `BarrierAction` | Action applied when a player enters a barrier: `Wall`, `Teleport`, `Bounce`, `Hurt`, or `Kill`. |
| `HurtDamage` | Damage dealt per second when `BarrierAction` is `Hurt`. |
| `BounceSpeed` | Horizontal speed used by the `Bounce` action. |
| `BounceUpwardVelocity` | Upward velocity used by the `Bounce` action. |
| `BarrierMessage` | Chat message shown when a player enters a barrier. Supports the `{bombsite}` placeholder. |
| `RoundStartHudDelaySeconds` | Delay after round start before the HUD and chat announcements are sent. |
| `RoundStartMessage` | Round-start message used while barriers are active. Supports the `{bombsite}` placeholder. |
| `RoundStartOpenMessage` | Round-start message used when both bombsites are open. |

The round-start HUD uses plain center-screen text and is refreshed four times at one-second intervals. The chat announcement is sent once. `RoundStartHudDurationSeconds` may still appear in older or generated config files, but is currently ignored.

Map-specific barriers are stored in:

```text
<csgo>/addons/counterstrikesharp/configs/plugins/DynamicBombsiteLimiter/maps/<map_name>.json
```

## In-game commands

Commands can be used from chat with the `!` prefix or from the server console with the `css_` prefix.

| Chat command | Description |
| --- | --- |
| `!dbl_addbarrier` | Starts barrier creation. Shoot two opposite corners with your weapon. |
| `!dbl_removebarrier <number>` | Removes a barrier by its list number. |
| `!dbl_list` | Lists all barriers configured for the current map. |
| `!dbl_preview` | Displays the configured barriers as a green wireframe. |
| `!dbl_save` | Saves the current map's barriers to its JSON file. |
| `!dbl_reload` | Reloads the current map configuration from disk. |
| `!dbl_status` | Displays the player count, threshold, action mode, and barrier state. |

### Creating a barrier

1. Join a team and make sure your player is alive.
2. Run `!dbl_addbarrier`.
3. Shoot the first corner of the desired barrier.
4. Shoot the opposite corner.
5. Use `!dbl_preview` to verify the result.
6. Use `!dbl_save` to persist the barrier for the current map.

Run `!dbl_addbarrier` again while selecting a barrier to cancel the current selection.

## How activation works

Barriers are active only when all of the following conditions are true:

- `Enabled` is `true`.
- At least one barrier is configured for the current map.
- At the start of the round, either T or CT has fewer alive players than `MinPlayersPerTeam`.

The alive-player count and barrier state are checked once at round start. Changes in player count during the round do not change the barrier state until the next round. Bots are excluded unless `CountBots` is enabled.

## Building from source

Clone the repository and build the project:

```powershell
git clone https://github.com/mihaigsm2003/DynamicBombsiteLimiter.git
cd DynamicBombsiteLimiter
dotnet build -c Release
```

The project targets `.NET 10` and references `CounterStrikeSharp.API`.

## License

This project is licensed under the terms of the [MIT License](LICENSE).
