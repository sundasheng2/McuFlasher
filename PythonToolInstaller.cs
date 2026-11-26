using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace McuFlasher;

internal static class PythonToolInstaller
{
    public const string ArchiveName = "python-3.13.17-embed-amd64.zip";
    private const string ArchiveSha256 = "9E35319BFFEDAB8B36AD44D8F377CB4F1733BCEC257A0B6D04D94F01486DDB38";
    private const string VersionDirectory = "python-3.13.17-x64";
    private const string ReadyMarker = "CGT6 pyOCD environment ready";
    public static string ToolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "McuFlasher", "tools");

    public static string FindInstalled(string directory) => Path.Combine(directory, VersionDirectory, "python.exe");

    public static async Task<string> Install(string packageDirectory, string installDirectory, IProgress<string> progress)
    {
        var libraries = Path.Combine(packageDirectory, Cgt6Programmer.PythonLibraries);
        var pack = Path.Combine(packageDirectory, Cgt6Programmer.PackName);
        if (!Directory.Exists(libraries) || !File.Exists(pack))
            throw new InvalidDataException("找不到 CGT6 设备包或 pyOCD 依赖。请从完整的烧录工具文件夹启动程序。");

        var existing = FindInstalled(installDirectory);
        if (File.Exists(existing))
        {
            await Check(existing, pack, libraries, progress).ConfigureAwait(false);
            return existing;
        }

        var archive = Path.Combine(packageDirectory, ArchiveName);
        if (!File.Exists(archive))
            throw new InvalidDataException($"找不到离线安装包 {ArchiveName}。请从完整的烧录工具文件夹启动程序。");

        var destination = Path.GetFullPath(Path.Combine(installDirectory, VersionDirectory));
        var staging = destination + ".setup-" + Guid.NewGuid().ToString("N");
        try
        {
            progress.Report("正在解压应用专用 Python…");
            await Task.Run(() =>
            {
                using var input = File.OpenRead(archive);
                if (Convert.ToHexString(SHA256.HashData(input)) != ArchiveSha256)
                    throw new InvalidDataException("Python 离线安装包校验失败，请重新复制原始文件。");
                input.Position = 0;
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                Directory.CreateDirectory(staging);
                foreach (var file in zip.Entries)
                {
                    // The signed-release archive has a flat layout.
                    if (file.Name != file.FullName || file.Name.Length == 0)
                        throw new InvalidDataException("Python 离线安装包的目录结构有误。");
                    file.ExtractToFile(Path.Combine(staging, file.Name));
                }
            }).ConfigureAwait(false);
            await Check(Path.Combine(staging, "python.exe"), pack, libraries, progress).ConfigureAwait(false);

            try { Directory.Move(staging, destination); }
            catch (IOException) when (File.Exists(existing))
            {
                await Check(existing, pack, libraries, progress).ConfigureAwait(false);
            }
            return existing;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                foreach (var file in Directory.EnumerateFiles(staging))
                    File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    private static async Task Check(string executable, string pack, string libraries, IProgress<string> progress)
    {
        var output = await Programmers.Run(executable, ["-B", "-c", CheckScript, libraries, pack, ReadyMarker],
            Path.GetDirectoryName(executable)!, progress).ConfigureAwait(false);
        if (!output.Contains(ReadyMarker))
            throw new InvalidDataException("Python 环境未能加载 pyOCD 和 CGT6 设备包。");
    }

    private const string CheckScript = """
        import sys
        libraries, pack, ready = sys.argv[1:]
        sys.path.insert(0, libraries)
        import hid
        import libusb_package
        from pyocd.core.helpers import ConnectHelper
        from pyocd.flash.loader import FlashLoader
        from pyocd.target.pack.cmsis_pack import CmsisPack
        if not any(part.part_number == "GD32F303CG" for part in CmsisPack(pack).devices):
            sys.exit("GD32F303CG is missing from the device pack")
        print(ready)
        """;
}
