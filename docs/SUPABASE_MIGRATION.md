# 🗄️ Plan de Migración a Supabase (PostgreSQL en servidor)

Este documento describe el terreno ya preparado en el código y los pasos pendientes para
migrar la persistencia de Alquitel desde SQLite local hacia una base de datos en servidor
(Supabase u otro PostgreSQL administrado), **sin romper el modo local actual**.

---

## 1. Estado actual (qué ya está hecho)

### 1.1 Capa de repositorios (abstracción de datos)
La UI ya no necesita conocer EF Core para operaciones estándar. Existen interfaces en Core
e implementaciones locales en Infrastructure:

| Interfaz (Alquitel.Core) | Implementación local (Alquitel.Infrastructure) |
|---|---|
| `Interfaces/Repositories/IClientRepository.cs` | `Persistence/Repositories/EfClientRepository.cs` |
| `Interfaces/Repositories/IProductRepository.cs` | `Persistence/Repositories/EfProductRepository.cs` |
| `Interfaces/Repositories/ILocationRepository.cs` | `Persistence/Repositories/EfLocationRepository.cs` |
| `Interfaces/Repositories/IOrderRepository.cs` | `Persistence/Repositories/EfOrderRepository.cs` |

Todas están registradas en el contenedor de DI (`App.xaml.cs > ConfigureServices`).
**Para migrar, solo se reemplazan esos cuatro registros** por implementaciones remotas.

### 1.2 Contrato de sincronización
- `Alquitel.Core/Interfaces/IRemoteSyncService.cs`: contrato con `IsRemoteConfigured`,
  `TestConnectionAsync()` y `PushPendingChangesAsync()`.
- `Alquitel.Infrastructure/Services/LocalOnlySyncService.cs`: implementación no-op actual.

### 1.3 Configuración
`Alquitel.UI/appsettings.json` incluye la sección:

```json
"Database": {
  "Provider": "sqlite",
  "Supabase": { "Url": "", "AnonKey": "", "Schema": "public" }
}
```

Mientras `Provider` sea `"sqlite"` nada cambia. Las credenciales reales **no** deben
commitearse: usar User Secrets en desarrollo (`dotnet user-secrets`) o variables de
entorno con prefijo `ALQUITEL_` (ya soportadas por `BuildConfiguration()`), por ejemplo
`ALQUITEL_Database__Supabase__AnonKey`.

### 1.4 Ventajas preexistentes del modelo
- **Todas las PK son `Guid`** generadas en cliente → mapean directo a `uuid` de PostgreSQL
  y permiten sincronización offline sin colisiones de identidad.
- **Borrado lógico** (`IsArchived`) en `Client` y `Product` → compatible con réplicas.
- **Snapshots** en `OrderItem` (`DescriptionSnapshot`, `CustomFieldsJson`) → el historial
  no depende de joins vivos.

---

## 2. Esquema propuesto en Supabase

```sql
create table clients (
  id uuid primary key,
  company_name text not null,
  cuit text not null default '',
  contact_name text,
  email text,
  phone text,
  is_archived boolean not null default false,
  updated_at timestamptz not null default now()
);
create unique index clients_cuit_unique on clients (cuit) where cuit <> '' and not is_archived;

create table products (
  id uuid primary key,
  description text not null,
  category text not null default 'General',
  base_price numeric(18,2) not null default 0,
  image_path text,
  custom_fields_json jsonb,
  is_archived boolean not null default false,
  updated_at timestamptz not null default now()
);

create table locations (
  id uuid primary key,
  name text not null
);

create table orders (
  id uuid primary key,
  budget_number text not null,
  admin_name text not null default '',
  client_id uuid not null references clients(id),
  location_id uuid not null references locations(id),
  created_date timestamptz not null,
  event_date timestamptz,
  status smallint not null default 0
);
create index orders_created_date_idx on orders (created_date);
create index orders_client_id_idx on orders (client_id);

create table order_items (
  id uuid primary key,
  order_id uuid not null references orders(id) on delete cascade,
  product_id uuid not null references products(id),
  quantity int not null default 1,
  unit_price numeric(18,2) not null default 0,
  dias int not null default 1,
  technical_notes text,
  image_path text,
  custom_fields_json jsonb,
  description_snapshot text,
  requested_measure text
);
create index order_items_order_id_idx on order_items (order_id);
```

Notas:
- `custom_fields_json` pasa de `TEXT` a `jsonb` (consulta indexable; el contenido es idéntico).
- Agregar `updated_at` + trigger `moddatetime` para resolución de conflictos en sync.
- Activar **RLS** (Row Level Security) y crear policies por rol antes de exponer la API.

---

## 3. Estrategias de conexión (elegir una al implementar)

### Opción A — EF Core + Npgsql directo (recomendada para esta app de escritorio)
1. `dotnet add Alquitel.Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL`
2. En `App.xaml.cs`, elegir provider según config:
   ```csharp
   var provider = configuration["Database:Provider"] ?? "sqlite";
   services.AddDbContextFactory<AlquitelDbContext>(options =>
   {
       if (provider.Equals("supabase", StringComparison.OrdinalIgnoreCase))
           options.UseNpgsql(configuration["Database:Supabase:ConnectionString"]);
       else
           options.UseSqlite(AppPaths.DbConnectionString);
   });
   ```
