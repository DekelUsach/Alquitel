<div align="center">
  <img src="https://img.shields.io/badge/Alquitel-Gestión%20y%20Operativa-003B57?style=for-the-badge" alt="Alquitel Logo">
  <h1>🏢 Alquitel - Gestión Corporativa y Documental</h1>
  
  <p>
    <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 8">
    <img src="https://img.shields.io/badge/WPF-Windows_Presentation_Foundation-blue?style=flat-square&logo=windows" alt="WPF">
    <img src="https://img.shields.io/badge/SQLite-003B57?style=flat-square&logo=sqlite&logoColor=white" alt="SQLite">
    <img src="https://img.shields.io/badge/Estado-Producción-success?style=flat-square" alt="Producción">
  </p>
  
  <p><i>Automatiza tus presupuestos, optimiza tu logística y olvídate del trabajo manual.</i></p>
</div>

<br>

Sistema integral de gestión interna diseñado específicamente para **Alquitel**, enfocado en la administración de clientes, control de pedidos y generación **100% automatizada** de documentación comercial y técnica, a través de una integración profunda y dinámica con Microsoft Word.

> [!NOTE]
> Esta versión (v2.5) introduce un motor algorítmico de parsing de texto, soporte completo de campos dinámicos y exportación PDF nativa, convirtiéndola en la actualización más importante del ecosistema.

---

