static class Program
{
    // WebView2 (y los dialogos de WinForms) exigen STA; los top-level statements corren en MTA.
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
