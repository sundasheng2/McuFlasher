using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace McuFlasher;

internal enum Backend { JLink, Cube, OpenOcd, PyOcd }

internal static partial class Programmers
{
    public static Backend Select(bool jlink, Target target) =>
        jlink ? Backend.JLink : target.Name == "GD32F303CGT6" ? Backend.PyOcd :
        target.IsGd ? Backend.OpenOcd : Backend.Cube;

    public static string Executable(Backend backend) => backend switch
    {
        Backend.JLink => "JLink.exe",
        Backend.Cube => "STM32_Programmer_CLI.exe",
        Backend.PyOcd => "python.exe",
        _ => "openocd.exe"
    };

    public static IReadOnlyList<string> Create(Backend backend, Target target, Firmware image,
        string work, int speed, bool reset)
    {
        var frequency = speed.ToString(CultureInfo.InvariantCulture);
        var address = image.Address is null ? Array.Empty<string>() : new[] { image.Address };
        switch (backend)
        {
            case Backend.JLink:
                File.WriteAllLines(Path.Combine(work, "flash.jlink"),
                    ["r", "h", $"loadfile {string.Join(' ', new[] { image.FileName }.Concat(address))}",
                        .. reset ? new[] { "r", "g" } : Array.Empty<string>(), "q"]);
                return ["-Device", target.JLinkName, "-If", "SWD", "-Speed", frequency,
                    "-AutoConnect", "1", "-NoGui", "1", "-ExitOnError", "1", "-CommandFile", "flash.jlink"];
            case Backend.Cube:
                return ["-c", "port=SWD", $"freq={frequency}", "-w", image.FileName, .. address, "-v",
                    .. reset ? new[] { "-rst" } : Array.Empty<string>()];
            case Backend.PyOcd:
                return Cgt6Programmer.Create(image, speed, reset);
            case Backend.OpenOcd:
                var resetOption = reset ? " reset" : "";
                var suffix = image.Address is null ? "" : " " + image.Address;
                return ["-c", "gdb_port disabled; tcl_port disabled; telnet_port disabled",
                    "-f", "interface/stlink.cfg", "-c", "set CPUTAPID 0x2ba01477",
                    "-f", "target/stm32f1x.cfg", "-c", $"adapter speed {frequency}",
                    "-c", "init; reset halt; flash probe 0; " +
                        "if {([lindex [read_memory 0xe000ed00 32 1] 0] & 0xfff0) != 0xc240} {error {Expected Cortex-M4}}; " +
                        "if {[lindex [read_memory 0x1ffff7e0 32 1] 0] != 0x00300100} {error {Expected 256 KiB flash and 48 KiB SRAM}}",
                    "-c", $"program {image.FileName} verify{resetOption} exit{suffix}"];
            default:
                throw new ArgumentOutOfRangeException(nameof(backend));
        }
    }

    public static bool Verified(Backend backend, IEnumerable<string> lines)
    {
        if (backend == Backend.JLink)
        {
            var downloading = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("Downloading file [", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("firmware.", StringComparison.OrdinalIgnoreCase))
                    downloading = true;
                if (downloading && line.Equals("O.K.", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        if (backend == Backend.PyOcd)
            return lines.Contains(Cgt6Programmer.VerifiedMessage);
        var marker = backend == Backend.Cube ? "Download verified successfully" : "** Verified OK **";
        return lines.Any(line => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static string CheckStm32Target(IEnumerable<string> lines, Target target)
    {
        var output = string.Join('\n', lines);
        var device = DeviceName().Match(output);
        var flash = FlashSize().Match(output);
        if (!device.Success || !flash.Success ||
            !decimal.TryParse(flash.Groups[1].Value.Replace(',', '.'), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var kib))
            throw new InvalidDataException("未能从 STM32CubeProgrammer 日志识别芯片或 Flash 容量。");
        if (flash.Groups[2].Value.Equals("M", StringComparison.OrdinalIgnoreCase))
            kib *= 1024;
        if (!device.Groups[1].Value.StartsWith(target.Name[..7], StringComparison.OrdinalIgnoreCase)
            || kib != target.FlashKiB)
            throw new InvalidDataException($"检测到 {device.Groups[1].Value}（{kib} KiB），与所选 {target.Name}（{target.FlashKiB} KiB）不一致。");
        var serial = ProbeSerial().Match(output);
        if (!serial.Success)
            throw new InvalidDataException("未能读取 ST-LINK 序列号，无法确认烧录时仍使用同一个下载器。");
        return serial.Groups[1].Value;
    }

    [GeneratedRegex(@"(?im)^\s*Device name\s*:\s*(STM32[A-Z0-9]+)")]
    private static partial Regex DeviceName();

    [GeneratedRegex(@"(?im)^\s*Flash size\s*:\s*(\d+(?:[.,]\d+)?)\s*([KM])(?:i)?B(?:ytes?)?\b")]
    private static partial Regex FlashSize();

    [GeneratedRegex(@"(?im)^\s*ST-LINK SN\s*:\s*([0-9A-F]+)[ \t]*$")]
    private static partial Regex ProbeSerial();

    public static async Task<IReadOnlyList<string>> Run(string executable, IReadOnlyList<string> arguments,
        string directory, IProgress<string> progress)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        var lines = new List<string>();
        var gate = new object();
        void Record(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            text = text.Trim();
            lock (gate)
            {
                lines.Add(text);
                progress.Report(text);
            }
        }
        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"{Path.GetFileName(executable)} 退出代码为 {process.ExitCode}。请查看上方日志。");
        return lines;
    }
}
