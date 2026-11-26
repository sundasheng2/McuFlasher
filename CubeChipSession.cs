using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace McuFlasher;

internal sealed class CubeChipSession : IChipSession
{
    private const uint FlashStart = 0x08000000;
    private const uint Dhcsr = 0xE000EDF0;
    private const uint Halted = 1u << 17;
    private readonly IProgress<string> _log;
    private DisplayCallbacks _callbacks;
    private nint _library;
    private bool _enumerated, _connectionAttempted;

    public IReadOnlyList<FlashSector> Sectors { get; private set; } = [];

    private CubeChipSession(IProgress<string> log)
    {
        _log = log;
        _callbacks = new DisplayCallbacks
        {
            Init = () => { },
            Progress = (_, _) => { },
            Message = (_, pointer) =>
            {
                var message = Marshal.PtrToStringUni(pointer)?.Trim();
                if (!string.IsNullOrEmpty(message)) _log.Report(message);
            }
        };
    }

    public static Task<IChipSession> Connect(string executable, Target target, int swdKHz, IProgress<string> log) =>
        Task.Run<IChipSession>(() =>
        {
            var session = new CubeChipSession(log);
            try
            {
                session.Open(executable, target, swdKHz);
                return session;
            }
            catch
            {
                session.Close();
                throw;
            }
        });

    private void Open(string executable, Target target, int swdKHz)
    {
        var identity = FlashGeometry.IdentityFor(target);
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var path = Path.Combine(directory, "CubeProgrammer_API.dll");
        // Resolve the vendor DLL's dependencies alongside the selected CLI installation.
        _library = LoadLibraryExW(path, 0, 0x100 | 0x1000);
        if (_library == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法加载 {path} 或其依赖库。");
        Export<SetCallbacks>("setDisplayCallbacks")(_callbacks);
        Export<SetPath>("setLoadersPath")(directory);
        Export<SetLevel>("setVerbosityLevel")(1);

        _enumerated = true;
        var count = Export<GetList>("getStLinkList")(out var probes, 0);
        if (count < 0) Check(count, "枚举 ST-LINK");
        if (count == 0 || probes == 0) throw new IOException("未找到可用的 ST-LINK。");
        var parameters = Marshal.PtrToStructure<DebugParameters>(probes);
        var serial = Encoding.ASCII.GetString(parameters.Serial).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(serial)) throw new IOException("无法读取 ST-LINK 序列号。");
        parameters.Port = 1;        // SWD
        parameters.Mode = 0;        // NORMAL_MODE resets and halts the core
        parameters.ResetMode = 0;   // SOFTWARE_RESET
        parameters.Shared = 0;
        parameters.Frequency = swdKHz;
        _connectionAttempted = true;
        Check(Export<ConnectProbe>("connectStLink")(parameters), "连接 ST-LINK");
        RequireConnection();
        var general = Export<GetPointer>("getDeviceGeneralInf")();
        if (general == 0) throw new IOException("CubeProgrammer 未返回芯片信息。");
        var deviceId = (ushort)Marshal.ReadInt16(general); // generalInf.deviceId
        if (deviceId != identity.DeviceId)
            throw new InvalidDataException($"芯片 ID 0x{deviceId:X3} 与 {target.Name} 所需的 0x{identity.DeviceId:X3} 不一致。");
        var capacity = ReadRegister(identity.CapacityAddress, 2);
        if (capacity != target.FlashKiB)
            throw new InvalidDataException($"检测到 {capacity} KiB Flash，所选 {target.Name} 为 {target.FlashKiB} KiB。");
        EnsureHalted();
        Sectors = ReadSectors(target);
        _log.Report($"ST-LINK {serial}，芯片 ID 0x{deviceId:X3}，Flash {capacity} KiB，内核已暂停。");
    }

    private IReadOnlyList<FlashSector> ReadSectors(Target target)
    {
        Check(Export<GetStorage>("getStorageStructure")(out var storage), "读取 Flash 页表");
        if (storage == 0) throw new InvalidDataException("CubeProgrammer 未返回 Flash 页表。");
        try
        {
            var banks = Marshal.PtrToStructure<NativeList>(storage);
            if (banks.Count is 0 or > 16 || banks.Items == 0)
                throw new InvalidDataException("Flash Bank 信息无效。");
            var sectors = new List<FlashSector>();
            var indices = new HashSet<int>();
            uint end = checked(FlashStart + (uint)target.FlashKiB * 1024);
            for (int b = 0; b < banks.Count; b++)
            {
                var pages = Marshal.PtrToStructure<NativeList>(banks.Items + b * Marshal.SizeOf<NativeList>());
                if (pages.Count is 0 or > 65536 || pages.Items == 0)
                    throw new InvalidDataException("Flash Bank 内的页数无效。");
                for (int i = 0; i < pages.Count; i++)
                {
                    var page = Marshal.PtrToStructure<NativeSector>(pages.Items + i * Marshal.SizeOf<NativeSector>());
                    // A family loader may describe more sectors than the fitted Flash density.
                    if (page.Address >= end) continue;
                    var index = checked((int)page.Index);
                    if (page.Address < FlashStart || page.Size == 0 || (ulong)page.Address + page.Size > end
                        || !indices.Add(index))
                        throw new InvalidDataException("Flash 页表包含越界扇区或重复的擦除编号。");
                    sectors.Add(new FlashSector(b, index, page.Address, page.Size));
                }
            }
            sectors.Sort((a, b) => a.Address.CompareTo(b.Address));
            FlashGeometry.Validate(target, sectors);
            return sectors.AsReadOnly();
        }
        finally
        {
            // The top-level result is allocated for the caller. Bank tables are held by the SDK.
            Export<Release>("freeLibraryMemory")(storage);
        }
    }

