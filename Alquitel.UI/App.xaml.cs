using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
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

        private static IConfiguration BuildConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("ALQUITEL_");

#if DEBUG
            builder.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
#endif
            return builder.Build();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppLog.Initialize();
            HookGlobalExceptionHandlers();

            try
            {
                var configuration = BuildConfiguration();
                var serviceCollection = new ServiceCollection();
                ConfigureServices(serviceCollection, configuration);

                ServiceProvider = serviceCollection.BuildServiceProvider();

                var initService = ServiceProvider.GetRequiredService<DataInitializationService>();
                initService.Initialize();

                var backupService = ServiceProvider.GetRequiredService<DatabaseBackupService>();
                backupService.Start();

                // ── Login multi-usuario ──────────────────────────────────
                // Bloquea hasta elegir usuario (y contraseña si tiene). Cancelar = salir.
                // Se cambia temporalmente a OnExplicitShutdown para evitar que la aplicación se cierre al cerrar la ventana de login
                var oldShutdownMode = this.ShutdownMode;
                this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var loginWindow = new Views.LoginWindow(
                    ServiceProvider.GetRequiredService<Alquitel.Core.Interfaces.Repositories.IUserRepository>(),
                    ServiceProvider.GetRequiredService<ICurrentUserService>());
                
                bool? loginResult = loginWindow.ShowDialog();
                
                if (loginResult != true)
                {
                    Shutdown();
                    return;
                }

                _ = Task.Run(() => ServiceProvider.GetRequiredService<IUpdateService>()
                        .CheckAndApplyUpdatesAsync())
                    .ContinueWith(
                        t => AppLog.Warning(t.Exception!.Flatten().InnerException!,
                            "Background update task faulted"),
                        System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();

                // Una vez que MainWindow está visible (y por lo tanto hay al menos una ventana abierta),
                // restablecemos el ShutdownMode original (OnLastWindowClose).
                this.ShutdownMode = oldShutdownMode;

                // Navigate to the Dashboard — without this the shell opens with an
                // empty content area until the user clicks a sidebar item.
                ServiceProvider.GetRequiredService<MainViewModel>().Initialize();

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                AppLog.Fatal(ex, "Startup failed");
                MessageBox.Show($"Error crítico al iniciar Alquitel:\n\n{ex.Message}",
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

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IConfiguration>(configuration);

            // ── Selección de proveedor de base de datos ──────────────────
            // "sqlite" (default): base local mono-puesto, comportamiento histórico.
            // "supabase"/"postgres": base compartida en servidor para todo el equipo.
            var provider = configuration["Database:Provider"] ?? "sqlite";
            var remoteConnectionString = configuration["Database:Supabase:ConnectionString"];
            bool useRemote =
                (provider.Equals("supabase", StringComparison.OrdinalIgnoreCase) ||
                 provider.Equals("postgres", StringComparison.OrdinalIgnoreCase));

            if (useRemote && string.IsNullOrWhiteSpace(remoteConnectionString))
            {
                AppLog.Warning("Database:Provider={Provider} pero ConnectionString vacío — fallback a SQLite local", provider);
                useRemote = false;
            }

            if (useRemote)
            {
                // La app persiste DateTime locales/UTC mezclados en columnas "timestamp";
                // sin este switch Npgsql exige DateTimeKind.Utc y rompe en runtime.
                AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
            }

            services.AddDbContextFactory<AlquitelDbContext>(options =>
            {
                if (useRemote)
                    options.UseNpgsql(remoteConnectionString);
                else
                    options.UseSqlite(AppPaths.DbConnectionString);
            });

            var updateUrl = configuration["Updates:UpdateUrl"];
            services.AddSingleton<IUpdateService>(new VelopackUpdateService(updateUrl));
            services.AddSingleton<DataInitializationService>();
            services.AddSingleton<DatabaseBackupService>();
            services.AddSingleton<IDocumentService, WordDocumentService>();
            
            // Core Services
            services.AddSingleton<IAppSettings>(sp => new AppSettings(AppPaths.SettingsFilePath));
            services.AddSingleton<IDispatcher, WpfDispatcher>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<INavigationService, NavigationService>();

            // Capa de repositorios: abstracción de datos para la futura migración a un
            // backend en servidor (Supabase/PostgreSQL). Hoy resuelven contra SQLite local;
            // al migrar solo se cambian estas cuatro líneas por las implementaciones remotas.
            services.AddSingleton<Alquitel.Core.Interfaces.Repositories.IClientRepository,
                Alquitel.Infrastructure.Persistence.Repositories.EfClientRepository>();
            services.AddSingleton<Alquitel.Core.Interfaces.Repositories.IProductRepository,
                Alquitel.Infrastructure.Persistence.Repositories.EfProductRepository>();
            services.AddSingleton<Alquitel.Core.Interfaces.Repositories.ILocationRepository,
                Alquitel.Infrastructure.Persistence.Repositories.EfLocationRepository>();
            services.AddSingleton<Alquitel.Core.Interfaces.Repositories.IOrderRepository,
                Alquitel.Infrastructure.Persistence.Repositories.EfOrderRepository>();
            services.AddSingleton<Alquitel.Core.Interfaces.Repositories.IUserRepository,
                Alquitel.Infrastructure.Persistence.Repositories.EfUserRepository>();

            // Multi-usuario: usuario logueado de la sesión actual.
            services.AddSingleton<ICurrentUserService, CurrentUserService>();

            // Plantillas centralizadas en Supabase Storage (bucket "templates").
            // Sin Url/AnonKey el servicio queda no-configurado y la app usa las
            // plantillas locales clásicas de IAppSettings.
            services.AddSingleton<ITemplateStorageService>(sp => new SupabaseTemplateStorageService(
                configuration["Database:Supabase:Url"],
                configuration["Database:Supabase:AnonKey"]));

            // Sincronización remota: con provider "supabase" el servicio prueba conexión
            // real y permite subir la base local histórica; con "sqlite" queda el no-op.
            if (useRemote)
                services.AddSingleton<IRemoteSyncService, PostgresSyncService>();
            else
                services.AddSingleton<IRemoteSyncService>(new LocalOnlySyncService(provider));

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<BudgetBuilderViewModel>();
            services.AddTransient<ProductEditorViewModel>();
            services.AddTransient<PresupuestosViewModel>();
            services.AddTransient<ClientsViewModel>();
            services.AddTransient<LocationsViewModel>();
            services.AddTransient<ReportsViewModel>();

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
