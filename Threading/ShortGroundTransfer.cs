using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Info.Game.Packets.Upstream;

namespace TreeTools.Threading;

public sealed class ShortGroundTransfer
{
    private Vector3 destination;
    private uint territoryID;
    private uint entityID;
    private long deadline;
    private int arrivedFrames;

    public bool IsPending => deadline != 0;

    public bool TryBegin(Vector3 position)
    {
        var services = DService.Instance();
        if (IsPending || UpstreamOpcode.DiveStartOpcode == 0 || services.ObjectTable.LocalPlayer is not { } player ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
            services.Condition.IsBetweenAreas || services.Condition.IsOccupiedInEvent ||
            services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Mounted] || player.IsCasting ||
            MathF.Abs(player.Position.Y - position.Y) > 1f || Vector3.DistanceSquared(player.Position, position) > 64f)
            return false;
        destination = position;
        territoryID = services.ClientState.TerritoryType;
        entityID = player.EntityID;
        deadline = Environment.TickCount64 + 3_000;
        arrivedFrames = 0;
        new DiveStartPacket(position + new Vector3(0, 2, 0), player.Rotation).Send();
        return true;
    }

    public bool Poll(out bool arrived)
    {
        arrived = false;
        if (!IsPending) return true;
        var services = DService.Instance();
        if (services.ObjectTable.LocalPlayer is not { } player || player.EntityID != entityID ||
            services.ClientState.TerritoryType != territoryID || services.Condition.IsBetweenAreas ||
            services.Condition[ConditionFlag.InCombat] || Environment.TickCount64 >= deadline)
        {
            Cancel();
            return true;
        }
        arrivedFrames = Vector3.DistanceSquared(player.Position, destination) <= 2.25f ? arrivedFrames + 1 : 0;
        if (arrivedFrames < 3) return false;
        arrived = true;
        Cancel();
        return true;
    }

    public void Cancel()
    {
        deadline = 0;
        arrivedFrames = 0;
    }
}
