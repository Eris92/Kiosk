namespace Kiosk.NewsEditor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(EditorSettings.Load()));
    }
}
