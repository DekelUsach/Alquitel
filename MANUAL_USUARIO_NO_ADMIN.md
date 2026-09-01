# Manual de uso de Alquitel

## Guía para Vendedores y Armadores

**Versión:** 1.0  
**Alcance:** este manual explica las funciones disponibles para los perfiles no administrativos. No incluye Configuración, Reportes, gestión de usuarios, plantillas, sincronización, copias de seguridad ni actualizaciones.

---

## 1. Antes de empezar

Alquitel se usa para registrar clientes y lugares, preparar pedidos de equipamiento, emitir documentos y seguir el estado de cada trabajo. El programa guarda información de forma automática mientras trabajás; aun así, conviene comprobar los avisos que aparecen antes de generar un documento.

### Los perfiles de usuario

| Perfil | Para qué se usa |
|---|---|
| **Vendedor** | Gestiona el circuito comercial: clientes, ubicaciones, catálogo, pedidos, presupuestos, historial y seguimiento. |
| **Armador** | Consulta el catálogo y trabaja principalmente con las órdenes de trabajo (OT) que debe preparar. |

El menú puede mostrar menos opciones según el perfil. Si una opción no aparece, no es un error: probablemente no corresponde a tu rol.

### Iniciar sesión

1. Abrí Alquitel.
2. Elegí tu nombre en el campo **Usuario**.
3. Si aparece el campo **Contraseña**, escribila.
4. Presioná **Entrar** o la tecla `Enter`.

Si la contraseña no es correcta, el programa lo informa debajo del campo. Pedí ayuda a un administrador si no recordás la contraseña o tu nombre no aparece.

### Leer la barra inferior

En el borde inferior se muestra tu nombre, tu rol y la versión del programa. También indica dónde se están guardando los datos:

- **Base de datos local (SQLite):** trabajás con los datos de este equipo.
- **Servidor compartido (Supabase):** los datos se comparten entre equipos.
- **Sin conexión al servidor - reintentando:** seguí trabajando con cuidado; el programa volverá a intentar conectarse. Si al generar un documento no puede guardar la orden, avisará qué ocurrió.

---

## 2. Moverse por el programa

Usá el menú de la izquierda para abrir cada sección. También podés usar estos atajos:

| Acción | Atajo |
|---|---|
| Panel de control | `Ctrl+1` |
| Crear presupuesto / pedido | `Ctrl+2` |
| Productos | `Ctrl+3` |
| Clientes | `Ctrl+4` |
| Ubicaciones | `Ctrl+5` |
| Presupuestos | `Ctrl+6` |
| Configuración (solo admin; no se trata aquí) | `Ctrl+7` |
| Reportes (solo admin; no se trata aquí) | `Ctrl+8` |
| Seguimiento | `Ctrl+9` |
| Cambiar tema claro/oscuro | `Ctrl+T` |
| Paleta de comandos | `Ctrl+K` |

### Paleta de comandos (`Ctrl+K`)

Es una forma rápida de llegar a una pantalla o encontrar algo sin recorrer el menú.

1. Presioná `Ctrl+K`.
2. Escribí el nombre de una pantalla, cliente, producto o número de presupuesto.
3. Usá las flechas `Arriba` y `Abajo` para elegir un resultado.
4. Presioná `Enter` para abrirlo. También podés hacer doble clic.
5. Presioná `Esc` para cerrar la paleta.

Con dos o más letras, la búsqueda también ofrece hasta cinco clientes, cinco productos y cinco presupuestos coincidentes. Elegir un presupuesto lo abre para editarlo. La paleta se cierra si hacés clic fuera de ella.

### Tema y avisos

- Usá el botón de tema, o `Ctrl+T`, para alternar entre modo claro y oscuro. El programa recuerda tu elección.
- Los avisos emergentes confirman acciones o alertan sobre problemas. Si un aviso ofrece una acción, por ejemplo **Abrir carpeta**, podés usarla inmediatamente. El botón `X` solo descarta el aviso.

### Cerrar sesión

1. Elegí **Cerrar sesión** al pie del menú.
2. Confirmá la pregunta.

El programa vuelve a abrir la ventana de inicio de sesión para que ingrese otra persona. Cerrá sesión si dejás el puesto de trabajo.

