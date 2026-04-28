using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Infrastructure.Services;
using Alquitel.UI.ViewModels;
using Alquitel.Core.Interfaces;
using Alquitel.UI.Services;
namespace Alquitel.UI
{
    public partial class App : Application
    {
        public static ServiceProvider? ServiceProvider { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppLog.Initialize();
            HookGlobalExceptionHandlers();

            try
            {
                var serviceCollection = new ServiceCollection();
                ConfigureServices(serviceCollection);

                ServiceProvider = serviceCollection.BuildServiceProvider();

                var initService = ServiceProvider.GetRequiredService<DataInitializationService>();
                initService.Initialize();

                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                AppLog.Fatal(ex, "Startup failed");
                MessageBox.Show($"Error crítico al iniciar Alquitel:\n\n{ex.Message}\n\nDetalles:\n{ex.StackTrace}",
                    "Error de Lanzamiento", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Information("Application exiting (code={Code})", e.ApplicationExitCode);
            AppLog.Shutdown();
            base.OnExit(e);
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContextFactory<AlquitelDbContext>(
                options => options.UseSqlite(AppPaths.DbConnectionString));
            services.AddSingleton<DataInitializationService>();
            services.AddSingleton<IDocumentService, WordDocumentService>();
            
            // Core Services
            services.AddSingleton<IAppSettings>(sp => new AppSettings(AppPaths.SettingsFilePath));
            services.AddSingleton<IDispatcher, WpfDispatcher>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<INavigationService, NavigationService>();

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<BudgetBuilderViewModel>();
            services.AddTransient<ProductEditorViewModel>();
            services.AddTransient<PresupuestosViewModel>();
            services.AddTransient<ClientsViewModel>();
            services.AddTransient<LocationsViewModel>();
            
            services.AddSingleton<MainWindow>();
        }

        private void HookGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ea) =>
            {
                AppLog.Fatal(ea.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception"),
                    "AppDomain unhandled exception (terminating={IsTerm})", ea.IsTerminating);
            };

            DispatcherUnhandledException += (s, ea) =>
            {
                AppLog.Error(ea.Exception, "Dispatcher unhandled exception");
                MessageBox.Show(
                    $"Ocurrió un error inesperado:\n\n{ea.Exception.Message}\n\n" +
                    "Se registró en el archivo de log. La aplicación continuará en ejecución.",
                    "Error inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
                ea.Handled = true;
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ea) =>
            {
                AppLog.Error(ea.Exception, "Unobserved task exception");
                ea.SetObserved();
            };
        }
    }
}
