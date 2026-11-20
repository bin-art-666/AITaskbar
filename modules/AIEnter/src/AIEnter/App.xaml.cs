using Microsoft.UI.Xaml;

namespace AIEnter;

public partial class App
{
    internal static App Current => ((AITaskbar.App)Application.Current).AiModule;
    Mutex? instance;
    internal static string DataDirectory { get; private set; } = "";
    static AiService? ai;
    static UiStyleStore? uiStyle;
    internal static AiService Ai => ai ??= new AiService(DataDirectory, verification: Environment.GetCommandLineArgs().Any(a => a is "--verify-toggle" or "--verify-taskbar" or "--verify-taskbar-crash" or "--verify-drop-entry" or "--verify-hotkey"));
    static UiStyleStore UiStyle => uiStyle ??= new(Path.Combine(DataDirectory, "ui-style.json"));
    internal static void StopAi() { ai?.Stop(); ai = null; }

    internal void Start(string[] arguments, string dataDirectory)
    {
        DataDirectory = dataDirectory;
        if (arguments.Contains("--verify-screenshot")) { _ = VerifyScreenshots(); return; }
        if (arguments.Contains("--verify-ai-modes"))
        {
            PanelTheme.Apply(UiStyle.Data);
            _ = VerifyAiModes();
            return;
        }
        LaunchAiOnly(arguments);
    }

    private static void Exit() => Application.Current.Exit();
}