---

## 3. Panel de control (Vendedor)

El **Panel de control** es la pantalla de inicio del vendedor. Resume la actividad y da accesos rápidos.

### Qué muestra

- Monto presupuestado y cantidad de pedidos de los últimos 30 días.
- Totales acumulados de presupuestos, clientes y productos.
- Actividad reciente: número, cliente, fecha, estado y total de los últimos pedidos.
- Productos más presupuestados.

### Acciones disponibles

- **Actualizar:** recalcula las métricas y refresca las listas.
- **Nuevo presupuesto:** abre un pedido vacío.
- **Ver historial:** abre Presupuestos.
- En un pedido reciente, usá el botón de abrir para editarlo o el de repetir para iniciar una copia.
- Los accesos rápidos abren Productos, Clientes o el historial de presupuestos.

Si todavía no hay presupuestos o datos suficientes, el panel muestra un mensaje en lugar de una lista. No requiere ninguna corrección.

---

## 4. Productos

La sección **Productos** es el catálogo de equipos y servicios. Es la fuente de los ítems que se agregan a un pedido.

### Buscar, actualizar y exportar

- Escribí en **Buscar por descripción o categoría** para filtrar al instante.
- Usá el botón de recarga para volver a leer el catálogo.
- Usá el botón de exportación para crear un archivo CSV, que puede abrirse con Excel.
- Hacé clic sobre un producto para abrir su ficha.

### Crear un producto

1. Elegí **Nuevo producto**.
2. Completá como mínimo la **Categoría**, el **Precio base** y la descripción.
3. Agregá los datos opcionales que correspondan.
4. Elegí **Guardar producto**.

Si no hay resultados, el mensaje **Catálogo vacío** también ofrece esta misma orientación.

### Editar un producto

Abrí el producto y usá el **Taller de producto**. Los cambios no quedan guardados hasta presionar **Guardar producto**.

#### Información general

- **Categoría:** grupo del equipo, por ejemplo “Video”, “Audio” o “Informática”. Sirve para ordenar y buscar.
- **Precio base:** precio unitario que se propone al agregar el producto a un pedido. En el pedido puede modificarse sin cambiar esta ficha.
- **Stock total:** cantidad física disponible. Dejalo vacío si se trata de un servicio o no querés controlar disponibilidad. Los botones `-` y `+` ajustan la cantidad de a una unidad.
- **Costo interno x día:** dato interno usado para cálculos administrativos; nunca aparece en los documentos que recibe el cliente.
- **Disponibilidad de stock:** muestra compromisos por día y las órdenes que ocupan unidades. Sirve para anticipar faltantes.

#### Descripción que verá el cliente

La descripción se arma con segmentos. Cada segmento puede tener texto, color, negrita y cursiva. Juntos forman el título que se inserta en el documento.

1. Escribí el texto de cada segmento.
2. Elegí color y, si corresponde, los botones de **N** (negrita) e **I** (cursiva).
3. Usá **Añadir segmento** para sumar otra parte y el icono de papelera para quitar una parte.

#### Imagen opcional

Elegí **Seleccionar...** para cargar una imagen. Se usará como miniatura junto al producto en los documentos. Usá la papelera para quitarla; no borra el archivo original de tu computadora.

#### Campos personalizados

Sirven para especificaciones técnicas, por ejemplo `Resolución: 1920x1080`.

1. Elegí **Añadir campo**.
2. Escribí una **Etiqueta** y un **Valor**.
3. Si hace falta, aplicá negrita, subrayado o color.
4. Usá la papelera para eliminar un campo.

#### Vista previa y acciones de la ficha

- **Vista Word:** muestra u oculta una simulación de cómo se verá el producto en el documento.
- Hacé clic sobre la vista previa para verla más grande; usá **Cerrar** para volver.
- **Duplicar:** crea una copia editable. Es útil cuando un nuevo producto se parece a uno existente.
- **Eliminar:** pide confirmación y retira el producto del catálogo. Los presupuestos ya generados conservan su información histórica.
- **Catálogo:** vuelve a la lista sin guardar los cambios de esta edición.

---

## 5. Clientes (Vendedor)

