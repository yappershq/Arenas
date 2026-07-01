using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace Arenas.Arena;

/// <summary>
/// Clusters map spawn points into 1v1(-NvN) arena pairs. Faithful port of K4's ArenaFinder
/// enemy-pairing union-find algorithm (median-enemy-distance threshold + centroid merge).
///
/// CYBERSHOKE info_teleport_destination compat is NOT ported — that map format is rare and the
/// spawn-replacement dance (disable info_player_*, spawn synthetic ones at teleport-dest origins)
/// adds a lot of surface for zero confirmed usage on our servers.
/// // ponytail: CYBERSHOKE compat stubbed — logs and falls back to normal info_player_* spawns.
/// Revisit only if a map actually needs it.
/// </summary>
internal sealed class ArenaFinder
{
    private const float MergeThresholdFactor = 1.5f;
    private const float Factor               = 1.1f;

    private readonly ILogger          _logger;
    private readonly List<IBaseEntity> _ctSpawns;
    private readonly List<IBaseEntity> _tSpawns;

    public ArenaFinder(ILogger logger, IEntityManager entityManager)
    {
        _logger = logger;

        _ctSpawns = EnumerateByClassname(entityManager, "info_player_counterterrorist");
        _tSpawns  = EnumerateByClassname(entityManager, "info_player_terrorist");

        var teleportDestCount = entityManager.FindEntityByClassname(null, "info_teleport_destination") is not null
            ? CountByClassname(entityManager, "info_teleport_destination")
            : 0;

        if (teleportDestCount > 0)
        {
            // ponytail: CYBERSHOKE-style info_teleport_destination maps are not supported in v1 —
            // fall back to the normal info_player_* spawns already collected above.
            _logger.LogWarning(
                "[Arenas] Detected {Count} info_teleport_destination entities — CYBERSHOKE compat not ported, " +
                "falling back to info_player_terrorist/counterterrorist spawns.", teleportDestCount);
        }

        if (_ctSpawns.Count == 0 || _tSpawns.Count == 0)
            _logger.LogWarning("[Arenas] No spawn points detected on this map (CT={Ct}, T={T}).", _ctSpawns.Count, _tSpawns.Count);
    }

    private static List<IBaseEntity> EnumerateByClassname(IEntityManager entityManager, string classname)
    {
        var list = new List<IBaseEntity>();
        IBaseEntity? cursor = null;
        while ((cursor = entityManager.FindEntityByClassname(cursor, classname)) is not null)
            list.Add(cursor);
        return list;
    }

    private static int CountByClassname(IEntityManager entityManager, string classname)
        => EnumerateByClassname(entityManager, classname).Count;

    /// <summary>Returns (ctSpawns, tSpawns) pairs — one pair per discovered arena.</summary>
    public List<(List<IBaseEntity> Ct, List<IBaseEntity> T)> GetArenaPairs()
    {
        var pairs = GetSpawnPairsUsingEnemyPairing();

        if (pairs.Count > 0)
        {
            var maxPairSize = pairs.Max(p => Math.Min(p.Ct.Count, p.T.Count));
            _logger.LogInformation("[Arenas] Set up {Count} arena(s). Supported modes: {Mode}",
                pairs.Count, maxPairSize > 1 ? $"1v1-{maxPairSize}v{maxPairSize}" : "1v1");
        }
        else
        {
            _logger.LogWarning("[Arenas] No arenas were created — players will not be able to spawn.");
        }

        return pairs;
    }

