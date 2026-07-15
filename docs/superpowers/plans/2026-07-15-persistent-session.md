# Sesión Persistente Entre Lanzamientos — Plan de Implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** App recuerda el último usuario logueado y evita mostrar `LoginWindow` en relanzamientos, salvo que la sesión sea de un Admin y hayan pasado más de 30 días.

**Architecture:** Nuevo `ISessionStore` (Core) + `FileSessionStore` (Infrastructure) persiste `{UserId, SavedAtUtc}` en JSON bajo `%LocalAppData%\Alquitel\session.json`. `App.xaml.cs::OnStartup` intenta auto-login resolviendo el usuario guardado antes de crear `LoginWindow`; si falla por cualquier motivo cae al flujo actual. `LoginWindow` guarda sesión tras login manual exitoso. `MainViewModel.Logout` la limpia antes de relanzar el proceso.

**Tech Stack:** .NET 8, C# 12, `System.Text.Json`, WPF, `Microsoft.Extensions.DependencyInjection`.

## Global Constraints

- Spec: [docs/superpowers/specs/2026-07-15-persistent-session-design.md](../specs/2026-07-15-persistent-session-design.md)
- Admin (`UserRole.Admin`) con sesión guardada hace más de 30 días → NO auto-login, `LoginWindow` normal.
- No-Admin → sin expiración de sesión.
- Cualquier error de I/O/parseo/red al intentar auto-login → fallback silencioso a `LoginWindow` (nunca crashea el startup).
- Sin tests unitarios nuevos en `Alquitel.Core.Tests` — este feature no tiene lógica pura de Core (todo toca EF, filesystem o WPF), consistente con el alcance actual de ese proyecto de tests (solo lógica pura: `CuitValidator`, `TagParser`, etc). Verificación es manual (Task 6).
- Nunca loggear ni serializar `PasswordHash` en el archivo de sesión — solo `UserId` y `SavedAtUtc`.

---

### Task 1: `AppPaths.SessionFilePath`

**Files:**
- Modify: `Alquitel.Infrastructure\AppPaths.cs`

**Interfaces:**
- Produces: `AppPaths.SessionFilePath` (string, ruta absoluta `%LocalAppData%\Alquitel\session.json`), usado por `FileSessionStore` en Task 3.

- [ ] **Step 1: Agregar la propiedad**

En `Alquitel.Infrastructure\AppPaths.cs`, agregar la declaración de propiedad junto a las demás (después de línea 17 `LogsFolder`):

```csharp
        public static string LogsFolder { get; }
        public static string SessionFilePath { get; }
```

Y en el constructor estático, junto a la inicialización de `SettingsFilePath` (línea 48):

```csharp
            SettingsFilePath = Path.Combine(AppDataRoot, "settings.json");
            SessionFilePath = Path.Combine(AppDataRoot, "session.json");
```

- [ ] **Step 2: Verificar que compila**

Run: `dotnet build Alquitel.Infrastructure\Alquitel.Infrastructure.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add Alquitel.Infrastructure/AppPaths.cs
git commit -m "feat: agregar AppPaths.SessionFilePath"
```

---

### Task 2: `ISessionStore` (Core)

**Files:**
- Create: `Alquitel.Core\Interfaces\ISessionStore.cs`

**Interfaces:**
- Produces: `ISessionStore` con `void Save(Guid userId)`, `bool TryLoad(out Guid userId, out DateTimeOffset savedAtUtc)`, `void Clear()`. Consumido por `FileSessionStore` (Task 3), `App.xaml.cs` (Task 4), `LoginWindow` (Task 5), `MainViewModel` (Task 6).

- [ ] **Step 1: Crear la interfaz**

```csharp
using System;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Persiste qué usuario quedó logueado por última vez, para saltear el
    /// <c>LoginWindow</c> en relanzamientos de la app. No guarda credenciales,
    /// solo el <see cref="Entities.User.Id"/> y la fecha de guardado.
    /// </summary>
    public interface ISessionStore
    {
        /// <summary>Guarda el usuario logueado y la marca de tiempo actual (UTC).</summary>
        void Save(Guid userId);

        /// <summary>
        /// Intenta leer la sesión guardada. Devuelve <c>false</c> si no existe, está
        /// corrupta, o no se pudo leer por cualquier motivo.
        /// </summary>
        bool TryLoad(out Guid userId, out DateTimeOffset savedAtUtc);

        /// <summary>Borra la sesión guardada (logout).</summary>
        void Clear();
    }
}
```

- [ ] **Step 2: Verificar que compila**

Run: `dotnet build Alquitel.Core\Alquitel.Core.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add Alquitel.Core/Interfaces/ISessionStore.cs
git commit -m "feat: agregar contrato ISessionStore"
```