La sección **Clientes** es el directorio de empresas y contactos. Registrar bien la ficha evita volver a escribir datos en cada presupuesto.

### Buscar, actualizar y exportar

- Buscá por empresa, CUIT o contacto.
- Usá el botón de recarga para actualizar el directorio.
- Usá el botón de exportación para generar un CSV compatible con Excel.
- Hacé clic sobre una ficha para editarla.

### Crear o editar una ficha

1. Elegí **Nuevo cliente**, o abrí un cliente existente.
2. Completá **Empresa / Cliente** y **CUIT**. El sistema valida el CUIT con la verificación de AFIP.
3. Completá, si están disponibles, **Contacto**, **Teléfono** y **Email**.
4. Si existe un precio especial habitual, escribí el porcentaje en **Descuento acordado %**. Dejalo vacío si no corresponde.
5. Escribí las **Notas internas** que necesite el equipo, como condiciones de pago o antecedentes. Estas notas no se imprimen en ningún documento.
6. Presioná **Guardar cliente**.

El descuento acordado se propone automáticamente al seleccionar al cliente en un pedido, pero puede modificarse para ese presupuesto puntual.

### CUIT inválido

Mientras escribís no se muestra error. Cuando se completan 11 dígitos, aparece una marca de validación o un aviso de que no supera la verificación de AFIP. Revisá el número antes de guardar.

### Resumen del historial del cliente

Al abrir una ficha puede aparecer **Resumen con IA externa**. Este botón prepara un resumen de alquileres, frecuencia y montos a partir del historial. Revisalo antes de usarlo como base de una decisión: es una ayuda, no reemplaza la lectura de las órdenes.

### Eliminar o cancelar

- **Eliminar** solicita confirmación y archiva la ficha. Los documentos históricos no se alteran.
- **Cancelar** o la `X` cierra la ficha sin guardar los cambios de esa edición.

---

## 6. Ubicaciones (Vendedor)

En **Ubicaciones** se mantiene el padrón de lugares de evento. Usar siempre el mismo nombre para un lugar mejora las búsquedas y el historial.

### Buscar y ordenar

- Escribí en el buscador para localizar un lugar. También reconoce variantes de nombres.
- Elegí **A-Z**, **Más usadas** o **Próximo evento** para cambiar el orden.
- Usá **Limpiar filtros** o `Esc` para volver a ver todo.
- El botón de recarga vuelve a leer el padrón.

### Estado del padrón

El programa marca de manera automática lugares duplicados, sin nombre o que conviene revisar. Usá el botón de revisión para ver solo esos casos. Si dice **El padrón está limpio**, no hay nada pendiente.

### Crear o renombrar un lugar

1. Elegí **Nueva ubicación**, o el lápiz sobre una fila existente.
2. Escribí el nombre claro y completo, por ejemplo `Predio de La Rural`.
3. Leé el mensaje debajo del campo: avisa si el nombre está vacío, es demasiado parecido a otro o coincide con uno existente.
4. Elegí **Guardar lugar**.

Al renombrar un lugar se actualiza la información de la base, pero no se reescriben los documentos Word que ya fueron generados. Por eso el historial puede mostrar el nombre anterior en archivos antiguos.

### Ficha de ubicación

Al abrir un lugar se ve:

- Cantidad de presupuestos y clientes asociados.
- Próximo evento y rango de actividad.
- Equipamiento más solicitado allí.
- Últimos presupuestos vinculados.

Usá **Ver sus presupuestos** para abrir Seguimiento filtrado por ese lugar.

### Fusionar lugares duplicados

Usalo cuando dos registros son el mismo lugar escrito de formas distintas.

1. Abrí la ficha del lugar que querés retirar.
2. En **Fusionar este lugar en otro**, elegí el lugar correcto de destino.
3. Leé el aviso: las órdenes pasarán al lugar elegido y el registro actual se elimina.
4. Presioná **Fusionar** y confirmá.

Esta acción no se deshace. No modifica los `.docx` ya generados.

### Eliminar un lugar

Usá la papelera de la fila o **Eliminar** en la ficha. El programa pide confirmación y las órdenes asociadas pasan a figurar como **(Sin ubicación)**. Preferí fusionar cuando se trate de un duplicado.

