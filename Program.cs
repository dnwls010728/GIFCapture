namespace GifCapture;

static class Program
{
    [STAThread]
    static void Main()
    {
        L.Init(AppSettings.Current.Language);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
