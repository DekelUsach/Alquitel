# Concurrencia de órdenes, estados y auditoría

## Objetivo

Evitar que una edición, un cambio de estado o un reintento de sincronización sobrescriba silenciosamente una versión más reciente de una orden. El usuario debe poder entender el conflicto, recargar la versión vigente, conservar su trabajo local o realizar una sobrescritura explícita. Cada mutación confirmada debe quedar auditada exactamente una vez y en la misma transacción que el cambio.

## Alcance

Esta entrega cubre:

- configuración real de concurrencia optimista para `Order.RowVersion`;
- persistencia de órdenes nuevas y existentes;
- cambios de estado desde el pool de órdenes;
- comparación y resolución explícita de conflictos;
- transiciones válidas de estado;
- auditoría transaccional de creación, edición y cambio de estado;
- compatibilidad con filas legadas cuyo `RowVersion` es `Guid.Empty`;
- pruebas de integración para SQLite y una ejecución opcional contra PostgreSQL.

Quedan fuera de esta entrega drafts, outbox, backups, generación documental, privacidad de IA y el resto de la extracción de `BudgetBuilderViewModel`. Esos frentes tendrán especificaciones separadas.

## Fallos confirmados

`Order.RowVersion` existe en la entidad y en ambas bases, pero `AlquitelDbContext` no lo configura como token de concurrencia. `OrderPersistenceService` carga la fila, compara el GUID en memoria y luego guarda. Otro proceso puede modificar la misma orden entre esa comparación y `SaveChangesAsync`; EF no agrega `RowVersion` al predicado del `UPDATE`, por lo que el segundo guardado pisa al primero.

`OrderPoolViewModel` abre un contexto propio, asigna `Status` y guarda sin comprobar la versión cargada por la grilla. El cambio tampoco pasa por una política de transiciones. La auditoría se escribe después mediante otro contexto; un fallo entre ambos pasos deja una orden modificada sin evento, y un reintento puede duplicar eventos.

`OrderPersistenceService` asigna el nuevo `RowVersion` al objeto de UI antes de confirmar la transacción. Si el guardado falla, la memoria queda adelantada respecto de la base. La estrategia de ejecución puede volver a ejecutar el cuerpo, por lo que la mutación del objeto y la auditoría necesitan identidad estable y verificación de éxito.

## Alternativas evaluadas

### GUID administrado por la aplicación

Configurar `RowVersion` mediante `.IsConcurrencyToken()` y rotarlo con `Guid.NewGuid()` en cada mutación. EF genera un `UPDATE` condicionado por `Id` y el token original. Es compatible con SQLite y PostgreSQL, utiliza la columna ya desplegada y permite el mismo contrato en ambos proveedores.

Esta es la alternativa elegida.

### Tokens específicos del proveedor

PostgreSQL podría usar `xmin` y SQL Server un `rowversion`, pero SQLite no ofrece un equivalente directo. Mantener dos modelos de concurrencia introduciría migraciones y comportamientos distintos, además de exigir coordinación sobre el esquema remoto de Supabase. Se descarta para esta fase.

### Bloqueos o aislamiento serializable

Las transacciones largas degradarían la experiencia y no protegerían una orden que permanece abierta en la UI durante minutos. SQLite y PostgreSQL también tienen semánticas de bloqueo diferentes. Se descarta.

## Modelo de concurrencia

`AlquitelDbContext.OnModelCreating` configurará:

```csharp
modelBuilder.Entity<Order>()
    .Property(o => o.RowVersion)
    .IsConcurrencyToken();
```

No se agrega ni cambia ninguna columna. PostgreSQL conserva `uuid`; SQLite conserva su representación actual de `Guid`. La diferencia entre proveedores queda limitada al tipo físico: en ambos casos EF compara el valor original en el `WHERE`.

Para una edición normal:

1. El llamador entrega la versión que cargó.
2. El servicio carga la fila vigente y conserva su snapshot para el posible conflicto.
3. La propiedad `OriginalValue` de `RowVersion` se fija al token del llamador.
4. La propiedad actual recibe un nuevo GUID candidato.
5. EF guarda la orden, los ítems y el evento de auditoría en una transacción.
6. Solo después del commit se copia el nuevo token al objeto del llamador.

