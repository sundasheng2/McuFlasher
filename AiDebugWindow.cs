using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace McuFlasher;

public partial class MainWindow
{
    private const int AiLogCapacity = 300;
    private readonly Queue<(long Cursor, string Text)> _aiLog = new();
    private long _aiCursor;
    private AiDebugTransport? _aiTransport;
    private AiAuthorization? _aiAuthorization;
    private AiJob? _aiJob;
    private bool _chipRunning;
    private CancellationTokenSource? _aiPreparation;

    private void ToggleAiDebug(object sender, RoutedEventArgs e)
    {
        if (_aiPreparation is { } preparation)
        {
            AiDebugButton.IsEnabled = false;
            preparation.Cancel();
            return;
        }
        if (_busy)
            return;
        if (_aiTransport is not null)
        {
            StopAiDebug();
            StatusText.Text = "AI 调试接入已关闭";
            EndOperation();
            return;
        }
        if (_chip is not { } chip || _activeBackend is not Backend backend || TargetBox.SelectedItem is not Target target)
        {
            StatusText.Text = "先连接本轮要调试的芯片";
            return;
        }
        var scope = AiDebugDialog.Show(this, target, (string)((ComboBoxItem)ProbeBox.SelectedItem).Content, chip.Sectors);
        if (scope is null)
            return;

        _aiLog.Clear();
        _aiCursor = 0;
        _aiJob = null;
        _aiAuthorization = new AiAuthorization(scope, target, backend, chip);
        try
        {
            _aiTransport = new AiDebugTransport(request =>
                Dispatcher.InvokeAsync(() => HandleAiRequest(request)).Task.Unwrap(), new Progress<string>(AppendLog));
            StatusText.Text = $"AI 调试接入已开启 · COM{scope.Port} · {scope.Allowed}";
            AppendLog(StatusText.Text);
        }
        catch (Exception ex) when (IsToolError(ex))
        {
            StopAiDebug();
            StatusText.Text = "无法开启 AI 调试接入";
            AppendLog(ex.Message);
        }
        EndOperation();
    }

    private void StopAiDebug()
    {
        _aiAuthorization = null;
        _aiTransport?.Dispose();
        _aiTransport = null;
        _aiJob = null;
        _aiLog.Clear();
    }

    private async Task<AiReply> HandleAiRequest(JsonElement request)
    {
        if (_aiAuthorization is not { } authorization)
            return new AiReply(false, Error: "本轮 AI 调试授权已关闭。");
        if (request.ValueKind != JsonValueKind.Object)
            return new AiReply(false, Error: "无效的 AI 请求。");
        if (!request.TryGetProperty("arguments", out var args))
            args = default;

        var tool = AiMcpRelay.Field(request, "name");
        switch (tool)
        {
            case "flash_status":
                return new AiReply(true, new
                {
                    sessionId = authorization.Scope.Id,
                    target = authorization.Target.Name,
                    backend = authorization.Backend.ToString(),
                    connection = _chip is null ? "disconnected" : _chipRunning ? "running" :
                        _busy ? "busy" : "halted",
                    writable = !authorization.Blocked && ReferenceEquals(_chip, authorization.Chip),
                    port = $"COM{authorization.Scope.Port}",
                    baud = authorization.Scope.Baud,
                    allowed = authorization.Scope.Allowed.ToString(),
                    logCursor = _aiCursor,
                    job = _aiJob
                });
            case "flash_read_log":
                long cursor = 0;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("cursor", out var value)
                    && (!value.TryGetInt64(out cursor) || cursor < 0 || cursor > _aiCursor))
                    return new AiReply(false, Error: "日志游标无效，请从当前会话的 0 重新读取。");
                var next = _aiLog.Where(line => line.Cursor > cursor).Take(60).ToArray();
                return new AiReply(true, new
                {
                    nextCursor = next.Length == 0 ? _aiCursor : next[^1].Cursor,
                    gap = next.Length == 0 ? cursor < _aiCursor : next[0].Cursor > cursor + 1,
                    lines = next.Select(line => line.Text)
                });
            case "flash_inspect_firmware":
            case "flash_start_program":
                break;
            default:
                return new AiReply(false, Error: "烧录工具不支持该操作。");
        }

