using Arenas.Plugins;
using Microsoft.Extensions.Logging;

namespace Arenas.Queue;

internal sealed class QueueModule : IModule
{
    public QueueManager     QueueManager     { get; }
    public ChallengeService ChallengeService { get; } = new();

    public QueueModule(InterfaceBridge bridge)
        => QueueManager = new QueueManager(bridge.LoggerFactory.CreateLogger<QueueManager>());

    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        // The arena ladder is fully SELF-CONTAINED: climb by winning your duel, drop by losing
        // (QueueManager.BuildRankedQueue). No external ranking dependency — LevelRanks (if installed)
        // runs out of the box awarding its own global points from kills; Arenas needs no integration.
        //
        // ponytail: optional-only seam. If arena SEEDING-by-global-rank is ever wanted, resolve
        // LevelRank's IRequestManager here via SharpModuleManager.GetOptionalSharpModuleInterface by
        // its Identity ("LevelRank.IRequestManager") and order the initial queue by RankInfo.Score;
        // fall back to internal FIFO when absent. Deliberately NOT built now (YAGNI + avoids a foreign
        // .Shared build dependency) — the initial-order seam is right here when needed.
    }

    public void Shutdown() { }
}
