using System;
using System.Windows;

namespace MinerU25Tool
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try { Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light); } catch { }
            base.OnStartup(e);
        }
    }
}
