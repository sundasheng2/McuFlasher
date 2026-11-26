using System.IO;

namespace McuFlasher;

internal interface IChipSession : IAsyncDisposable
{
    IReadOnlyList<FlashSector> Sectors { get; }
    Task Erase(IReadOnlyList<FlashSector> sectors);
    Task Program(string absolutePath, string? address);
    Task ResetAndRun();
}

internal sealed record FlashSector(int Bank, int Index, uint Address, uint Size)
{
    public uint LastAddress => checked(Address + Size - 1);
}

internal static class FlashGeometry
{
    public const uint StartAddress = 0x08000000;

    public static (int DeviceId, uint CapacityAddress) IdentityFor(Target target)
    {
        int id = target.Name switch
        {
            "STM32F100C8" => 0x420,
            "STM32F101CB" or "STM32F103C8" or "STM32F103CB" or "STM32F103RB" => 0x410,
            "STM32F103RC" or "STM32F103RE" or "STM32F103VE" or "STM32F103ZE" => 0x414,
            "STM32F107VC" => 0x418,
            "STM32F401CC" => 0x423,
            "STM32F401CE" => 0x433,
            "STM32F405RG" or "STM32F407VE" or "STM32F407VG" or "STM32F407ZG" => 0x413,
            "STM32F411CE" => 0x431,
            "STM32F412ZG" => 0x441,
            "STM32F429ZI" => 0x419,
            "STM32F446RE" => 0x421,
            "STM32L011F4" => 0x457,
            "STM32L031K6" => 0x425,
            "STM32L051C8" or "STM32L052K8" or "STM32L053R8" => 0x417,
            "STM32L071RZ" or "STM32L072CB" or "STM32L072CZ" or "STM32L073RZ" => 0x447,
            "STM32L412KB" => 0x464,
            "STM32L432KC" or "STM32L433CB" => 0x435,
            "STM32L452RE" => 0x462,
            "STM32L476RG" or "STM32L486RG" => 0x415,
            "STM32L496ZG" => 0x461,
            _ => throw new InvalidDataException("没有所选 STM32 的芯片识别配置。")
        };
        var capacityAddress = target.Name[..7] switch
        {
            "STM32F1" => 0x1FFFF7E0u,
            "STM32F4" => 0x1FFF7A22u,
            "STM32L0" => 0x1FF8007Cu,
            "STM32L4" => 0x1FFF75E0u,
            _ => throw new InvalidDataException("没有所选 STM32 的容量寄存器地址。")
        };
        return (id, capacityAddress);
    }

    public static IReadOnlyList<FlashSector> For(Target target, Func<uint, uint> readWord)
    {
        if (target.IsGd)
        {
            var density = target.Name == "GD32F303CGT6" ? 0x00600400u : 0x00300100u;
            if ((readWord(0xE000ED00) & 0xFFF0) != 0xC240 || readWord(0x1FFFF7E0) != density
                || (target.Name == "GD32F303CGT6" && (readWord(0xE0042000) & 0xFFF) != 0x430))
                throw new InvalidDataException("连接到的 GD32 内核、芯片 ID 或存储容量与所选型号不符。");
            return For(target);
        }

        var (expectedId, capacityAddress) = IdentityFor(target);
        var idAddress = target.Name.StartsWith("STM32L0", StringComparison.Ordinal) ? 0x40015800u : 0xE0042000u;
        var deviceId = readWord(idAddress) & 0xFFF;
        var capacity = (readWord(capacityAddress & ~3u) >> ((int)(capacityAddress & 3u) * 8)) & 0xFFFF;
        if (deviceId != expectedId || capacity != target.FlashKiB)
            throw new InvalidDataException($"检测到芯片 ID 0x{deviceId:X3}、{capacity} KiB Flash，与所选 {target.Name} 不符。");
        return For(target);
    }

    public static IReadOnlyList<FlashSector> For(Target target, uint? optionValue = null)
    {
        var name = target.Name;
        var bytes = checked((uint)target.FlashKiB * 1024);
        var sectors = new List<FlashSector>();
        var address = StartAddress;

        void Uniform(int bank, uint length, uint size)
        {
            for (var i = 0; i < length / size; i++, address += size)
                sectors.Add(new FlashSector(bank, i, address, size));
        }
        void F4Bank(int bank, uint length, int firstIndex)
        {
            var end = address + length;
            for (var i = 0; address < end; i++)
            {
                uint size = i < 4 ? 16u * 1024 : i == 4 ? 64u * 1024 : 128u * 1024;
                sectors.Add(new FlashSector(bank, firstIndex + i, address, size));
                address += size;
            }
        }

        if (name == "GD32F303CGT6")
        {
            Uniform(0, 512 * 1024, 2048);
            Uniform(1, 512 * 1024, 4096);
        }
        else if (name == "GD32F303CCT6")
            Uniform(0, bytes, 2048);
        else if (name.StartsWith("STM32F1", StringComparison.Ordinal))
            Uniform(0, bytes, target.FlashKiB <= 128 ? 1024u : 2048u);
        else if (name.StartsWith("STM32F4", StringComparison.Ordinal))
        {
            if (name.StartsWith("STM32F429", StringComparison.Ordinal) && bytes == 2 * 1024 * 1024)
            {
                F4Bank(0, bytes / 2, 0);
                F4Bank(1, bytes / 2, 12);
            }
            else
                F4Bank(0, bytes, 0);
        }
        else if (name.StartsWith("STM32L0", StringComparison.Ordinal))
            Uniform(0, bytes, 128);
        else if (name.StartsWith("STM32L4", StringComparison.Ordinal))
        {
            var dual = (name.StartsWith("STM32L47", StringComparison.Ordinal) ||
                        name.StartsWith("STM32L48", StringComparison.Ordinal) ||
                        name.StartsWith("STM32L49", StringComparison.Ordinal)) &&
                       (target.FlashKiB == 1024 || (optionValue.GetValueOrDefault() & (1u << 21)) != 0);
            Uniform(0, dual ? bytes / 2 : bytes, 2048);
            if (dual)
                Uniform(1, bytes / 2, 2048);
        }
        Validate(target, sectors);
        return sectors.AsReadOnly();
    }

    public static void Validate(Target target, IReadOnlyList<FlashSector> sectors)
    {
        ulong next = StartAddress;
        var keys = new HashSet<(int, int)>();
        foreach (var sector in sectors.OrderBy(s => s.Address))
        {
            if (sector.Address != next || sector.Size == 0 || sector.Bank < 0 || sector.Index < 0
                || !keys.Add((sector.Bank, sector.Index)))
                throw new InvalidDataException("芯片内部 Flash 页表存在缺页、重叠或重复编号。");
            next += sector.Size;
        }
        if (sectors.Count == 0 || next != StartAddress + (ulong)target.FlashKiB * 1024)
            throw new InvalidDataException("芯片内部 Flash 页表和所选容量不一致。");
    }

    public static string Describe(IReadOnlyList<FlashSector> selection)
    {
        var ranges = new List<(int Bank, uint Begin, uint End)>();
        foreach (var sector in selection.OrderBy(s => s.Address))
        {
            if (ranges.Count > 0 && ranges[^1].Bank == sector.Bank && ranges[^1].End + 1 == sector.Address)
            {
                var last = ranges[^1];
                ranges[^1] = (last.Bank, last.Begin, sector.LastAddress);
            }
            else
                ranges.Add((sector.Bank, sector.Address, sector.LastAddress));
        }
        return string.Join('\n', ranges.Select(range => $"0x{range.Begin:X8}–0x{range.End:X8}"));
    }
}