---

## 7. Crear un pedido y un presupuesto (Vendedor)

Abrí **Crear presupuesto** o elegí **Nuevo presupuesto** desde el panel. Esta es la pantalla principal para armar un pedido, calcular importes y generar documentos.

### Cómo se organiza la pantalla

- A la izquierda está el **Catálogo** para buscar y agregar equipamiento.
- En el centro está el **Pedido** con sus cantidades, días, medida, precios y total.
- A la derecha está el panel de datos, cliente, evento, comentarios y generación. Podés plegarlo o desplegarlo con el botón de flecha.

El estado de autoguardado indica que el borrador se guarda cada 30 segundos. Si cerrás o cambiás de pantalla, al volver se puede recuperar ese trabajo pendiente.

### Agregar productos manualmente

1. Escribí en **Buscar equipamiento**.
2. Presioná el botón `+` del resultado para agregar una unidad.
3. Usá `-` para quitar una unidad o el botón de papelera en el pedido para retirar ese ítem completo.

Atajo útil: presioná `Enter` en el buscador para agregar el primer resultado. También podés escribir un prefijo de cantidad, por ejemplo `3*proyector`, y presionar `Enter` para agregar tres unidades del primer resultado.

### Armar el pedido desde un texto del cliente

Esta opción sirve para pegar un mail o un WhatsApp, por ejemplo: “Necesito 2 pantallas y una notebook por 3 días”.

1. Elegí el botón de la varita junto al buscador.
2. Pegá el texto en el cuadro.
3. Presioná **Armar pedido desde texto**.
4. Revisá cuidadosamente el resultado: productos, cantidades y días.

El programa puede usar asistencia externa de IA si está disponible; si no, usa el buscador interno. Podés elegir **Cancelar análisis** mientras está procesando. Nunca generes un documento sin revisar lo interpretado.

### Editar cada línea del pedido

En cada producto del pedido podés cambiar:

- **Cantidad:** con `-` y `+`.
- **Días:** cantidad de días de alquiler para ese artículo.
- **Medida:** requerimiento particular, por ejemplo `8 x 3`.
- **Precio unitario:** precio específico de este pedido. Cambiarlo no modifica el precio del catálogo.

El campo **Días para todo el pedido** aplica la misma duración a todos los ítems. Usalo antes de personalizar excepciones por artículo.

El total de cada línea se calcula como cantidad × días × precio unitario. El total general se actualiza automáticamente.

### Stock insuficiente

Un símbolo de advertencia en una línea indica que la cantidad podría superar el stock comprometido para la fecha del evento. No impide continuar, pero antes de generar se pedirá confirmación. Revisá las órdenes activas o consultá al equipo antes de aceptar el riesgo.

### Deshacer y vaciar

- **Deshacer** revierte el último cambio de productos. También funciona con `Ctrl+Z`.
- **Vaciar pedido** quita todos los productos después de pedir confirmación. No borra productos del catálogo ni clientes.

### Combos de evento

Un combo guarda una selección habitual de productos, cantidades y días para reutilizarla.

**Guardar un combo**

1. Armá el pedido base.
2. Elegí **Guardar pedido como combo**.
3. Escribí un nombre reconocible cuando el programa lo pida.

**Usar un combo**

1. Elegí el combo en la lista.
2. Presioná el botón de carga (triángulo).
3. Revisá el pedido: al cargarlo se utilizan los precios vigentes del catálogo.

**Eliminar un combo**

Elegí el combo y usá la papelera. Esto no modifica presupuestos ni documentos que ya existen.

### Datos del documento

- **N° Presupuesto:** número identificador del pedido. El sistema suele proponerlo. Si otro usuario lo utilizó primero, se renumera y avisa antes de guardar.
- **Nueva versión:** crea una copia de la misma serie, por ejemplo `31294` pasa a `31294/2`. La versión anterior queda intacta. Usala para una revisión formal del mismo presupuesto.
- **Estado:** refleja la situación actual del pedido. Los estados son **Borrador**, **Aprobado**, **Enviado a OF**, **Enviado a OT**, **Rechazado** y **Archivado**.

### Cliente

