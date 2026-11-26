using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace McuFlasher;

public partial class MainWindow : Window
{
    private static readonly Dictionary<string, (string Model, int KiB)[]> Catalog = new()
    {
        ["STM32F1"] =
        [
            ("STM32F103C8", 64), ("STM32F100C8", 64), ("STM32F101CB", 128),
            ("STM32F103CB", 128), ("STM32F103RB", 128), ("STM32F103RC", 256),
            ("STM32F103RE", 512), ("STM32F103VE", 512), ("STM32F103ZE", 512), ("STM32F107VC", 256)
        ],
        ["STM32F4"] =
        [
            ("STM32F401CC", 256), ("STM32F401CE", 512), ("STM32F405RG", 1024),
            ("STM32F407VE", 512), ("STM32F407VG", 1024), ("STM32F407ZG", 1024),
            ("STM32F411CE", 512), ("STM32F412ZG", 1024), ("STM32F429ZI", 2048), ("STM32F446RE", 512)
        ],
        ["STM32L0"] =
        [
            ("STM32L011F4", 16), ("STM32L031K6", 32), ("STM32L051C8", 64),
            ("STM32L052K8", 64), ("STM32L053R8", 64), ("STM32L071RZ", 192),
            ("STM32L072CB", 128), ("STM32L072CZ", 192), ("STM32L073RZ", 192)
        ],
        ["STM32L4"] =
        [
            ("STM32L412KB", 128), ("STM32L432KC", 256), ("STM32L433CB", 128),
            ("STM32L452RE", 512), ("STM32L476RG", 1024), ("STM32L486RG", 1024),
            ("STM32L496ZG", 1024)
        ],
        ["GD32F303"] = [("GD32F303CCT6", 256), ("GD32F303CGT6", 1024)]
    };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "McuFlasher", "tools.json");

    private readonly Dictionary<Backend, string> _paths;
    private Backend? _activeBackend;
    private bool _loadingTool;
    private bool _ready;
    private bool _busy;
    private IChipSession? _chip;
    private SectorRow[] _sectorRows = [];
    private const int MaxVisibleLogChars = 64 * 1024;
    private readonly StringBuilder _pendingVisibleLog = new();
    private readonly DispatcherTimer _logFlushTimer;
    private int _visibleLogLength;

    public MainWindow()
    {
        _paths = LoadPaths();
        InitializeComponent();
        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _logFlushTimer.Tick += (_, _) => FlushVisibleLog();
        _ready = true;
        FamilyBox.ItemsSource = Catalog.Keys;
        FamilyBox.SelectedIndex = 0;
        ProbeBox.SelectedIndex = 0;
        FirmwareChanged(this, null);
    }

    private void FamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || FamilyBox.SelectedItem is not string family)
            return;
        TargetBox.ItemsSource = Catalog[family].Select(item =>
            new Target(item.Model, item.Model == "GD32F303CCT6" ? "GD32F303CC" : item.Model, item.KiB));
        TargetBox.SelectedIndex = 0;
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ProbeBox.SelectedIndex < 0 || TargetBox.SelectedItem is not Target target)
            return;
        CapacityText.Text = $"{target.FlashKiB} KiB Flash";
        var backend = Programmers.Select(ProbeBox.SelectedIndex == 0, target);
        if (_activeBackend == backend)
            return;
        _activeBackend = backend;
        var tool = Programmers.Executable(backend);
        ToolLabel.Content = backend switch
        {
            Backend.JLink => "J-Link 工具",
            Backend.Cube => "CubeProgrammer",
            Backend.PyOcd => "Python（pyOCD）",
            _ => "OpenOCD"
        };
        InstallToolButton.Content = backend == Backend.PyOcd ? "一键安装" : "下载工具…";
        _loadingTool = true;
        ToolBox.Text = _paths.TryGetValue(backend, out var saved) && File.Exists(saved) ? saved : FindTool(backend);
        ToolBox.ToolTip = tool;
        _loadingTool = false;
        StatusText.Text = File.Exists(ToolBox.Text) ? "请选择固件，或开始烧录" : $"请选择 {tool}";
    }

    private void ToolChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingTool && _activeBackend is Backend backend)
            _paths[backend] = ToolBox.Text;
    }

    private void ChooseTool(object sender, RoutedEventArgs e)
    {
        if (_activeBackend is not Backend backend)
            return;
        var name = Programmers.Executable(backend);
        var dialog = new OpenFileDialog { Title = $"选择 {name}", Filter = $"{name}|{name}", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            ToolBox.Text = dialog.FileName;
            SavePaths();
        }
    }

    private async void InstallTool(object sender, RoutedEventArgs e)
    {
        if (_busy || _activeBackend is not Backend backend)
            return;
        if (backend != Backend.PyOcd)
        {
            var page = backend switch
            {
                Backend.JLink => "https://www.segger.com/downloads/jlink/",
                Backend.Cube => "https://www.st.com/en/development-tools/stm32cubeprog.html",
                _ => "https://openocd.org/pages/getting-openocd.html"
            };
            try
            {
                Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                StatusText.Text = "安装完成后，点击「选择工具」填写路径";
            }
            catch (Win32Exception ex)
            {
                StatusText.Text = "无法打开下载页面";
                AppendLog($"{page} · {ex.Message}");
            }
            return;
        }

        _busy = true;
        SettingsPanel.IsEnabled = false;
        ActivityBar.Visibility = Visibility.Visible;
        ActivityBar.IsIndeterminate = true;
        StatusText.Text = "正在安装并检查烧录工具…";
        try
        {
            ToolBox.Text = await PythonToolInstaller.Install(AppContext.BaseDirectory,
                PythonToolInstaller.ToolDirectory, new Progress<string>(AppendLog));
            SavePaths();
            StatusText.Text = "烧录工具已准备好";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
        {
            StatusText.Text = "工具安装未完成，请查看日志";
            AppendLog(ex.Message);
        }
        finally
        {
            ActivityBar.IsIndeterminate = false;
            ActivityBar.Visibility = Visibility.Collapsed;
            SettingsPanel.IsEnabled = true;
            _busy = false;
        }
    }

    private void ChooseFirmware(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择固件",
            Filter = "固件文件 (*.hex;*.bin)|*.hex;*.bin",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            FirmwareBox.Text = dialog.FileName;
    }

    private void FirmwareChanged(object sender, TextChangedEventArgs? e)
    {
        if (_ready)
            AddressBox.IsEnabled = IsBinary(FirmwareBox.Text.Trim().Trim('"'));
    }

    private void FirmwareDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
            (IsBinary(paths[0]) || paths[0].EndsWith(".hex", StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FirmwareDrop(object sender, DragEventArgs e)
    {
        FirmwareDragOver(sender, e);
        if (e.Effects == DragDropEffects.Copy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            FirmwareBox.Text = paths[0];
    }

    private static bool IsBinary(string path) => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    private async void ConnectChip(object sender, RoutedEventArgs e)
    {
        if (_busy || _activeBackend is not Backend backend || TargetBox.SelectedItem is not Target target)
            return;
        if (_chip is not null)
        {
            BeginOperation("正在断开连接…");
            try
            {
                await DisconnectChip();
                StatusText.Text = "已断开连接";
            }
            finally { EndOperation(); }
            return;
        }
        var tool = ToolBox.Text.Trim().Trim('"');
        if (!File.Exists(tool))
        {
            StatusText.Text = $"请选择 {Programmers.Executable(backend)}";
            return;
        }
        var speed = SwdSpeed();
        var progress = new Progress<string>(AppendLog);
        BeginOperation("正在连接芯片并暂停内核…");
        try
        {
            tool = Path.GetFullPath(tool);
            _paths[backend] = tool;
            SavePaths();
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 连接 {target.Name} · {Path.GetFileName(tool)} · SWD {speed} kHz");
            _chip = await (backend switch
            {
                Backend.JLink => JLinkChipSession.Connect(tool, target, speed, progress),
                Backend.Cube => CubeChipSession.Connect(tool, target, speed, progress),
                Backend.OpenOcd => OpenOcdChipSession.Connect(tool, target, speed, progress),
                Backend.PyOcd => PyOcdChipSession.Connect(tool, target, speed, progress),
                _ => throw new InvalidOperationException()
            });
            FlashGeometry.Validate(target, _chip.Sectors);
            _sectorRows = _chip.Sectors.OrderBy(sector => sector.Address).Select(sector =>
                new SectorRow(sector, target.Name == "GD32F303CGT6" && sector.Address < 0x08010000)).ToArray();
            SectorGrid.ItemsSource = _sectorRows;
            StatusText.Text = "芯片已连接，内核已暂停";
            AppendLog($"{StatusText.Text} · {_sectorRows.Length} 个 Flash 页／扇区");
        }
        catch (Exception ex) when (IsToolError(ex))
        {
            await DisconnectChip();
            StatusText.Text = "连接失败，请查看日志";
            AppendLog(ex.Message);
        }
        finally { EndOperation(); }
    }

    private async void ErasePages(object sender, RoutedEventArgs e)
    {
        if (_busy || _chip is not { } chip)
            return;
        var selection = _sectorRows.Where(row => row.IsSelected).ToArray();
        if (selection.Length == 0)
        {
            EraseHint.Text = "请勾选要擦除的页或扇区";
            return;
        }

        var sectors = selection.Select(row => row.Sector).ToArray();
        var message = $"将擦除 {sectors.Length} 个页／扇区，范围如下。\n\n{FlashGeometry.Describe(sectors)}";
        if (selection.Any(row => row.IsBootloader))
            message += "\n\n选中范围包含 CGT6 Bootloader 区域。";
        if (!EraseConfirmation.Show(this, message))
            return;

        BeginOperation("正在擦除选中的页／扇区…");
        try
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 擦除 {sectors.Length} 个页／扇区\n{FlashGeometry.Describe(sectors)}");
            await chip.Erase(sectors);
            foreach (var row in selection)
                row.IsSelected = false;
            SectorGrid.Items.Refresh();
            StatusText.Text = "选中页已擦除，内核保持暂停";
            AppendLog(StatusText.Text);
        }
        catch (Exception ex) when (IsToolError(ex))
        {
            AppendLog(ex.Message);
            await DisconnectChip();
            StatusText.Text = "擦除未完成，目标页可能已部分擦除";
        }
        finally { EndOperation(); }
    }

    private int SwdSpeed() => int.Parse((string)((ComboBoxItem)SpeedBox.SelectedItem).Content,
        CultureInfo.InvariantCulture);

    private async Task DisconnectChip()
    {
        if (_chip is not { } chip)
            return;
        _chip = null;
        _chipRunning = false;
        _sectorRows = [];
        SectorGrid.ItemsSource = null;
        try { await chip.DisposeAsync(); }
        catch (Exception ex) when (IsToolError(ex)) { AppendLog("释放连接时出现错误：" + ex.Message); }
    }

    private void BeginOperation(string status)
    {
        _busy = true;
        SettingsPanel.IsEnabled = false;
        ErasePanel.IsEnabled = false;
        AiDebugButton.IsEnabled = false;
        OpsTabs.SelectedIndex = 0;
        ActivityBar.Visibility = Visibility.Visible;
        ActivityBar.IsIndeterminate = true;
        StatusText.Text = status;
    }

    private void EndOperation()
    {
        _busy = false;
        ActivityBar.IsIndeterminate = false;
        ActivityBar.Visibility = Visibility.Collapsed;
        SettingsPanel.IsEnabled = true;
        var connected = _chip is not null;
        var aiActive = _aiTransport is not null;
        ProbeSettings.IsEnabled = TargetSettings.IsEnabled = ToolBox.IsEnabled = ToolActions.IsEnabled = !connected;
        ErasePanel.IsEnabled = connected && !aiActive;
        ConnectButton.IsEnabled = FlashButton.IsEnabled = !aiActive;
        AiDebugButton.IsEnabled = true;
        AiDebugButton.Content = aiActive ? "结束 AI 调试" : "AI 调试接入…";
        ConnectButton.Content = connected ? "断开连接" : "连接芯片";
        ConnectionText.Text = !connected ? "未连接" : _chipRunning ? "已连接 · 固件运行中" : "已连接 · 内核已暂停";
        EraseHint.Text = connected ? "勾选要擦除的页／扇区" : "连接芯片后显示 Flash 页表";
    }

    private static bool IsToolError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or Win32Exception or InvalidOperationException or ArgumentException or TimeoutException or DllNotFoundException
        or BadImageFormatException or EntryPointNotFoundException;

    private async void Flash(object sender, RoutedEventArgs e)
    {
        if (_busy || _activeBackend is not Backend backend || TargetBox.SelectedItem is not Target target)
            return;
        var path = FirmwareBox.Text.Trim().Trim('"');
        var tool = ToolBox.Text.Trim().Trim('"');
        if (!File.Exists(path))
        {
            ShowProblem("请选择存在的固件文件。");
            return;
        }
        if (!File.Exists(tool))
        {
            ShowProblem($"请选择 {Programmers.Executable(backend)} 的实际路径。");
            return;
        }

        var address = AddressBox.Text;
        var speed = SwdSpeed();
        var reset = ResetBox.IsChecked == true;
        var progress = new Progress<string>(AppendLog);
        DirectoryInfo? work = null;

        var usedSession = false;
        BeginOperation("正在检查固件…");
        try
        {
            tool = Path.GetFullPath(tool);
            path = Path.GetFullPath(path);
            _paths[backend] = tool;
            SavePaths();
            work = Directory.CreateTempSubdirectory("mcu-flasher-");
            var image = await Task.Run(() => Firmware.Stage(path, work.FullName, address, target));
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {target.Name} · {Path.GetFileName(tool)} · {Path.GetFileName(path)}");
            if (_chip is { } chip)
            {
                usedSession = true;
                _chipRunning = false;
                StatusText.Text = "正在通过当前连接烧录并校验…";
                await chip.Program(Path.Combine(work.FullName, image.FileName), image.Address);
                if (reset)
                {
                    await chip.ResetAndRun();
                    await DisconnectChip();
                    StatusText.Text = "烧录并校验成功，已复位运行";
                }
                else
                    StatusText.Text = "烧录并校验成功，内核保持暂停";
                AppendLog(StatusText.Text);
                return;
            }
            var arguments = Programmers.Create(backend, target, image, work.FullName, speed, reset);
            if (backend == Backend.OpenOcd)
                arguments = [.. OpenOcdTool.ScriptArguments(tool), .. arguments];
            if (backend == Backend.Cube)
            {
                StatusText.Text = "正在检查 STM32 型号系列和容量…";
                var probe = await Programmers.Run(tool, arguments.Take(3).ToArray(), work.FullName, progress);
                var serial = Programmers.CheckStm32Target(probe, target);
                arguments = [.. arguments.Take(3), $"sn={serial}", .. arguments.Skip(3)];
            }
            StatusText.Text = "正在烧录并校验…";
            var output = await Programmers.Run(tool, arguments, work.FullName, progress);
            if (!Programmers.Verified(backend, output))
                throw new IOException("烧录工具未返回可识别的校验成功信息。请查看上方日志。");
            StatusText.Text = "烧录并校验成功";
            AppendLog(StatusText.Text);
        }
        catch (Exception ex) when (IsToolError(ex))
        {
            ShowProblem(ex.Message);
            if (usedSession)
                await DisconnectChip();
        }
        finally
        {
            if (work is not null)
            {
                try
                {
                    foreach (var name in new[] { "firmware.bin", "firmware.hex", "flash.jlink" })
                        File.Delete(Path.Combine(work.FullName, name));
                    work.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            EndOperation();
        }
    }

    private void ShowProblem(string message)
    {
        StatusText.Text = "烧录未完成，请查看日志";
        AppendLog(message);
    }

    private void AppendLog(string line)
    {
        if (_aiTransport is not null)
        {
            if (_aiLog.Count == AiLogCapacity)
                _aiLog.Dequeue();
            _aiLog.Enqueue((++_aiCursor, line.Length <= 1000 ? line : line[..1000] + "…"));
        }
        _pendingVisibleLog.AppendLine(line);
        if (_pendingVisibleLog.Length > MaxVisibleLogChars)
            _pendingVisibleLog.Remove(0, _pendingVisibleLog.Length - MaxVisibleLogChars);
        _logFlushTimer.Start();
    }

    private void FlushVisibleLog()
    {
        _logFlushTimer.Stop();
        if (_pendingVisibleLog.Length == 0)
            return;

        var batch = _pendingVisibleLog.ToString();
        _pendingVisibleLog.Clear();
        if (_visibleLogLength + batch.Length > MaxVisibleLogChars)
        {
            // Keep some room for subsequent batches so trimming isn't needed on each tick.
            var keep = Math.Min(_visibleLogLength, Math.Min(MaxVisibleLogChars / 2, MaxVisibleLogChars - batch.Length));
            var previous = keep == 0 ? string.Empty : LogBox.Text;
            LogBox.Text = previous[^keep..] + batch;
            _visibleLogLength = keep + batch.Length;
        }
        else
        {
            LogBox.AppendText(batch);
            _visibleLogLength += batch.Length;
        }
        LogBox.ScrollToEnd();
    }

    private void ClearLog(object sender, RoutedEventArgs e)
    {
        _aiLog.Clear();
        _logFlushTimer.Stop();
        _pendingVisibleLog.Clear();
        _visibleLogLength = 0;
        LogBox.Clear();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            if (_aiPreparation is { } preparation)
            {
                preparation.Cancel();
                AppendLog("正在取消固件准备，忙碌状态解除后可关闭窗口。");
            }
            else
                AppendLog("请等待当前操作完成后再关闭窗口。");
            return;
        }
        StopAiDebug();
        if (_chip is not null)
        {
            e.Cancel = true;
            BeginOperation("正在断开连接…");
            try { await DisconnectChip(); }
            finally { EndOperation(); }
            // The first Closing event must return before Close can be called again.
            await System.Windows.Threading.Dispatcher.Yield();
            Close();
            return;
        }
        _logFlushTimer.Stop();
        SavePaths();
        base.OnClosing(e);
    }

    private sealed class SectorRow(FlashSector sector, bool bootloader)
    {
        public FlashSector Sector { get; } = sector;
        public bool IsSelected { get; set; }
        public bool IsBootloader { get; } = bootloader;
        public int Bank => Sector.Bank;
        public int Index => Sector.Index;
        public string Address => $"0x{Sector.Address:X8}";
        public string Size => Sector.Size >= 1024 ? $"{Sector.Size / 1024} KiB" : $"{Sector.Size} B";
        public string Area => IsBootloader ? "Bootloader" : "";
    }

    private static Dictionary<Backend, string> LoadPaths()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Dictionary<Backend, string>>(File.ReadAllText(SettingsPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    private void SavePaths()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog("工具路径保存失败。下次启动时可以重新选择。");
        }
    }

    private static string FindTool(Backend backend)
    {
        if (backend == Backend.OpenOcd && OpenOcdTool.FindPackaged(AppContext.BaseDirectory) is { } openOcd)
            return openOcd;
        if (backend == Backend.PyOcd)
        {
            var installed = PythonToolInstaller.FindInstalled(PythonToolInstaller.ToolDirectory);
            if (File.Exists(installed))
                return installed;
        }
        var name = Programmers.Executable(backend);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim('"'), name);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                return candidate;
        }

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
        {
            if (backend == Backend.Cube)
            {
                var cube = Path.Combine(root, "STMicroelectronics", "STM32Cube", "STM32CubeProgrammer", "bin", name);
                if (File.Exists(cube))
                    return cube;
            }
            if (backend == Backend.JLink && Directory.Exists(Path.Combine(root, "SEGGER")))
            {
                var segger = Directory.EnumerateDirectories(Path.Combine(root, "SEGGER"), "JLink*")
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists);
                if (segger is not null)
                    return segger;
            }
        }
        return "";
    }
}
