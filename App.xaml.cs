using System.Windows;

namespace McuFlasher;

public partial class App : Application { }

internal static class Launcher
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--mcp"])
            return AiMcpRelay.Run().GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
