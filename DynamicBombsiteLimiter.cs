using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector3 = System.Numerics.Vector3;

namespace DynamicBombsiteLimiter;

public sealed class DynamicBombsiteLimiter : BasePlugin
{
    private const float BarrierMessageCooldownSeconds = 2.0f;
    private const float BodyCenterHeight = 36.0f;
    private const float FallbackEyeHeight = 64.0f;
    private const float GrenadeBounceScale = 0.6f;
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
    private readonly HashSet<int> beamViewerSlots = [];
    private readonly List<TrackedProjectile> trackedProjectiles = [];
    private readonly HashSet<string> reportedBadMessages = [];
    private Config config = new();
    private MapConfig mapConfig = new();
    private string mapName = string.Empty;
    private string? roundStartHudKey;
    private Vector3? lastFlashPosition;
    private float roundStartHudExpiresAt;
    private int terroristPlayerCount;
    private int counterTerroristPlayerCount;
    private int roundSequence;
    private bool barrierDrawingScheduled;
    private bool barriersActive;
    private bool mapInitialized;

    public override string ModuleName => "DynamicBombsiteLimiter";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "GSM-RO";
    public override string ModuleDescription => "Configurable logical bombsite barriers.";

    public override void Load(bool hotReload)
    {
        AddCommand("css_dbl_addbarrier", "Starts barrier selection using weapon impacts.", AddBarrierCommand);
        AddCommand("css_dbl_removebarrier", "Removes a barrier: css_dbl_removebarrier <number>.", RemoveBarrierCommand);
        AddCommand("css_dbl_list", "Lists barriers for the current map.", ListBarriersCommand);
        AddCommand("css_dbl_preview", "Displays a wireframe preview of configured barriers.", PreviewCommand);
        AddCommand("css_dbl_save", "Saves the current map configuration.", SaveCommand);
        AddCommand("css_dbl_reload", "Reloads the current map configuration.", ReloadCommand);
        AddCommand("css_dbl_status", "Displays the loaded configuration status.", StatusCommand);

        RegisterEventHandler<EventBulletImpact>(OnBulletImpact);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventFlashbangDetonate>(OnFlashbangDetonate);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        Logger.LogInformation("{PluginName} loaded and waiting for map initialization.", ModuleName);
    }

