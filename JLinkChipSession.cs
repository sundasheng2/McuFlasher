using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace McuFlasher;

internal sealed class JLinkChipSession : IChipSession
{
    private readonly Process _process;
    private readonly DirectoryInfo _work;
    private readonly IProgress<string> _log;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _firstReply = true;
    private bool _disposed;

    private JLinkChipSession(Process process, DirectoryInfo work, IProgress<string> log)
    {
        _process = process;
        _work = work;
        _log = log;
    }

    public IReadOnlyList<FlashSector> Sectors { get; private set; } = [];

    public static async Task<IChipSession> Connect(string executable, Target target, int swdKHz, IProgress<string> log)
    {
        var work = Directory.CreateTempSubdirectory("McuFlasher-JLink-");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = work.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-Device", target.JLinkName, "-If", "SWD", "-Speed",
                     swdKHz.ToString(CultureInfo.InvariantCulture), "-AutoConnect", "1",
                     "-NoGui", "1", "-ExitOnError", "1" })
            start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start };
        var session = new JLinkChipSession(process, work, log);
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data); };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
            await session.Command("exec SetRestartOnClose = 0").ConfigureAwait(false);
            await session.Halt().ConfigureAwait(false);
            // An old Commander without range support must never receive an Erase command.
            var help = await session.Command("? erase").ConfigureAwait(false);
            if (!help.Any(line => line.StartsWith("Syntax:", StringComparison.OrdinalIgnoreCase)
                                  && line.Contains("SAddr", StringComparison.OrdinalIgnoreCase)
                                  && line.Contains("EAddr", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("此版本 J-Link Commander 未报告按地址范围擦除功能。");
            session.Sectors = await Task.Run(() => FlashGeometry.For(target, session.ReadWord)).ConfigureAwait(false);
            await session.Command("r").ConfigureAwait(false);
            await session.Halt().ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private uint ReadWord(uint address)
    {
        var lines = Command($"mem32 0x{address:X8}, 1").GetAwaiter().GetResult();
        foreach (var line in lines)
        {
            var match = Regex.Match(line, $@"^(?:0x)?{address:X8}\s*=\s*([0-9a-f]{{8}})\b", RegexOptions.IgnoreCase);
            if (match.Success)
                return uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        throw new IOException($"无法从 J-Link 读取芯片寄存器 0x{address:X8}。");
    }

    public async Task Erase(IReadOnlyList<FlashSector> sectors)
    {
        var selected = sectors.Distinct().OrderBy(s => s.Address).ToArray();
        if (selected.Length == 0 || selected.Any(s => !Sectors.Contains(s)))
            throw new InvalidDataException("擦除请求包含不在当前连接页表中的地址。");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Halt().ConfigureAwait(false);
            for (var i = 0; i < selected.Length; i++)
            {
                var first = selected[i];
                while (i + 1 < selected.Length && selected[i + 1].Bank == first.Bank
                       && selected[i + 1].Address == selected[i].LastAddress + 1)
                    i++;
                var command = $"erase 0x{first.Address:X8} 0x{selected[i].LastAddress:X8} noreset";
                var result = await Command(command, 300).ConfigureAwait(false);
                if (!result.Any(line => line.Contains("Erasing done", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException($"J-Link 未确认擦除范围 0x{first.Address:X8}–0x{selected[i].LastAddress:X8}。");
            }
            await Halt().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task Program(string absolutePath, string? address)
    {
        var ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        if (ext is not (".bin" or ".hex") || ext == ".bin" && address is null ||
            address is not null && !Regex.IsMatch(address, @"\A0x[0-9a-f]{1,8}\z", RegexOptions.IgnoreCase))
            throw new InvalidDataException("J-Link 固件格式或写入地址无效。");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Halt().ConfigureAwait(false);
            // A relative ASCII file name also works on older ANSI versions of Commander.
            var name = "firmware" + ext;
            File.Copy(absolutePath, Path.Combine(_work.FullName, name), true);
            await Command("exec SetVerifyDownload = 1").ConfigureAwait(false);
            var reply = await Command($"loadfile {name} {address ?? "0"} noreset", 300).ConfigureAwait(false);
            if (!Programmers.Verified(Backend.JLink, reply))
                throw new IOException("J-Link 未确认固件写入和校验成功。");
            await Halt().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ResetAndRun()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Command("r").ConfigureAwait(false);
            await Command("g").ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task Halt()
    {
        var state = await Command("h\nih").ConfigureAwait(false);
        if (!state.Any(line => line.StartsWith("CPU is halted", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("J-Link 未确认 CPU 已暂停。");
    }

    private async Task<List<string>> Command(string command, int seconds = 45)
    {
        if (_disposed || _process.HasExited)
            throw new IOException("J-Link 连接已关闭，请重新连接。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            // Commander 7.x fully buffers stdout on Windows pipes. Help provides harmless,
            // distinct reply boundaries, and its long command list flushes the closing boundary.
            await _process.StandardInput.WriteLineAsync($"? sleep\n{command}\n? log\n?".AsMemory(), deadline.Token)
                .ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            var lines = new List<string>();
            var sawHeader = false;
            var inReply = false;
            while (await _process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } raw)
            {
                var line = raw.Replace("J-Link>", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!inReply)
                {
                    if (line.Equals("Command long name: Sleep", StringComparison.OrdinalIgnoreCase))
                    {
                        sawHeader = true;
                        _firstReply = false;
                    }
                    else if (sawHeader && line.StartsWith("------", StringComparison.Ordinal))
                        inReply = true;
                    else if (_firstReply && line.Length != 0)
                        _log.Report(line);
                    continue;
                }
                if (line.Equals("Command long name: Log", StringComparison.OrdinalIgnoreCase))
                    return lines;
                if (line.Length == 0 || line.StartsWith("------", StringComparison.Ordinal))
                    continue;
                _log.Report(line);
                if (Regex.IsMatch(line, @"\b(?:failed|error|cannot|unknown command|not supported)\b", RegexOptions.IgnoreCase)
                    || !command.StartsWith('?') && line.StartsWith("Syntax:", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("J-Link 操作失败：" + line);
                lines.Add(line);
            }
            throw new IOException("J-Link 已退出，请查看连接日志并重新连接。");
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            throw new IOException("等待 J-Link 响应超时，连接已结束。");
        }
        catch (IOException)
        {
            KillProcess();
            throw;
        }
    }

    private void KillProcess()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    var drain = _process.StandardOutput.ReadToEndAsync();
                    await _process.StandardInput.WriteLineAsync("exit").ConfigureAwait(false);
                    _process.StandardInput.Close();
                    await Task.WhenAll(drain, _process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                KillProcess();
            }
            finally
            {
                _process.Dispose();
                try { _work.Delete(recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Report(ex.Message); }
            }
        }
        finally { _gate.Release(); }
    }
}