    private List<(List<IBaseEntity> Ct, List<IBaseEntity> T)> GetSpawnPairsUsingEnemyPairing()
    {
        var allSpawns = new List<IBaseEntity>(_ctSpawns.Count + _tSpawns.Count);
        allSpawns.AddRange(_ctSpawns);
        allSpawns.AddRange(_tSpawns);

        if (allSpawns.Count == 0)
        {
            _logger.LogError("[Arenas] No valid spawn points found for grouping.");
            return [];
        }

        var origins = allSpawns.ToDictionary(e => e, e => e.GetAbsOrigin());
        var classOf = allSpawns.ToDictionary(e => e, e => e.Classname);

        var enemyDistances = new List<float>();
        foreach (var spawn in allSpawns)
        {
            var enemySpawns = allSpawns.Where(s => classOf[s] != classOf[spawn]).ToList();
            if (enemySpawns.Count == 0) continue;
            var minDist = enemySpawns.Min(s => Distance(origins[spawn], origins[s]));
            enemyDistances.Add(minDist);
        }

        if (enemyDistances.Count == 0)
        {
            _logger.LogWarning("[Arenas] Failed to compute enemy distances.");
            return [];
        }

        enemyDistances.Sort();
        var medianEnemyDistance = enemyDistances.Count % 2 == 1
            ? enemyDistances[enemyDistances.Count / 2]
            : (enemyDistances[(enemyDistances.Count / 2) - 1] + enemyDistances[enemyDistances.Count / 2]) / 2;

        var threshold = medianEnemyDistance * Factor;

        var uf     = new UnionFind<IBaseEntity>(allSpawns);
        var ctList = allSpawns.Where(s => classOf[s] == "info_player_counterterrorist").ToList();
        var tList  = allSpawns.Where(s => classOf[s] == "info_player_terrorist").ToList();

        foreach (var ct in ctList)
        foreach (var t in tList)
        {
            if (Distance(origins[ct], origins[t]) <= threshold)
                uf.Union(ct, t);
        }

        var clustersDict = new Dictionary<IBaseEntity, List<IBaseEntity>>();
        foreach (var spawn in allSpawns)
        {
            var root = uf.Find(spawn);
            if (!clustersDict.TryGetValue(root, out var list))
                clustersDict[root] = list = [];
            list.Add(spawn);
        }

        var rawClusters    = clustersDict.Values.ToList();
        var mergeThreshold = threshold * MergeThresholdFactor;
        var mergedClusters = MergeClusters(rawClusters, origins, mergeThreshold);

        var arenaPairs = new List<(List<IBaseEntity> Ct, List<IBaseEntity> T)>();
        foreach (var cluster in mergedClusters)
        {
            var clusterCt = cluster.Where(s => classOf[s] == "info_player_counterterrorist").ToList();
            var clusterT  = cluster.Where(s => classOf[s] == "info_player_terrorist").ToList();
            if (clusterCt.Count > 0 && clusterT.Count > 0)
                arenaPairs.Add((clusterCt, clusterT));
        }

        // Fallback: no valid clusters — dump everything into a single arena.
        if (arenaPairs.Count == 0 && ctList.Count > 0 && tList.Count > 0)
        {
            _logger.LogWarning("[Arenas] No suitable arenas found via clustering — falling back to a single arena with all spawns.");
            arenaPairs.Add((ctList, tList));
        }

        return arenaPairs;
    }

    private static float Distance(Vector a, Vector b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));

    private static Vector Centroid(List<IBaseEntity> cluster, Dictionary<IBaseEntity, Vector> origins)
    {
        float sx = 0, sy = 0, sz = 0;
        foreach (var e in cluster)
        {
            var o = origins[e];
            sx += o.X; sy += o.Y; sz += o.Z;
        }
        var n = cluster.Count;
        return new Vector(sx / n, sy / n, sz / n);
    }

    private static List<List<IBaseEntity>> MergeClusters(
        List<List<IBaseEntity>> clusters, Dictionary<IBaseEntity, Vector> origins, float mergeThreshold)
    {
        bool merged;
        do
        {
            merged = false;
            for (var i = 0; i < clusters.Count && !merged; i++)
            {
                for (var j = i + 1; j < clusters.Count; j++)
                {
                    if (Distance(Centroid(clusters[i], origins), Centroid(clusters[j], origins)) < mergeThreshold)
                    {
                        clusters[i].AddRange(clusters[j]);
                        clusters.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }
        } while (merged);
        return clusters;
    }

    private sealed class UnionFind<T> where T : notnull
    {
        private readonly Dictionary<T, T> _parent;

        public UnionFind(IEnumerable<T> items)
        {
            _parent = [];
            foreach (var item in items)
                _parent[item] = item;
        }

        public T Find(T item)
        {
            var value = _parent[item];
            if (!EqualityComparer<T>.Default.Equals(item, value))
                _parent[item] = Find(value);
            return _parent[item];
        }

        public void Union(T a, T b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (!EqualityComparer<T>.Default.Equals(rootA, rootB))
                _parent[rootB] = rootA;
        }
    }
}
