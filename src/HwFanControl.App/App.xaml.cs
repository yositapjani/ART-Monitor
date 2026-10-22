using System.Windows;
using HwFanControl.App.Services;

namespace HwFanControl.App;

public partial class App : System.Windows.Application
{
    public static FanControlService Service { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Service = new FanControlService();
        Service.Start();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        try
        {
            Service?.EmergencyRestore();
            Service?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
