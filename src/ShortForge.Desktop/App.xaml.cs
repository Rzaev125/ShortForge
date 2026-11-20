using System.IO;
using System.Windows;

namespace ShortForge.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShortForge");
        new MainWindow(directory).Show();
    }
}