---

### Task 3: `FileSessionStore` (Infrastructure) + registro DI

**Files:**
- Create: `Alquitel.Infrastructure\Services\FileSessionStore.cs`
- Modify: `Alquitel.UI\App.xaml.cs:192` (junto al registro de `ICurrentUserService`)

**Interfaces:**
- Consumes: `AppPaths.SessionFilePath` (Task 1), `ISessionStore` (Task 2).
- Produces: `FileSessionStore` registrado como `ISessionStore` singleton en DI, resoluble por `App`, `LoginWindow`, `MainViewModel`.

- [ ] **Step 1: Crear la implementación**

```csharp
using System;
using System.IO;
using System.Text.Json;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación de <see cref="Core.Interfaces.ISessionStore"/> sobre un archivo
    /// JSON en <see cref="AppPaths.SessionFilePath"/>. Cualquier error de I/O o de
    /// parseo se trata como "sin sesión guardada" — nunca debe tumbar el startup.
    /// </summary>
    public class FileSessionStore : Core.Interfaces.ISessionStore
    {
        private class SessionData
        {
            public Guid UserId { get; set; }
            public DateTimeOffset SavedAtUtc { get; set; }
        }

        public void Save(Guid userId)
        {
            try
            {
                var data = new SessionData { UserId = userId, SavedAtUtc = DateTimeOffset.UtcNow };
                File.WriteAllText(AppPaths.SessionFilePath, JsonSerializer.Serialize(data));
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo guardar la sesión persistente");
            }
        }

        public bool TryLoad(out Guid userId, out DateTimeOffset savedAtUtc)
        {
            userId = Guid.Empty;
            savedAtUtc = default;

            try
            {
                if (!File.Exists(AppPaths.SessionFilePath)) return false;

                var json = File.ReadAllText(AppPaths.SessionFilePath);
                var data = JsonSerializer.Deserialize<SessionData>(json);
                if (data == null || data.UserId == Guid.Empty) return false;

                userId = data.UserId;
                savedAtUtc = data.SavedAtUtc;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Sesión persistente corrupta o ilegible, se ignora");
                return false;
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(AppPaths.SessionFilePath))
                    File.Delete(AppPaths.SessionFilePath);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo borrar la sesión persistente");
            }
        }
    }
}
```

- [ ] **Step 2: Registrar en DI**

En `Alquitel.UI\App.xaml.cs`, en `ConfigureServices`, junto a la línea 192 (`services.AddSingleton<ICurrentUserService, CurrentUserService>();`), agregar:

```csharp
            services.AddSingleton<ICurrentUserService, CurrentUserService>();
            services.AddSingleton<Alquitel.Core.Interfaces.ISessionStore, FileSessionStore>();
```

- [ ] **Step 3: Verificar que compila**

