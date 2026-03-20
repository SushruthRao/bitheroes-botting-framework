using System.Windows.Forms;
using BitHeroesClient.Config;
using BitHeroesClient.Gui;
using BitHeroesClient.Logging;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // ── Configuration ────────────────────────────────────────────────────────

        AppConfig config = ConfigLoader.Load("config.json");

        Logger.Configure(
            logToFile:   config.Logging.LogToFile,
            logFilePath: config.Logging.LogFile,
            verboseMode: config.Logging.VerboseMode,
            bufferLines: 500);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Logger.Shutdown();

        // ── WinForms bootstrap ───────────────────────────────────────────────────

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
            Logger.Error($"[UI] Unhandled: {e.Exception.GetType().Name}: {e.Exception.Message}");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.Run(new MainForm(config));
    }
}
