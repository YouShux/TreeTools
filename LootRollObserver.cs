using System.Threading;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.OmenService;

namespace TreeTools;

/// <summary>在已核对的国服协议版本中，只读复制战利品更新、选择和结算字段。</summary>
public sealed unsafe class LootRollObserver : IDisposable
{
    private readonly Action<LootRollNotice> callback;
    private int active;

    /// <summary>注册只读接收回调；未知客户端版本保持未注册状态。</summary>
    /// <param name="callback">接收已复制的托管字段，不得保留原生数据。</param>
    public LootRollObserver(Action<LootRollNotice> callback)
    {
        this.callback = callback;
        var framework = Framework.Instance();
        IsSupported = framework != null && framework->GameVersionString == SUPPORTED_VERSION;
        if (!IsSupported)
            return;
        active = 1;
        if (!GamePacketManager.Instance().RegPostReceivePacket(OnPacket))
        {
            active = 0;
            throw new InvalidOperationException("Loot observation registration failed.");
        }
    }

    /// <summary>当前客户端是否使用已验证的战利品协议布局。</summary>
    public bool IsSupported { get; }

    /// <summary>立即停止观察并解绑接收回调，不向游戏发送任何请求。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref active, 0) != 0)
            GamePacketManager.Instance().Unreg((GamePacketManager.PostReceivePacketDelegate)OnPacket);
    }

    private void OnPacket(int opcode, nint packet)
    {
        if (Volatile.Read(ref active) == 0 || packet == 0)
            return;
        try
        {
            var data = (byte*)packet + 0x20;
            var notice = new LootRollNotice { Time = DateTimeOffset.UtcNow };
            switch (opcode)
            {
                case 0x14E:
                    notice = notice with
                    {
                        Kind = data[0x1D] != 0 ? LootRollNoticeKind.Item : LootRollNoticeKind.Removed,
                        ChestID = *(uint*)data,
                        ChestItemIndex = data[0x1A],
                        ItemID = *(uint*)(data + 4),
                        Count = *(ushort*)(data + 0x18),
                        State = data[0x1B]
                    };
                    break;
                case 0x11D:
                    notice = notice with
                    {
                        Kind = LootRollNoticeKind.Result,
                        ChestID = *(uint*)data,
                        ChestItemIndex = *(uint*)(data + 4),
                        Code = data[8],
                        Value = data[9]
                    };
                    break;
                case 0xBE:
                    var type = *(ushort*)data;
                    if (type is not (0 or 1 or 3 or 7))
                        return;
                    notice = notice with
                    {
                        Kind = type switch
                        {
                            0 => LootRollNoticeKind.Score,
                            7 => LootRollNoticeKind.Choice,
                            _ => LootRollNoticeKind.Award
                        },
                        ActorID = *(uint*)(data + 4),
                        ItemID = *(uint*)(data + (type is 3 or 7 ? 8 : 0x10)),
                        Count = *(uint*)(data + (type is 3 or 7 ? 0x0C : 0x14)),
                        Code = type == 0 ? data[8] : 0u,
                        Value = type == 0 ? data[0x0C] : 0
                    };
                    break;
                case 0x198:
                case 0xC8:
                case 0x1E6:
                    var category = *(ushort*)data;
                    if (category is not (95 or 96))
                        return;
                    notice = notice with
                    {
                        Kind = LootRollNoticeKind.Assignment,
                        ChestID = *(uint*)(data + 4),
                        ChestItemIndex = *(uint*)(data + 8),
                        State = *(uint*)(data + 0x0C),
                        Code = category
                    };
                    break;
                default:
                    return;
            }
            if (Volatile.Read(ref active) != 0)
                callback(notice);
        }
        catch (Exception exception)
        {
            Runtime.Current.PluginLog.Error(exception, "Failed to copy loot observation.");
        }
    }

    #region 常量

    private const string SUPPORTED_VERSION = "2026.10.06.0000.0000";

    #endregion
}

/// <summary>战利品观察中已验证的消息类别。</summary>
public enum LootRollNoticeKind
{
    /// <summary>增加或刷新一份带有宝箱及箱内序号的掉落。</summary>
    Item,
    /// <summary>本人选择的成功或失败回执。</summary>
    Result,
    /// <summary>某位玩家已选择，尚不公开其具体选择。</summary>
    Choice,
    /// <summary>公开的需求或贪婪点数。</summary>
    Score,
    /// <summary>正式获得或分配消息。</summary>
    Award,
    /// <summary>服务器取消该宝箱的掉落记录。</summary>
    Removed,
    /// <summary>带有物品实例键的本地分配权限变化。</summary>
    Assignment
}

/// <summary>已从原生回调复制的战利品标量，不包含原生指针。</summary>
public readonly record struct LootRollNotice
{
    /// <summary>观察到消息的时间。</summary>
    public DateTimeOffset Time { get; init; }
    /// <summary>消息类别。</summary>
    public LootRollNoticeKind Kind { get; init; }
    /// <summary>消息明确提供的宝箱对象 ID，未提供时为零。</summary>
    public uint ChestID { get; init; }
    /// <summary>消息明确提供的箱内物品序号。</summary>
    public uint ChestItemIndex { get; init; }
    /// <summary>物品数据行 ID，未提供时为零。</summary>
    public uint ItemID { get; init; }
    /// <summary>消息提供的物品数量。</summary>
    public uint Count { get; init; }
    /// <summary>消息提供的玩家实体 ID。</summary>
    public uint ActorID { get; init; }
    /// <summary>当前类别对应的选择、回执或权限代码。</summary>
    public uint Code { get; init; }
    /// <summary>公开点数或本人的回执点数。</summary>
    public int Value { get; init; }
    /// <summary>服务器提供的本地 Roll 状态值。</summary>
    public uint State { get; init; }
}
