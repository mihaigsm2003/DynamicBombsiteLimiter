# Dynamic Bombsite Limiter

<img width="895" height="606" alt="image" src="https://github.com/user-attachments/assets/5ee9e5d4-1c13-441e-ac8d-62f75c5c8205" />


Dynamic Bombsite Limiter is a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for Counter-Strike 2 servers.

It allows server administrators to define custom 3D barriers around bombsites or other areas of a map. The barriers are enabled automatically when the number of active players falls below a configurable threshold, making it possible to limit the playable bombsite area on low-population servers.

## Features

- Per-map 3D barrier configuration.
- Automatic barrier activation based on the active player count.
- Optional bot counting.
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
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) for building from source.

## Installation

1. Download the latest release, or build the plugin from source:

   ```powershell
   dotnet build -c Release
   ```

2. Copy `bin/Release/net8.0/DynamicBombsiteLimiter.dll` to:

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
  "MinPlayers": 10,
  "BlockedBombsite": "B",
  "CountBots": false,
  "BarrierAction": "Wall",
  "HurtDamage": 10,
  "BounceSpeed": 300,
  "BounceUpwardVelocity": 100,
  "BarrierMessage": "You cannot pass this barrier. Bombsite {bombsite} is closed.",
  "RoundStartMessage": "This map is not fully playable. Bombsite {bombsite} is closed this round."
}
```

### Configuration options

| Option | Description |
| --- | --- |
| `Enabled` | Enables or disables the plugin. |
| `MinPlayers` | Barriers are active when the number of active players is lower than this value. |
| `BlockedBombsite` | Bombsite name used in messages, for example `A` or `B`. |
| `CountBots` | Includes bots in the active player count. |
| `BarrierAction` | Action applied when a player enters a barrier: `Wall`, `Teleport`, `Bounce`, `Hurt`, or `Kill`. |
| `HurtDamage` | Damage dealt per second when `BarrierAction` is `Hurt`. |
| `BounceSpeed` | Horizontal speed used by the `Bounce` action. |
| `BounceUpwardVelocity` | Upward velocity used by the `Bounce` action. |
| `BarrierMessage` | Message shown when a player enters a barrier. Supports the `{bombsite}` placeholder. |
| `RoundStartMessage` | Message shown at round start while barriers are active. Supports the `{bombsite}` placeholder. |

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
- The number of active human players, or human players and bots when `CountBots` is enabled, is lower than `MinPlayers`.

When the player count reaches the configured threshold, the barriers are automatically disabled.

## Building from source

Clone the repository and build the project:

```powershell
git clone https://github.com/mihaigsm2003/DynamicBombsiteLimiter.git
cd DynamicBombsiteLimiter
dotnet build -c Release
```

The project targets `.NET 8` and references `CounterStrikeSharp.API`.

## License

This project is licensed under the terms of the [MIT License](LICENSE).
