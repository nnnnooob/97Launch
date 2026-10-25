using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Launcher.App.Services;

/// <summary>Edits only the server list, preserving unrelated NBT tags and each existing entry's settings.</summary>
internal static class MinecraftServerList
{
    private const int Limit = 8 * 1024 * 1024;

    public static async Task AddAsync(string directory, CatalogServer server, CancellationToken token)
    {
        ServerCatalogService.ValidateAddress(server);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "servers.dat");
        var before = await ReadFileAsync(path, token);
        var compressed = before is { Length: > 2 } && before[0] == 0x1f && before[1] == 0x8b;
        var data = before ?? new byte[] { 10, 0, 0, 0 };
        if (compressed)
        {
            using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            var block = new byte[8192];
            int read;
            while ((read = await gzip.ReadAsync(block, token)) > 0)
            {
                if (decoded.Length + read > Limit) throw new InvalidDataException("服务器列表解压后过大，未修改原文件。");
                decoded.Write(block, 0, read);
            }
            data = decoded.ToArray();
        }
        var merged = Merge(data, server.Name, Address(server));
        if (data.AsSpan().SequenceEqual(merged))
        {
            Serilog.Log.Information("服务器已在多人游戏列表中：{Address}", Address(server));
            return;
        }
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(merged);
            merged = output.ToArray();
        }
        var temp = path + ".97launch-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, merged, token);
            var current = await ReadFileAsync(path, token);
            if (before is null ? current is not null : current is null || !before.AsSpan().SequenceEqual(current))
                throw new IOException("游戏服务器列表刚被其他程序修改，请重试。");
            token.ThrowIfCancellationRequested();
            if (before is not null)
            {
                var backups = Path.Combine(directory, "97Launch", "backups", "servers");
                Directory.CreateDirectory(backups);
                var backup = Path.Combine(backups, $"servers-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.dat");
                File.Replace(temp, path, backup);
                Serilog.Log.Information("多人游戏服务器列表备份：{Path}", backup);
            }
            else File.Move(temp, path);
            Serilog.Log.Information("已更新多人游戏服务器列表：{Name}（{Address}），位置：{Path}", server.Name, Address(server), path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (file.Length > Limit) throw new InvalidDataException("服务器列表文件过大，未修改原文件。");
        var data = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(data, token);
        return data;
    }

    internal static string Address(CatalogServer server) => server.Host.Contains(':')
        ? $"[{server.Host}]:{server.Port}" : $"{server.Host}:{server.Port}";

    private static string Normalize(string address)
    {
        if (!Uri.TryCreate("tcp://" + address.Trim(), UriKind.Absolute, out var uri)) return address.Trim().ToLowerInvariant();
        return uri.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant() + ":" + (uri.Port < 0 ? 25565 : uri.Port);
    }

    private static byte[] Merge(byte[] data, string name, string address)
    {
        using var input = new MemoryStream(data);
        using var reader = new BinaryReader(input);
        if (reader.ReadByte() != 10) throw new InvalidDataException("服务器列表 NBT 根标签无效，未修改原文件。");
        Text(reader);
        var headerEnd = checked((int)input.Position);
        using var output = new MemoryStream();
        output.Write(data, 0, headerEnd);
        var found = false;
        while (true)
        {
            var start = checked((int)input.Position);
            var type = reader.ReadByte();
            if (type == 0) break;
            var key = Text(reader);
            if (key == "servers")
            {
                if (found || type != 9) throw new InvalidDataException("服务器列表标签重复或格式无效，未修改原文件。");
                found = true;
                var elementType = reader.ReadByte();
                var count = Count(reader);
                if (elementType != 10 && !(count == 0 && elementType == 0))
                    throw new InvalidDataException("服务器条目格式无效，未修改原文件。");
                var entries = new List<byte[]>();
                var matched = false;
                for (var i = 0; i < count; i++)
                {
                    var entryStart = checked((int)input.Position);
                    string? ip = null;
                    while (true)
                    {
                        var child = reader.ReadByte();
                        if (child == 0) break;
                        var tagName = Text(reader);
                        if (child == 8 && tagName == "ip") ip = Text(reader);
                        else Skip(reader, child, 1);
                    }
                    var same = ip is not null && Normalize(ip) == Normalize(address);
                    if (same && matched) continue;
                    if (same) matched = true;
                    entries.Add(data[entryStart..checked((int)input.Position)]);
                }
                if (!matched) entries.Add(NewEntry(name, address));
                WriteList(output, entries);
            }
            else
            {
                Skip(reader, type, 0);
                output.Write(data, start, checked((int)input.Position) - start);
            }
        }
        if (input.Position != input.Length) throw new InvalidDataException("服务器列表包含无法识别的附加数据，未修改原文件。");
        if (!found) WriteList(output, [NewEntry(name, address)]);
        output.WriteByte(0);
        return output.ToArray();
    }

    private static byte[] NewEntry(string name, string address)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(8); WriteText(stream, "name"); WriteText(stream, name);
        stream.WriteByte(8); WriteText(stream, "ip"); WriteText(stream, address);
        stream.WriteByte(0);
        return stream.ToArray();
    }

    private static void WriteList(Stream stream, List<byte[]> entries)
    {
        stream.WriteByte(9); WriteText(stream, "servers"); stream.WriteByte(10);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, entries.Count); stream.Write(length);
        foreach (var entry in entries) stream.Write(entry);
    }

    private static string Text(BinaryReader reader)
    {
        Span<byte> size = stackalloc byte[2];
        reader.BaseStream.ReadExactly(size);
        var length = BinaryPrimitives.ReadUInt16BigEndian(size);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("服务器列表不完整，未修改原文件。");
        // Inspected names and server addresses are ASCII; all other NBT string bytes are copied unchanged.
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteText(Stream stream, string value)
    {
        // Java's NBT uses modified UTF-8, including three-byte UTF-16 surrogate code units.
        using var encoded = new MemoryStream();
        foreach (var ch in value)
        {
            if (ch is > '\0' and <= '\x7f') encoded.WriteByte((byte)ch);
            else if (ch <= '\u07ff') { encoded.WriteByte((byte)(0xc0 | ch >> 6)); encoded.WriteByte((byte)(0x80 | ch & 63)); }
            else { encoded.WriteByte((byte)(0xe0 | ch >> 12)); encoded.WriteByte((byte)(0x80 | ch >> 6 & 63)); encoded.WriteByte((byte)(0x80 | ch & 63)); }
        }
        if (encoded.Length > ushort.MaxValue) throw new InvalidDataException("服务器名称过长。");
        Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)encoded.Length);
        stream.Write(length); stream.Write(encoded.ToArray());
    }

    private static int Count(BinaryReader reader)
    {
        Span<byte> size = stackalloc byte[4];
        reader.BaseStream.ReadExactly(size);
        var count = BinaryPrimitives.ReadInt32BigEndian(size);
        if (count is < 0 or > 100000) throw new InvalidDataException("服务器列表长度无效。");
        return count;
    }

    private static void Skip(BinaryReader reader, byte type, int depth)
    {
        if (depth > 32) throw new InvalidDataException("服务器列表嵌套层级过深。");
        void Bytes(long count)
        {
            var stream = reader.BaseStream;
            if (count < 0 || count > stream.Length - stream.Position) throw new InvalidDataException("服务器列表标签长度无效。");
            stream.Position += count;
        }
        switch (type)
        {
            case 1: Bytes(1); break;
            case 2: Bytes(2); break;
            case 3: case 5: Bytes(4); break;
            case 4: case 6: Bytes(8); break;
            case 7: Bytes(Count(reader)); break;
            case 8: Text(reader); break;
            case 9:
                var subtype = reader.ReadByte(); var count = Count(reader);
                if (subtype == 0 && count != 0) throw new InvalidDataException("服务器列表标签无效。");
                for (var i = 0; i < count; i++) Skip(reader, subtype, depth + 1);
                break;
            case 10:
                byte child;
                while ((child = reader.ReadByte()) != 0) { Text(reader); Skip(reader, child, depth + 1); }
                break;
            case 11: Bytes((long)Count(reader) * 4); break;
            case 12: Bytes((long)Count(reader) * 8); break;
            default: throw new InvalidDataException("服务器列表含未知 NBT 标签，未修改原文件。");
        }
    }
}
