# Configuración por variables de entorno

Alquitel lee su configuración en este orden (lo último pisa lo anterior):

1. `appsettings.json` (junto al ejecutable)
2. **Variables de entorno con prefijo `ALQUITEL_`**
3. User Secrets (`dotnet user-secrets`) — solo en compilaciones DEBUG

Definido en `Alquitel.UI/App.xaml.cs` → `BuildConfiguration()`.

## Convención de nombres

Cada nivel de la jerarquía JSON se separa con **doble guion bajo** (`__`) y se
antepone el prefijo `ALQUITEL_`:

| Clave de configuración | Variable de entorno |
|---|---|
| `Database:Provider` | `ALQUITEL_Database__Provider` |
| `Database:Supabase:ConnectionString` | `ALQUITEL_Database__Supabase__ConnectionString` |
| `Database:Supabase:Url` | `ALQUITEL_Database__Supabase__Url` |
| `Database:Supabase:AnonKey` | `ALQUITEL_Database__Supabase__AnonKey` |

## Cómo agregar la variable (connection string)

### Opción A — PowerShell (recomendada, persistente para el usuario actual)

```powershell
[Environment]::SetEnvironmentVariable(
    "ALQUITEL_Database__Supabase__ConnectionString",
    "Host=aws-1-sa-east-1.pooler.supabase.com;Port=5432;Database=postgres;Username=alquitel_app.qgtaugmxmoxtpxvmugvt;Password=<PASSWORD>",
    "User")

[Environment]::SetEnvironmentVariable("ALQUITEL_Database__Provider", "supabase", "User")
```

Verificar (en una terminal **nueva**):

```powershell
$env:ALQUITEL_Database__Supabase__ConnectionString
```

### Opción B — Interfaz de Windows

1. `Win + R` → `sysdm.cpl` → pestaña **Opciones avanzadas** → **Variables de entorno...**
2. En "Variables de usuario" → **Nueva...**
3. Nombre: `ALQUITEL_Database__Supabase__ConnectionString`
4. Valor: el connection string completo.
5. Aceptar todo.

### Opción C — Solo para una sesión de terminal (pruebas)

```powershell
$env:ALQUITEL_Database__Supabase__ConnectionString = "Host=...;Password=..."
dotnet run --project Alquitel.UI\Alquitel.UI.csproj
```

Se pierde al cerrar la terminal.

## Importante

- **Reiniciar la app** (y la terminal/IDE desde donde se lanza) después de setear una
  variable persistente: los procesos ya abiertos no ven variables nuevas.
- En DEBUG, un valor en User Secrets pisa a la variable de entorno. En Release
  (instalador), la variable de entorno pisa al `appsettings.json`.
- Con la variable definida se puede **eliminar el connection string del
  `appsettings.json`** distribuido, evitando que la credencial viaje en el instalador.
- Si `Database:Provider` es `supabase`/`postgres` pero el connection string queda vacío,
  la app cae a SQLite local y lo registra como warning en el log.
