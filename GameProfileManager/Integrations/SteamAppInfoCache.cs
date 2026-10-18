using System.Buffers.Binary;
using System.Text;

namespace GameProfileManager.Integrations;

// Steam's local appinfo cache supplies the app type omitted from appmanifest_*.acf.
internal sealed class SteamAppInfoCache
{
    private readonly byte[] _data = [];
    private readonly int _commonKey = -1;
    private readonly int _typeKey = -1;

    public SteamAppInfoCache(string path)
    {
        try
        {
            _data = File.ReadAllBytes(path);
            if (_data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(_data) != 0x07564429) return;
            int offset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(8));
            if (offset < 12 || offset > _data.Length - 4) return;
            int count = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset));
            if (count is < 1 or > 500000) return;
            int cursor = offset + 4;
            int common = -1, type = -1;
            for (int index = 0; index < count; index++)
            {
                if (cursor >= _data.Length) return;
                int end = Array.IndexOf(_data, (byte)0, cursor);
                if (end < 0) return;
                string key = Encoding.UTF8.GetString(_data, cursor, end - cursor);
                if (key == "common") common = index;
                if (key == "type") type = index;
                cursor = end + 1;
            }
            _commonKey = common;
            _typeKey = type;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _data = []; }
    }

    public string? GetType(string appId)
    {
        if (_commonKey < 0 || _typeKey < 0 || !uint.TryParse(appId, out uint id)) return null;
        byte[] header = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(header, id);
        header[4] = 0; // common KeyValues object
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), _commonKey);
        int start = _data.AsSpan().IndexOf(header);
        if (start < 0) return null;
        byte[] typeKey = new byte[5];
        typeKey[0] = 1; // string value
        BinaryPrimitives.WriteInt32LittleEndian(typeKey.AsSpan(1), _typeKey);
        int relative = _data.AsSpan(start + header.Length, Math.Min(512, _data.Length - start - header.Length)).IndexOf(typeKey);
        if (relative < 0) return null;
        int valueStart = start + header.Length + relative + typeKey.Length;
        int valueEnd = Array.IndexOf(_data, (byte)0, valueStart);
        return valueEnd > valueStart && valueEnd - valueStart < 64 ? Encoding.UTF8.GetString(_data, valueStart, valueEnd - valueStart) : null;
    }
}
