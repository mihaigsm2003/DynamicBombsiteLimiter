namespace DynamicBombsiteLimiter;

public sealed class Config
{
    public bool Enabled { get; set; } = true;
    public bool DisableBarriersDuringWarmup { get; set; } = true;
    public int MinPlayersPerTeam { get; set; } = 5;
    public string BlockedBombsite { get; set; } = "B";
    public bool CountBots { get; set; }
    public bool ShowBarriersPermanently { get; set; } = true;
    public BarrierAction BarrierAction { get; set; } = BarrierAction.Wall;
    public int HurtDamage { get; set; } = 10;
    public float BounceSpeed { get; set; } = 300.0f;
    public float BounceUpwardVelocity { get; set; } = 100.0f;
    public string BarrierMessage { get; set; } = "Nu poti trece de aceasta bariera. Bombsite-ul {bombsite} este inchis.";
    public float RoundStartHudDelaySeconds { get; set; } = 5.0f;
    public int RoundStartHudDurationSeconds { get; set; } = 5;
    public string RoundStartMessage { get; set; } = "Nu se joaca pe toata mapa. Bombsite-ul {bombsite} este inchis in aceasta runda.";
    public string RoundStartOpenMessage { get; set; } = "Se joaca pe toata mapa. Ambele bombsite-uri sunt deschise in aceasta runda.";
}

public enum BarrierAction
{
    Bounce,
    Hurt,
    Kill,
    Teleport,
    Wall
}