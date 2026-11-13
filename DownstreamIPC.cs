using FFXIVClientStructs.FFXIV.Client.Game.Network;

namespace TreeTools;

public static unsafe class DownstreamIPC
{
    /// <summary>从 Omen 的下行封包回调读取有效负载，结果仅在回调期间有效。</summary>
    public static bool TryReadPayload(nint packet, int opcode, int minimumLength, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        const int SEGMENT_HEADER_SIZE = 16;
        if (packet == 0) return false;

        var headerSize = SEGMENT_HEADER_SIZE + sizeof(ServerIpcSegmentHeader);
        var size = *(int*)packet;
        if (size < headerSize || (uint)(size - headerSize) < (uint)minimumLength) return false;

        var header = (ServerIpcSegmentHeader*)((byte*)packet + SEGMENT_HEADER_SIZE);
        if (header->Magic != 0x14 || header->OpCode != opcode) return false;

        payload = new ReadOnlySpan<byte>((byte*)packet + headerSize, size - headerSize);
        return true;
    }
}
