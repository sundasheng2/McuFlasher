using System.Globalization;
using System.IO;

namespace McuFlasher;

internal readonly record struct FlashRange(uint First, uint Last)
{
    public override string ToString() => $"0x{First:X8}–0x{Last:X8}";
}

internal sealed record AiDebugScope(string Id, int Port, int Baud, FlashRange Allowed)
{
    public static AiDebugScope Create(string port, string baud, string first, string last, Target target,
        IReadOnlyList<FlashSector> sectors)
    {
        port = port.Trim();
        if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(port.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number is < 1 or > 256)
            throw new InvalidDataException("请输入本轮使用的串口（COM1–COM256）。");
        if (!int.TryParse(baud.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var rate) ||
            rate is < 1 or > 12000000)
            throw new InvalidDataException("请输入有效的串口波特率。");
        FlashGeometry.Validate(target, sectors);
        var range = new FlashRange(ParseAddress(first), ParseAddress(last));
        if (range.First > range.Last || !sectors.Any(page => page.Address == range.First) ||
            !sectors.Any(page => page.LastAddress == range.Last))
            throw new InvalidDataException("授权范围的首尾必须与所连接芯片的完整擦除页边界对齐。");
        return new AiDebugScope(Guid.NewGuid().ToString("N"), number, rate, range);
    }

    public FlashRange Check(FlashRange image, IReadOnlyList<FlashSector> sectors)
    {
        if (image.First > image.Last || image.First < Allowed.First || image.Last > Allowed.Last)
            throw new InvalidDataException($"固件范围 {image} 超出本轮授权 {Allowed}。");
        var first = sectors.FirstOrDefault(page => page.Address <= image.First && page.LastAddress >= image.First);
        var last = sectors.FirstOrDefault(page => page.Address <= image.Last && page.LastAddress >= image.Last);
        if (first is null || last is null || first.Address < Allowed.First || last.LastAddress > Allowed.Last)
            throw new InvalidDataException("固件涉及授权范围外的擦除页。");
        // Some vendor loaders erase gaps between HEX records. Include the entire
        // bounding range in the authorization and report its worst-case erase envelope.
        return new FlashRange(first.Address, last.LastAddress);
    }

    private static uint ParseAddress(string value)
    {
        var digits = value.Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];
        if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
            throw new InvalidDataException("请输入十六进制 Flash 起始地址和结束地址。");
        return address;
    }
}
