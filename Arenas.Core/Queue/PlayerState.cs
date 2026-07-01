namespace Arenas.Queue;

/// <summary>
/// Ephemeral per-player arena state. Slot-indexed arrays own the AFK/queue-position bookkeeping;
/// this record is the per-slot payload held inside those arrays (round + weapon prefs are looked up
/// live from cookies via PreferencesModule instead of being cached here, since cookies are the
/// SteamID64-keyed source of truth and slots get reused across reconnects).
/// </summary>
internal sealed class PlayerState
{
    public bool   Afk;
    public string ArenaTag = string.Empty;
    public int    Mvps;
}
