using System.Collections.Generic;
using Arenas.Arena;

namespace Arenas.Rounds;

/// <summary>
/// Live round-type registry. Owned by <see cref="RoundFlowModule"/>; read by MenusModule and
/// anything that needs the current round-type list without taking a RoundFlowModule dep.
///
/// IDs are process-lifetime counters reset each time <see cref="Reset"/> is called.
/// Do NOT persist round-type IDs — persist by stable <see cref="RoundType.Name"/> (locale key).
/// </summary>
internal sealed class RoundTypeRegistry
{
    private List<RoundType> _types = [];

    /// <summary>Current live list (built-ins + config overrides + registered specials).</summary>
    public IReadOnlyList<RoundType> All => _types;

    /// <summary>
    /// Reset to a fresh base list (built-in defaults or config-driven overrides).
    /// Called in Init and whenever the base config changes.
    /// </summary>
    public void Reset(IEnumerable<RoundType> baseTypes)
        => _types = [.. baseTypes];

    /// <summary>Append a special round type registered by an external addon (OAM phase).</summary>
    public void AppendSpecial(RoundType rt) => _types.Add(rt);

    /// <summary>Remove a special round type by its runtime Id (called on addon shutdown).</summary>
    public void RemoveById(int id) => _types.RemoveAll(r => r.Id == id);
}
