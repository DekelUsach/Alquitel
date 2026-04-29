using System;
using Velopack;

namespace Alquitel.UI
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Must be called first — handles install/uninstall hooks before any WPF code runs.
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
