namespace Kiosk.NewsEditor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
#if KIOSK_SELF_TEST
        if (args.Length == 1 && args[0] == "--self-test")
        {
            var result = Path.Combine(AppContext.BaseDirectory, "self-test-news.txt");
            try { StatsView.SelfTest(); File.WriteAllText(result, "Bounded and cancellable statistics reading and safe CSV export passed."); return 0; }
            catch (Exception ex) { File.WriteAllText(result, ex.ToString()); return 1; }
        }
#endif
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(EditorSettings.Load()));
        return 0;
    }
}
