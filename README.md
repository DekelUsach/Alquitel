## Alquitel - Sistema Administrativo Local

Este repositorio contiene el código fuente del sistema de gestión para Alquitel. La aplicación automatiza la generación de presupuestos, órdenes de trabajo y órdenes de facturación.

### Arquitectura y Limitaciones

El sistema opera exclusivamente mediante el sistema de archivos local. Modifica documentos alojados en directorios sincronizados por OneDrive. Esta decisión técnica impone restricciones de concurrencia. El software aplica rutinas de reintento para mitigar bloqueos de archivos causados por el cliente de sincronización externo.

### Tecnologías Implementadas

* .NET 8 con WPF para la interfaz de usuario nativa en Windows 10.
* C# para la lógica de negocio y manipulación de rutas locales.
* SQLite para el almacenamiento persistente de clientes y listas de precios.
* DocumentFormat.OpenXml para la inserción de datos en plantillas de Word.
* Microsoft.Office.Interop.Word para la exportación de documentos a formato PDF.

### Flujo de Trabajo Automatizado

1. El usuario ingresa la información del cliente y los productos en la interfaz.
2. El sistema lee las plantillas base en las carpetas locales.
3. El motor de OpenXml interpola los datos en los documentos Word.
4. El responsable aprueba los montos.
5. El sistema convierte el presupuesto a PDF.
6. El programa genera las órdenes de trabajo y facturación correspondientes.
7. El software deposita cada archivo en su directorio local específico.

### Requisitos de Ejecución

* Windows 10.
* Cliente de sincronización de Microsoft OneDrive activo.
* Microsoft Word instalado localmente.
* Permisos de lectura y escritura en los directorios objetivo.
