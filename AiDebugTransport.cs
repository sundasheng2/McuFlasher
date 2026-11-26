using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace McuFlasher;

internal sealed record AiReply(bool Ok, object? Value = null, string? Error = null);

internal sealed class AiDebugTransport : IDisposable
{
    public const string PipeName = "mcu-flasher-ai-debug";
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly Encoding Utf8 = new UTF8Encoding(false);

    public AiDebugTransport(Func<JsonElement, Task<AiReply>> call, IProgress<string> errors)
    {
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var cancellation = _stop.Token;
        _ = Listen(call, errors, cancellation);
    }

    private async Task Listen(Func<JsonElement, Task<AiReply>> call, IProgress<string> errors, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                try
                {
                    await ServeClient(call, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // A client can close or time out before reading its reply.
                }
                finally
                {
                    // An accepted pipe may be Broken with IsConnected == false.
                    // Disconnect still has to reset it before the next accept.
                    if (!stop.IsCancellationRequested)
                        _pipe.Disconnect();
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            {
                if (!stop.IsCancellationRequested)
                    errors.Report("AI 接入监听已停止，请重新开启接入：" + ex.Message);
                return;
            }
        }
    }

    private async Task ServeClient(Func<JsonElement, Task<AiReply>> call, CancellationToken stop)
    {
        using var input = new StreamReader(_pipe, Utf8, leaveOpen: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var line = await input.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        if (line is null)
            return;
        AiReply reply;
        try
        {
            using var json = JsonDocument.Parse(line);
            reply = await call(json.RootElement).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            reply = new AiReply(false, Error: ex.Message);
        }
        await _pipe.WriteAsync(Utf8.GetBytes(JsonSerializer.Serialize(reply, Json) + "\n"), deadline.Token)
            .ConfigureAwait(false);
        // The client closes after reading the reply. Disconnecting earlier
        // can discard bytes Windows still has queued in the pipe.
        await input.ReadLineAsync(deadline.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _pipe.Dispose();
        _stop.Dispose();
    }

    public static async Task<AiReply> Request(JsonElement parameters, CancellationToken stop)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(2000, stop).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new AiReply(false, Error: "未能连接上位机的 AI 接口。若窗口已开启接入，请先检查界面是否忙碌或无响应。");
        }
        using var output = new StreamWriter(pipe, Utf8, leaveOpen: true);
        using var input = new StreamReader(pipe, Utf8, leaveOpen: true);
        await output.WriteLineAsync(parameters.GetRawText()).ConfigureAwait(false);
        await output.FlushAsync(stop).ConfigureAwait(false);
        var response = await input.ReadLineAsync(stop).ConfigureAwait(false);
        return response is not null
            ? JsonSerializer.Deserialize<AiReply>(response, Json) ?? new AiReply(false, Error: "烧录工具响应无效。")
            : new AiReply(false, Error: "烧录工具已关闭 AI 连接。");
    }
}

internal sealed record SerialCursor(int SessionId, ulong Cursor)
{
    public static async Task<SerialCursor> Connect(AiDebugScope scope)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        using var pipe = new NamedPipeClientStream(".", "stm32-serial-console-ai", PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(2500, stop.Token).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Message;
            var command = AiDebugTransport.Utf8.GetBytes(FormattableString.Invariant($"CONNECT\t{scope.Port}\t{scope.Baud}"));
            await pipe.WriteAsync(command, stop.Token).ConfigureAwait(false);
            var response = new byte[16384];
            var count = await pipe.ReadAsync(response, stop.Token).ConfigureAwait(false);
            if (count == 0 || !pipe.IsMessageComplete)
                throw new IOException("串口助手响应不完整。");
            var output = AiDebugTransport.Utf8.GetString(response, 0, count).Trim();
            var fields = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields is ["OK", var id, var cursor] &&
                int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var session) && session > 0 &&
                ulong.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                return new SerialCursor(session, offset);
            throw new IOException($"串口 COM{scope.Port} 未准备好：{output}");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            throw new IOException("串口助手未响应。请打开桌面 STM32 串口助手并开启它的「AI 接入」。", ex);
        }
    }
}