Elegí un cliente registrado en el buscador para completar sus datos. También podés escribir un CUIT: si es válido y existe, la ficha se completa automáticamente.

Al seleccionar una ficha registrada, el programa puede mostrar pedidos recientes, una marca de **Cliente frecuente** (tres o más pedidos en los últimos 12 meses) y notas internas. Esa información no sale en los documentos.

Si el cliente tiene un descuento acordado y el presupuesto no tiene descuento todavía, se propone automáticamente. Revisalo siempre antes de generar.

### Evento y comentarios

1. Elegí la **Fecha de inicio**. La fecha de fin es opcional para eventos de un solo día.
2. Si indicás una fecha de fin, no puede ser anterior a la fecha de inicio.
3. Escribí o elegí el **Lugar del evento**.
4. Revisá el campo **Administrador**, que identifica a quien arma el documento.
5. Escribí los **Comentarios** necesarios. Se incorporan en la Orden de Trabajo si la plantilla tiene esa sección.

### Vista comercial, descuentos e IVA

Elegí **Presupuesto** para ver el cálculo comercial:

- **Subtotal:** suma de las líneas.
- **DESC. %:** descuento porcentual general.
- **DESC. $:** descuento fijo adicional. Entre ambos nunca pueden bajar el total por debajo de cero.
- **Discriminar IVA (21%):** activalo si los precios son netos y el documento debe sumar IVA. Si queda desactivado, se consideran precios finales.
- **Total:** importe final que se imprime.

Elegí **Orden de Trabajo** para priorizar la información técnica y de armado. Esta vista oculta los importes comerciales, no cambia el contenido del pedido.

### Sugerir notas técnicas

El botón de sugerencias técnicas puede preparar una nota breve para la OT mediante asistencia externa. Leé y corregí el resultado antes de generar; no reemplaza las indicaciones operativas del responsable del evento.

### Avisos antes de generar

El programa puede avisar, sin bloquear, que falta el lugar, falta el CUIT, la fecha ya pasó o hay posibles conflictos de stock. Corregí lo que corresponda. Los avisos no son meramente decorativos: evitan rehacer documentos.

### Generar documentos

Con el pedido revisado, elegí el documento que necesitás:

- **Generar Presupuesto:** crea el documento comercial.
- **Generar O. Facturación:** crea la orden de facturación.
- **Generar O. Trabajo:** crea el documento técnico para el armado.

Al generar, el sistema valida que haya datos mínimos, controla el stock y guarda la orden. Si exportar a PDF está habilitado por la empresa, se guarda también el PDF. El aviso de éxito permite abrir la carpeta del archivo.

Si aparece un conflicto de edición porque otra persona cambió el mismo presupuesto, elegí una de estas opciones con cuidado:

- **Recargar:** trae la versión más reciente y descarta la vista anterior.
- **Sobrescribir:** conserva tus cambios y reemplaza conscientemente la versión vigente.
- **No continuar:** deja tus cambios locales sin guardar para revisarlos.

Si el documento se generó pero la conexión falló, el programa lo informa. No cierres la pantalla si avisa que no pudo respaldar la orden localmente; intentá de nuevo cuando vuelva la conexión.

### Enviar por correo y enlace de aprobación

Después de generar un documento:

- **Enviar por mail** abre un borrador de Outlook con el último documento adjunto y, si existe, también el PDF. No envía el correo automáticamente: revisá destinatarios y texto antes de enviar desde Outlook.
- **Link de aprobación** copia un enlace público para que el cliente apruebe o rechace el presupuesto desde su navegador. Al responder, el estado se actualiza automáticamente. Enviá el enlace solo al contacto correcto.

---

## 8. Presupuestos: archivos generados (Vendedor)

La pantalla **Presupuestos** muestra los documentos guardados en la carpeta de presupuestos.

### Buscar y filtrar

- Usá **Recargar** para releer la carpeta.
- Escribí para buscar por número, empresa, ubicación, creador o nombre de archivo.
- Usá los campos **Desde** y **Hasta** para limitar por fecha de modificación.
- Elegí **Limpiar filtros** para volver a ver todo.
- El campo **Carpeta** indica dónde se está buscando. **Elegir...** permite seleccionar otra carpeta para esta consulta.

