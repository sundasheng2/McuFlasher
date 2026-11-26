using McuFlasher;

if (args is ["--install-python", var packageDirectory, var installationDirectory])
{
    var progress = new Progress<string>(Console.WriteLine);
    var installed = await PythonToolInstaller.Install(packageDirectory, installationDirectory, progress);
    if (!File.Exists(installed) ||
        installed != await PythonToolInstaller.Install(packageDirectory, installationDirectory, progress))
        throw new Exception("Offline installation did not create a reusable Python environment.");
    Console.WriteLine($"PASS offline installation and repeat use: {installed}");
    return;
}

if (args is ["--mock-cli", var firmwarePath, var address])
{
    Console.WriteLine(File.Exists(firmwarePath) && address == "0x08004000" ? "Download verified successfully" : "Invalid input");
    Console.Error.WriteLine("Probe diagnostic");
    return;
}
if (args is ["--mock-exit"])
{
    Environment.ExitCode = 7;
    return;
}

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine($"PASS {++checks} {name}");
}

void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception(name);
}

var directory = Directory.CreateTempSubdirectory("mcu-test-");
try
{
    var gd = new Target("GD32F303CCT6", "GD32F303CC", 256);
    var stm = new Target("STM32F103C8", "STM32F103C8", 64);
    var bin = Path.Combine(directory.FullName, "固件 测试.bin");
    File.WriteAllBytes(bin, [1, 2, 3, 4]);
    var binary = Firmware.Stage(bin, directory.FullName, "0x08004000", gd);
    Check(binary.Address == "0x08004000" && binary.FileName == "firmware.bin", "BIN 地址和中文路径");
    Reject(() => Firmware.Stage(bin, directory.FullName, "0x0803FFFF", gd), "BIN 越过 Flash");
    Reject(() => Firmware.Stage(bin, directory.FullName, "0x20000000", gd), "BIN 拒绝 SRAM 地址");

    var hex = Path.Combine(directory.FullName, "test.hex");
    File.WriteAllLines(hex, [":020000040800F2", ":0400000001020304F2", ":00000001FF"]);
    var image = Firmware.Stage(hex, directory.FullName, "", gd);
    Check(image.Address is null, "HEX 保留内嵌地址");
    File.WriteAllLines(hex, [":020000040804EE", ":0400000001020304F2", ":00000001FF"]);
    Reject(() => Firmware.Stage(hex, directory.FullName, "", gd), "HEX 拒绝越界");
    File.WriteAllLines(hex, [":020000040800F2", ":0400000001020304F3", ":00000001FF"]);
    Reject(() => Firmware.Stage(hex, directory.FullName, "", gd), "HEX 拒绝错误校验和");
    File.WriteAllLines(hex, [":020000040800F2", ":0400000001020304F2"]);
    Reject(() => Firmware.Stage(hex, directory.FullName, "", gd), "HEX 拒绝缺少结束记录");

    var cube = Programmers.Create(Backend.Cube, stm, binary, directory.FullName, 1000, false);
    Check(cube.SequenceEqual(["-c", "port=SWD", "freq=1000", "-w", "firmware.bin", "0x08004000", "-v"]),
        "CubeProgrammer 写入后校验");
    var jlink = Programmers.Create(Backend.JLink, gd, image, directory.FullName, 1000, true);
    Check(jlink.Contains("GD32F303CC") && File.ReadAllLines(Path.Combine(directory.FullName, "flash.jlink"))
        .SequenceEqual(["r", "h", "loadfile firmware.hex", "r", "g", "q"]), "J-Link HEX 与复位命令");
    var openocd = Programmers.Create(Backend.OpenOcd, gd, binary, directory.FullName, 1000, false);
    Check(openocd.Contains("set CPUTAPID 0x2ba01477") && openocd.Contains("program firmware.bin verify exit 0x08004000"),
        "OpenOCD CCT6 配置和校验参数");
    Check(Programmers.Select(false, gd) == Backend.OpenOcd && Programmers.Select(false, stm) == Backend.Cube
        && Programmers.Select(true, gd) == Backend.JLink, "下载器路由");
    var portable = Directory.CreateTempSubdirectory("mcu-openocd-discovery-");
    var toolRoot = Path.Combine(portable.FullName, "tools", "openocd");
    var scripts = Path.Combine(toolRoot, "openocd", "scripts");
    var toolPath = Path.Combine(toolRoot, "bin", "openocd.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(toolPath)!);
    Directory.CreateDirectory(Path.Combine(scripts, "interface"));
    Directory.CreateDirectory(Path.Combine(scripts, "target"));
    try
    {
        File.WriteAllBytes(toolPath, []);
        Check(OpenOcdTool.FindPackaged(portable.FullName) is null, "没有配置的 OpenOCD 包不参与自动选择");
        File.WriteAllText(Path.Combine(scripts, "interface", "stlink.cfg"), "");
        File.WriteAllText(Path.Combine(scripts, "target", "stm32f1x.cfg"), "");
        Check(OpenOcdTool.FindPackaged(portable.FullName) == toolPath &&
            OpenOcdTool.ScriptArguments(toolPath).SequenceEqual(["-s", scripts]),
            "CCT6 自动找到随附的 OpenOCD 和脚本目录");
    }
    finally
    {
        File.Delete(toolPath);
        File.Delete(Path.Combine(scripts, "interface", "stlink.cfg"));
        File.Delete(Path.Combine(scripts, "target", "stm32f1x.cfg"));
        foreach (var folder in new[] { Path.Combine(scripts, "interface"), Path.Combine(scripts, "target"), scripts,
            Path.GetDirectoryName(scripts)!, Path.GetDirectoryName(toolPath)!, toolRoot, Path.GetDirectoryName(toolRoot)! })
            Directory.Delete(folder);
        portable.Delete();
    }
    var cgt = new Target("GD32F303CGT6", "GD32F303CGT6", 1024);
    var map = FlashGeometry.For(cgt);
    Check(map.Count == 384 && map[255] == new FlashSector(0, 255, 0x0807F800, 2048)
        && map[256] == new FlashSector(1, 0, 0x08080000, 4096), "CGT6 页表跨 Bank 切换页大小");
    var chosen = new[] { map[34], map[32], map[33], map[40] };
    Check(FlashGeometry.Describe(chosen) == "0x08010000–0x080117FF\n0x08014000–0x080147FF",
        "擦除范围合并连续页且保留未选间隔");
    Reject(() => FlashGeometry.Validate(cgt, map.Skip(1).ToArray()), "缺页的 Flash 布局无法发布");
    var f429 = new Target("STM32F429ZI", "STM32F429ZI", 2048);
    uint F429Register(uint address) => address == 0xE0042000 ? 0x10000419u : 0x08000000u;
    var f4Map = FlashGeometry.For(f429, F429Register);
    Check(f4Map.Count == 24 && f4Map[4].Size == 64u * 1024 && f4Map[5].Size == 128u * 1024
        && f4Map[12] == new FlashSector(1, 12, 0x08100000, 16u * 1024),
        "F429 容量半字、异形扇区及第二 Bank");
    Reject(() => FlashGeometry.For(f429, address => address == 0xE0042000 ? 0x413u : F429Register(address)),
        "芯片 ID 不匹配时不生成擦除页表");
    Check(FlashGeometry.For(new Target("STM32L031K6", "STM32L031K6", 32)).All(s => s.Size == 128),
        "STM32L0 使用 128 字节擦除页");
    Check(FlashGeometry.For(new Target("STM32L476RG", "STM32L476RG", 1024))[256] ==
        new FlashSector(1, 0, 0x08080000, 2048), "STM32L476RG 页表分 Bank");
    Check(FlashGeometry.For(stm).All(s => s.Size == 1024), "STM32F103C8 使用 1 KiB 擦除页");
    var scope = AiDebugScope.Create("com7", "115200", "0x08010000", "0x0807F7FF", cgt, map);
    Check(scope.Port == 7 && scope.Baud == 115200, "AI 调试记录本轮指定 COM 和波特率");
    Reject(() => AiDebugScope.Create("COM7", "115200", "0x08010001", "0x0807F7FF", cgt, map),
        "AI 授权拒绝未按擦除页对齐的范围");
    Reject(() => scope.Check(new FlashRange(0x08000000, 0x08010005), map), "AI 拒绝涉及 Bootloader 的固件");
    Reject(() => scope.Check(new FlashRange(0x0807F800, 0x0807F801), map), "AI 拒绝授权范围外的标志页");
    scope.Check(new FlashRange(0x08010000, 0x08027DFF), map);
    Check(true, "AI 允许授权范围内的 APP 固件");
    using var workerGate = new ManualResetEventSlim();
    var lateDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stalled = AiPreparation.Run(_ =>
    {
        workerGate.Wait();
        return new DisposeProbe(lateDispose);
    }, TimeSpan.FromMilliseconds(150), CancellationToken.None);
    try
    {
        await stalled.WaitAsync(TimeSpan.FromSeconds(3));
        throw new Exception("暂停的固件准备工作没有超时。");
    }
    catch (TimeoutException) when (stalled.IsCompleted)
    {
        Check(true, "固件准备超时后调用方无需等待阻塞的文件操作");
    }
    finally { workerGate.Set(); }
    await lateDispose.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(true, "后台工作迟到返回的固件会释放");
    var upperBank = Firmware.Stage(bin, directory.FullName, "0x08080000", cgt);
    Check(upperBank.Address == "0x08080000", "CGT6 支持第二个 Flash Bank");
    Reject(() => Firmware.Stage(bin, directory.FullName, "0x080FFFFD", cgt), "CGT6 拒绝超出 1 MiB");
    Check(Programmers.Select(false, cgt) == Backend.PyOcd && Programmers.Select(true, cgt) == Backend.JLink,
        "CGT6 下载器路由");
    Check(Programmers.Create(Backend.JLink, cgt, upperBank, directory.FullName, 1000, false).Contains("GD32F303CGT6"),
        "J-Link 使用 CGT6 专用型号");
    var pack = Path.Combine(directory.FullName, Cgt6Programmer.PackName);
    Reject(() => Cgt6Programmer.Create(upperBank, 1000, true, pack), "CGT6 缺少设备包时停止");
    File.WriteAllBytes(pack, []);
    var cgtArgs = Cgt6Programmer.Create(upperBank, 1000, true, pack);
    Check(cgtArgs.TakeLast(6).SequenceEqual([pack, "firmware.bin", "0x08080000", "1000000", "1",
        Cgt6Programmer.VerifiedMessage]), "CGT6 ST-LINK 使用指定设备包、Bank1 地址及 SWD 速率");
    Check(!Programmers.Verified(Backend.PyOcd, ["Verifying Flash via SWD..."]) &&
        Programmers.Verified(Backend.PyOcd, [Cgt6Programmer.VerifiedMessage]), "CGT6 校验完成后才报告成功");
    Check(!Programmers.Verified(Backend.Cube, ["File download complete"]) &&
        Programmers.Verified(Backend.Cube, ["Download verified successfully"]),
        "只有出现校验结果才认定成功");
    Check(!Programmers.Verified(Backend.JLink, ["O.K."]) &&
        Programmers.Verified(Backend.JLink, ["Downloading file [firmware.hex]...", "O.K."]),
        "J-Link 校验结果绑定烧录步骤");
    var serial = Programmers.CheckStm32Target(
        ["ST-LINK SN  : 066FFF575156781234523227", "Device name : STM32F103 Medium-density", "Flash size : 64 KBytes"], stm);
    Programmers.CheckStm32Target(["ST-LINK SN  : AA1122", "Device name : STM32F42xxx/F43xxx", "Flash size : 2 MBytes"],
        new Target("STM32F429ZI", "STM32F429ZI", 2048));
    Check(serial == "066FFF575156781234523227", "CubeProgrammer 解析容量格式与下载器序列号");
    Reject(() => Programmers.CheckStm32Target(["Device name : STM32F401xB/C", "Flash size : 64 KB"], stm),
        "CubeProgrammer 拒绝其他 STM32 系列");
    Reject(() => Programmers.CheckStm32Target(["Device name : STM32F103", "Flash size : 128 KB"], stm),
        "CubeProgrammer 拒绝容量不符");
    Reject(() => Programmers.CheckStm32Target(["Device name : STM32F103", "Flash size : 64 KB"], stm),
        "CubeProgrammer 缺少下载器序列号时停止");
    var child = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位测试程序。");
    var mock = await Programmers.Run(child, ["--mock-cli", "firmware.bin", "0x08004000"],
        directory.FullName, new Progress<string>());
    Check(Programmers.Verified(Backend.Cube, mock) && mock.Contains("Probe diagnostic"),
        "后台工具读取 stdout 和 stderr，并在指定目录访问固件");
    try
    {
        await Programmers.Run(child, ["--mock-exit"], directory.FullName, new Progress<string>());
        throw new Exception("未处理子进程失败。");
    }
    catch (IOException e)
    {
        Check(e.Message.Contains("7"), "后台工具非零退出会报告错误");
    }
}
finally
{
    foreach (var file in directory.EnumerateFiles())
        file.Delete();
    directory.Delete();
}

sealed class DisposeProbe(TaskCompletionSource completed) : IDisposable
{
    public void Dispose() => completed.TrySetResult();
}
