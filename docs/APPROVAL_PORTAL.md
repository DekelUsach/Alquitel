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
4. El cliente abre el link → página con el número de presupuesto y botones
   **Aprobar / Rechazar**. Al responder:
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

`--no-verify-jwt` es necesario: el cliente final no tiene sesión de Supabase; la
autorización es el token uuid del link (secreto por presupuesto, un solo uso).

## Seguridad

- El token es un uuid aleatorio: adivinar uno equivale a adivinar 122 bits.
- El link se invalida al responder (idempotencia ante doble clic vía
  `UPDATE ... WHERE Status = Pending`).
- La Edge Function usa la service role key **del lado servidor** (secret del
  proyecto); al navegador solo viaja HTML.
- `BudgetNumber` se escapa antes de interpolar en el HTML (XSS almacenado).
- `OrderApprovals` tiene RLS deny-all para los roles de PostgREST.