3. Usar el *connection pooler* de Supabase (puerto 6543, modo transaction) porque la app
   abre/cierra contextos por operación.
4. Generar migraciones separadas por provider (carpeta `Migrations/Npgsql`).

### Opción B — supabase-csharp (PostgREST + Realtime)
1. `dotnet add Alquitel.Infrastructure package supabase-csharp`
2. Implementar `SupabaseClientRepository` etc. contra la API REST usando `Url` + `AnonKey`.
3. Ventaja: Realtime subscriptions (varios puestos de trabajo ven cambios en vivo);
   desventaja: reescritura de queries complejas (Include, IgnoreQueryFilters).

### Opción C — Híbrido offline-first (objetivo final sugerido)
SQLite sigue siendo la base operativa (rápida, sin internet) y `IRemoteSyncService`
implementa push/pull incremental contra Supabase usando `updated_at` como watermark.
La interfaz ya contempla este camino (`PushPendingChangesAsync`).

---

## 4. Checklist de migración (ejecutada el 2026-07-06)

- [x] Crear proyecto en Supabase y ejecutar el DDL. Proyecto `Alquitel`
      (ref `qgtaugmxmoxtpxvmugvt`, región sa-east-1, PostgreSQL 17). **Nota**: el esquema
      real usa nombres PascalCase citados (`"Clients"`, `"Products"`, ...) idénticos a las
      convenciones default de EF Core, no el snake_case propuesto en la sección 2, para que
      el mismo `AlquitelDbContext` mapee sin configuración extra. `CustomFieldsJson` quedó
      `text` (no `jsonb`). Timestamps `timestamp` sin time zone + switch
      `Npgsql.EnableLegacyTimestampBehavior` en el cliente.
- [x] Activar RLS + policies. Rol dedicado `alquitel_app` (login) con policy full-access por
      tabla; `anon`/`authenticated` sin grants → la API REST no expone nada.
- [x] Estrategia elegida: **Opción A** (EF Core + Npgsql directo vía session pooler
      `aws-1-sa-east-1.pooler.supabase.com:5432`, usuario `alquitel_app.qgtaugmxmoxtpxvmugvt`).
- [x] Repositorios: los `Ef*Repository` existentes sirven para ambos providers (mismo
      DbContext); no hizo falta implementación PostgREST separada. Se agregó `EfUserRepository`.
- [x] `PostgresSyncService : IRemoteSyncService` (test de conexión + carga inicial one-shot
      SQLite → Supabase desde Configuración → "Subir datos locales al servidor").
- [x] `App.xaml.cs > ConfigureServices` elige `UseNpgsql`/`UseSqlite` según
      `Database:Provider`; con ConnectionString vacío cae a SQLite con warning.
- [x] Carga inicial: botón en Configuración (upsert masivo conservando Guid).
- [x] Credenciales: User Secrets en desarrollo; en producción editar el `appsettings.json`
      desplegado o variable `ALQUITEL_Database__Supabase__ConnectionString`.
- [x] Ambos modos probados: `Provider=sqlite` intacto; `Provider=supabase` verificado con
      smoke test de conexión, RLS e insert/delete.

---

## 4.b Plantillas centralizadas en Supabase Storage (ejecutado el 2026-07-06)

- Bucket privado `templates` (límite 10 MB, solo MIME .docx) con objetos de nombre fijo:
  `presupuesto.docx`, `of.docx`, `ot.docx`.
- Policies RLS sobre `storage.objects`: el rol `anon` tiene select/insert/update/delete
  **solo** en `bucket_id = 'templates'`; el resto del storage sigue inaccesible.
- La app usa `Database:Supabase:Url` + `AnonKey` de `appsettings.json` (la anon key es
  pública por diseño; el acceso al bucket es el único privilegio que otorga).
- Código: `ITemplateStorageService` (Core) + `SupabaseTemplateStorageService`
  (Infrastructure, REST `/storage/v1`). Al generar un documento, la plantilla publicada
  en la nube tiene prioridad sobre la ruta local; cada descarga se cachea en
  `%LocalAppData%\Alquitel\templates_cache` para funcionar sin internet.
- Publicación: Configuración → "Plantillas en la nube" (sección visible solo para Admin;
  Configuración entera ya está gateada por rol). La plantilla inicial de presupuesto
  (`template - copia.docx`) fue publicada como `presupuesto.docx`.

---

## 5. Qué NO cambia con la migración

- Entidades de `Alquitel.Core` (mismas clases, mismos Guid).
- Generación de documentos Word (consume entidades en memoria, agnóstica al backend).
- Backups locales (`DatabaseBackupService`) mientras SQLite siga en uso.
- ViewModels: dependen de interfaces, no del proveedor concreto.
