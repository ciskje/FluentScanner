using Microsoft.UI.Xaml;

namespace FluentScanner;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Headless self-test (see windows/AGENTS.md): FSCAN_SELFTEST=1 <exe>
        if (Environment.GetEnvironmentVariable("FSCAN_SELFTEST") == "1")
        {
            bool ok;
            try { ok = Scanner.SelfTest(); }
            catch (Exception ex) { Console.Error.WriteLine("SELFTEST EXCEPTION " + ex); ok = false; }
            Console.Error.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FAILED");
            Console.Error.Flush();
            Environment.Exit(ok ? 0 : 1);
            return;
        }
        Window = new MainWindow();
        Window.Activate();
    }
}