La tabla muestra número, cliente, ubicación, creador, archivo, fecha de modificación y tamaño.

### Abrir el detalle y trabajar con un archivo

Hacé clic en una fila. A la derecha se abre su detalle con la ruta completa, fecha, tamaño y acciones:

- **Abrir archivo:** abre el documento con la aplicación predeterminada, normalmente Word.
- **Crear nueva versión:** crea una rama, por ejemplo `31294/2`, con un editor rápido de ítems. El original no se modifica.
- **Repetir pedido:** abre una nueva copia con cliente y productos equivalentes, pero número y fechas nuevos y precios actuales del catálogo. Usalo para una nueva contratación, no para corregir el documento original.
- **Mostrar en el Explorador:** abre la carpeta de Windows y selecciona el archivo.
- **Eliminar archivo:** borra el documento de la carpeta después de confirmar. Esta acción no elimina necesariamente el registro histórico de la orden, por lo que debe usarse con cuidado.

Usá la `X` del panel de detalle para cerrarlo sin hacer cambios.

---

## 9. Seguimiento de órdenes (Vendedor)

**Seguimiento** es la vista de todas las órdenes registradas, incluso si el archivo está en otra carpeta. Es el lugar recomendado para controlar el estado de cada trabajo.

### Encontrar una orden

1. Escribí número, cliente, lugar o creador en el buscador.
2. Elegí un estado en el panel **Estados**, por ejemplo Borrador, Aprobado o Enviado a OT.
3. Usá **Limpiar filtros** para volver a **Todos**.
4. Usá recargar si necesitás incorporar cambios realizados por otra persona.

Cada fila muestra fecha, número, cliente, evento, ubicación, vigencia, creador, total y estado. La etiqueta de vigencia se calcula según la fecha de emisión.

### Cambiar el estado

Elegí el nuevo estado en la fila. El cambio se guarda inmediatamente. Confirmá que seleccionaste la orden correcta antes de cambiarlo.

### Acciones de cada orden

- **Abrir:** abre el pedido en el armador para editarlo.
- **Repetir:** crea una nueva copia con precios vigentes y fechas nuevas.
- **Historial:** muestra quién generó, editó o cambió el estado, y cuándo.

---

## 10. Órdenes de trabajo (Armador)

La pantalla **Órdenes de trabajo** está pensada para el personal que prepara los equipos. Lee directamente los documentos de la carpeta de OT.

### Consultar una OT

1. Usá el buscador para localizarla por nombre de archivo.
2. Elegí la OT en la lista de la izquierda.
3. Leé la vista previa de texto a la derecha.
4. Usá **Abrir en Word** para ver el documento original con su formato completo.

La vista previa es solo de lectura. Si no hay archivos o ninguno coincide con la búsqueda, aparecerá un mensaje indicándolo.

### Actualizar y eliminar

- Usá el botón de recarga para releer la carpeta de OT, por ejemplo después de que un vendedor genere una nueva orden.
- **Eliminar** borra el documento seleccionado de la carpeta de OT después de confirmar. Usalo solo cuando la empresa haya definido que el archivo ya no es necesario; no hay recuperación desde esta pantalla.

---

## 11. Casos de uso habituales

### Caso A: presupuestar un pedido recibido por WhatsApp

1. Abrí **Crear presupuesto**.
2. Abrí la varita de pedido automático y pegá el mensaje.
3. Revisá producto por producto, cantidades, días, medidas y precios.
4. Elegí o cargá el cliente; verificá CUIT y descuento.
5. Cargá fecha, lugar y comentarios.
6. Revisá los avisos de stock y datos faltantes.
7. Elegí **Generar Presupuesto**.
8. Si corresponde, abrí el borrador de Outlook con **Enviar por mail** o copiá el **Link de aprobación**.

### Caso B: el cliente pide cambios a un presupuesto ya enviado

1. Buscá el presupuesto en **Presupuestos** o **Seguimiento**.
2. Elegí **Crear nueva versión**.
3. Cambiá los productos, cantidades, días, precios o datos necesarios.
4. Generá el nuevo documento.

La versión anterior queda preservada. No uses “Repetir pedido” para una corrección: repetir crea una contratación nueva con precios y fechas actuales.