        if (AiMcpRelay.Field(args, "sessionId") != authorization.Scope.Id || authorization.Blocked
            || !ReferenceEquals(_chip, authorization.Chip))
            return new AiReply(false, Error: "授权已更换或目标连接已失效，请在上位机结束并重新开启本轮。");
        if (_busy)
            return new AiReply(false, Error: "已有操作进行中，请先查询 flash_status。");
        var path = AiMcpRelay.Field(args, "path");
        if (path is null || !Path.IsPathFullyQualified(path) || path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':')
            return new AiReply(false, Error: "请提供本地已编译固件的完整路径。");
        var address = AiMcpRelay.Field(args, "address") ?? "";
        if (tool == "flash_inspect_firmware")
        {
            using var image = await AiPreparation.Run(token => AiImage.Stage(path, address, authorization, token),
                TimeSpan.FromSeconds(12), CancellationToken.None);
            return new AiReply(true, image.Description);
        }

        var sha = AiMcpRelay.Field(args, "sha256");
        if (sha is not { Length: 64 } || !sha.All(char.IsAsciiHexDigit))
            return new AiReply(false, Error: "请先检查固件并提交返回的 SHA-256。");
        var job = new AiJob(Guid.NewGuid().ToString("N"), "running");
        _aiJob = job;
        _ = RunAiJob(authorization, job, path, address, sha);
        return new AiReply(true, new { jobId = job.Id, state = job.State });
    }

    private async Task RunAiJob(AiAuthorization authorization, AiJob job, string path, string address, string sha)
    {
        var writing = false;
        SerialCursor? serial = null;
        using var preparation = new CancellationTokenSource();
        try
        {
            BeginOperation("AI 正在校验固件和授权范围…");
            _aiPreparation = preparation;
            AiDebugButton.Content = "取消本次准备";
            AiDebugButton.IsEnabled = true;
            AppendLog($"AI 调试 {job.Id} · 开始准备 {Path.GetFileName(path)}");
            var progress = new Progress<string>(line =>
            {
                if (ReferenceEquals(_aiPreparation, preparation))
                    AppendLog(line);
            });
            using var image = await AiPreparation.Run(
                token => AiImage.Stage(path, address, authorization, token, progress),
                TimeSpan.FromSeconds(20), preparation.Token);
            preparation.Token.ThrowIfCancellationRequested();
            _aiPreparation = null;
            AiDebugButton.IsEnabled = false;
            if (!image.Description.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("固件自上次检查后已变化，请重新检查再提交。");

            StatusText.Text = $"AI 正在准备 COM{authorization.Scope.Port} 串口日志…";
            serial = await SerialCursor.Connect(authorization.Scope);
            AppendLog($"AI 调试 {job.Id} · {Path.GetFileName(path)} · 写入 {image.Description.DataRange} · 擦除边界 {image.Description.EraseRange}");
            FirmwareBox.Text = path;
            if (image.Firmware.Address is { } binAddress)
                AddressBox.Text = binAddress;
            StatusText.Text = "AI 正在烧录并校验…";
            writing = true;
            _chipRunning = false;
            await authorization.Chip.Program(image.Path, image.Firmware.Address);
            await authorization.Chip.ResetAndRun();
            _chipRunning = true;
            _aiJob = job with { State = "succeeded", Firmware = image.Description, Serial = serial };
            StatusText.Text = $"AI 烧录校验成功，已复位运行 · COM{authorization.Scope.Port}";
            AppendLog(StatusText.Text);
        }
        catch (Exception ex) when (IsToolError(ex) || ex is OperationCanceledException)
        {
            if (writing)
            {
                authorization.Blocked = true;
                await DisconnectChip();
            }
            var error = ex is OperationCanceledException && preparation.IsCancellationRequested
                ? "已取消固件准备。本次未擦写芯片。" : ex.Message;
            _aiJob = job with { State = "failed", Error = error, Serial = serial };
            StatusText.Text = writing ? "AI 烧录失败，本轮写入已停止" : "AI 调试未开始烧录";
            AppendLog($"{StatusText.Text}：{error}");
        }
        finally
        {
            _aiPreparation = null;
            EndOperation();
        }
    }

    private sealed record AiAuthorization(AiDebugScope Scope, Target Target, Backend Backend, IChipSession Chip)
    {
        public bool Blocked { get; set; }
    }

    private sealed record AiJob(string Id, string State, AiFirmwareDetails? Firmware = null,
        SerialCursor? Serial = null, string? Error = null);

    private sealed record AiFirmwareDetails(string Sha256, string DataRange, string EraseRange, string? BinAddress);

    private sealed class AiImage : IDisposable
    {
        private readonly DirectoryInfo _work;
        private readonly FileStream _held;
        public Firmware Firmware { get; }
        public string Path { get; }
        public AiFirmwareDetails Description { get; }

        private AiImage(DirectoryInfo work, Firmware firmware, string path, FileStream held,
            FlashRange eraseRange, string sha256)
        {
            _work = work;
            Firmware = firmware;
            Path = path;
            _held = held;
            Description = new AiFirmwareDetails(sha256, firmware.Range.ToString(), eraseRange.ToString(), firmware.Address);
        }

        public static AiImage Stage(string path, string address, AiAuthorization auth,
            CancellationToken cancellation, IProgress<string>? progress = null)
        {
            var work = Directory.CreateTempSubdirectory("mcu-flasher-ai-");
            FileStream? held = null;
            try
            {
                var image = Firmware.Stage(path, work.FullName, address, auth.Target, cancellation, progress);
                progress?.Report("正在检查授权擦除页范围…");
                var erase = auth.Scope.Check(image.Range, auth.Chip.Sectors);
                var staged = System.IO.Path.Combine(work.FullName, image.FileName);
                cancellation.ThrowIfCancellationRequested();
                progress?.Report("正在锁定临时固件并读取 SHA-256…");
                held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = SHA256.HashDataAsync(held, cancellation).AsTask().GetAwaiter().GetResult();
                cancellation.ThrowIfCancellationRequested();
                progress?.Report("临时固件 SHA-256 已计算。");
                return new AiImage(work, image, staged, held, erase, Convert.ToHexString(hash));
            }
            catch
            {
                held?.Dispose();
                Cleanup(work);
                throw;
            }
        }

        public void Dispose() => _ = Task.Run(() =>
        {
            _held.Dispose();
            Cleanup(_work);
        });

        private static void Cleanup(DirectoryInfo work)
        {
            try
            {
                foreach (var file in work.EnumerateFiles())
                    file.Delete();
                work.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal static class AiDebugDialog
{
    public static AiDebugScope? Show(Window owner, Target target, string probe, IReadOnlyList<FlashSector> sectors)
    {
        var dialog = new Window
        {
            Title = "授权 AI 调试", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, Width = 500, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontSize = 14
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = $"{probe} · {target.Name} · {target.FlashKiB} KiB Flash",
            FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "授权期间，AI 可通过当前连接反复烧录并复位。请填写本轮串口和允许擦写的完整 Flash 页范围。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12)
        });
        var port = AddField(panel, "串口（例如 COM7）", "");
        var baud = AddField(panel, "波特率", "115200");
        var cgt6 = target.Name == "GD32F303CGT6";
        var start = AddField(panel, "允许的起始地址", cgt6 ? "0x08010000" : "");
        var end = AddField(panel, "允许的最后地址（包含）", cgt6 ? "0x0807F7FF" : "");
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
        var accept = new Button { Content = "开启本轮授权", Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 5) };
        AiDebugScope? scope = null;
        accept.Click += (_, _) =>
        {
            try
            {
                scope = AiDebugScope.Create(port.Text, baud.Text, start.Text, end.Text, target, sectors);
                dialog.DialogResult = true;
            }
            catch (InvalidDataException ex) { error.Text = ex.Message; }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => port.Focus();
        return dialog.ShowDialog() == true ? scope : null;
    }

    private static TextBox AddField(StackPanel panel, string label, string initial)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var field = new TextBox { Text = initial, MinWidth = 160, Padding = new Thickness(4) };
        var caption = new Label { Content = label, Target = field, Width = 190 };
        DockPanel.SetDock(caption, Dock.Left);
        row.Children.Add(caption);
        row.Children.Add(field);
        panel.Children.Add(row);
        return field;
    }
}
