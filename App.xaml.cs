using System.Configuration;
using System.Data;
using System.Globalization;
using System.Windows;
using System.Threading;

namespace StudyHub.Desktop
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            var ci = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = ci;
            CultureInfo.DefaultThreadCurrentUICulture = ci;
            Thread.CurrentThread.CurrentCulture = ci;
            Thread.CurrentThread.CurrentUICulture = ci;

            base.OnStartup(e);
        }
    }

}
