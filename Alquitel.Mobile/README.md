# 📱 Alquitel.Mobile

App Android (.NET MAUI) de Alquitel: todas las funcionalidades del sistema de escritorio que **no dependen de archivos locales** (OneDrive / Word). Comparte la base de datos de Supabase (PostgreSQL) y el dominio de `Alquitel.Core` con la app WPF.

## Funcionalidades

- **Login multiusuario** contra la tabla `Users` compartida (PBKDF2, mismo hash que el desktop).
- **Dashboard**: métricas del mes, aprobaciones pendientes y pedidos recientes.
- **Nuevo presupuesto**: pegás el pedido del cliente en texto libre → parser IA (Pollinations `nova-fast`) con fallback al `ProductMatcher` local de Core → carrito editable (cantidad, días, precio, medida) → cliente, ubicación, fechas, descuento, IVA → se guarda la `Order` en el pool compartido. *El documento Word lo genera después la app de escritorio.*
- **Pool de pedidos**: filtros por estado y texto, detalle con ítems, cambio de estado con bitácora (`OrderAuditEvents`).
- **Aprobaciones**: genera el link público (Edge Function `aprobar`) y lo comparte por WhatsApp/email con el Share nativo; muestra el estado (pendiente / aprobado / rechazado).
- **Clientes**: ABM completo con validación de CUIT (Módulo 11) y archivado lógico.
- **Catálogo**: búsqueda y filtro por categoría, detalle con descripción segmentada renderizada con colores (`TagParser`) y campos técnicos.
- **Ubicaciones**: CRUD.
- **Reportes**: facturación por mes, conversión y top 5 de productos (últimos 6 meses).
- **Ajustes**: conexión a la base (SecureStorage), API key de IA, tema claro/oscuro.

**Fuera de alcance (viven en el desktop):** generación Word/PDF, plantillas, explorador de `.docx`, rutas OneDrive, drafts locales, Velopack, Outlook, imágenes del catálogo (rutas locales).

## Requisitos de build

```powershell
# Workload (una sola vez)
dotnet workload install maui-android

# Android SDK + JDK (una sola vez; los instala el propio SDK de .NET)
dotnet build Alquitel.Mobile\Alquitel.Mobile.csproj -t:InstallAndroidDependencies -f net8.0-android `
  "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" `
  "-p:JavaSdkDirectory=$env:LOCALAPPDATA\Microsoft\jdk-17" `
  -p:AcceptAndroidSDKLicenses=true
```

## Compilar y publicar APK

```powershell
# Debug
dotnet build Alquitel.Mobile\Alquitel.Mobile.csproj `
  "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" `
  "-p:JavaSdkDirectory=$env:LOCALAPPDATA\Microsoft\jdk-17"

# APK de distribución interna (Release, firmado con debug keystore)
dotnet publish Alquitel.Mobile\Alquitel.Mobile.csproj -c Release -f net8.0-android `
  "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" `
  "-p:JavaSdkDirectory=$env:LOCALAPPDATA\Microsoft\jdk-17"
# APK resultante: Alquitel.Mobile\bin\Release\net8.0-android\publish\com.alquitel.mobile-Signed.apk
```

> El proyecto **no** está en `Alquitel.sln` a propósito: el CI compila la solución en `windows-latest` sin workload de MAUI. Compilalo siempre por csproj.

## Configuración (secretos)

Dos caminos, en orden de prioridad:

1. **En el dispositivo**: la primera vez, la pantalla de login pide la cadena de conexión del pooler de Supabase y la guarda cifrada (SecureStorage). La API key de Pollinations se carga en Ajustes.
2. **Embebida en el APK**: copiá `Resources/Raw/appsettings.mobile.example.json` como `appsettings.mobile.json` (gitignoreado) con los valores reales antes de publicar. Ideal para distribuir el APK ya configurado al equipo.

## Arquitectura

- `net8.0-android`, MVVM con `CommunityToolkit.Mvvm`, navegación con Shell (5 tabs + rutas de detalle).
- Referencia **solo** `Alquitel.Core` (Infrastructure es `net8.0-windows`).
- `MobileDbContext` replica el mapeo del desktop (índices, FKs `Restrict`, soft-delete con `HasQueryFilter`) pero **no corre migraciones**: el schema lo gobiernan las migraciones existentes.
- `MobileDbContextFactory` crea contextos por operación (regla de thread-safety del proyecto).
- Reutiliza de Core: `ProductMatcher`, `TagParser`, `CuitValidator`, `PasswordHasher`, `BudgetNumberHelper`, `IAiOrderParser`.
