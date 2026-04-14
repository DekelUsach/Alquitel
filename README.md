# 🏢 Alquitel - Gestión Corporativa y Documental

Sistema de gestión interna para **Alquitel**, diseñado para la administración de clientes, pedidos y generación automatizada de documentación técnica y comercial mediante integración con Microsoft Word.

---

## 🎨 Características Principales
- **Interfaz Moderna y Minimalista**: Diseño institucional en Azul Alquitel con bordes redondeados y experiencia de usuario fluida.
- **Generación de Documentos (Motor Late-Bound)**: 
  - Generación dinámica de **Presupuestos**, **Órdenes de Facturación (OF)** y **Órdenes de Trabajo (OT)**.
  - Compatible con cualquier versión de Microsoft Word instalada.
  - Soporta marcadores (`Bookmarks`) y reemplazo de texto global (`Find & Replace`).
- **Consola de Seguimiento (Debug)**: Panel integrado para monitoreo de procesos y detección de errores en tiempo real.
- **Base de Datos Local**: Persistencia robusta mediante SQLite y Entity Framework Core 8.
- **Auto-Guardado Inteligente**: Integración con OneDrive para almacenamiento automático en carpetas estructurales (`1_PRESUPUESTOS`, `2_OF`, `3_OT`).

---

## 🚀 Requisitos de Ejecución e Instalación
1. **.NET 8 SDK** instalado.
2. **Microsoft Word** instalado localmente (Versión de escritorio obligatoria; requiere registro del *ProgID* `Word.Application`). El sistema utiliza automatización COM nativa, por lo que licencias de solo lectura o versiones web no son compatibles.
3. **Estructura de Directorios Crítica**: El sistema tiene rutas absolutas preconfiguradas. **DEBE existir** la carpeta `C:\Alquitel` en el disco local y, dentro de ella, la subcarpeta `1_PRESUPUESTOS`.
4. **Plantilla Base**: El archivo `template.docx` debe residir exactamente en `C:\Alquitel\1_PRESUPUESTOS\template.docx` para que la generación de presupuestos funcione.

---

## 🛠️ Stack Tecnológico
- **Frontend**: WPF (Windows Presentation Foundation) con C# 12.
- **Backend**: .NET 8.0 Windows.
- **Arquitectura**: Clean Architecture / MVVM.
- **Servicios**: COM Interop (Dynamic Late-Binding) para automatización de Word.
- **Librerías**:
  - `CommunityToolkit.Mvvm` (MVVM)
  - `Microsoft.EntityFrameworkCore` (ORM)
  - `Polly` (Resiliencia ante bloqueos de archivos en OneDrive)

---

## 📦 Estructura del Proyecto
```text
Alquitel/
├── Alquitel.Core/           # Entidades e Interfaces de negocio
├── Alquitel.Infrastructure/ # Persistencia y Servicios Externos (Word)
├── Alquitel.UI/             # Interfaz de usuario (WPF) e Instrucciones UI
├── 1_PRESUPUESTOS/          # Salida de presupuestos generados
├── 2_OF/                    # Salida de Órdenes de Facturación
└── 3_OT/                    # Salida de Órdenes de Trabajo
```

---

## 📝 Instrucciones de Instalación
```bash
# Clonar repositorio
git clone <url-repo>

# Restaurar dependencias
dotnet restore

# Compilar y Ejecutar
dotnet run --project Alquitel.UI\Alquitel.UI.csproj
```

---

## 📄 Funcionamiento del Sistema de Documentos

El motor de generación de Alquitel automatiza la creación de archivos `.docx` inyectando datos directamente desde la interfaz de usuario en tus plantillas de Word.

### ¿Cómo funciona la inyección de datos?
A diferencia de otros sistemas que manipulan el archivo XML, Alquitel abre una instancia invisible de Word y realiza una búsqueda y reemplazo inteligente. Soporta tres métodos simultáneos:
1.  **Etiquetas Literales**: Busca textos como `[CLIENTE]`, `{{CUIT}}`, `[NUMERO]`, `[LUGAR]` o `(fecha)`.
2.  **Marcadores (Bookmarks)**: Si tu plantilla tiene marcadores de Word (e.g., `BK_CLIENT_NAME`), el sistema escribirá directamente sobre ellos.
3.  **Tablas Dinámicas**: Si existe un marcador llamado `BK_EQUIPMENT_TABLE`, el sistema generará automáticamente una tabla con todos los productos seleccionados, cantidades y subtotales.

### ¿Por qué a un colega podría no funcionarle?
Si a ti te funciona pero a otra persona no, revisen estos tres puntos en su PC:
*   **Falta la carpeta o la plantilla**: El programa busca la plantilla en `C:\Alquitel\1_PRESUPUESTOS\template.docx`. Si el colega no creó esa carpeta manualmente o no puso el archivo allí con ese nombre exacto, el sistema fallará.
*   **Word no es "Desktop"**: Si tiene una versión de prueba o una versión de Windows Store que no registra correctamente el componente `Word.Application` en el registro de Windows, el código no podrá "llamar" a Word de manera externa.
*   **Confusión de Etiquetas**: Asegúrate de que las etiquetas en el Word coincidan con lo que el código busca (`[...]`, `{{...}}`). Puedes usar la herramienta `ReadTemplate` para listar qué etiquetas están en el archivo.

---

## ⚠️ Notas de Integración
Si el programa no genera documentos:
1. Desplegar la **Consola de Seguimiento** en la parte inferior de la ventana principal.
2. Verificar que las plantillas no estén abiertas en Word para evitar bloqueos de archivo.
3. El sistema buscará automáticamente etiquetas como `[CLIENTE]`, `{{CLIENTE}}` o marcadores de Word en tus plantillas.

---
*© 2026 Alquitel - Sistema de Gestión de Activos.*
