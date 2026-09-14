using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace DynamicBombsiteLimiter;

public sealed class DynamicBombsiteLimiter : BasePlugin
{
    private const int PlayerCountUpdateIntervalTicks = 16;
    private const float BarrierMessageCooldownSeconds = 2.0f;
    private const string ChatPrefix = " \x04[DBL]\x01";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly Dictionary<int, BarrierSelection> pendingSelections = [];
    private readonly Dictionary<int, VectorData> lastValidPositions = [];
    private readonly Dictionary<int, VectorData> spawnPositions = [];
    private readonly Dictionary<int, float> lastActionTimes = [];
    private readonly Dictionary<int, float> lastBarrierMessageTimes = [];
    private Config config = new();
    private MapConfig mapConfig = new();
    private string mapName = string.Empty;
    private int activePlayerCount;
    private int ticksUntilPlayerCountUpdate;
    private bool barriersActive;
    private bool mapInitialized;

    public override string ModuleName => "DynamicBombsiteLimiter";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "MihaiGsm2003";
    public override string ModuleDescription => "Configurable logical bombsite barriers.";

    public override void Load(bool hotReload)
    {
        AddCommand("css_dbl_addbarrier", "Porneste selectarea cu impacturile armei.", AddBarrierCommand);
        AddCommand("css_dbl_removebarrier", "Sterge o bariera: css_dbl_removebarrier <numar>.", RemoveBarrierCommand);
        AddCommand("css_dbl_list", "Afiseaza barierele hartii curente.", ListBarriersCommand);
        AddCommand("css_dbl_preview", "Afiseaza wireframe-ul barierelor configurate.", PreviewCommand);
        AddCommand("css_dbl_save", "Salveaza configuratia hartii curente.", SaveCommand);
        AddCommand("css_dbl_reload", "Reincarca configuratia hartii curente.", ReloadCommand);
        AddCommand("css_dbl_status", "Afiseaza configuratia incarcata.", StatusCommand);

        RegisterEventHandler<EventBulletImpact>(OnBulletImpact);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnTick>(OnTick);
        Logger.LogInformation("{PluginName} loaded and waiting for map initialization.", ModuleName);
    }

