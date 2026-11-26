# STM32 / GD32 烧录工具

Windows x64 中文上位机，通过 SWD 烧录 STM32F1、F4、L0、L4 列表中的型号，以及 GD32F303CCT6 和 GD32F303CGT6。

| 下载器 | 芯片 | 需要的工具 |
| --- | --- | --- |
| J-Link | STM32、GD32F303CCT6、GD32F303CGT6 | [SEGGER J-Link Software](https://www.segger.com/downloads/jlink/)，选择 `JLink.exe` |
| ST-LINK | STM32 | [STM32CubeProgrammer](https://www.st.com/content/st_com/en/stm32cubeprogrammer.html)，选择 `STM32_Programmer_CLI.exe` |
| ST-LINK | GD32F303CCT6 | 自动使用软件文件夹内的便携版 OpenOCD，也可手动选择兼容版本 |
| ST-LINK | GD32F303CGT6 | [pyOCD](https://pyocd.io/docs/debug_probes.html) 和 [兆易创新设备包](https://www.keil.arm.com/packs/gd32f30x_dfp-gigadevice/devices/)，选择 `python.exe` |

选好下载器和芯片后，工具栏会显示该组合需要的工具。**ST-LINK + GD32F303CGT6** 可以点「一键安装」，将随软件提供的 Python 离线安装到当前用户的 `%LOCALAPPDATA%\McuFlasher\tools`。上位机检查 pyOCD 和设备包后会自动填写路径。其余组合可以点「下载工具」打开对应项目或厂商的下载页，安装后用「选择工具」填写路径。USB 驱动由下载器厂商提供。

## 使用

1. 安装所用下载器的驱动和烧录工具。一次只连接准备使用的下载器与目标板。
2. 打开 `McuFlasher.exe`，选择下载器、目标芯片和固件。如果未自动找到烧录工具，点击「选择工具」。
3. `.hex` 按文件地址写入。使用 `.bin` 时检查起始地址，默认是 `0x08000000`。
4. 点击「烧录并校验」。窗口会显示工具日志。需要烧录后运行时，勾选「校验后复位运行」。

## 连接与按页擦除

1. 选好下载器、芯片和工具，点击「连接芯片」。核对目标并暂停内核后，状态栏会显示已连接。
2. 打开「按页擦除」，按页号和地址勾选。默认没有勾选项。点击「擦除选中页」后会显示精确的擦除范围，CGT6 的前 64 KiB 标为 Bootloader。
3. 点击「烧录并校验」可继续使用当前连接。若勾选「校验后复位运行」，校验后会运行固件并释放连接。手动「断开连接」时，默认不启动固件。需要继续烧录或擦除时保持连接。

连接模式包括 J-Link Commander、STM32CubeProgrammer API、OpenOCD Tcl 和 pyOCD。STM32 + ST-LINK 需要完整安装 CubeProgrammer，程序通过 CLI 同目录的 `CubeProgrammer_API.dll` 保持连接并取得设备的 Flash 页表。OpenOCD 需要支持 Tcl 本机接口的版本。连接失败时先看日志。已连接烧录和页擦除仍需分别上板验证。

## AI 半自动调试

1. 在桌面 STM32 串口助手开启「AI 接入」。在本工具点「连接芯片」，确认连接的是本轮的板子。
2. 点「AI 调试接入」，填写本次 COM 口、波特率以及允许擦写的 Flash 页范围。CGT6 提供 APP 范围的初始值 `0x08010000–0x0807F7FF`，包含完整擦除页，避开 Bootloader 和末尾标志页。请按实际 Flash 分区确认。
3. 在 Codex 中说明工程目录、编译目标和希望串口验证的结果。Codex 按工程原有命令编译，用 `flash_inspect_firmware` 取固件地址和 SHA-256，再用 `flash_start_program` 提交并轮询 `flash_status`。
4. 上位机在烧录前打开指定串口，烧录校验后复位运行并保留 SWD 会话。成功任务返回 `job.serial.sessionId` 和 `job.serial.cursor`，Codex 可以用串口助手的 `serial_read` 读取这次启动日志。若写入或复位失败，先检查日志，重新连接和授权后才可再写。
5. 点击「结束 AI 调试」撤销这轮授权，之后可手动断开芯片。关闭上位机也会撤销授权。AI 可以通过 `flash_read_log` 读取接入期间的烧录工具日志。

MCP 入口是新版上位机 EXE 加 `--mcp` 参数。每台电脑只需注册一次，注册后重新启动 Codex 以加载新工具。

```powershell
$exe = (Resolve-Path '.\McuFlasher-AI调试.exe').Path
codex mcp add stm32-flasher -- $exe --mcp
```

固件必须位于所选芯片的内部 Flash 区间。烧录工具会按页或扇区擦除，更新前请确认固件涉及的页没有需要保留的数据。通过 ST-LINK 烧录 STM32 时，上位机会用 CubeProgrammer 比对系列和 Flash 容量，使用前仍需核对芯片丝印。

GD32F303CCT6 的 ST-LINK 路线使用 [GD32F303CC 项目配置](https://github.com/CommunityGD32Cores/platform-gd32/blob/main/boards/genericGD32F303CC.json) 中的目标和 SWD ID。写入前会读取 CPU 类型和芯片的 [存储器容量寄存器](https://www.gd32mcu.com/data/documents/userManual/GD32F30x_User_Manual_Rev3.0.pdf)，并在写入后调用 OpenOCD 校验。首版需要实机验证。

桌面版附带 [xPack OpenOCD 0.12.0-7](https://github.com/xpack-dev-tools/openocd-xpack/releases/tag/v0.12.0-7)，位于 `tools\openocd`。切换到「ST-LINK + GD32F303CCT6」时，若没有已保存的有效路径，上位机会自动找到随附的程序和脚本。换电脑时请一并复制该目录。

**GD32F303CGT6** 配置的 Flash 地址范围是 `0x08000000–0x080FFFFF`。ST-LINK 路线使用厂商的 XD 烧录算法，第二个 Flash Bank 从 `0x08080000` 开始，按 4 KiB 擦除页处理。写入完成后通过 SWD 逐块回读固件并比对。该路线需要上板验证。

桌面版已附上 `GigaDevice.GD32F30x_DFP.2.2.1.pack`、`pyocd-python313` 依赖目录和 [Python 官方离线安装包](https://www.python.org/downloads/release/python-31317/)，移动程序时请一起复制。也可以使用已有的 Python 环境，执行 `python -m pip install pyocd==0.46.0` 后选择该环境的 `python.exe`。

## 构建

使用 .NET 10 SDK，在本目录执行。

```powershell
dotnet run --project tests/Smoke.csproj -c Release
dotnet publish McuFlasher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o dist
```