    public Task Erase(IReadOnlyList<FlashSector> sectors) => Task.Run(() =>
    {
        RequireConnection();
        if (sectors.Count == 0 || sectors.Any(s => !Sectors.Contains(s)))
            throw new InvalidDataException("请选择当前连接页表中的扇区。");
        var indices = sectors.Select(s => checked((uint)s.Index)).Distinct().Order().ToArray();
        EnsureHalted();
        Check(Export<ErasePages>("sectorErase")(indices, (uint)indices.Length, 0), "擦除选中扇区");
        EnsureHalted();
    });

    public Task Program(string absolutePath, string? address) => Task.Run(() =>
    {
        RequireConnection();
        uint start = address == null ? FlashStart : uint.Parse(
            address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address,
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        EnsureHalted();
        Check(Export<Download>("downloadFile")(absolutePath, start, 0, 1, ""), "烧录并校验");
        EnsureHalted();
    });

    public Task ResetAndRun() => Task.Run(() =>
    {
        RequireConnection();
        Check(Export<ResetTarget>("reset")(0), "软件复位");
        if ((ReadRegister(Dhcsr, 4) & Halted) != 0)
            Check(Export<RunTarget>("execute")(FlashStart), "运行应用");
    });

    private void EnsureHalted()
    {
        if ((ReadRegister(Dhcsr, 4) & Halted) != 0) return;
        // ARM DHCSR requires DBGKEY for writes. C_DEBUGEN | C_HALT requests a debug halt.
        Check(Export<WriteWord>("writeMemory")(Dhcsr, BitConverter.GetBytes(0xA05F0003u), 4), "暂停内核");
        for (var i = 0; i < 20; i++)
        {
            if ((ReadRegister(Dhcsr, 4) & Halted) != 0) return;
            Thread.Sleep(5);
        }
        throw new IOException("无法确认内核已暂停。");
    }

    private uint ReadRegister(uint address, uint size)
    {
        var read = Export<ReadData>("readMemory");
        var release = Export<Release>("freeLibraryMemory");
        var status = read(address, out var buffer, size);
        try
        {
            Check(status, $"读取 0x{address:X8}");
            if (buffer == 0) throw new IOException($"读取 0x{address:X8} 时返回空缓冲。");
            return size == 2 ? (ushort)Marshal.ReadInt16(buffer) : (uint)Marshal.ReadInt32(buffer);
        }
        finally { if (buffer != 0) release(buffer); }
    }

    private void RequireConnection()
    {
        ObjectDisposedException.ThrowIf(_library == 0, this);
        if (Export<GetStatus>("checkDeviceConnection")() != 1) throw new IOException("ST-LINK 连接已断开。");
    }

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new IOException($"{operation}失败，CubeProgrammer 返回代码 {status}。");
    }

    public ValueTask DisposeAsync() => new(Task.Run(Close));

    private void Close()
    {
        if (_library == 0) return;
        try
        {
            if (_connectionAttempted) Export<NoArgs>("disconnect")();
        }
        finally
        {
            try { if (_enumerated) Export<NoArgs>("deleteInterfaceList")(); }
            finally { NativeLibrary.Free(_library); _library = 0; }
        }
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    // STM32CubeProgrammer's CubeProgrammer_API.h and DeviceDataStructure.h use C alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct DebugParameters
    {
        public int Port, Index;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)] public byte[] Serial;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Firmware;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public byte[] Voltage;
        public int AccessPortNumber, AccessPort, Mode, ResetMode, IsOldFirmware;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public uint[] JtagFrequencies;
        public uint JtagCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public uint[] SwdFrequencies;
        public uint SwdCount;
        public int Frequency, IsBridge, Shared;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100)] public byte[] Board;
        public int DebugSleep, Speed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeList { public uint Count; public nint Items; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSector { public uint Index, Size, Address; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayCallbacks { public NoArgs Init; public MessageCallback Message; public ProgressCallback Progress; }

    [DllImport("kernel32", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetPath([MarshalAs(UnmanagedType.LPStr)] string directory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLevel(int level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetCallbacks(DisplayCallbacks callbacks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MessageCallback(int type, nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProgressCallback(int current, int total);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetList(out nint list, int shared);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConnectProbe(DebugParameters parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetPointer();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetStorage(out nint storage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetStatus();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ErasePages([In] uint[] indices, uint count, nint memoryName);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate int Download(string path, uint address, uint skipErase, uint verify, string binaries);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResetTarget(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RunTarget(uint address);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadData(uint address, out nint buffer, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteWord(uint address, [In] byte[] data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NoArgs();
}
