using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace McuFlasher;

internal sealed class PyOcdChipSession : IChipSession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Process _process;
    private readonly IProgress<string> _log;
    private bool _disposed;

    private PyOcdChipSession(Process process, IProgress<string> log)
    {
        _process = process;
        _log = log;
    }

    public IReadOnlyList<FlashSector> Sectors { get; private set; } = [];

    public static async Task<IChipSession> Connect(string executable, Target target, int swdKHz, IProgress<string> log)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in Cgt6Programmer.Create(new Firmware("--session", null), swdKHz, false))
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        var session = new PyOcdChipSession(process, log);
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data); };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
            var response = await session.ReadResponse(TimeSpan.FromSeconds(40)).ConfigureAwait(false);
            if (!response.Ok || response.Sectors is null)
                throw new IOException(response.Error ?? "pyOCD 未返回 Flash 页表。");
            FlashGeometry.Validate(target, response.Sectors);
            session.Sectors = Array.AsReadOnly(response.Sectors);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task Erase(IReadOnlyList<FlashSector> sectors)
    {
        if (sectors.Count == 0 || sectors.Any(sector => !Sectors.Contains(sector)))
            throw new InvalidDataException("擦除请求包含不在当前会话页表中的地址。");
        return Send(new { command = "erase", addresses = sectors.Select(sector => sector.Address).Distinct() },
            TimeSpan.FromMinutes(5));
    }

    public Task Program(string absolutePath, string? address) =>
        Send(new { command = "program", path = absolutePath, address = address ?? "-" }, TimeSpan.FromMinutes(5));

    public Task ResetAndRun() => Send(new { command = "run" }, TimeSpan.FromSeconds(15));

    private async Task Send(object command, TimeSpan timeout)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions)).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        var reply = await ReadResponse(timeout).ConfigureAwait(false);
        if (!reply.Ok)
            throw new IOException(reply.Error ?? "pyOCD 操作失败。");
    }

    private async Task<Reply> ReadResponse(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("@mcu:", StringComparison.Ordinal))
                    return JsonSerializer.Deserialize<Reply>(line.AsSpan(5), JsonOptions)
                        ?? throw new IOException("pyOCD 返回了空响应。");
                if (!string.IsNullOrWhiteSpace(line))
                    _log.Report(line);
            }
        }
        catch (OperationCanceledException)
        {
            throw new IOException("等待 pyOCD 响应超时。");
        }
        catch (JsonException ex)
        {
            throw new IOException("pyOCD 返回了无法识别的响应。", ex);
        }
        throw new IOException("pyOCD 已退出，请查看上方日志。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!_process.HasExited)
                        _process.Kill();
                }
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        finally { _process.Dispose(); }
    }

    private sealed record Reply(bool Ok, string? Error, FlashSector[]? Sectors);
}