Run: `dotnet build Alquitel.sln`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add Alquitel.Infrastructure/Services/FileSessionStore.cs Alquitel.UI/App.xaml.cs
git commit -m "feat: implementar FileSessionStore y registrarlo en DI"
```

---

### Task 4: Auto-login en `App.xaml.cs::OnStartup`

**Files:**
- Modify: `Alquitel.UI\App.xaml.cs:60-76`

**Interfaces:**
- Consumes: `ISessionStore.TryLoad` (Task 2/3), `IUserRepository.GetByIdAsync(Guid)` (existente), `ICurrentUserService.SetCurrentUser(User)` (existente), `User.Role`, `User.IsArchived` (existente, `Alquitel.Core.Entities`).
- Produces: comportamiento de `OnStartup` — saltea `LoginWindow` cuando hay sesión válida.

- [ ] **Step 1: Reemplazar el bloque de login**

En `Alquitel.UI\App.xaml.cs`, reemplazar el bloque actual (líneas 60-76):

```csharp
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
```

por:

```csharp
                // ── Login multi-usuario ──────────────────────────────────
                // Si hay sesión guardada válida, saltea LoginWindow (ver TryAutoLogin).
                // Si no, bloquea hasta elegir usuario (y contraseña si tiene). Cancelar = salir.
                // Se cambia temporalmente a OnExplicitShutdown para evitar que la aplicación se cierre al cerrar la ventana de login
                var oldShutdownMode = this.ShutdownMode;
                this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var userRepository = ServiceProvider.GetRequiredService<Alquitel.Core.Interfaces.Repositories.IUserRepository>();
                var currentUserService = ServiceProvider.GetRequiredService<ICurrentUserService>();
                var sessionStore = ServiceProvider.GetRequiredService<Alquitel.Core.Interfaces.ISessionStore>();

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
```

- [ ] **Step 2: Agregar el método `TryAutoLogin`**

Agregar como método privado estático de `App`, después de `OnStartup` (antes de `OnExit`):

```csharp
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
                user = Task.Run(() => userRepository.GetByIdAsync(userId)).GetAwaiter().GetResult();
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
```

- [ ] **Step 3: Agregar el using que falta**

Verificar que `System.Threading.Tasks` está importado (se usa `Task.Run`). Si no está, agregar arriba del namespace en `App.xaml.cs`:

```csharp
using System.Threading.Tasks;
```

- [ ] **Step 4: Verificar que compila**

Run: `dotnet build Alquitel.sln`
Expected: `Build succeeded.` (fallará hasta completar Task 5, que cambia el constructor de `LoginWindow` — está bien, se corrige en el próximo task; si preferís compilar limpio task por task, hacer Task 5 antes de este Step 4).

- [ ] **Step 5: Commit**

```bash
git add Alquitel.UI/App.xaml.cs
git commit -m "feat: auto-login desde sesión guardada en OnStartup"
```

---

### Task 5: `LoginWindow` guarda sesión tras login exitoso

**Files:**
- Modify: `Alquitel.UI\Views\LoginWindow.xaml.cs:18-35` (constructor), `Alquitel.UI\Views\LoginWindow.xaml.cs:118` (`TryLoginAsync`)

**Interfaces:**
- Consumes: `ISessionStore.Save(Guid)` (Task 2/3).
- Produces: `LoginWindow` con nuevo parámetro de constructor `ISessionStore sessionStore` (usado por Task 4, que ya lo pasa).

- [ ] **Step 1: Agregar el campo y parámetro de constructor**

En `Alquitel.UI\Views\LoginWindow.xaml.cs`, reemplazar:

```csharp
        private readonly ICurrentUserService _currentUserService;
        private readonly IUserRepository _userRepository;
        private List<User> _users = new();

        public LoginWindow(IUserRepository userRepository, ICurrentUserService currentUserService)
        {
            _currentUserService = currentUserService;
            _userRepository = userRepository;
            InitializeComponent();
```

por:

```csharp
        private readonly ICurrentUserService _currentUserService;
        private readonly IUserRepository _userRepository;
        private readonly Alquitel.Core.Interfaces.ISessionStore _sessionStore;
        private List<User> _users = new();

        public LoginWindow(
            IUserRepository userRepository,
            ICurrentUserService currentUserService,
            Alquitel.Core.Interfaces.ISessionStore sessionStore)
        {
            _currentUserService = currentUserService;
            _userRepository = userRepository;
            _sessionStore = sessionStore;
            InitializeComponent();
```

- [ ] **Step 2: Guardar sesión tras login exitoso**

En el mismo archivo, en `TryLoginAsync`, reemplazar:

```csharp
            _currentUserService.SetCurrentUser(user);
            DialogResult = true;
```

por:

```csharp
            _currentUserService.SetCurrentUser(user);
            _sessionStore.Save(user.Id);
            DialogResult = true;
```

- [ ] **Step 3: Verificar que compila**

Run: `dotnet build Alquitel.sln`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add Alquitel.UI/Views/LoginWindow.xaml.cs
git commit -m "feat: LoginWindow guarda sesión tras login exitoso"
```

---

### Task 6: `MainViewModel.Logout` limpia la sesión

**Files:**
- Modify: `Alquitel.UI\ViewModels\MainViewModel.cs:18-134` (campo, constructor), `Alquitel.UI\ViewModels\MainViewModel.cs:286-297` (`Logout`)
- Modify: `Alquitel.UI\App.xaml.cs` (`ConfigureServices` ya registra `MainViewModel` como singleton vía DI — no requiere cambios ahí, DI resuelve el nuevo parámetro automáticamente)

**Interfaces:**
- Consumes: `ISessionStore.Clear()` (Task 2/3).

- [ ] **Step 1: Agregar el campo y parámetro de constructor**

En `Alquitel.UI\ViewModels\MainViewModel.cs`, agregar el campo junto a los demás (después de línea 27 `_weeklySummaryService`):

```csharp
        private readonly IWeeklySummaryService _weeklySummaryService;
        private readonly Alquitel.Core.Interfaces.ISessionStore _sessionStore;
```

Y en el constructor (línea 114-124), reemplazar:

```csharp
        public MainViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDocumentService documentService, INavigationService navigationService, IAppSettings appSettings, ICurrentUserService currentUserService, IRemoteSyncService remoteSyncService, Alquitel.UI.Services.ToastService toastService, IDialogService dialogService, IWeeklySummaryService weeklySummaryService)
        {
            _weeklySummaryService = weeklySummaryService;
            _dbContextFactory = dbContextFactory;
            _documentService = documentService;
            _navigationService = navigationService;
            _appSettings = appSettings;
            _currentUserService = currentUserService;
            _remoteSyncService = remoteSyncService;
            Toasts = toastService;
            _dialogService = dialogService;
```

por:

```csharp
        public MainViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDocumentService documentService, INavigationService navigationService, IAppSettings appSettings, ICurrentUserService currentUserService, IRemoteSyncService remoteSyncService, Alquitel.UI.Services.ToastService toastService, IDialogService dialogService, IWeeklySummaryService weeklySummaryService, Alquitel.Core.Interfaces.ISessionStore sessionStore)
        {
            _weeklySummaryService = weeklySummaryService;
            _sessionStore = sessionStore;
            _dbContextFactory = dbContextFactory;
            _documentService = documentService;
            _navigationService = navigationService;
            _appSettings = appSettings;
            _currentUserService = currentUserService;
            _remoteSyncService = remoteSyncService;
            Toasts = toastService;
            _dialogService = dialogService;
```

- [ ] **Step 2: Limpiar sesión en `Logout`**

En el mismo archivo, reemplazar:

```csharp
        [RelayCommand]
        private void Logout()
        {
            if (!_dialogService.ShowConfirm("Cerrar sesión", "¿Cerrar la sesión actual?"))
                return;

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                System.Diagnostics.Process.Start(exePath);

            System.Windows.Application.Current.Shutdown();
        }
```

por:

```csharp
        [RelayCommand]
        private void Logout()
        {
            if (!_dialogService.ShowConfirm("Cerrar sesión", "¿Cerrar la sesión actual?"))
                return;

            _sessionStore.Clear();

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                System.Diagnostics.Process.Start(exePath);

            System.Windows.Application.Current.Shutdown();
        }
```

- [ ] **Step 3: Verificar que compila**

Run: `dotnet build Alquitel.sln`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add Alquitel.UI/ViewModels/MainViewModel.cs
git commit -m "feat: Logout limpia la sesión persistente"
```

---

### Task 7: Verificación manual end-to-end

**Files:** ninguno (solo verificación, sin cambios de código)

**Interfaces:** ninguna — consume el sistema completo ensamblado en Tasks 1-6.

- [ ] **Step 1: Build completo**

Run: `dotnet build Alquitel.sln`
Expected: `Build succeeded.` sin warnings nuevos relacionados a `ISessionStore`/`FileSessionStore`.

- [ ] **Step 2: Tests de Core (no deben romperse — este feature no los toca)**

Run: `dotnet test Alquitel.Core.Tests\Alquitel.Core.Tests.csproj`
Expected: todos los tests existentes en verde (mismo resultado que antes de este plan).

- [ ] **Step 3: Primer login guarda sesión**

Run: `dotnet run --project Alquitel.UI\Alquitel.UI.csproj`
- Se muestra `LoginWindow` (no hay sesión previa). Elegir un usuario sin password. Login.
- Verificar que existe `%LocalAppData%\Alquitel\session.json` con contenido `{"UserId":"<guid>","SavedAtUtc":"<fecha reciente>"}`.
- Cerrar la app.

- [ ] **Step 4: Relanzamiento saltea LoginWindow**

Run: `dotnet run --project Alquitel.UI\Alquitel.UI.csproj`
Expected: la app abre directo en el Dashboard (o pantalla de Armador), sin mostrar `LoginWindow`.

- [ ] **Step 5: Logout limpia sesión y fuerza re-login**

Con la app abierta, click en "Cerrar sesión" → confirmar.
Expected: el proceso se relanza y esta vez SÍ muestra `LoginWindow`. Verificar que `session.json` fue borrado o ya no corresponde al usuario anterior (se reescribe con el próximo login).

- [ ] **Step 6: Expiración de Admin a los 30 días**

Con un usuario Admin (con password) logueado y `session.json` generado:
- Cerrar la app.
- Editar manualmente `session.json`, cambiando `SavedAtUtc` a una fecha 31+ días atrás (ISO 8601, ej. `2026-06-10T00:00:00+00:00`).
- Relanzar la app.
Expected: se muestra `LoginWindow` pidiendo password del Admin de nuevo (no auto-login). Login exitoso reescribe `SavedAtUtc` con la fecha actual.

- [ ] **Step 7: Sesión corrupta no crashea el startup**

- Cerrar la app.
- Sobreescribir `session.json` con texto inválido (ej. `not json`).
- Relanzar la app.
Expected: no crashea; muestra `LoginWindow` normalmente (fallback silencioso).

- [ ] **Step 8: Commit final (si hubo algún ajuste durante la verificación)**

```bash
git status
# Si hay cambios pendientes de fixes encontrados durante verificación:
git add -A
git commit -m "fix: ajustes de verificación manual sesión persistente"
```
