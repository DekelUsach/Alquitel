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

## 🚀 Requisitos de Ejecución
1. **.NET 8 SDK** instalado.
2. **Microsoft Word** instalado localmente (requerido para la generación de documentos `.docx`).
3. **Estructura de Directorios**: Los archivos `template.docx` deben residir en la raíz o carpetas correspondientes dentro de `C:\Alquitel`.

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

## ⚠️ Notas de Integración
Si el programa no genera documentos:
1. Desplegar la **Consola de Seguimiento** en la parte inferior de la ventana principal.
2. Verificar que las plantillas no estén abiertas en Word para evitar bloqueos de archivo.
3. El sistema buscará automáticamente etiquetas como `[CLIENTE]`, `{{CLIENTE}}` o marcadores de Word en tus plantillas.

---
*© 2026 Alquitel - Sistema de Gestión de Activos.*