    public override void Unload(bool hotReload)
    {
        pendingSelections.Clear();
        lastValidPositions.Clear();
        spawnPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.ClearDraw();
        }
    }

    private void AddBarrierCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true, PawnIsAlive: true })
        {
            Reply(player, "Comanda trebuie executata de un jucator viu.");
            return;
        }

        if (pendingSelections.Remove(player.Slot))
        {
            Reply(player, "Selectarea barierei a fost anulata.");
            return;
        }

        pendingSelections[player.Slot] = new BarrierSelection();
        Reply(player, "Tinteste primul colt si trage o data. Apoi tinteste coltul opus si trage din nou.");
    }

    private HookResult OnBulletImpact(EventBulletImpact @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true } || !pendingSelections.TryGetValue(player.Slot, out var selection))
        {
            return HookResult.Continue;
        }

        if (Server.CurrentTime - selection.LastShot < 0.1f)
        {
            return HookResult.Continue;
        }

        selection.LastShot = Server.CurrentTime;
        var point = new Vector(@event.X, @event.Y, @event.Z);
        if (selection.FirstPoint is null)
        {
            selection.FirstPoint = new VectorData(point.X, point.Y, point.Z);
            Reply(player, "Primul punct a fost selectat. Tinteste al doilea colt si trage din nou.");
            return HookResult.Continue;
        }

        pendingSelections.Remove(player.Slot);
        var first = selection.FirstPoint;
        var barrier = Barrier.Create($"Barrier_{mapConfig.Barriers.Count + 1}", new Vector(first.X, first.Y, first.Z), point);
        mapConfig.Barriers.Add(barrier);
        barrier.Draw();
        Reply(player, $"Bariera #{mapConfig.Barriers.Count} creata si afisata: {Format(barrier)}");
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        lastValidPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        UpdateBarrierState();
        if (barriersActive)
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsValid && IsActiveTeam(player.Team))
                {
                    SendConfiguredMessage(player, config.RoundStartMessage);
                }
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        var origin = player?.PlayerPawn.Value?.AbsOrigin;
        if (player is { IsValid: true } && origin is not null)
        {
            spawnPositions[player.Slot] = new VectorData(origin.X, origin.Y, origin.Z);
        }

        return HookResult.Continue;
    }

    private void OnMapStart(string newMapName)
    {
        pendingSelections.Clear();
        lastValidPositions.Clear();
        spawnPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        LoadMapConfig(newMapName);
        mapInitialized = true;
        UpdateBarrierState();
    }

    private void OnTick()
    {
        if (!mapInitialized)
        {
            if (!TrySetMapName(Server.MapName))
            {
                return;
            }

            LoadMapConfig(mapName);
            mapInitialized = true;
            Logger.LogInformation("{PluginName} initialized for map {MapName} with {BarrierCount} configured barriers.", ModuleName, mapName, mapConfig.Barriers.Count);
        }

        if (ticksUntilPlayerCountUpdate-- <= 0)
        {
            ticksUntilPlayerCountUpdate = PlayerCountUpdateIntervalTicks;
            UpdateBarrierState();
        }

        if (!barriersActive)
        {
            return;
        }

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, PawnIsAlive: true } || !IsActiveTeam(player.Team))
            {
                continue;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid || pawn.AbsOrigin is null)
            {
                continue;
            }

            var origin = pawn.AbsOrigin;
            if (IsInsideAnyBarrier(origin))
            {
                ApplyBarrierAction(player, pawn);

                continue;
            }

            lastValidPositions[player.Slot] = new VectorData(origin.X, origin.Y, origin.Z);
        }
    }

    private void RemoveBarrierCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2 || !int.TryParse(command.GetArg(1), out var number) || number < 1 || number > mapConfig.Barriers.Count)
        {
            Reply(player, "Utilizare: !dbl_removebarrier <numar>.");
            return;
        }

        mapConfig.Barriers[number - 1].ClearDraw();
        mapConfig.Barriers.RemoveAt(number - 1);
        Reply(player, $"Bariera #{number} a fost stearsa. Ruleaza !dbl_save pentru persistenta.");
    }

    private void ListBarriersCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (mapConfig.Barriers.Count == 0)
        {
            Reply(player, "Nu exista bariere configurate pentru aceasta harta.");
            return;
        }

        for (var index = 0; index < mapConfig.Barriers.Count; index++)
        {
            Reply(player, $"Bariera #{index + 1} ({mapConfig.Barriers[index].Name}): {Format(mapConfig.Barriers[index])}");
        }
    }

    private void SaveCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!SaveMapConfig())
        {
            Reply(player, "Configuratia nu poate fi salvata: numele hartii nu este disponibil inca.");
            return;
        }

        Reply(player, $"Configuratia pentru {mapName} a fost salvata ({mapConfig.Barriers.Count} bariere).");
    }

    private void PreviewCommand(CCSPlayerController? player, CommandInfo command)
    {
        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.Draw();
        }

        Reply(player, $"Au fost afisate {mapConfig.Barriers.Count} bariere. Foloseste !dbl_preview din nou pentru a reimprospata afisarea.");
    }

    private void ReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        pendingSelections.Clear();
        lastValidPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        LoadMapConfig(mapName);
        mapInitialized = !string.IsNullOrWhiteSpace(mapName);
        UpdateBarrierState();
        Reply(player, $"Configuratia pentru {mapName} a fost reincarcata ({mapConfig.Barriers.Count} bariere).");
    }

    private void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        Reply(player, $"Players: {activePlayerCount}; Required: {config.MinPlayers}; Bombsite: {config.BlockedBombsite}; Mode: {config.BarrierAction}; Status: {(barriersActive ? "LOCKED" : "UNLOCKED")}; Active barriers: {(barriersActive ? mapConfig.Barriers.Count : 0)}.");
    }

    private void LoadMapConfig(string? requestedMapName = null)
    {
        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.ClearDraw();
        }

        ticksUntilPlayerCountUpdate = 0;
        MigrateLegacyConfigs();
        LoadGlobalConfig();
        if (!TrySetMapName(requestedMapName ?? Server.MapName))
        {
            mapConfig = new MapConfig();
            Logger.LogWarning("DynamicBombsiteLimiter did not load a map configuration because the current map name is empty.");
            return;
        }

        var path = GetMapConfigPath();
        if (!File.Exists(path))
        {
            mapConfig = new MapConfig();
            return;
        }

        try
        {
            mapConfig = JsonSerializer.Deserialize<MapConfig>(File.ReadAllText(path), JsonOptions) ?? new MapConfig();
        }
        catch (JsonException exception)
        {
            mapConfig = new MapConfig();
            Logger.LogError(exception, "Could not parse DynamicBombsiteLimiter map configuration {ConfigPath}.", path);
        }
    }

    private bool SaveMapConfig()
    {
        if (!TrySetMapName(mapName))
        {
            return false;
        }

        var path = GetMapConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(mapConfig, JsonOptions));
        return true;
    }

    private string GetMapConfigPath() => Path.Combine(GetConfigDirectory(), "maps", $"{mapName}.json");

    private void LoadGlobalConfig()
    {
        var configDirectory = GetConfigDirectory();
        var path = Path.Combine(configDirectory, "config.json");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
            return;
        }

        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions) ?? new Config();
        }
        catch (JsonException exception)
        {
            config = new Config();
            Logger.LogError(exception, "Could not parse DynamicBombsiteLimiter global configuration {ConfigPath}.", path);
        }
    }

    private string GetConfigDirectory() => Path.Combine(
        GetCsgoDirectory(),
        "addons",
        "counterstrikesharp",
        "configs",
        "plugins",
        ModuleName);

    private static string GetCsgoDirectory()
    {
        var gameDirectory = Path.TrimEndingDirectorySeparator(Server.GameDirectory);
        return string.Equals(Path.GetFileName(gameDirectory), "csgo", StringComparison.OrdinalIgnoreCase)
            ? gameDirectory
            : Path.Combine(gameDirectory, "csgo");
    }

    private void MigrateLegacyConfigs()
    {
        var configDirectory = GetConfigDirectory();
        Directory.CreateDirectory(configDirectory);

        MigrateConfigLocation(ModuleDirectory, configDirectory);

        var incorrectConfigDirectory = Path.Combine(
            Server.GameDirectory,
            "addons",
            "counterstrikesharp",
            "configs",
            "plugins",
            ModuleName);
        MigrateConfigLocation(incorrectConfigDirectory, configDirectory);
    }

    private void MigrateConfigLocation(string sourceDirectory, string destinationDirectory)
    {
        if (string.Equals(
            Path.GetFullPath(sourceDirectory),
            Path.GetFullPath(destinationDirectory),
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MoveLegacyFile(
            Path.Combine(sourceDirectory, "config.json"),
            Path.Combine(destinationDirectory, "config.json"));

        var legacyMapsDirectory = Path.Combine(sourceDirectory, "maps");
        if (!Directory.Exists(legacyMapsDirectory))
        {
            return;
        }

        var mapsDirectory = Path.Combine(destinationDirectory, "maps");
        Directory.CreateDirectory(mapsDirectory);
        foreach (var legacyMapPath in Directory.EnumerateFiles(legacyMapsDirectory, "*.json"))
        {
            MoveLegacyFile(legacyMapPath, Path.Combine(mapsDirectory, Path.GetFileName(legacyMapPath)));
        }
    }

    private void MoveLegacyFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
        {
            return;
        }

        try
        {
            File.Move(sourcePath, destinationPath);
            Logger.LogInformation("Migrated DynamicBombsiteLimiter configuration from {SourcePath} to {DestinationPath}.", sourcePath, destinationPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(exception, "Could not migrate DynamicBombsiteLimiter configuration from {SourcePath} to {DestinationPath}.", sourcePath, destinationPath);
        }
    }

    private bool TrySetMapName(string? requestedMapName)
    {
        var normalizedMapName = Path.GetFileNameWithoutExtension(requestedMapName?.Trim());
        if (string.IsNullOrWhiteSpace(normalizedMapName))
        {
            return false;
        }

        mapName = normalizedMapName;
        return true;
    }

    private void UpdateBarrierState()
    {
        activePlayerCount = CountActivePlayers();
        var shouldActivate = config.Enabled && mapConfig.Barriers.Count > 0 && activePlayerCount < config.MinPlayers;
        if (shouldActivate == barriersActive)
        {
            return;
        }

        barriersActive = shouldActivate;
        lastValidPositions.Clear();
        Logger.LogInformation("DynamicBombsiteLimiter barriers for {MapName} are now {State}. Active players: {PlayerCount}; threshold: {MinPlayers}; mode: {BarrierAction}.", mapName, barriersActive ? "LOCKED" : "UNLOCKED", activePlayerCount, config.MinPlayers, config.BarrierAction);
    }

    private int CountActivePlayers()
    {
        var count = 0;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !IsActiveTeam(player.Team) || (!config.CountBots && player.IsBot))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private bool IsInsideAnyBarrier(Vector position)
    {
        foreach (var barrier in mapConfig.Barriers)
        {
            if (barrier.IntersectsPlayer(position))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyBarrierAction(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (!lastBarrierMessageTimes.TryGetValue(player.Slot, out var lastMessageTime) ||
            Server.CurrentTime - lastMessageTime >= BarrierMessageCooldownSeconds)
        {
            lastBarrierMessageTimes[player.Slot] = Server.CurrentTime;
            SendConfiguredMessage(player, config.BarrierMessage);
        }

        switch (config.BarrierAction)
        {
            case BarrierAction.Bounce:
                ApplyBounce(player, pawn);
                break;

            case BarrierAction.Hurt:
                ApplyHurt(player, pawn);
                break;

            case BarrierAction.Kill:
                pawn.CommitSuicide(true, true);
                break;

            case BarrierAction.Teleport:
                if (spawnPositions.TryGetValue(player.Slot, out var spawnPosition))
                {
                    pawn.Teleport(new Vector(spawnPosition.X, spawnPosition.Y, spawnPosition.Z), velocity: Vector.Zero);
                }
                else
                {
                    ApplyWall(player, pawn);
                }
                break;

            case BarrierAction.Wall:
                ApplyWall(player, pawn);
                break;
        }
    }

    private void ApplyWall(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (lastValidPositions.TryGetValue(player.Slot, out var lastValidPosition))
        {
            pawn.Teleport(new Vector(lastValidPosition.X, lastValidPosition.Y, lastValidPosition.Z), velocity: Vector.Zero);
        }
    }

    private void ApplyBounce(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (!lastValidPositions.TryGetValue(player.Slot, out var lastValidPosition))
        {
            return;
        }

        var velocity = pawn.AbsVelocity;
        var horizontalSpeed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        var bounceVelocity = horizontalSpeed > 0.01f
            ? new Vector(-velocity.X / horizontalSpeed * config.BounceSpeed, -velocity.Y / horizontalSpeed * config.BounceSpeed, config.BounceUpwardVelocity)
            : new Vector(0, 0, config.BounceUpwardVelocity);

        pawn.Teleport(new Vector(lastValidPosition.X, lastValidPosition.Y, lastValidPosition.Z), velocity: bounceVelocity);
    }

    private void ApplyHurt(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (config.HurtDamage <= 0 || (lastActionTimes.TryGetValue(player.Slot, out var lastActionTime) && Server.CurrentTime - lastActionTime < 1.0f))
        {
            return;
        }

        lastActionTimes[player.Slot] = Server.CurrentTime;
        pawn.Health -= config.HurtDamage;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private static bool IsActiveTeam(CsTeam team) => team is CsTeam.CounterTerrorist or CsTeam.Terrorist;

    private static string Format(Barrier barrier) =>
        $"Min: {barrier.Min.X:F1} {barrier.Min.Y:F1} {barrier.Min.Z:F1}; Max: {barrier.Max.X:F1} {barrier.Max.Y:F1} {barrier.Max.Z:F1}";

    private void SendConfiguredMessage(CCSPlayerController player, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Reply(player, message.Replace("{bombsite}", config.BlockedBombsite, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Reply(CCSPlayerController? player, string message)
    {
        if (player is null)
        {
            Logger.LogInformation("[DBL] {Message}", message);
            return;
        }

        player.PrintToChat($"{ChatPrefix} {message}");
    }

    private sealed class BarrierSelection
    {
        public VectorData? FirstPoint { get; set; }
        public float LastShot { get; set; }
    }
}