Si otro escritor cambió la fila, EF lanza `DbUpdateConcurrencyException`. El servicio revierte la transacción, abre un contexto limpio, carga la versión vigente y devuelve un conflicto estructurado. La excepción no llega a la UI.

Una sobrescritura explícita no desactiva la concurrencia. El servicio vuelve a leer la versión vigente, la utiliza como `OriginalValue`, aplica el contenido local y rota el token. Si aparece un tercer cambio antes del commit, vuelve a devolver conflicto.

## Contratos de aplicación

El resultado actual basado únicamente en un enum se reemplazará por contratos con información suficiente para resolver el conflicto:

```csharp
public enum OrderPersistStatus
{
    Saved,
    Conflict,
    Error
}

public enum OrderConflictResolution
{
    Reject,
    OverwriteLatest
}

public sealed record OrderConflictDetails(
    Guid OrderId,
    Guid ExpectedRowVersion,
    Guid ActualRowVersion,
    IReadOnlyList<string> ChangedFields,
    Order LatestOrder);

public sealed record OrderPersistOutcome(
    OrderPersistStatus Status,
    Guid? PersistedRowVersion = null,
    OrderConflictDetails? Conflict = null,
    string? ErrorCode = null);
```

`IOrderPersistenceService` expondrá:

```csharp
Task<OrderPersistOutcome> PersistAsync(
    Order order,
    OrderConflictResolution resolution = OrderConflictResolution.Reject,
    CancellationToken cancellationToken = default);
```

Se agregará `IOrderStatusService`:

```csharp
Task<OrderPersistOutcome> ChangeAsync(
    Guid orderId,
    Guid expectedRowVersion,
    OrderStatus newStatus,
    OrderConflictResolution resolution = OrderConflictResolution.Reject,
    CancellationToken cancellationToken = default);
```

El resultado `Error` llevará un código estable y no un texto técnico. La UI decidirá el mensaje. Los servicios registrarán identificadores técnicos mínimos, sin contenido de la orden.

## Comparación de versiones

`OrderConflictComparer` será lógica pura en Core. Informará nombres de campos comprensibles para la UI al detectar diferencias en:

- número de presupuesto;
- responsable;
- cliente y ubicación;
- fechas del evento;
- estado;
- comentarios;
- descuento porcentual y fijo;
- IVA;
- productos, cantidades, días, precios, notas y snapshots.

La comparación de ítems se realizará por identidad y valores persistidos, no por referencias de navegación ni propiedades transitorias de UI. `LatestOrder` será una copia `AsNoTracking` con cliente, ubicación e ítems incluidos.

## Resolución en la UI

`BudgetBuilderViewModel` conservará los cambios locales mientras se decide. Ante conflicto mostrará los campos modificados y ofrecerá, mediante los diálogos existentes:

1. recargar la versión vigente;
2. si no se recarga, sobrescribir explícitamente;
3. si tampoco se sobrescribe, seguir editando sin perder el contenido local.

La recarga reutilizará el flujo existente de carga de una orden. La sobrescritura llamará nuevamente al servicio con `OverwriteLatest`. Un segundo conflicto se mostrará de nuevo; no habrá bucles automáticos.

`OrderPoolRow` conservará el `RowVersion` con el que fue cargado. `OrderPoolViewModel` dejará de usar `IDbContextFactory` para persistir estados y delegará en `IOrderStatusService`. Tras un guardado actualizará el token de la fila; ante conflicto restaurará visualmente el estado anterior y permitirá recargar la lista o sobrescribir de forma consciente.

## Política de estados

`OrderStatusTransitionPolicy` vivirá en Core y permitirá:

- `Draft` → `Approved`, `Rejected`, `Archived`;
- `Approved` → `Draft`, `SentToOF`, `SentToOT`, `Rejected`, `Archived`;
- `SentToOF` → `SentToOT`, `Approved`, `Archived`;
- `SentToOT` → `SentToOF`, `Approved`, `Archived`;
- `Rejected` → `Draft`, `Archived`;
- `Archived` → `Draft`.

Asignar el mismo estado será idempotente: devolverá éxito sin rotar el token ni crear auditoría. Una transición no permitida devolverá `Error` con el código `invalid_status_transition`, sin tocar la base.

## Auditoría transaccional e idempotencia

La creación, edición y transición de estado agregarán un `OrderAuditEvent` al mismo contexto y a la misma transacción que la orden. Un helper interno construirá el evento con el usuario actual.

