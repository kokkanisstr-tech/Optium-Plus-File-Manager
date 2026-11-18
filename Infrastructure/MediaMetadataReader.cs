using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace OptiumPlusFileManager.Infrastructure;

/// <summary>
/// Reads a small, bounded set of technical fields without decoding or executing the input file.
/// </summary>
public static class MediaMetadataReader
{
    private const int MaxHeaderBytes = 1024 * 1024;
    private static readonly IReadOnlyDictionary<int, int[]> Mpeg1Bitrates = new Dictionary<int, int[]>
    {
        [1] = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0],
        [2] = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0],
        [3] = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0]
    };
    private static readonly IReadOnlyDictionary<int, int[]> Mpeg2Bitrates = new Dictionary<int, int[]>
    {
        [1] = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0],
        [2] = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0],
        [3] = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0]
    };

    public static string Read(string path, string fileType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var details = fileType switch
            {
                "png" or "jpeg" or "gif" or "webp" or "bmp" or "tiff" or "ico" => ReadImage(path, fileType),
                "wav" => ReadWave(path),
                "mp3" => ReadMp3(path),
                "mp4" or "m4a" => ReadIsoMedia(path),
                "avi" => ReadAvi(path),
                "executable" => ReadExecutable(path),
                "ogg" => "Контейнер: Ogg; аудио- и видеодорожки не запускались.",
                "flac" => ReadFlac(path),
                "mkv" => "Контейнер: Matroska; аудио- и видеодорожки не запускались.",
                "heif" or "avif" => $"Контейнер: {fileType.ToUpperInvariant()}; размеры изображения не извлечены.",
                _ => string.Empty
            };
            return string.IsNullOrWhiteSpace(details) ? string.Empty : "Технические метаданные (извлечены локально):\n" + details;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                               InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            return "Технические метаданные не удалось безопасно прочитать.";
        }
    }

    private static string ReadImage(string path, string type)
    {
        var dimensions = ReadImageDimensions(path, type);
        var size = new FileInfo(path).Length;
        return dimensions.Width > 0 && dimensions.Height > 0
            ? $"Формат: {type.ToUpperInvariant()}; размеры: {dimensions.Width} × {dimensions.Height} пикс.; размер: {size} байт."
            : $"Формат: {type.ToUpperInvariant()}; размер: {size} байт; размеры не найдены в ограниченном заголовке.";
    }

    public static (uint Width, uint Height) ReadImageDimensions(string path, string type)
    {
        var bytes = ReadHeader(path);
        return type switch
        {
            "png" when bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) =>
                (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4))),
            "gif" when bytes.Length >= 10 && (bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)) =>
                ((uint)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)),
                 (uint)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2))),
            "bmp" when bytes.Length >= 26 && bytes.AsSpan(0, 2).SequenceEqual("BM"u8) =>
                (AbsDimension(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4))), AbsDimension(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4)))),
            "jpeg" => ReadJpegDimensions(bytes),
            "webp" => ReadWebpDimensions(bytes),
            "ico" when bytes.Length >= 22 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 1, 0 }) =>
                ((uint)(bytes[6] == 0 ? 256 : bytes[6]), (uint)(bytes[7] == 0 ? 256 : bytes[7])),
            "tiff" => ReadTiffDimensions(bytes),
            _ => (0u, 0u)
        };
    }

    private static (uint Width, uint Height) ReadJpegDimensions(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return (0, 0);
        var position = 2;
        while (position + 4 <= bytes.Length)
        {
            while (position < bytes.Length && bytes[position] != 0xFF) position++;
            while (position < bytes.Length && bytes[position] == 0xFF) position++;
            if (position >= bytes.Length) break;
            var marker = bytes[position++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;
            if (position + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position, 2));
            if (length < 2 || position + length > bytes.Length) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (length < 7) return (0, 0);
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 3, 2)));
            }
            position += length;
        }
        return (0, 0);
    }

    private static (uint Width, uint Height) ReadWebpDimensions(byte[] bytes)
    {
        if (bytes.Length < 30 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return (0, 0);
        var chunk = Encoding.ASCII.GetString(bytes, 12, 4);
        if (chunk == "VP8X")
        {
            var width = 1u + (uint)(bytes[24] | bytes[25] << 8 | bytes[26] << 16);
            var height = 1u + (uint)(bytes[27] | bytes[28] << 8 | bytes[29] << 16);
            return (width, height);
        }
        if (chunk == "VP8 " && bytes.Length >= 30 && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
            return ((uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) & 0x3FFF),
                (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) & 0x3FFF));
        if (chunk == "VP8L" && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            var width = 1u + (uint)(((bytes[22] & 0x3F) << 8) | bytes[21]);
            var height = 1u + (uint)(((bytes[24] & 0x0F) << 10) | (bytes[23] << 2) | ((bytes[22] & 0xC0) >> 6));
            return (width, height);
        }
        return (0, 0);
    }

    private static (uint Width, uint Height) ReadTiffDimensions(byte[] bytes)
    {
        if (bytes.Length < 8) return (0, 0);
        var littleEndian = bytes[0] == (byte)'I' && bytes[1] == (byte)'I';
        if (!littleEndian && !(bytes[0] == (byte)'M' && bytes[1] == (byte)'M')) return (0, 0);
        ushort Read16(int offset) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
        uint Read32(int offset) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        if (Read16(2) != 42) return (0, 0);
        var ifd = Read32(4);
        if (ifd > bytes.Length - 2) return (0, 0);
        var count = Math.Min(Read16((int)ifd), (ushort)256);
        uint width = 0, height = 0;
        var offset = checked((int)ifd + 2);
        for (var i = 0; i < count && offset + 12 <= bytes.Length; i++, offset += 12)
        {
            var tag = Read16(offset);
            var type = Read16(offset + 2);
            var values = Read32(offset + 4);
            if (values == 0) continue;
            uint value = type switch
            {
                3 when values == 1 => Read16(offset + 8),
                4 when values == 1 => Read32(offset + 8),
                _ => 0
            };
            if (tag == 256) width = value;
            if (tag == 257) height = value;
        }
        return (width, height);
    }

    private static string ReadWave(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 12 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") return "Контейнер: WAV.";
        _ = reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") return "Контейнер: WAV.";
        uint sampleRate = 0, byteRate = 0, dataSize = 0;
        ushort channels = 0, bits = 0;
        var scanned = 12;
        while (stream.Position + 8 <= stream.Length && scanned < MaxHeaderBytes)
        {
            var chunkStart = stream.Position;
            var name = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            var end = Math.Min(stream.Length, chunkStart + 8L + length);
            if (name == "fmt " && length >= 16 && length <= MaxHeaderBytes)
            {
                var format = reader.ReadBytes(16);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2));
            }
            else if (name == "data") dataSize = length;
            if (name == "data" && byteRate > 0) break;
            if (end < stream.Position || end > stream.Length) break;
            stream.Position = Math.Min(stream.Length, end + (length & 1));
            scanned = checked((int)Math.Min(int.MaxValue, stream.Position));
        }
        var detail = $"Формат: WAV; размер: {stream.Length} байт";
        if (sampleRate > 0) detail += $"; частота: {sampleRate} Гц; каналов: {channels}; разрядность: {bits} бит";
        if (byteRate > 0 && dataSize > 0) detail += $"; длительность: {TimeSpan.FromSeconds(dataSize / (double)byteRate):hh\\:mm\\:ss}";
        return detail + ".";
    }

    private static string ReadMp3(string path)
    {
        var bytes = ReadHeader(path);
        var start = 0;
        if (bytes.Length >= 10 && bytes.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            var tagLength = ((bytes[6] & 0x7F) << 21) | ((bytes[7] & 0x7F) << 14) | ((bytes[8] & 0x7F) << 7) | (bytes[9] & 0x7F);
            start = Math.Min(bytes.Length, 10 + tagLength);
        }
        var fileLength = new FileInfo(path).Length;
        for (var i = start; i + 4 <= bytes.Length; i++)
        {
            var header = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i, 4));
            if ((header & 0xFFE00000) != 0xFFE00000) continue;
            var version = (int)((header >> 19) & 0x3);
            var layer = (int)((header >> 17) & 0x3);
            var bitrateIndex = (int)((header >> 12) & 0xF);
            var rateIndex = (int)((header >> 10) & 0x3);
            if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) continue;
            var layerNumber = 4 - layer;
            var versionName = version == 3 ? "MPEG-1" : version == 2 ? "MPEG-2" : "MPEG-2.5";
            var rate = new[] { 44100, 48000, 32000 }[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            var bitrate = (version == 3 ? Mpeg1Bitrates : Mpeg2Bitrates)[layerNumber][bitrateIndex];
            var detail = $"Формат: MP3 ({versionName}, Layer {layerNumber}); частота: {rate} Гц; битрейт первого кадра: {bitrate} кбит/с; размер: {fileLength} байт";
            if (bitrate > 0)
                detail += $"; приблизительная длительность при постоянном битрейте: {TimeSpan.FromSeconds(fileLength * 8d / (bitrate * 1000)):hh\\:mm\\:ss}";
            return detail + ".";
        }
        return $"Формат: MP3; размер: {fileLength} байт; корректный аудиокадр не найден в первых {MaxHeaderBytes} байтах.";
    }

    private static string ReadFlac(string path)
    {
        var bytes = ReadHeader(path);
        var size = new FileInfo(path).Length;
        if (bytes.Length < 42 || !bytes.AsSpan(0, 4).SequenceEqual("fLaC"u8))
            return $"Формат: FLAC; размер: {size} байт.";
        var blockType = bytes[4] & 0x7F;
        var blockLength = bytes[5] << 16 | bytes[6] << 8 | bytes[7];
        if (blockType != 0 || blockLength < 34) return $"Формат: FLAC; размер: {size} байт.";
        var packed = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18, 8));
        var sampleRate = (uint)(packed >> 44);
        var totalSamples = packed & 0xFFFFFFFFF;
        var channels = (int)((packed >> 41) & 0x7) + 1;
        var bits = (int)((packed >> 36) & 0x1F) + 1;
        var detail = $"Формат: FLAC; размер: {size} байт; частота: {sampleRate} Гц; каналов: {channels}; разрядность: {bits} бит";
        if (sampleRate > 0 && totalSamples > 0)
            detail += $"; длительность: {TimeSpan.FromSeconds(totalSamples / (double)sampleRate):hh\\:mm\\:ss}";
        return detail + ".";
    }

    private static string ReadIsoMedia(string path)
    {
        using var stream = File.OpenRead(path);
        var size = stream.Length;
        var position = 0L;
        var boxesRead = 0;
        var header = new byte[16];
        while (position + 8 <= size && boxesRead++ < 100_000)
        {
            stream.Position = position;
            try { stream.ReadExactly(header.AsSpan(0, 8)); }
            catch (EndOfStreamException) { break; }
            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerLength = 8;
            long actualSize = boxSize;
            if (boxSize == 1)
            {
                try { stream.ReadExactly(header.AsSpan(8, 8)); }
                catch (EndOfStreamException) { break; }
                actualSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)));
                headerLength = 16;
            }
            else if (boxSize == 0) actualSize = size - position;
            if (actualSize < headerLength || actualSize > size - position) break;
            if (type == "moov")
            {
                var duration = ReadMovieDuration(stream, position + headerLength, position + actualSize);
                return duration is null
                    ? $"Контейнер: ISO Base Media ({Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}); размер: {size} байт."
                    : $"Контейнер: ISO Base Media ({Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}); длительность: {duration:hh\\:mm\\:ss}; размер: {size} байт.";
            }
            position += actualSize;
        }
        return $"Контейнер: ISO Base Media ({Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}); размер: {size} байт; блок метаданных длительности не найден.";
    }

    private static TimeSpan? ReadMovieDuration(FileStream stream, long start, long end)
    {
        var count = 0;
        var header = new byte[16];
        while (start + 8 <= end && count++ < 10_000)
        {
            stream.Position = start;
            try { stream.ReadExactly(header.AsSpan(0, 8)); }
            catch (EndOfStreamException) { return null; }
            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerLength = 8;
            long actualSize = boxSize;
            if (boxSize == 1)
            {
                try { stream.ReadExactly(header.AsSpan(8, 8)); }
                catch (EndOfStreamException) { return null; }
                actualSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)));
                headerLength = 16;
            }
            else if (boxSize == 0) actualSize = end - start;
            if (actualSize < headerLength || actualSize > end - start) return null;
            if (type == "mvhd")
            {
                var payloadLength = (int)Math.Min(40, actualSize - headerLength);
                var payload = new byte[payloadLength];
                try { stream.ReadExactly(payload); }
                catch (EndOfStreamException) { return null; }
                if (payload.Length < 20) return null;
                if (payload[0] == 0)
                {
                    var timeScale = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(12, 4));
                    var duration = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(16, 4));
                    return timeScale == 0 ? null : TimeSpan.FromSeconds(duration / (double)timeScale);
                }
                if (payload[0] == 1 && payload.Length >= 32)
                {
                    var timeScale = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(20, 4));
                    var duration = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(24, 8));
                    return timeScale == 0 ? null : TimeSpan.FromSeconds(duration / (double)timeScale);
                }
                return null;
            }
            start += actualSize;
        }
        return null;
    }

    private static string ReadAvi(string path)
    {
        var bytes = ReadHeader(path);
        var size = new FileInfo(path).Length;
        for (var i = 12; i + 48 <= bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, 4).SequenceEqual("avih"u8)) continue;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 4, 4));
            if (length < 40 || i + 8 + 40 > bytes.Length) break;
            var payload = bytes.AsSpan(i + 8, 40);
            var microsPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
            var totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
            var width = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(32, 4));
            var height = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(36, 4));
            var detail = $"Контейнер: AVI; кадр: {width} × {height} пикс.; размер: {size} байт";
            if (microsPerFrame > 0 && totalFrames > 0)
                detail += $"; длительность: {TimeSpan.FromSeconds(microsPerFrame * (double)totalFrames / 1_000_000):hh\\:mm\\:ss}";
            return detail + ".";
        }
        return $"Контейнер: AVI; размер: {size} байт; заголовок дорожек не найден в первых {MaxHeaderBytes} байтах.";
    }

    private static string ReadExecutable(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var size = new FileInfo(path).Length;
        var fields = new List<string> { $"формат PE; размер: {size} байт" };
        if (!string.IsNullOrWhiteSpace(info.ProductName)) fields.Add($"продукт: {CleanMetadata(info.ProductName)}");
        if (!string.IsNullOrWhiteSpace(info.FileVersion)) fields.Add($"версия: {CleanMetadata(info.FileVersion)}");
        if (!string.IsNullOrWhiteSpace(info.CompanyName)) fields.Add($"издатель из метаданных: {CleanMetadata(info.CompanyName)}");
        return string.Join("; ", fields) + ". Файл не запускался; доверие к цифровой подписи не проверялось.";
    }

    private static string CleanMetadata(string value) =>
        new string(value.Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();

    private static byte[] ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, MaxHeaderBytes);
        var bytes = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = stream.Read(bytes, read, length - read);
            if (count == 0) break;
            read += count;
        }
        return read == length ? bytes : bytes[..read];
    }

    private static uint AbsDimension(int value) => value == int.MinValue ? 0u : (uint)Math.Abs(value);
}
