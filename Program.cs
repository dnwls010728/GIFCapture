namespace GifCapture;

static class Program
{
    [STAThread]
    static void Main()
    {
        Updater.CleanUp();
        L.Init(AppSettings.Current.Language);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
