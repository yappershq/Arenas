using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Arenas.Config;

public sealed class CommandSettings
{
    [JsonPropertyName("gun-pref-commands")]     public List<string> GunsCommands            { get; set; } = ["guns", "gunpref", "weaponpref"];
    [JsonPropertyName("round-pref-commands")]   public List<string> RoundsCommands          { get; set; } = ["rounds", "roundpref"];
    [JsonPropertyName("queue-commands")]        public List<string> QueueCommands           { get; set; } = ["queue"];
    [JsonPropertyName("afk-commands")]          public List<string> AfkCommands             { get; set; } = ["afk"];
    [JsonPropertyName("challenge-commands")]    public List<string> ChallengeCommands       { get; set; } = ["challenge", "duel"];
    [JsonPropertyName("challenge-accept-commands")] public List<string> ChallengeAcceptCommands  { get; set; } = ["caccept", "capprove"];
    [JsonPropertyName("challenge-decline-commands")] public List<string> ChallengeDeclineCommands { get; set; } = ["cdecline", "cdeny"];
    [JsonPropertyName("center-menu-mode")]      public bool CenterMenuMode      { get; set; } = true;
    [JsonPropertyName("center-announce-mode")]  public bool CenterAnnounceMode  { get; set; } = true;
    [JsonPropertyName("freeze-in-center-menu")] public bool FreezeInMenu       { get; set; } = true;
}

public sealed class CompatibilitySettings
{
    [JsonPropertyName("force-arena-clantags")]        public bool ForceArenaClantags       { get; set; }
    [JsonPropertyName("block-flash-of-not-opponent")] public bool BlockFlashOfNotOpponent  { get; set; }
    [JsonPropertyName("block-damage-of-not-opponent")] public bool BlockDamageOfNotOpponent { get; set; }
    [JsonPropertyName("give-knife-by-default")]       public bool GiveKnifeByDefault       { get; set; } = true;
    [JsonPropertyName("disable-clantags")]            public bool DisableClantags          { get; set; }
    [JsonPropertyName("prevent-draw-rounds")]         public bool PreventDrawRounds        { get; set; } = true;
}

public sealed class AllowedWeaponPreferences
{
    [JsonPropertyName("rifle")]   public bool Rifle   { get; set; } = true;
    [JsonPropertyName("sniper")]  public bool Sniper  { get; set; } = true;
    [JsonPropertyName("smg")]     public bool Smg     { get; set; } = true;
    [JsonPropertyName("lmg")]     public bool Lmg     { get; set; } = true;
    [JsonPropertyName("shotgun")] public bool Shotgun { get; set; } = true;
    [JsonPropertyName("pistol")]  public bool Pistol  { get; set; } = true;
}

public sealed class DefaultWeaponSettings
{
    [JsonPropertyName("default-rifle")]   public string? DefaultRifle   { get; set; }
    [JsonPropertyName("default-sniper")]  public string? DefaultSniper  { get; set; }
    [JsonPropertyName("default-smg")]     public string? DefaultSmg     { get; set; }
    [JsonPropertyName("default-lmg")]     public string? DefaultLmg     { get; set; }
    [JsonPropertyName("default-shotgun")] public string? DefaultShotgun { get; set; }
    [JsonPropertyName("default-pistol")]  public string? DefaultPistol  { get; set; }
    [JsonPropertyName("default-round")]   public string  DefaultRound   { get; set; } = "Arenas_Round_Rifle";
}

/// <summary>Config-driven round type entry (port of K4 RoundTypeReader). See RoundTypeCatalog for the 12 built-ins.</summary>
public sealed class RoundTypeSettings
{
    [JsonPropertyName("translation-name")]         public required string TranslationName { get; set; }
    [JsonPropertyName("team-size")]                public int    TeamSize              { get; set; } = 1;
    [JsonPropertyName("primary-weapon")]           public string? PrimaryWeapon         { get; set; }
    [JsonPropertyName("secondary-weapon")]         public string? SecondaryWeapon       { get; set; }
    [JsonPropertyName("use-preferred-primary")]    public bool   UsePreferredPrimary    { get; set; }
    [JsonPropertyName("primary-preference")]       public Shared.WeaponType? PrimaryPreference { get; set; }
    [JsonPropertyName("use-preferred-secondary")]  public bool   UsePreferredSecondary  { get; set; }
    [JsonPropertyName("armor")]                    public bool   Armor                 { get; set; } = true;
    [JsonPropertyName("helmet")]                   public bool   Helmet                { get; set; } = true;
    [JsonPropertyName("enabled-by-default")]       public bool   EnabledByDefault      { get; set; } = true;
}

public sealed class ArenasConfig
{
    [JsonPropertyName("command-settings")]        public CommandSettings          CommandSettings        { get; set; } = new();
    [JsonPropertyName("compatibility-settings")]  public CompatibilitySettings    CompatibilitySettings  { get; set; } = new();
    [JsonPropertyName("default-weapon-settings")] public DefaultWeaponSettings    DefaultWeaponSettings  { get; set; } = new();
    [JsonPropertyName("allowed-weapon-prefs")]    public AllowedWeaponPreferences AllowedWeaponPreferences { get; set; } = new();

    /// <summary>Empty = use built-in RoundTypeCatalog.Defaults(). Non-empty overrides them entirely (matches K4).</summary>
    [JsonPropertyName("round-settings")] public List<RoundTypeSettings> RoundSettings { get; set; } = [];
}
