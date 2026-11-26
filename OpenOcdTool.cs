using System.IO;

namespace McuFlasher;

internal static class OpenOcdTool
{
    public static string? FindPackaged(string applicationDirectory)
    {
        var executable = Path.Combine(applicationDirectory, "tools", "openocd", "bin", "openocd.exe");
        return File.Exists(executable) && FindScripts(executable) is not null ? executable : null;
    }

    public static string[] ScriptArguments(string executable) =>
        FindScripts(executable) is { } scripts ? ["-s", scripts] : [];

    private static string? FindScripts(string executable)
    {
        var binaryDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var packageDirectory = Path.GetDirectoryName(binaryDirectory) ?? binaryDirectory;
        string[] candidates =
        [
            Path.Combine(packageDirectory, "openocd", "scripts"),
            Path.Combine(packageDirectory, "share", "openocd", "scripts"),
            Path.Combine(packageDirectory, "scripts"),
            Path.Combine(binaryDirectory, "scripts")
        ];
        return candidates.FirstOrDefault(directory =>
            File.Exists(Path.Combine(directory, "interface", "stlink.cfg")) &&
            File.Exists(Path.Combine(directory, "target", "stm32f1x.cfg")));
    }
}
