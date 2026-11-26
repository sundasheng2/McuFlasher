using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace McuFlasher;

internal sealed partial class OpenOcdChipSession : IChipSession
{
    private const uint FlashBase = 0x08000000;
    private const uint PageSize = 2048;
    private const int PageCount = 128;
    private const int EndOfFrame = 0x1A;

    private const string CheckIdentity =
        "if {([lindex [read_memory 0xe000ed00 32 1] 0] & 0xfff0) != 0xc240} {error {Expected Cortex-M4}}; " +
        "if {[lindex [read_memory 0x1ffff7e0 32 1] 0] != 0x00300100} {error {Expected 256 KiB flash and 48 KiB SRAM}}";

    private readonly Process process;
    private readonly IProgress<string> log;
    private readonly string token;
    private readonly SemaphoreSlim gate = new(1);
    private TcpClient? client;
    private Stream? rpc;
    private bool disposed;

    private OpenOcdChipSession(Process process, IProgress<string> log, string token)
    {
        this.process = process;
        this.log = log;
        this.token = token;
    }

    public IReadOnlyList<FlashSector> Sectors { get; private set; } = Array.Empty<FlashSector>();

    public static async Task<IChipSession> Connect(string executable, Target target, int swdKHz, IProgress<string> log)
    {
        if (target.Name != "GD32F303CCT6" || target.FlashKiB != 256)
            throw new ArgumentException("此 OpenOCD 连接仅适用于 GD32F303CCT6。", nameof(target));

        var token = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] arguments =
        [
            .. OpenOcdTool.ScriptArguments(executable),
            "-c", "bindto 127.0.0.1; gdb_port disabled; telnet_port disabled; tcl_port 0",
            "-f", "interface/stlink.cfg", "-c", "set CPUTAPID 0x2ba01477",
            "-f", "target/stm32f1x.cfg", "-c", FormattableString.Invariant($"adapter speed {swdKHz}"),
            "-c", $"set mcuflasher_session {token}"
        ];
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var session = new OpenOcdChipSession(process, log, token);
        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        void Record(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            var match = ListeningPort().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number) && number is > 0 and <= 65535)
                port.TrySetResult(number);
            log.Report(line.Trim());
        }
        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        process.Exited += (_, _) => port.TrySetException(new IOException("OpenOCD 已退出。请查看连接日志。"));

        try
        {
            started = process.Start();
            if (!started)
                throw new IOException("无法启动 OpenOCD。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var localPort = await port.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            session.client = new TcpClient(AddressFamily.InterNetwork);
            await session.client.ConnectAsync(IPAddress.Loopback, localPort, timeout.Token).ConfigureAwait(false);
            session.rpc = session.client.GetStream();

            await session.Command("reset halt; " + CheckIdentity + "; flash probe 0").ConfigureAwait(false);
            var info = await session.Command("flash info 0 sectors").ConfigureAwait(false);
            session.Sectors = ReadSectors(info);
            log.Report("已识别 GD32F303CCT6 Flash：128 页，每页 2 KiB。");
            return session;
        }
        catch (Exception error)
        {
            if (started)
                await session.DisposeAsync().ConfigureAwait(false);
            else
                process.Dispose();
            if (error is OperationCanceledException)
                throw new TimeoutException("等待 OpenOCD 启动超时。请查看连接日志。", error);
            throw;
        }
    }

    public async Task Erase(IReadOnlyList<FlashSector> sectors)
    {
        var indices = new SortedSet<int>();
        foreach (var sector in sectors)
        {
            if (sector.Bank != 0 || sector.Index < 0 || sector.Index >= Sectors.Count ||
                sector != Sectors[sector.Index])
                throw new ArgumentException("擦除页不属于当前连接的 Flash。", nameof(sectors));
            indices.Add(sector.Index);
        }
        if (indices.Count == 0)
            return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Command("halt; " + CheckIdentity + "; " + EraseCommands(indices)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task Program(string absolutePath, string? address)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("固件需要使用绝对路径。", nameof(absolutePath));

        var extension = Path.GetExtension(absolutePath).ToLowerInvariant();
        uint offset;
        string type;
        if (extension == ".hex" && address is null)
        {
            offset = 0;
            type = "ihex";
        }
        else if (extension == ".bin" && address?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true &&
                 uint.TryParse(address.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset))
        {
            type = "bin";
        }
        else
        {
            throw new InvalidDataException("请选择 HEX 文件或带有效起始地址的 BIN 文件。");
        }

        var image = $"{QuotePath(absolutePath)} 0x{offset:X8} {type}";
        // Keep the staged file stable while OpenOCD opens it for test, write and verification.
        using var file = File.Open(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var description = await Command($"halt; {CheckIdentity}; test_image {image}").ConfigureAwait(false);
            var (pages, bytes) = ReadImagePages(description);
            // OpenOCD 0.12's stm32f1x driver separates write runs across completely unused sectors.
            // Explicit page erases also avoid its whole-bank mass erase when an image fills every page.
            var result = await Command($"{EraseCommands(pages)}; flash write_image {image}; verify_image {image}")
                .ConfigureAwait(false);
            var verified = VerifiedBytes().Matches(result).Cast<Match>().LastOrDefault();
            if (verified is null || ReadDecimal(verified.Groups[1].Value) != bytes)
                throw new InvalidDataException("OpenOCD 未完成对全部固件数据的校验。");
            log.Report("OpenOCD 烧录校验成功。");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetAndRun()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Command("reset run").ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string EraseCommands(SortedSet<int> indices)
    {
        var commands = new List<string>();
        var first = -1;
        var last = -1;
        foreach (var index in indices)
        {
            if (first < 0 || index != last + 1 || (first == 0 && index == PageCount - 1))
            {
                if (first >= 0)
                    commands.Add(FormattableString.Invariant($"flash erase_sector 0 {first} {last}"));
                first = index;
            }
            last = index;
        }
        commands.Add(FormattableString.Invariant($"flash erase_sector 0 {first} {last}"));
        return string.Join("; ", commands);
    }

    private static IReadOnlyList<FlashSector> ReadSectors(string info)
    {
        var sectors = SectorLine().Matches(info);
        if (!BankLine().IsMatch(info) || sectors.Count != PageCount)
            throw new InvalidDataException("OpenOCD 返回的 Flash Bank 与 GD32F303CCT6 不一致。");

        var map = new FlashSector[PageCount];
        for (var index = 0; index < map.Length; index++)
        {
            var fields = sectors[index].Groups;
            var offset = ReadHex(fields[2].Value);
            var size = ReadHex(fields[3].Value);
            if (ReadDecimal(fields[1].Value) != index || offset != (uint)index * PageSize || size != PageSize)
                throw new InvalidDataException("OpenOCD 返回的 Flash 页布局与 GD32F303CCT6 不一致。");
            map[index] = new FlashSector(0, index, FlashBase + offset, size);
        }
        return Array.AsReadOnly(map);
    }

    private static (SortedSet<int> Pages, uint Bytes) ReadImagePages(string description)
    {
        var regions = ImageRegion().Matches(description).Cast<Match>()
            .Select(match => (Address: ReadHex(match.Groups[1].Value), Size: ReadHex(match.Groups[2].Value)))
            .OrderBy(region => region.Address);
        var pages = new SortedSet<int>();
        uint bytes = 0;
        ulong previousEnd = FlashBase;
        foreach (var (address, size) in regions)
        {
            var end = (ulong)address + size;
            if (size == 0)
                continue;
            if (address < previousEnd || end > FlashBase + PageSize * PageCount)
                throw new InvalidDataException("固件区域重叠或超出目标内部 Flash。");
            var first = (address - FlashBase) / PageSize;
            var last = (end - 1 - FlashBase) / PageSize;
            for (var page = first; page <= last; page++)
                pages.Add((int)page);
            previousEnd = end;
            bytes += size;
        }
        var total = VerifiedBytes().Match(description);
        if (bytes == 0 || !total.Success || ReadDecimal(total.Groups[1].Value) != bytes)
            throw new InvalidDataException("OpenOCD 未能识别全部固件区域。");
        return (pages, bytes);
    }

    private static string QuotePath(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in path.Replace('\\', '/'))
        {
            if (char.IsControl(character))
                throw new InvalidDataException("固件路径包含控制字符。");
            if (character is '"' or '$' or '[' or ']' or '{' or '}')
                quoted.Append('\\');
            quoted.Append(character);
        }
        return quoted.Append('"').ToString();
    }

    private async Task<string> Command(string command)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (rpc is null)
            throw new IOException("OpenOCD 会话已断开，请重新连接。");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string response;
        try
        {
            var request = $"if {{[info exists mcuflasher_session] && ${{mcuflasher_session}} eq \"{token}\"}} {{ " +
                $"set status [catch {{capture {{{command}}}}} result]; format \"%s %d\\n%s\" $mcuflasher_session $status $result }}";
            await Send(request, timeout.Token).ConfigureAwait(false);
            using var data = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await rpc.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0)
                    throw new IOException("OpenOCD 已断开。请查看工具日志。");
                var end = Array.IndexOf(buffer, (byte)EndOfFrame, 0, count);
                data.Write(buffer, 0, end < 0 ? count : end);
                if (data.Length > 4 * 1024 * 1024)
                    throw new IOException("OpenOCD 返回的数据超出允许长度。");
                if (end >= 0)
                    break;
            }
            response = Encoding.UTF8.GetString(data.ToArray());
        }
        catch (OperationCanceledException error)
        {
            CloseRpc();
            throw new TimeoutException("等待 OpenOCD 执行超时，请重新连接。", error);
        }
        catch (IOException)
        {
            CloseRpc();
            throw;
        }

        var separator = response.IndexOf('\n');
        if (separator < 0 || !response.StartsWith(token + " ", StringComparison.Ordinal))
        {
            CloseRpc();
            throw new IOException("OpenOCD 会话响应无法识别，请重新连接。");
        }
        var status = response[(token.Length + 1)..separator];
        var output = response[(separator + 1)..].Trim();
        if (output.Length > 0)
            log.Report(output);
        if (status != "0")
            throw new IOException("OpenOCD 命令执行失败。请查看工具日志。");
        return output;
    }

    private Task Send(string command, CancellationToken cancellationToken) =>
        rpc!.WriteAsync(Encoding.UTF8.GetBytes(command + (char)EndOfFrame), cancellationToken).AsTask();

    private void CloseRpc()
    {
        client?.Dispose();
        client = null;
        rpc = null;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (rpc is not null)
                {
                    await Send($"if {{[info exists mcuflasher_session] && $mcuflasher_session eq \"{token}\"}} {{shutdown}}",
                        timeout.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            finally
            {
                CloseRpc();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                finally
                {
                    process.Dispose();
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static uint ReadHex(string text) => uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static uint ReadDecimal(string text) => uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\bListening on port (\d+) for tcl connections\b")]
    private static partial Regex ListeningPort();

    [GeneratedRegex(@"(?im)^\s*#0\s*:\s*stm32f1x\s+at\s+0x0*8000000,\s*size\s+0x0*40000\b")]
    private static partial Regex BankLine();

    [GeneratedRegex(@"(?im)^\s*#\s*(\d+)\s*:\s*0x([\da-f]+)\s+\(0x([\da-f]+)\s")]
    private static partial Regex SectorLine();

    [GeneratedRegex(@"(?im)^\s*address\s+0x([\da-f]+)\s+length\s+0x([\da-f]+)\s*$")]
    private static partial Regex ImageRegion();

    [GeneratedRegex(@"(?im)^\s*verified\s+(\d+)\s+bytes\b")]
    private static partial Regex VerifiedBytes();
}
