using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Infrastructure.Services;
using Alquitel.UI.ViewModels;
using Alquitel.Core.Interfaces;

namespace Alquitel.UI
{
    public partial class App : Application
    {
        public static ServiceProvider? ServiceProvider { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                var serviceCollection = new ServiceCollection();
                ConfigureServices(serviceCollection);

                ServiceProvider = serviceCollection.BuildServiceProvider();

                // Initialize DB - This could be the failure point
                var initService = ServiceProvider.GetRequiredService<DataInitializationService>();
                initService.Initialize();

                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                // Mostraremos el error real en Windows para debuggear
                MessageBox.Show($"Error crítico al iniciar Alquitel:\n\n{ex.Message}\n\nDetalles:\n{ex.StackTrace}", "Error de Lanzamiento", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<AlquitelDbContext>();
            services.AddSingleton<DataInitializationService>();
            services.AddSingleton<IDocumentService, WordDocumentService>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }
    }
}
