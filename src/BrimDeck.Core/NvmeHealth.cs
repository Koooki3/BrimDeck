using System.Buffers.Binary;

namespace BrimDeck.Core;

// An NVMe drive's temperature from its SMART / Health Information log (log page 2, the Composite Temperature), for the drives
// whose storage-stack temperature property fails (a Kingston SKC3000D answers it with ERROR_IO_DEVICE) though the drive itself
// reports one. The log is asked for with the standard, read-only IOCTL_STORAGE_QUERY_PROPERTY on StorageDeviceProtocolSpecificProperty
// (a plain query for a log page, no pass-through command, nothing written to the drive, no administrator rights). This holds the
// request's bytes and reads the answer's, so both are checked without a drive.
public static class NvmeHealth
{
    // STORAGE_PROPERTY_ID StorageDeviceProtocolSpecificProperty.
    public const int PropertyId = 50;
    // STORAGE_PROTOCOL_TYPE ProtocolTypeNvme, STORAGE_PROTOCOL_NVME_DATA_TYPE NVMeDataTypeLogPage, NVME_LOG_PAGE_HEALTH_INFO.
    public const int ProtocolTypeNvme = 3, DataTypeLogPage = 2, LogPageHealthInfo = 2;
    // The log is 512 bytes; STORAGE_PROPERTY_QUERY up to AdditionalParameters (8 bytes) and the 40 bytes of
    // STORAGE_PROTOCOL_SPECIFIC_DATA that start there make the request; the answer is STORAGE_PROTOCOL_DATA_DESCRIPTOR (Version
    // and Size, 8 bytes) with the same 40 bytes and then the log. The structure is 40 bytes: a request whose data offset says 32 is
    // refused with ERROR_INVALID_PARAMETER (seen on Windows 11), one that says 40 is answered.
    public const int LogLength = 512, SpecificDataLength = 40, QueryLength = 8 + SpecificDataLength, BufferLength = QueryLength + LogLength;
    // The Composite Temperature in the log: two bytes at 1, in kelvin.
    private const int TemperatureAt = 1;

    // The request in the front of a buffer of at least BufferLength bytes (the answer is written to the same buffer): the standard
    // query (QueryType 0) for the health log from its start.
    public static void WriteQuery(Span<byte> buffer)
    {
        if (buffer.Length < QueryLength) throw new ArgumentException("The buffer is too small for the request.", nameof(buffer));
        buffer[..QueryLength].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(buffer, PropertyId);
        // QueryType (at 4) stays 0, PropertyStandardQuery. Then STORAGE_PROTOCOL_SPECIFIC_DATA at 8.
        var data = buffer[8..];
        BinaryPrimitives.WriteInt32LittleEndian(data, ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], DataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], LogPageHealthInfo);
        // ProtocolDataRequestSubValue (at 12) 0: the log from its first byte. ProtocolDataOffset: the log follows the structure.
        BinaryPrimitives.WriteInt32LittleEndian(data[16..], SpecificDataLength);
        BinaryPrimitives.WriteInt32LittleEndian(data[20..], LogLength);
    }

    // The drive's temperature in degrees Celsius from the first `returned` bytes of the answer, or null when the answer is short,
    // is not an NVMe log page, puts the temperature outside what was returned, or gives a temperature no working drive gives (0 K, a
    // log of 0xFF bytes from a drive that has nothing to say).
    public static double? Celsius(ReadOnlySpan<byte> answer, int returned)
    {
        if (returned > answer.Length) returned = answer.Length;
        if (returned < 8 + SpecificDataLength) return null;
        var data = answer[8..];
        if (BinaryPrimitives.ReadInt32LittleEndian(data) != ProtocolTypeNvme || BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != DataTypeLogPage) return null;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data[16..]), length = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        // The offset is from the start of the structure; the temperature is the log's bytes 1 and 2.
        if (offset < SpecificDataLength || length < TemperatureAt + 2 || offset > returned - 8 - (TemperatureAt + 2)) return null;
        int start = 8 + offset;
        return Temperatures.FromKelvin(BinaryPrimitives.ReadUInt16LittleEndian(answer[(start + TemperatureAt)..]));
    }
}