### Caso C: mismo evento o paquete frecuente

1. Armá una vez el conjunto de equipos.
2. Guardalo como **combo** con un nombre claro.
3. En un pedido futuro, aplicá el combo y revisá los precios actuales, fechas y disponibilidad.

### Caso D: un cliente frecuente llama otra vez

1. Buscá al cliente desde **Clientes** o desde el campo de cliente del pedido.
2. Revisá las notas internas y pedidos recientes.
3. Usá **Repetir pedido** desde el historial si el nuevo trabajo se parece al anterior.
4. Cambiá fechas, ubicación, productos y precios antes de generar.

### Caso E: hay dos nombres para el mismo lugar

1. Abrí **Ubicaciones** y buscá ambos nombres.
2. Elegí el registro que querés retirar.
3. Abrí su ficha, comprobá actividad y fusionálo con el nombre correcto.
4. Confirmá solo después de verificar el destino: no se deshace.

### Caso F: preparar una OT en depósito

1. Iniciá sesión como Armador.
2. Abrí **Órdenes de trabajo**.
3. Actualizá la carpeta si no aparece la orden esperada.
4. Buscala, revisá la vista previa y abrila en Word para leerla completa.
5. Usá los comentarios y medidas de la OT como referencia de armado.

---

## 12. Buenas prácticas y resolución rápida de problemas

### Antes de generar

- Comprobá cliente, CUIT, fechas y lugar.
- Confirmá cantidades, días, medidas y precios.
- Leé cualquier advertencia de stock.
- Verificá si el precio incluye IVA o si debés activar **Discriminar IVA (21%)**.
- Para una revisión de una oferta ya enviada, creá una versión en vez de editar el documento Word a mano.

### Si no encontrás algo

- Probá `Ctrl+K` y escribí su nombre o número.
- Quitá filtros con **Limpiar filtros**.
- Usá el botón de recarga de la pantalla.
- Verificá que hayas iniciado sesión con el perfil adecuado.

### Si el programa dice que hay un conflicto de stock

No significa necesariamente que no se pueda alquilar. Indica que otras órdenes activas podrían usar las mismas unidades en la fecha elegida. Confirmá disponibilidad con el responsable antes de generar.

### Si aparece “fecha de fin anterior a la de inicio”

Corregí las fechas. Un evento de un día solo necesita fecha de inicio; podés dejar vacía la fecha de fin.

### Si un CUIT no valida

Revisá los 11 dígitos. El programa ignora puntos y guiones al validar, pero el número debe superar el control de AFIP.

### Si un documento se generó pero no aparece en Seguimiento

Revisá el mensaje mostrado por el programa. Puede haber un problema temporal de conexión. Recargá Seguimiento cuando el servidor vuelva a estar disponible. Si el aviso indica que no hubo respaldo local, no cierres el pedido y consultá a un administrador.

### Si Outlook no se abre

La función de correo prepara un borrador en Outlook de Windows. Verificá que Outlook esté instalado y configurado en ese equipo. El documento ya generado sigue disponible en su carpeta.

### Si necesitás una función que no aparece

No intentes modificar archivos, carpetas de plantillas o configuraciones del sistema. Informalo a un administrador: esas tareas se gestionan por separado para proteger los documentos y los datos compartidos.

---

## 13. Resumen: qué función usar

| Necesidad | Dónde hacerlo |
|---|---|
| Registrar una empresa o contacto | Clientes |
| Registrar o corregir un lugar de evento | Ubicaciones |
| Crear o actualizar equipamiento | Productos |
| Armar una propuesta | Crear presupuesto |
| Copiar un pedido habitual | Crear presupuesto, Combos; o Repetir pedido |
| Cambiar formalmente una propuesta ya emitida | Crear nueva versión |
| Generar presupuesto, OF u OT | Crear presupuesto |
| Enviar el último documento por Outlook | Crear presupuesto, Enviar por mail |
| Pedir aprobación al cliente | Crear presupuesto, Link de aprobación |
| Abrir o eliminar un archivo de presupuesto | Presupuestos |
| Controlar estados e historial de acciones | Seguimiento |
| Preparar equipos con una orden técnica | Órdenes de trabajo |

Fin del manual.