## 📑 Tabla de Contenidos
1. [🎯 Casos de Uso y Valor de Negocio](#-casos-de-uso-y-valor-de-negocio)
2. [✨ Novedades y Evolución del Sistema (v2.5)](#-novedades-y-evolución-del-sistema-v25)
3. [🏗️ Arquitectura del Sistema](#️-arquitectura-del-sistema)
4. [📂 Estructura de Directorios](#-estructura-de-directorios)
5. [🚀 Requisitos de Ejecución e Instalación](#-requisitos-de-ejecución-e-instalación)
6. [📄 Funcionamiento del Motor de Documentos](#-funcionamiento-del-motor-de-documentos)
7. [🛠️ Stack Tecnológico Completo](#️-stack-tecnológico-completo)
8. [⚠️ Resolución de Problemas (Troubleshooting)](#️-resolución-de-problemas-troubleshooting)

---

## 🎯 Casos de Uso y Valor de Negocio

La plataforma Alquitel no es solo un gestor de bases de datos, es un **acelerador de flujos de trabajo**. Diseñado para ahorrarle horas al área comercial y al área técnica:

- **Cotizaciones en Segundos**: Copiando un correo de un cliente ("Necesito 3 pantallas y 2 notebooks por 3 días"), el **Buscador Inteligente** inserta los productos en el carrito de manera automática.
- **Doble Perfil de Documentos**: Con un solo clic se genera la cotización para el cliente (con precios de alquiler) y la **Orden de Trabajo (OT)** técnica (ocultando precios, mostrando especificaciones de cableado o logística).
- **Adiós a los Errores de Tipeo**: Al conectar la base de datos de SQLite directamente con el archivo `.docx` corporativo, los errores de importes y matemáticas en presupuestos desaparecen por completo.

---

## ✨ Novedades y Evolución del Sistema (v2.5)

El sistema ha sido evolucionado para ofrecer una experiencia premium y una robustez de grado empresarial. Las principales funcionalidades son:

### 1. 🧠 Búsqueda Inteligente (Smart Search Engine)
Un potente motor algorítmico (basado en _Coeficientes de Dice_ y _extracción de Trigramas_) capaz de analizar lenguaje natural y **detectar automáticamente los productos, cantidades y días**.

### 2. 🎛️ Arquitectura de Campos Dinámicos en JSON
Los productos ya no dependen de propiedades estáticas. Implementamos un sistema visual donde los usuarios configuran:
- **Descripción Segmentada**: Título del producto con fragmentos independientes (color, negrita, cursiva).
- **Campos Dinámicos**: Propiedades técnicas ilimitadas con formato independiente.

### 3. 📄 Motor de Generación Dinámica y Exportación PDF
Generación ultra-rápida vía **STA Threads** y exportación nativa a **PDF** de alta fidelidad utilizando `ExportAsFixedFormat` de Word COM.

### 4. 👥 Gestión de Clientes y Ubicaciones
Módulo ABM (Alta, Baja, Modificación) completo para la administración de la base instalada.
- **Validación de CUIT**: Implementación del algoritmo Checksum de AFIP (Módulo 11) para garantizar datos válidos.
- **Directorio de Ubicaciones**: Repositorio centralizado para agilizar la logística de eventos.

### 5. 💾 Resiliencia con Autosave
Sistema de **Autosave** cada 30 segundos que persiste borradores en JSON bajo `%AppData%\Alquitel\Drafts`, previniendo la pérdida de información ante cierres inesperados.

### 6. 🔍 Búsqueda Integrada en el Editor
Filtrado reactivo en tiempo real para el catálogo de productos, optimizando la edición de inventarios extensos.

### 7. 🌙 Interfaz Premium UX
Diseño institucional con soporte de temas (Dark/Light Mode), animaciones fluidas y validaciones reactivas instantáneas.

### 8. ⚡ Rendimiento de Grado Senior
- **Async Loading**: Carga asíncrona de datos para una navegación fluida.
- **Debounce de I/O**: Optimización del sistema de archivos para evitar recargas redundantes.
- **Polly Resiliency**: Manejo avanzado de errores I/O y bloqueos de archivos COM.

---

## 🏗️ Arquitectura del Sistema

La solución aplica el patrón **MVVM** bajo los lineamientos de _Clean Architecture_.

```mermaid
graph TD
    UI["🖥️ Alquitel.UI (WPF / C#)"] --> Core["📦 Alquitel.Core (Domain)"]
    Infra["⚙️ Alquitel.Infrastructure"] --> Core
    UI --> Infra
    
    subgraph Capa de Infraestructura
        DB[(SQLite)] <--> EF["Entity Framework 8"]
        Word["Word Document Service"] <--> COM["Word.Application COM"]
        Polly["Polly Resiliency"] --> Word
    end
    
    EF --> |JSON & Meta-Data| Core
    COM -.-> |Genera Archivos| Docs["Presupuesto_Final.docx/pdf"]
```

---

## 📂 Estructura de Directorios

> [!TIP]
> Los datos de configuración y bases de datos se almacenan de forma segura en `%LocalAppData%\Alquitel` para soporte multi-usuario.

```text
Alquitel/
├── Alquitel.Core/           # Capa de Dominio (Entidades y Lógica Core)
├── Alquitel.Infrastructure/ # Servicios, Persistencia y Word Interop
├── Alquitel.UI/             # Interfaz WPF, ViewModels y Temas
└── [OutputFolders]/         # Rutas configurables para documentos generados
```

---

## 🚀 Requisitos de Ejecución e Instalación

> [!IMPORTANT]  
> Es obligatorio contar con Microsoft Office (versión de escritorio) instalado para la generación de documentos.

1. **.NET 8 Runtime** instalado.
2. **Microsoft Word**: Versión clásica de escritorio (para acceso al componente COM).
3. **Fuentes**: Se recomienda la instalación de la fuente **Montserrat** para fidelidad visual.

---

## 📄 Funcionamiento del Motor de Documentos

El `WordDocumentService` utiliza una arquitectura desacoplada para inyectar datos en plantillas `.docx`.

### Etiquetas Soportadas
- `[CLIENTE]`, `{{CLIENTE}}`
- `[CUIT]`, `{{CUIT}}`
- `[NUMERO]`, `{{NUMERO}}`
- `{{PRODUCTOS_AQUI}}` (Tag principal de renderizado dinámico)

### Renderizado de Productos
1. **Layout Dinámico**: Inserción de imágenes flotantes y textos Montserrat con estilos de color condicionales.
2. **Tablas Resumen**: Generación de tablas 1×4 con cálculos automáticos de subtotales y totales.

---

## 🔧 Roadmap de Próximas Implementaciones

Secciones técnicas pendientes de ejecución para completar la evolución del sistema:

### Fase 6 — Observabilidad y Resiliencia (En Progreso)
- **6.1 Serilog Integration**: Implementación de logging estructurado en archivo y consola para diagnóstico de errores COM silenciosos.
- **6.2 Global Exception Handler**: Captura de excepciones no controladas a nivel de AppDomain y Dispatcher para evitar cierres abruptos.
- **6.3 Auto-Backup Database**: Job en segundo plano para respaldar `Alquitel.db` en `%AppData%\Alquitel\backups` cada 6 horas.

### Fase 7 — Seguridad y DevOps (Pendiente)
- **7.1 Path Validation**: Sanitización de rutas en `Process.Start` para prevenir Path Traversal en la apertura de documentos.
- **7.2 Secrets Management**: Migración de cadenas de conexión y configuraciones sensibles a `UserSecrets` de .NET.
- **7.3 Auto-Update Engine**: Implementación de Velopack para actualizaciones automáticas "over-the-air" sin intervención manual del usuario.

---

## 🛠️ Stack Tecnológico Completo

| Capa | Tecnología |
| :--- | :--- |
| **UI Framework** | WPF / XAML (.NET 8) |
| **Logic/Binding** | CommunityToolkit.Mvvm |
| **Database** | EF Core / SQLite |
| **Interop** | Microsoft.Office.Interop.Word |
| **Resilience** | Polly |
| **Logging** | Serilog |

---

## ⚠️ Resolución de Problemas (Troubleshooting)

> [!WARNING]
> **Error de archivo en uso**: Si el documento de destino está abierto, Polly realizará 5 reintentos automáticos. Cierra el archivo para permitir que el motor complete la operación.

> [!CAUTION]
> **Word no responde**: Asegúrate de que Word no esté ejecutándose como Administrador si la aplicación no lo hace, ya que los niveles de integridad COM pueden bloquear la comunicación.

---
*© 2026 Alquitel - Gestión Innovadora para Arquitectura de Eventos.*