Cada intento lógico tendrá un `auditEventId` estable creado fuera del cuerpo reintentable. El nuevo `RowVersion` candidato también será estable durante ese intento. La estrategia de ejecución verificará el éxito consultando ambos valores cuando el commit haya podido completarse pero la confirmación de red se haya perdido. Así no se repetirá la mutación ni se duplicará la auditoría.

`IOrderAuditService.LogAsync` continuará para eventos posteriores e independientes, como “documento generado”. La consulta de historial seguirá en el mismo servicio.

## Filas legadas

Una fila cuyo token vigente y token cargado sean `Guid.Empty` podrá guardarse una vez y recibirá un GUID real. Si el llamador trae `Guid.Empty` pero la base ya contiene un GUID, se devolverá conflicto. Esto convierte las filas antiguas de forma gradual sin una migración destructiva ni una actualización masiva de Supabase.

## Numeración e ítems

El índice único de `BudgetNumber` seguirá siendo la autoridad para colisiones de numeración. La renumeración continuará con un máximo acotado de intentos.

La orden, el reemplazo de ítems y la auditoría se confirmarán juntos. Si la comprobación de concurrencia falla, la eliminación o inserción de ítems se revierte. El objeto de UI no recibirá el número renumerado ni el nuevo token hasta que el commit esté confirmado.

## Pruebas

### Core

- matriz completa de transiciones permitidas y rechazadas;
- asignación idempotente del mismo estado;
- comparación de cada campo de la orden;
- comparación de altas, bajas y cambios de ítems;
- exclusión de propiedades transitorias como `HasStockConflict`.

### Infraestructura con SQLite real

- dos contextos cargan el mismo token y solo el primero guarda;
- cambio ocurrido después de la lectura pero antes del `SaveChanges` produce conflicto;
- la sobrescritura explícita usa la última versión y rota el token;
- un tercer escritor puede hacer fallar también la sobrescritura;
- la creación, edición y transición generan exactamente un evento;
- una mutación fallida no deja evento ni cambios parciales de ítems;
- `Guid.Empty` se promueve una vez y luego participa de la concurrencia;
- cambio al mismo estado no rota token ni audita;
- transición inválida no modifica datos;
- cancelación revierte la transacción;
- colisión de número se renumera sin duplicar auditoría.

### PostgreSQL opcional

El mismo conjunto crítico de concurrencia se ejecutará cuando exista una cadena de pruebas en `ALQUITEL_TEST_POSTGRES`. Sin esa variable, las pruebas quedarán omitidas con motivo explícito. No utilizarán la base de producción ni modificarán RLS, funciones o esquema remoto.

## Coordinación con otros agentes

Antigravity debe crear `Alquitel.Infrastructure.Tests/Alquitel.Infrastructure.Tests.csproj`, agregarlo a `Alquitel.sln` y gestionar las versiones mediante la política de dependencias que está preparando. El proyecto necesita:

- target `net8.0-windows`;
- referencias de proyecto a `Alquitel.Core` y `Alquitel.Infrastructure`;
- `Microsoft.NET.Test.Sdk`;
- `xunit`;
- `xunit.runner.visualstudio`;
- `coverlet.collector`;
- `Microsoft.EntityFrameworkCore.Sqlite`;
- `Npgsql.EntityFrameworkCore.PostgreSQL`.

El cambio en `AlquitelDbContext` se limitará a `.IsConcurrencyToken()`. No se tocarán `supabase/**`, RLS, autenticación ni migraciones remotas. `App.xaml.cs` solo se modificará para registrar `IOrderStatusService`; se avisará a Claude porque puede editar el mismo archivo.

## Criterios de aceptación

- ningún guardado de orden o estado puede sobrescribir una versión más reciente sin confirmación explícita;
- un conflicto devuelve la versión vigente y los campos divergentes;
- recargar, conservar y sobrescribir son caminos recuperables;
- cada mutación confirmada genera exactamente un evento de auditoría en la misma transacción;
- SQLite y PostgreSQL comparten el mismo contrato de concurrencia;
- las filas legadas se actualizan gradualmente;
- todos los tests disponibles, el build Release y las nuevas pruebas de integración finalizan sin errores;
- las pruebas no crean procesos `WINWORD.EXE`.
