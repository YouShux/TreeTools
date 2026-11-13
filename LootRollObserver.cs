using System.Buffers.Binary;
using System.Threading;
using OmenTools.OmenService;

namespace TreeTools;

/// <summary>只读复制战利品更新、选择和结算字段。</summary>
public sealed class LootRollObserver : IDisposable
{
    private readonly Action<LootRollNotice> callback;
    private int active;

    /// <param name="callback">接收已复制的托管字段，不得保留原生数据。</param>
    public LootRollObserver(Action<LootRollNotice> callback)
    {
        this.callback = callback;
        active = 1;
        if (!GamePacketManager.Instance().RegPostReceivePacket(OnPacket))
        {
            active = 0;
            throw new InvalidOperationException("Loot observation registration failed.");
        }
    }

    public bool IsSupported => Volatile.Read(ref active) != 0;

    /// <summary>立即停止观察并解绑接收回调，不向游戏发送任何请求。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref active, 0) != 0)
            GamePacketManager.Instance().Unreg((GamePacketManager.PostReceivePacketDelegate)OnPacket);
    }

    private void OnPacket(int opcode, nint packet)
    {
        if (!IsSupported)
            return;
        var required = opcode switch { 0x14E => 30, 0x11D => 10, 0xBE => 16, 0x198 or 0xC8 or 0x1E6 => 16, _ => 0 };
        if (required == 0 || !DownstreamIPC.TryReadPayload(packet, opcode, required, out var data))
            return;
        try
        {
            var notice = new LootRollNotice { Time = DateTimeOffset.UtcNow };
            switch (opcode)
            {
                case 0x14E:
                    notice = notice with
                    {
                        Kind = data[0x1D] != 0 ? LootRollNoticeKind.Item : LootRollNoticeKind.Removed,
                        ChestID = BinaryPrimitives.ReadUInt32LittleEndian(data),
                        ChestItemIndex = data[0x1A],
                        ItemID = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                        Count = BinaryPrimitives.ReadUInt16LittleEndian(data[0x18..]),
                        State = data[0x1B]
                    };
                    break;
                case 0x11D:
                    notice = notice with
                    {
                        Kind = LootRollNoticeKind.Result,
                        ChestID = BinaryPrimitives.ReadUInt32LittleEndian(data),
                        ChestItemIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                        Code = data[8],
                        Value = data[9]
                    };
                    break;
                case 0xBE:
                    var type = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    if (type is not (0 or 1 or 3 or 7))
                        return;
                    var shortForm = type is 3 or 7;
                    if (!shortForm && data.Length < 24)
                        return;
                    notice = notice with
                    {
                        Kind = type switch
                        {
                            0 => LootRollNoticeKind.Score,
                            7 => LootRollNoticeKind.Choice,
                            _ => LootRollNoticeKind.Award
                        },
                        ActorID = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                        ItemID = BinaryPrimitives.ReadUInt32LittleEndian(data[(shortForm ? 8 : 0x10)..]),
                        Count = BinaryPrimitives.ReadUInt32LittleEndian(data[(shortForm ? 0x0C : 0x14)..]),
                        Code = type == 0 ? data[8] : 0u,
                        Value = type == 0 ? data[0x0C] : 0
                    };
                    break;
                case 0x198:
                case 0xC8:
                case 0x1E6:
                    var category = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    if (category is not (95 or 96))
                        return;
                    notice = notice with
                    {
                        Kind = LootRollNoticeKind.Assignment,
                        ChestID = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                        ChestItemIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
                        State = BinaryPrimitives.ReadUInt32LittleEndian(data[0x0C..]),
                        Code = category
                    };
                    break;
                default:
                    return;
            }
            if (IsSupported)
                callback(notice);
        }
        catch (Exception exception)
        {
            Runtime.Current.PluginLog.Error(exception, "Failed to copy loot observation.");
        }
    }
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
    public DateTimeOffset Time { get; init; }
    public LootRollNoticeKind Kind { get; init; }
    /// <summary>消息明确提供的宝箱对象 ID，未提供时为零。</summary>
    public uint ChestID { get; init; }
    /// <summary>消息明确提供的箱内物品序号。</summary>
    public uint ChestItemIndex { get; init; }
    /// <summary>物品数据行 ID，未提供时为零。</summary>
    public uint ItemID { get; init; }
    public uint Count { get; init; }
    public uint ActorID { get; init; }
    /// <summary>日志消息提供的玩家名称，无法提供实体 ID 时使用。</summary>
    public string? ActorName { get; init; }
    public uint ActorWorldID { get; init; }
    /// <summary>当前类别对应的选择、回执或权限代码。</summary>
    public uint Code { get; init; }
    /// <summary>公开点数或本人的回执点数。</summary>
    public int Value { get; init; }
    /// <summary>服务器提供的本地 Roll 状态值。</summary>
    public uint State { get; init; }
}
