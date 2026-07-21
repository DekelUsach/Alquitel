using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;
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
                // Secretos por máquina (connection string real, service key). Gitignoreado,
                // sobreescribe appsettings.json solo en el equipo desplegado. Nunca se commitea.
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
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

                // Reintento en segundo plano de órdenes que quedaron sin persistir
                // (outbox offline): primer intento al minuto, luego cada 5.
                ServiceProvider.GetRequiredService<OrderOutboxService>().Start();

                // ── Flujo de Inicio Asíncrono (Actualización + Login) ──────
                // Cambiamos temporalmente a OnExplicitShutdown para evitar que la aplicación se cierre al cerrar la ventana de actualización o de login
                var oldShutdownMode = this.ShutdownMode;
                this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                _ = RunStartupFlowAsync(e, oldShutdownMode);
            }
            catch (Exception ex)
            {
                AppLog.Fatal(ex, "Startup failed");
                MessageBox.Show($"Error crítico al iniciar Alquitel:\n\n{ex.Message}",
                    "Error de Lanzamiento", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private async Task RunStartupFlowAsync(StartupEventArgs e, ShutdownMode oldShutdownMode)
        {
            try
            {
                // 1. Chequeo de actualizaciones de Velopack
                var updateService = ServiceProvider!.GetRequiredService<IUpdateService>();
                bool updateApplied = await CheckAndApplyUpdatesOnStartupInternalAsync(updateService);

                if (updateApplied)
                {
                    // Si se aplicó la actualización, Velopack reiniciará la app. Cerramos esta instancia.
                    Shutdown();
                    return;
                }

                // 2. Login multi-usuario
                var userRepository = ServiceProvider!.GetRequiredService<Alquitel.Core.Interfaces.Repositories.IUserRepository>();
                var currentUserService = ServiceProvider!.GetRequiredService<ICurrentUserService>();
                var sessionStore = ServiceProvider!.GetRequiredService<Alquitel.Core.Interfaces.ISessionStore>();

                if (!TryAutoLogin(userRepository, currentUserService, sessionStore))
                {
                    var loginWindow = new Views.LoginWindow(userRepository, currentUserService, sessionStore);

                    bool? loginResult = loginWindow.ShowDialog();

                    if (loginResult != true)
                    {
                        Shutdown();
                        return;
                    }
                }

                // 3. Lanzamiento de MainWindow
                var mainWindow = ServiceProvider!.GetRequiredService<MainWindow>();
                mainWindow.Show();

                // Restablecemos el ShutdownMode original
                this.ShutdownMode = oldShutdownMode;

                // Navigate to the Dashboard
                ServiceProvider!.GetRequiredService<MainViewModel>().Initialize();

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                AppLog.Fatal(ex, "Error in startup flow");
                MessageBox.Show($"Error crítico en el flujo de inicio de Alquitel:\n\n{ex.Message}",
                    "Error de Lanzamiento", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private async Task<bool> CheckAndApplyUpdatesOnStartupInternalAsync(IUpdateService updateService)
        {
            if (updateService is not VelopackUpdateService velopackService || !velopackService.IsUpdateCheckEnabled)
            {
                return false;
            }

            var updateWindow = new Window
            {
                Title = "Actualización de Alquitel",
                Width = 450,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E2E")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4F46E5")),
                BorderThickness = new Thickness(2),
                AllowsTransparency = true,
                Topmost = true
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            var textBlock = new TextBlock
            {
                Text = "Buscando actualizaciones...",
                Foreground = Brushes.White,
                FontSize = 16,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetRow(textBlock, 0);
            grid.Children.Add(textBlock);

            var progressBar = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 8,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E2E3E")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4F46E5")),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 10, 0, 10)
            };
            Grid.SetRow(progressBar, 1);
            grid.Children.Add(progressBar);

            updateWindow.Content = grid;
            updateWindow.Show();

            bool updateApplied = false;
            try
            {
                // Breve delay para asegurar que el render de la UI de la ventana sea visible
                await Task.Delay(500);

                updateApplied = await velopackService.CheckAndApplyUpdatesOnStartupAsync(status =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        textBlock.Text = status;
                    });
                });
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Error checking or applying updates on startup UI");
            }
            finally
            {
                updateWindow.Close();
            }

            return updateApplied;
        }

        /// <summary>
        /// Intenta restaurar la sesión guardada sin mostrar LoginWindow. Devuelve false
        /// (y deja la sesión guardada intacta o corrupta sin arreglar) ante cualquier
        /// motivo de invalidez: sin sesión, usuario borrado, red caída, o Admin con
        /// sesión de más de 30 días.
        /// </summary>
        private static bool TryAutoLogin(
            Alquitel.Core.Interfaces.Repositories.IUserRepository userRepository,
            ICurrentUserService currentUserService,
            Alquitel.Core.Interfaces.ISessionStore sessionStore)
        {
            if (!sessionStore.TryLoad(out var userId, out var savedAtUtc))
                return false;

            Alquitel.Core.Entities.User? user;
            try
            {
                var resolveTask = Task.Run(() => userRepository.GetByIdAsync(userId));
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
                var completed = Task.WhenAny(resolveTask, timeoutTask).GetAwaiter().GetResult();
                if (completed != resolveTask)
                {
                    AppLog.Warning("Timeout resolviendo usuario de sesión guardada (>5s), se pide login manual");
                    return false;
                }
                user = resolveTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo resolver el usuario de la sesión guardada, se pide login manual");
                return false;
            }

            if (user == null || user.IsArchived)
                return false;

            if (user.Role == Alquitel.Core.Entities.UserRole.Admin &&
                DateTimeOffset.UtcNow - savedAtUtc > TimeSpan.FromDays(30))
                return false;

            currentUserService.SetCurrentUser(user);
            return true;
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

            var githubRepoUrl = configuration["Updates:GithubRepoUrl"];
            services.AddSingleton<IUpdateService>(new VelopackUpdateService(githubRepoUrl));
            services.AddSingleton<DataInitializationService>();
            services.AddSingleton<DatabaseBackupService>();
            // Feature flag del motor documental: "com" (default, Word Interop completo)
            // u "openxml" (experimental: genera .docx sin Word instalado, sin PDF).
            // Permite probar el motor nuevo y volver atrás con solo tocar appsettings.
            var documentEngine = configuration["Documents:Engine"] ?? "com";
            if (documentEngine.Equals("openxml", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Information("Motor documental: OpenXML (experimental, sin Word)");
                services.AddSingleton<IDocumentService, OpenXmlDocumentService>();
            }
            else
            {
                services.AddSingleton<IDocumentService, WordDocumentService>();
            }
            
            // Core Services
            services.AddSingleton<IAppSettings>(sp => new AppSettings(AppPaths.SettingsFilePath));
            services.AddSingleton<IDispatcher, WpfDispatcher>();
            services.AddSingleton<IDialogService, DialogService>();

            // Toasts no bloqueantes para éxitos/avisos; misma instancia expuesta como
            // colección bindeable (MainWindow) y como servicio (ViewModels).
            services.AddSingleton<ToastService>();
            services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
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
            services.AddSingleton<Alquitel.Core.Interfaces.ISessionStore, FileSessionStore>();

            // Persistencia de órdenes y borradores del armador (extraídos del VM).
            services.AddSingleton<IOrderPersistenceService, OrderPersistenceService>();
            services.AddSingleton<IDraftService, DraftService>();

            // Outbox offline: órdenes que no pudieron guardarse (sin internet en modo
            // servidor) quedan encoladas en disco y se reintentan en segundo plano.
            services.AddSingleton<OrderOutboxService>();
            services.AddSingleton<IOrderOutboxService>(sp => sp.GetRequiredService<OrderOutboxService>());

            // Borradores de correo con Outlook COM (botón "Enviar por mail").
            services.AddSingleton<IEmailService, OutlookEmailService>();

            // Bitácora multi-usuario de presupuestos (quién generó/editó/cambió estado).
            services.AddSingleton<IOrderAuditService, EfOrderAuditService>();

            // Resumen semanal automático ("el papelito del lunes"): .docx con OpenXML puro,
            // disparado por MainViewModel en el primer arranque de cada semana.
            services.AddSingleton<IWeeklySummaryService, WeeklySummaryService>();

            // Pedido automático con IA (Pollinations.ai). Sin ApiKey el servicio queda
            // no-configurado y el armador usa el motor de coincidencia local de siempre.
            // Key por máquina: appsettings.local.json o ALQUITEL_Ai__Pollinations__ApiKey.
            services.AddSingleton<IAiOrderParser>(sp => new PollinationsOrderParser(
                configuration["Ai:Pollinations:ApiKey"],
                configuration["Ai:Pollinations:Model"]));

            // Asistente de texto para los quick-wins de IA (notas OT, resumen de cliente,
            // detección de datos de contacto). Misma key barata de Pollinations.
            services.AddSingleton<IAiTextAssistant>(sp => new PollinationsTextAssistant(
                configuration["Ai:Pollinations:ApiKey"],
                configuration["Ai:Pollinations:Model"]));

            // Plantillas centralizadas en Supabase Storage (bucket "templates").
            // - Url/AnonKey: solo LECTURA (viajan en el binario) -> descargar plantillas.
            // - ServiceKey: solo el equipo del Admin la tiene (env var ALQUITEL_... o
            //   appsettings.local.json, nunca commiteada) -> publicar plantillas.
            // Sin Url/AnonKey el servicio queda no-configurado y la app usa las
            // plantillas locales clásicas de IAppSettings.
            services.AddSingleton<ITemplateStorageService>(sp => new SupabaseTemplateStorageService(
                configuration["Database:Supabase:Url"],
                configuration["Database:Supabase:AnonKey"],
                configuration["Database:Supabase:ServiceKey"]));

            // Portal de aprobación por link (§4 PENDING_FEATURES): genera la URL pública
            // de la Edge Function "aprobar". Requiere la Url de Supabase; sin ella el
            // botón queda deshabilitado. Ver docs/APPROVAL_PORTAL.md.
            services.AddSingleton<IApprovalLinkService>(sp => new EfApprovalLinkService(
                sp.GetRequiredService<IDbContextFactory<AlquitelDbContext>>(),
                configuration["Database:Supabase:Url"]));

            // Sincronización remota: con provider "supabase" el servicio prueba conexión
            // real y permite subir la base local histórica; con "sqlite" queda el no-op.
            if (useRemote)
                services.AddSingleton<IRemoteSyncService, PostgresSyncService>();
            else
                // Provider EFECTIVO, no el declarado: si hubo fallback por ConnectionString
                // vacío la app corre contra SQLite y la barra de estado no debe decir
                // "Servidor compartido (Supabase)".
                services.AddSingleton<IRemoteSyncService>(new LocalOnlySyncService("sqlite"));

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<BudgetBuilderViewModel>();
            services.AddTransient<ProductEditorViewModel>();
            services.AddTransient<PresupuestosViewModel>();
            services.AddTransient<OrderPoolViewModel>();
            services.AddTransient<ClientsViewModel>();
            services.AddTransient<LocationsViewModel>();
            services.AddTransient<ReportsViewModel>();
            services.AddTransient<WorkOrdersViewModel>();

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