    public override void Unload(bool hotReload)
    {
        pendingSelections.Clear();
        lastValidPositions.Clear();
        spawnPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        beamViewerSlots.Clear();
        trackedProjectiles.Clear();
        roundStartHudKey = null;
        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.ClearDraw();
        }
    }

    private void AddBarrierCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!HasBarrierCommandPermission(player))
        {
            return;
        }

        if (player is not { IsValid: true, PawnIsAlive: true })
        {
            Reply(player, "Error.MustBeAlive");
            return;
        }

        if (pendingSelections.Remove(player.Slot))
        {
            Reply(player, "Selection.Cancelled");
            return;
        }

        pendingSelections[player.Slot] = new BarrierSelection();
        Reply(player, "Selection.Start");
    }

    private HookResult OnBulletImpact(EventBulletImpact @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true } || !pendingSelections.TryGetValue(player.Slot, out var selection))
        {
            return HookResult.Continue;
        }

        if (!HasBarrierCommandPermission(player))
        {
            pendingSelections.Remove(player.Slot);
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
            Reply(player, "Selection.FirstPoint");
            return HookResult.Continue;
        }

        pendingSelections.Remove(player.Slot);
        var first = selection.FirstPoint;
        var barrier = Barrier.Create($"Barrier_{mapConfig.Barriers.Count + 1}", new Vector(first.X, first.Y, first.Z), point);
        mapConfig.Barriers.Add(barrier);
        if (barriersActive)
        {
            barrier.Draw();
        }

        Reply(player, barriersActive ? "Barrier.CreatedDisplayed" : "Barrier.Created", mapConfig.Barriers.Count, Format(barrier));
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        trackedProjectiles.Clear();
        lastFlashPosition = null;
        lastValidPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        roundStartHudKey = null;
        UpdateBarrierStateForRound();
        if (!barriersActive)
        {
            ClearConfiguredBarrierDrawings();
        }

        var currentRoundSequence = ++roundSequence;
        if (barriersActive && config.ShowBarriersPermanently)
        {
            ScheduleConfiguredBarrierDrawing();
        }

        ScheduleRoundStartAnnouncement(
            currentRoundSequence,
            Math.Max(0.0f, config.RoundStartHudDelaySeconds),
            barriersActive);

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

        if (player is { IsValid: true })
        {
            RefreshBeamViewer(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            beamViewerSlots.Remove(player.Slot);
        }

        return HookResult.Continue;
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (mapConfig.Barriers.All(barrier => barrier.Beams.Count == 0))
        {
            return;
        }

        foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
        {
            if (player is not null && beamViewerSlots.Contains(player.Slot))
            {
                continue;
            }

            foreach (var barrier in mapConfig.Barriers)
            {
                foreach (var beam in barrier.Beams)
                {
                    if (beam.IsValid)
                    {
                        info.TransmitEntities.Remove(beam);
                    }
                }
            }
        }
    }

    private void OnMapStart(string newMapName)
    {
        roundSequence++;
        pendingSelections.Clear();
        beamViewerSlots.Clear();
        trackedProjectiles.Clear();
        lastValidPositions.Clear();
        spawnPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        roundStartHudKey = null;
        LoadMapConfig(newMapName);
        mapInitialized = true;
        barriersActive = false;
    }

    private void OnTick()
    {
        RefreshRoundStartHud();

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

        if (!barriersActive)
        {
            return;
        }

        UpdateProjectiles();

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true } || !IsActiveTeam(player.Team))
            {
                continue;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid || pawn.Health <= 0 || pawn.AbsOrigin is null)
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
        if (!HasBarrierCommandPermission(player))
        {
            return;
        }

        if (command.ArgCount < 2 || !int.TryParse(command.GetArg(1), out var number) || number < 1 || number > mapConfig.Barriers.Count)
        {
            Reply(player, "Barrier.RemoveUsage");
            return;
        }

        mapConfig.Barriers[number - 1].ClearDraw();
        mapConfig.Barriers.RemoveAt(number - 1);
        Reply(player, "Barrier.Removed", number);
    }

    private void ListBarriersCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (mapConfig.Barriers.Count == 0)
        {
            Reply(player, "List.Empty");
            return;
        }

        for (var index = 0; index < mapConfig.Barriers.Count; index++)
        {
            Reply(player, "List.Entry", index + 1, mapConfig.Barriers[index].Name, Format(mapConfig.Barriers[index]));
        }
    }

    private void SaveCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!HasBarrierCommandPermission(player))
        {
            return;
        }

        if (!SaveMapConfig())
        {
            Reply(player, "Save.Failed");
            return;
        }

        Reply(player, "Save.Done", mapName, mapConfig.Barriers.Count);
    }

    private void PreviewCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!barriersActive)
        {
            Reply(player, "Preview.Inactive");
            return;
        }

        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.Draw();
        }

        Reply(player, "Preview.Done", mapConfig.Barriers.Count);
    }

    private void ReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!HasBarrierCommandPermission(player))
        {
            return;
        }

        pendingSelections.Clear();
        lastValidPositions.Clear();
        lastActionTimes.Clear();
        lastBarrierMessageTimes.Clear();
        LoadMapConfig(mapName);
        mapInitialized = !string.IsNullOrWhiteSpace(mapName);
        foreach (var connectedPlayer in Utilities.GetPlayers())
        {
            RefreshBeamViewer(connectedPlayer);
        }

        Reply(player, "Reload.Done", mapName, mapConfig.Barriers.Count);
    }

    private bool HasBarrierCommandPermission(CCSPlayerController? player)
    {
        if (player is null)
        {
            return true;
        }

        if (!player.IsValid)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.BarrierCommandPermission))
        {
            Logger.LogError("DynamicBombsiteLimiter.BarrierCommandPermission is empty; barrier management commands are denied.");
            Reply(player, "Perm.NotConfigured");
            return false;
        }

        if (AdminManager.PlayerHasPermissions(player, config.BarrierCommandPermission.Trim()))
        {
            return true;
        }

        Reply(player, "Perm.Denied");
        return false;
    }

    private bool IsBarrierAdmin(CCSPlayerController player) =>
        player is { IsValid: true, IsBot: false } &&
        !string.IsNullOrWhiteSpace(config.BarrierCommandPermission) &&
        AdminManager.PlayerHasPermissions(player, config.BarrierCommandPermission.Trim());

    private void RefreshBeamViewer(CCSPlayerController player)
    {
        if (IsBarrierAdmin(player))
        {
            beamViewerSlots.Add(player.Slot);
        }
        else
        {
            beamViewerSlots.Remove(player.Slot);
        }
    }

    private void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        Reply(player, "Status.Line", terroristPlayerCount, counterTerroristPlayerCount, config.MinPlayersPerTeam, config.BlockedBombsite, config.BarrierAction, barriersActive ? "LOCKED" : "UNLOCKED", barriersActive ? mapConfig.Barriers.Count : 0);
    }

    private void LoadMapConfig(string? requestedMapName = null)
    {
        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.ClearDraw();
        }

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
            if (config.ShowBarriersPermanently)
            {
                ScheduleConfiguredBarrierDrawing();
            }
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

    private void UpdateBarrierStateForRound()
    {
        (terroristPlayerCount, counterTerroristPlayerCount) = CountAlivePlayersByTeam();
        var isWarmup = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules?.WarmupPeriod == true;
        barriersActive = config.Enabled &&
            !(config.DisableBarriersDuringWarmup && isWarmup) &&
            mapConfig.Barriers.Count > 0 &&
            (terroristPlayerCount < config.MinPlayersPerTeam || counterTerroristPlayerCount < config.MinPlayersPerTeam);
        lastValidPositions.Clear();
        Logger.LogInformation("DynamicBombsiteLimiter barriers for {MapName} are {State} for this round. Alive players at round start: T={TerroristCount}, CT={CounterTerroristCount}; required per team: {MinPlayersPerTeam}; mode: {BarrierAction}.", mapName, barriersActive ? "LOCKED" : "UNLOCKED", terroristPlayerCount, counterTerroristPlayerCount, config.MinPlayersPerTeam, config.BarrierAction);
    }

    private (int Terrorists, int CounterTerrorists) CountAlivePlayersByTeam()
    {
        var terrorists = 0;
        var counterTerrorists = 0;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !player.PawnIsAlive || (!config.CountBots && player.IsBot))
            {
                continue;
            }

            if (player.Team == CsTeam.Terrorist)
            {
                terrorists++;
            }
            else if (player.Team == CsTeam.CounterTerrorist)
            {
                counterTerrorists++;
            }
        }

        return (terrorists, counterTerrorists);
    }

    private HookResult OnFlashbangDetonate(EventFlashbangDetonate @event, GameEventInfo info)
    {
        lastFlashPosition = new Vector3(@event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        if (barriersActive && @event.Userid is { IsValid: true } player)
        {
            // The detonate event may fire after this one, so wait until both have run.
            Server.NextFrame(() => CancelFlashAcrossBarrier(player));
        }

        return HookResult.Continue;
    }

    private void CancelFlashAcrossBarrier(CCSPlayerController player)
    {
        if (!player.IsValid || lastFlashPosition is not { } flash)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || GetEyePosition(pawn) is not { } eye || !CrossesBarrier(flash, eye))
        {
            return;
        }

        pawn.FlashDuration = 0.0f;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashDuration");
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (entity.DesignerName.EndsWith("_projectile", StringComparison.Ordinal))
        {
            trackedProjectiles.Add(new TrackedProjectile(entity.EntityHandle));
        }
    }

    private void UpdateProjectiles()
    {
        if (trackedProjectiles.Count == 0)
        {
            return;
        }

        var boxes = mapConfig.Barriers.Select(barrier => barrier.ToBox()).ToList();
        for (var index = trackedProjectiles.Count - 1; index >= 0; index--)
        {
            var tracked = trackedProjectiles[index];
            var entity = tracked.Handle.Value;
            if (entity is null || !entity.IsValid)
            {
                trackedProjectiles.RemoveAt(index);
                continue;
            }

            var projectile = entity.As<CBaseCSGrenadeProjectile>();
            if (projectile.AbsOrigin is not { } origin)
            {
                continue;
            }

            var current = ToNumerics(origin);
            if (tracked.LastPosition is not { } last)
            {
                tracked.LastPosition = current;
                continue;
            }

            if (BarrierProtection.FindNearestHit(last, current, boxes) is not { } hit)
            {
                tracked.LastPosition = current;
                continue;
            }

            var bounced = BarrierProtection.Reflect(ToNumerics(projectile.AbsVelocity), hit.Normal, GrenadeBounceScale);
            projectile.Teleport(new Vector(last.X, last.Y, last.Z), velocity: new Vector(bounced.X, bounced.Y, bounced.Z));
        }
    }

    private HookResult OnPlayerTakeDamagePre(CCSPlayerPawn victim, CTakeDamageInfo info)
    {
        if (!barriersActive || victim.AbsOrigin is not { } victimOrigin)
        {
            return HookResult.Continue;
        }

        var bodyCenter = ToNumerics(victimOrigin) + new Vector3(0, 0, BodyCenterHeight);
        Vector3 start;
        Vector3 end;
        var inflictor = info.Inflictor.Value;
        if (inflictor is { IsValid: true } && IsAreaDamageSource(inflictor.DesignerName))
        {
            if (inflictor.AbsOrigin is not { } inflictorOrigin)
            {
                return HookResult.Continue;
            }

            start = ToNumerics(inflictorOrigin);
            end = bodyCenter;
        }
        else
        {
            if (info.Attacker.Value is not { IsValid: true, DesignerName: "player" } attacker ||
                attacker.Handle == victim.Handle ||
                !IsDirectWeaponDamage(inflictor, attacker) ||
                GetEyePosition(attacker.As<CCSPlayerPawn>()) is not { } eye)
            {
                return HookResult.Continue;
            }

            var damagePosition = ToNumerics(info.DamagePosition);
            start = eye;
            end = damagePosition == Vector3.Zero ? bodyCenter : damagePosition;
        }

        return CrossesBarrier(start, end) ? HookResult.Handled : HookResult.Continue;
    }

    private static bool IsDirectWeaponDamage(CBaseEntity? inflictor, CBaseEntity attacker) =>
        inflictor is { IsValid: true } &&
        (inflictor.Handle == attacker.Handle ||
         inflictor.DesignerName.StartsWith("weapon_", StringComparison.Ordinal));

    private static bool IsAreaDamageSource(string designerName) =>
        designerName == "inferno" || designerName.EndsWith("_projectile", StringComparison.Ordinal);

    private bool CrossesBarrier(Vector3 start, Vector3 end) =>
        BarrierProtection.FindNearestHit(start, end, mapConfig.Barriers.Select(barrier => barrier.ToBox())) is not null;

    private static Vector3? GetEyePosition(CCSPlayerPawn pawn)
    {
        if (pawn.AbsOrigin is not { } origin)
        {
            return null;
        }

        var eyeHeight = pawn.ViewOffset.Z > 0 ? pawn.ViewOffset.Z : FallbackEyeHeight;
        return ToNumerics(origin) + new Vector3(0, 0, eyeHeight);
    }

    private static Vector3 ToNumerics(Vector vector) => new(vector.X, vector.Y, vector.Z);

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
            Reply(player, "Barrier.Blocked", config.BlockedBombsite);
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

    private void SendRoundStartHud(CCSPlayerController player, string key) =>
        player.PrintToCenterHtml(ResolveMessage(player, key, config.BlockedBombsite));

    private void ScheduleRoundStartAnnouncement(int roundId, float delaySeconds, bool restricted)
    {
        AddTimer(delaySeconds, () =>
        {
            if (roundId != roundSequence)
            {
                return;
            }

            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsValid && IsActiveTeam(player.Team))
                {
                    Reply(player, restricted ? "Round.Restricted" : "Round.Open", config.BlockedBombsite);
                }
            }

            roundStartHudKey = restricted ? "Hud.Restricted" : "Hud.Open";
            roundStartHudExpiresAt = Server.CurrentTime + Math.Clamp(config.RoundStartHudDurationSeconds, 1, 10);
        });
    }

    private void RefreshRoundStartHud()
    {
        if (roundStartHudKey is null)
        {
            return;
        }

        if (Server.CurrentTime >= roundStartHudExpiresAt)
        {
            roundStartHudKey = null;
            return;
        }

        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsValid && IsActiveTeam(player.Team))
            {
                SendRoundStartHud(player, roundStartHudKey);
            }
        }
    }

    private void ScheduleConfiguredBarrierDrawing()
    {
        if (barrierDrawingScheduled)
        {
            return;
        }

        barrierDrawingScheduled = true;
        AddTimer(1.0f, () =>
        {
            barrierDrawingScheduled = false;
            if (mapInitialized && barriersActive && config.ShowBarriersPermanently)
            {
                DrawConfiguredBarriers();
            }
            else if (!barriersActive)
            {
                ClearConfiguredBarrierDrawings();
            }
        });
    }

    private void DrawConfiguredBarriers()
    {
        if (!barriersActive)
        {
            ClearConfiguredBarrierDrawings();
            return;
        }

        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.Draw();
        }
    }

    private void ClearConfiguredBarrierDrawings()
    {
        foreach (var barrier in mapConfig.Barriers)
        {
            barrier.ClearDraw();
        }
    }

    private void Reply(CCSPlayerController? player, string key, params object[] args)
    {
        var message = ResolveMessage(player, key, args);
        if (player is null)
        {
            Logger.LogInformation("[DBL] {Message}", MessageFormatter.StripForConsole(message));
            return;
        }

        player.PrintToChat(MessageFormatter.BuildChat(ResolveMessage(player, "Prefix"), message));
    }

    private string ResolveMessage(CCSPlayerController? player, string key, params object[] args)
    {
        // ForPlayer throws for bots and other players with no SteamID.
        var raw = player is null || player.IsBot || player.SteamID == 0
            ? Localizer[key].Value
            : Localizer.ForPlayer(player, key);

        // ReplaceColorTags adds one leading space, and BuildChat adds its own.
        var colored = raw.ReplaceColorTags().Trim();
        if (args.Length == 0)
        {
            return colored;
        }

        if (!MessageFormatter.TryFormat(colored, args, out var formatted) && reportedBadMessages.Add(key))
        {
            Logger.LogError("Lang key {Key} has invalid placeholders. Check its braces.", key);
        }

        return formatted;
    }

    private sealed class TrackedProjectile(CHandle<CEntityInstance> handle)
    {
        public CHandle<CEntityInstance> Handle { get; } = handle;
        public Vector3? LastPosition { get; set; }
    }

    private sealed class BarrierSelection
    {
        public VectorData? FirstPoint { get; set; }
        public float LastShot { get; set; }
    }
}