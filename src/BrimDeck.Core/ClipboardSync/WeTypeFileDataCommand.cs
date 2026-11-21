namespace BrimDeck.Core.ClipboardSync;

// A complete WXP2P application message, after the session has reassembled and delivered it.
internal readonly record struct WeTypeFileDataCommand(uint ChunkId, WeTypeWire FileChunk)
{
    private const uint DataCommand = 1200;

    internal static WeTypeFileDataCommand Decode(ReadOnlySpan<byte> message)
    {
        var envelope = WeTypeWire.Parse(message, WeTypeFileStream.MaxFrameSize + 32);
        byte[] content = envelope.GetBytes(2);
        ulong id = envelope.GetNumber(3);
        if (envelope.GetNumber(1) != DataCommand || content.Length == 0 || id > uint.MaxValue)
            throw new InvalidDataException("Invalid WXP2P file data command.");
        return new((uint)id, WeTypeWire.Parse(content, WeTypeFileStream.MaxFrameSize));
    }

    internal byte[] Encode()
    {
        var content = FileChunk.Encode();
        if (content.Length is 0 or > WeTypeFileStream.MaxFrameSize)
            throw new InvalidDataException("Invalid WXP2P file chunk size.");
        return new WeTypeWire().Number(1, DataCommand).Bytes(2, content).Number(3, ChunkId).Encode();
    }
}
