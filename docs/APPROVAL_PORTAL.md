# Portal de aprobación por link (§4 de PENDING_FEATURES)

El cliente final aprueba o rechaza un presupuesto desde el navegador, sin llamar ni
responder mails. Queda registro auditable (fecha, hora, IP) y el estado de la orden
se actualiza solo en la base compartida.

## Flujo

1. El vendedor genera el presupuesto como siempre (el documento se persiste).
2. Botón **"Link de aprobación"** en el armador → crea una fila en `OrderApprovals`
   (token uuid secreto) y copia al portapapeles la URL pública:
   `https://qgtaugmxmoxtpxvmugvt.supabase.co/functions/v1/aprobar?token=<uuid>`
3. El vendedor pega el link en el mail al cliente (el borrador de Outlook del botón
   "Enviar por mail" es el lugar natural).
4. El cliente abre el link → página con el presupuesto **completo**: cliente
   (empresa, CUIT, contacto), evento (fechas y lugar), comentarios, ítems con
   descripción estilada (BBCode renderizado), campos técnicos, medidas y notas,
   y desglose de totales (descuentos, IVA si corresponde, total final). Tiene
   modo claro/oscuro automático y confirmación en dos pasos en los botones
   **Aprobar / Rechazar**. Nunca expone datos internos (`InternalNotes`,
   `Cost`, `SpecialDiscountPercent`). Al responder:
   - `OrderApprovals.Status` pasa a Approved/Rejected con `RespondedAt` y `ClientIp`.
   - `Orders.Status` pasa a `Approved` (1) o `Rejected` (5).
5. La app de escritorio ve el estado nuevo al recargar presupuestos (base compartida).

## Componentes

| Pieza | Archivo |
|---|---|
| Entidad + índices | `Alquitel.Core/Entities/OrderApproval.cs`, `AlquitelDbContext` |
| Servicio de links | `Alquitel.Infrastructure/Services/EfApprovalLinkService.cs` |
| Comando UI | `BudgetBuilderViewModel.CopyApprovalLinkCommand` |
| Página pública | `supabase/functions/aprobar/index.ts` (Edge Function) |
| Esquema servidor | `supabase/migrations/20260713_order_approvals_and_rowversion.sql` |

## Deploy (una vez, máquina del Admin)

```bash
# 1. Esquema: correr el SQL en el SQL Editor del proyecto (o supabase db push)
#    supabase/migrations/20260713_order_approvals_and_rowversion.sql

# 2. Edge Function (requiere Supabase CLI logueada en el proyecto):
supabase functions deploy aprobar --project-ref qgtaugmxmoxtpxvmugvt --no-verify-jwt
```

Re-correr el deploy de la función cada vez que cambie `index.ts` (los links ya
emitidos siguen funcionando: el token vive en la base, no en la función).

`--no-verify-jwt` es necesario: el cliente final no tiene sesión de Supabase; la
autorización es el token uuid del link (secreto por presupuesto, un solo uso).

## Seguridad

Endurecido el 2026-08-29. Detalle en `docs/THREAT_MODEL.md` (T4, T5, T7, T9) y
en los encabezados de las migraciones.

- **El token no se guarda.** La base conserva solo su SHA-256
  (`20260829000700`); un trigger hashea y descarta el texto plano, así que ni un
  cliente viejo puede persistirlo. Consecuencia buscada: un token no se puede
  recuperar de la base, así que reenviar el presupuesto emite un link nuevo y
  revoca el anterior.
- **Un solo uso, sin carreras.** Consumir el token, cambiar el estado de la orden
  y registrar en la bitácora ocurren en una transacción, con el número de filas
  afectadas verificado (`20260829000800`). La versión anterior tenía una carrera
  real: un UPDATE que no matchea ninguna fila no devuelve error en PostgREST, así
  que dos pedidos simultáneos podían dejar la aprobación en "Aprobado" y la orden
  en "Rechazado", mostrándole al cliente un cartel de éxito en los dos casos.
- **Idempotente.** Repetir la misma acción devuelve el mismo comprobante sin
  volver a escribir. La acción contraria devuelve 409 y respeta el primer
  veredicto.
- **Sin service role key.** La Edge Function corre con la clave pública; toda la
  autorización está en dos RPC `SECURITY DEFINER`. El secreto administrativo
  salió del entorno de una función expuesta a internet.
- **Vencimiento y revocación** validados en la base, no en el navegador: 30 días
  para responder, y un link queda revocado en cuanto se emite otro para la misma
  orden.
- **Límite de intentos**: 20 respuestas por IP y 10 por token cada 10 minutos.
- **Retención** (`20260829000900`): detalle completo mientras está pendiente y 90
  días después de responder como comprobante; luego solo el sello; a los 180 días
  se anonimiza la IP a su /24.
- **Nunca se exponen** `InternalNotes`, `SpecialDiscountPercent`, `Products.Cost`,
  `AdminName` ni `CreatedByUserId`. La lista de columnas públicas vive en el RPC
  --o sea en el esquema, revisable en code review--, no en TypeScript.
- **El token no se loguea** en ningún lado, ni aparece en mensajes de error. Los
  errores del portal son genéricos: nada del backend llega al navegador.
- **Cabeceras**: `Referrer-Policy: no-referrer` (más un `<meta>` de respaldo),
  `Cache-Control: no-store, private`, `X-Robots-Tag: noindex`, CSP restrictiva,
  `nosniff` y `X-Frame-Options: DENY`.
- Todo dato de la base se escapa antes de interpolar en el HTML, y los colores de
  estilos dinámicos se validan contra `/^#[0-9a-f]{6}$/i`.

**Riesgo residual aceptado**: el token viaja en la query string, así que queda en
el historial del navegador del cliente y en su casilla. Cambiarlo a un fragmento
o a un POST rompería los links ya emitidos; el vencimiento, la revocación y las
cabeceras acotan el daño.
