using System.Windows;
using Wpf.Ui.Appearance;

namespace EngineeringMcp.ControlCenter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Start in the Windows theme so the control center never behaves like a dark-only app.
        ApplicationThemeManager.ApplySystemTheme();
        base.OnStartup(e);
    }
}
