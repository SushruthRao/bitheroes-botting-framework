using System.Windows.Forms;
using BitHeroesClient.Config;
using BitHeroesClient.Gui;
using BitHeroesClient.Logging;

// ── Configuration ──────────────────────────────────────────────────────────────

AppConfig config = ConfigLoader.Load("config.json");

Logger.Configure(
    logToFile:   config.Logging.LogToFile,
    logFilePath: config.Logging.LogFile,
    verboseMode: config.Logging.VerboseMode,
    bufferLines: 500);

AppDomain.CurrentDomain.ProcessExit += (_, _) => Logger.Shutdown();

// ── WinForms bootstrap ─────────────────────────────────────────────────────────

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new MainForm(config));
