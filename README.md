# Alquitel - Sistema de Gestión Administrativa local

Este software automatiza el flujo de trabajo de Alquitel, desde la recepción de pedidos hasta la generación de órdenes de producción y facturación, operando directamente sobre el sistema de archivos local sincronizado con OneDrive.

## Flujo de Trabajo Soportado

1.  [cite_start]**Presupuestación**: Carga de datos de clientes (Nombre, CUIT, Fecha) y productos mediante formularios[cite: 2, 75, 99].
2.  **Validación**: Intervención del jefe de área para corrección de precios antes del cierre del documento.
3.  [cite_start]**Generación de Documentos**: Creación automática de Presupuestos (PDF), Órdenes de Facturación (OF) y Órdenes de Trabajo (OT)[cite: 71, 95].
4.  **Sincronización**: Movimiento de archivos entre las carpetas locales `PRESUPUESTO`, `OF` y `OT`.

## Stack Tecnológico

* **Framework**: .NET 8 con WPF (Windows Presentation Foundation) para una interfaz nativa y robusta en Windows 10.
* **Lenguaje**: C# 12.
* **Base de Datos**: SQLite (Local). Almacena datos recurrentes como:
    * [cite_start]Clientes y Contactos (ej: B + T, Eugenia, Sheila Gomez)[cite: 2, 75, 70].
    * [cite_start]Listado de Equipamiento (Pantallas LED P2.9, TV 43", Notebooks, etc.)[cite: 6, 85, 39].
    * [cite_start]Logística y Técnicos (Traslados, Operadores)[cite: 45, 48].
* **Manipulación de Documentos**:
    * [cite_start]**Microsoft Office Interop Word**: Necesario para mantener la fidelidad de las plantillas que contienen tablas complejas, imágenes y formatos específicos de Alquitel[cite: 6, 18, 29].
    * [cite_start]**DocumentFormat.OpenXml**: Para la inserción rápida de texto en campos de datos simples (Nro de Presupuesto, Fechas, CUIT)[cite: 2, 74, 100].
* **Gestión de Archivos**: `System.IO` con lógica de reintentos (Retry Logic) para manejar bloqueos de archivos durante la sincronización activa de OneDrive.

## Estructura de Automatización de Datos

El sistema elimina la carga manual duplicada extrayendo información del presupuesto aprobado para poblar las órdenes:

| Dato Automatizado | Origen (Presupuesto) | Destino (OF / OT) |
| :--- | :--- | :--- |
| [cite_start]Cliente / Empresa | [cite: 75] [cite_start]| [cite: 99] |
| [cite_start]Número de Presupuesto | [cite: 74] [cite_start]| [cite: 98] |
| [cite_start]Detalle de Equipamiento | [cite: 5, 85] [cite_start]| [cite: 108, 109] |
| [cite_start]Lugar y Fecha del Evento | [cite: 78, 81] [cite_start]| [cite: 103, 106] |

## Requisitos de Instalación

1.  **SO**: Windows 10 o superior.
2.  **Software**: Microsoft Office (Word) instalado localmente.
3.  **Configuración**: Cliente de OneDrive iniciado y carpetas `PRESUPUESTO`, `OT`, `OF` mapeadas correctamente en el explorador de archivos.
4.  **Runtime**: .NET Desktop Runtime 8.0.

## Notas de Implementación Local

Debido a la naturaleza del manejo de archivos local en carpetas de nube, el sistema implementa un `FileSystemWatcher` para detectar cuándo un presupuesto ha sido modificado y sincronizado, permitiendo la transición de estados en el flujo administrativo sin intervención manual del usuario.
