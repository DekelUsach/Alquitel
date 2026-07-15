# Sesión persistente entre lanzamientos — Diseño

Fecha: 2026-07-15

## Problema

Cada lanzamiento de Alquitel muestra `LoginWindow` (selección de usuario + password si aplica) y bloquea hasta elegir. El usuario quiere que la app recuerde la sesión y evite este paso en la mayoría de los relanzamientos.

## Requisitos (confirmados con el usuario)

1. Auto-login silencioso: si hay sesión guardada válida, la app entra directo sin mostrar `LoginWindow`.
2. Usuarios sin password: sesión sin expiración.
3. Usuarios Admin (con password obligatoria): sesión expira a los 30 días — pasado ese plazo, vuelve a pedir password normalmente vía `LoginWindow`.
4. Botón "Cerrar sesión" existente (`MainViewModel.Logout`) debe limpiar la sesión guardada, para que el próximo lanzamiento muestre `LoginWindow` de cero.

## Componentes nuevos

- **`AppPaths.SessionFilePath`**: `%LocalAppData%\Alquitel\session.json`.
- **`ISessionStore`** (Alquitel.Core/Interfaces): contrato de persistencia de sesión.
  ```csharp
  public interface ISessionStore
  {
      void Save(Guid userId);
      bool TryLoad(out Guid userId, out DateTimeOffset savedAtUtc);
      void Clear();
  }
  ```
- **`FileSessionStore`** (Alquitel.Infrastructure/Services): implementación JSON simple sobre `AppPaths.SessionFilePath`. Cualquier error de I/O o deserialización (archivo corrupto, permisos) se trata como "sin sesión" (catch silencioso + log warning), nunca crashea el startup.
  - `Save`: escribe `{ "UserId": "<guid>", "SavedAtUtc": "<iso8601>" }`.
  - `TryLoad`: lee y parsea; `false` si no existe o falla parseo.
  - `Clear`: borra el archivo si existe.

## Flujo de `App.xaml.cs::OnStartup`

Antes de instanciar `LoginWindow`:

1. `sessionStore.TryLoad(out userId, out savedAtUtc)`.
2. Si `true`: resolver usuario vía `IUserRepository.GetByIdAsync(userId)`, bloqueando con `Task.Run(() => repo.GetByIdAsync(userId)).GetAwaiter().GetResult()` (corre en threadpool, no en el hilo de UI — no genera deadlock).
3. Auto-login válido solo si:
   - usuario existe y `!user.IsArchived`, **y**
   - si `user.Role == UserRole.Admin`: `(DateTimeOffset.UtcNow - savedAtUtc) <= TimeSpan.FromDays(30)`.
4. Si válido: `currentUserService.SetCurrentUser(user)`, se saltea `LoginWindow` por completo, sigue directo a `mainWindow.Show()`.
5. Si inválido por cualquier motivo (sin sesión, usuario borrado/archivado, red caída al resolver, Admin con sesión vencida): fallback normal — se muestra `LoginWindow` como hoy.

## Cambios en `LoginWindow.TryLoginAsync`

Tras `_currentUserService.SetCurrentUser(user)` exitoso (línea 118 actual), agregar:
```csharp
_sessionStore.Save(user.Id);
```
Esto re-arma el timestamp también para Admins que vuelven a loguearse tras expiración — el reloj de 30 días arranca de nuevo en cada login manual exitoso.

## Cambios en `MainViewModel.Logout`

Agregar `_sessionStore.Clear()` antes de relanzar el proceso. El comentario existente en el método ("relanzar vuelve a mostrar LoginWindow limpio") sigue siendo válido, solo que ahora requiere el `Clear()` explícito porque hay sesión persistida de por medio.

## Registro DI

`ISessionStore` → `FileSessionStore`, Singleton, registrado en `App.xaml.cs::ConfigureServices` junto a los demás servicios de infraestructura. Inyectar en `App` (constructor de la clase o resuelto vía `ServiceProvider` en `OnStartup`, igual que `ICurrentUserService`), en `LoginWindow` y en `MainViewModel`.

## Seguridad / riesgos aceptados

- Sesión no-Admin queda indefinida en la PC hasta logout manual — igual al modelo actual donde elegir usuario sin password ya no tenía fricción real.
- Admin queda logueado hasta 30 días sin repetir password — riesgo aceptado explícitamente por el usuario.
- El archivo `session.json` no contiene password ni hash, solo `UserId` (no es secreto sensible, pero igual queda fuera del repo por estar en `%LocalAppData%`).

## Testing

- `FileSessionStore`: no aplica a `Alquitel.Core.Tests` (vive en Infrastructure, con I/O real y EF — fuera del alcance de xUnit de Core). Verificación manual: lanzar app, loguear, cerrar, relanzar → sin `LoginWindow`. Cerrar sesión → relanzar pide login. Editar `SavedAtUtc` en el JSON a >30 días atrás con usuario Admin → relanzar pide password.
- No se agregan tests unitarios nuevos a `Alquitel.Core.Tests` porque no hay lógica pura de Core involucrada (todo el flujo toca EF, filesystem y WPF).
