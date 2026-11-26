using System.Globalization;
using System.IO;

namespace McuFlasher;

internal sealed record Target(string Name, string JLinkName, int FlashKiB)
{
    public bool IsGd => Name.StartsWith("GD32F303", StringComparison.Ordinal);
    public override string ToString() => Name;
}

internal sealed record Firmware(string FileName, string? Address)
{
    public FlashRange Range { get; private init; }
    private const uint FlashStart = 0x08000000;
    private const int MaxHexFileSize = 32 * 1024 * 1024;

    public static Firmware Stage(string path, string directory, string address, Target target,
        CancellationToken cancellation = default, IProgress<string>? progress = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".bin" or ".hex"))
            throw new InvalidDataException("请选择 .bin 或 .hex 固件。");

        cancellation.ThrowIfCancellationRequested();
        progress?.Report("正在打开固件文件…");
        using var input = File.OpenRead(path);
        var flashLimit = FlashStart + (ulong)target.FlashKiB * 1024;
        uint start = 0;
        FlashRange range;
        if (extension == ".bin")
        {
            var digits = address.Trim();
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                digits = digits[2..];
            if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out start))
                throw new InvalidDataException("请输入十六进制烧录地址，例如 0x08000000。");
            if (input.Length == 0)
                throw new InvalidDataException("固件文件为空。");
            CheckRange(start, (ulong)input.Length, flashLimit);
            range = new FlashRange(start, checked(start + (uint)input.Length - 1));
        }
        else
        {
            if (input.Length > MaxHexFileSize)
                throw new InvalidDataException("HEX 文件超过 32 MiB。");
            progress?.Report("正在解析 HEX 数据记录和地址…");
            range = ValidateHex(input, flashLimit, cancellation);
            input.Position = 0;
        }

        var stagedName = "firmware" + extension;
        cancellation.ThrowIfCancellationRequested();
        progress?.Report($"固件地址 {range}，正在复制到临时目录…");
        using (var output = File.Create(Path.Combine(directory, stagedName)))
        {
            var buffer = new byte[65536];
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = input.Read(buffer);
                if (count == 0)
                    break;
                output.Write(buffer, 0, count);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("临时固件已复制。");
        return new Firmware(stagedName, extension == ".bin" ? $"0x{start:X8}" : null) { Range = range };
    }

    private static FlashRange ValidateHex(Stream input, ulong flashLimit, CancellationToken cancellation)
    {
        using var reader = new StreamReader(input, leaveOpen: true);
        var hasData = false;
        var ended = false;
        var lineNumber = 0;
        ulong recordBase = 0;
        ulong first = ulong.MaxValue, last = 0;
        while (reader.ReadLine() is { } raw)
        {
            cancellation.ThrowIfCancellationRequested();
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var error = $"第 {lineNumber} 行的 Intel HEX 记录无效。";
            if (ended || line.Length is < 11 or > 521 || line[0] != ':' || (line.Length & 1) == 0)
                throw new InvalidDataException(error);
            byte[] bytes;
            try { bytes = Convert.FromHexString(line.AsSpan(1)); }
            catch (FormatException) { throw new InvalidDataException(error); }
            var count = bytes[0];
            var offset = (bytes[1] << 8) | bytes[2];
            if (bytes.Length != count + 5 || (bytes.Sum(value => (int)value) & 0xFF) != 0)
                throw new InvalidDataException(error);

            switch (bytes[3])
            {
                case 0 when offset + count <= 0x10000:
                    if (count > 0)
                    {
                        var start = recordBase + (uint)offset;
                        CheckRange(start, count, flashLimit);
                        first = Math.Min(first, start);
                        last = Math.Max(last, start + count - 1);
                        hasData = true;
                    }
                    break;
                case 1 when offset == 0 && count == 0:
                    ended = true;
                    break;
                case 2 or 4 when offset == 0 && count == 2:
                    recordBase = (ulong)((bytes[4] << 8) | bytes[5]) << (bytes[3] == 2 ? 4 : 16);
                    break;
                case 3 or 5 when offset == 0 && count == 4:
                    break;
                default:
                    throw new InvalidDataException(error);
            }
        }

        if (!hasData || !ended)
            throw new InvalidDataException("HEX 缺少固件数据或结束记录。");
        return new FlashRange((uint)first, (uint)last);
    }

    private static void CheckRange(ulong start, ulong length, ulong limit)
    {
        if (start < FlashStart || start + length > limit)
            throw new InvalidDataException($"固件超出目标内部 Flash 的地址范围（0x{FlashStart:X8}–0x{limit - 1:X8}）。");
    }
}
