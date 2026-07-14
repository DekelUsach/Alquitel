// Edge Function "aprobar" — portal público de aprobación de presupuestos.
//
// GET  /aprobar?token=<uuid>  → página HTML con el presupuesto y botones Aprobar/Rechazar.
// POST /aprobar?token=<uuid>  → body {"action":"approve"|"reject"}; registra la respuesta
//                                y actualiza el estado de la orden.
//
// Seguridad: el token uuid ES la autorización (link secreto por presupuesto). La función
// corre con la service role key (secret del proyecto, nunca viaja al navegador) y toca
// solo la fila de OrderApprovals del token + el Status de su orden.
//
// Deploy (una vez, desde la máquina del Admin):
//   supabase functions deploy aprobar --project-ref qgtaugmxmoxtpxvmugvt --no-verify-jwt
// (--no-verify-jwt: el cliente final no tiene JWT de Supabase; el token propio autoriza.)

import { createClient } from "jsr:@supabase/supabase-js@2";

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

// Valores del enum OrderStatus de la app de escritorio (Alquitel.Core/Entities/Order.cs)
const ORDER_STATUS_APPROVED = 1;
const ORDER_STATUS_REJECTED = 5;
// Valores del enum ApprovalStatus (OrderApproval.cs)
const APPROVAL_PENDING = 0;
const APPROVAL_APPROVED = 1;
const APPROVAL_REJECTED = 2;

const UUID_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// BudgetNumber viene de la base (lo tipean los vendedores): se escapa antes de
// interpolarlo en el HTML para cerrar cualquier vector de XSS almacenado.
function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!)
  );
}

function html(body: string, status = 200): Response {
  const content = `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">
<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="es">
<head>
  <title>Grupo Alquitel — Aprobación de presupuesto</title>
  <style type="text/css">
    body {
      font-family: 'Segoe UI Variable Text', 'Segoe UI', system-ui, -apple-system, sans-serif;
      background: #F3F6F9;
      margin: 0;
      padding: 0;
      display: flex;
      justify-content: center;
      align-items: center;
      min-height: 100vh;
      color: #0D1117;
    }
    .container {
      width: 100%;
      max-width: 480px;
      padding: 24px;
      box-sizing: border-box;
    }
    .card {
      background: #FFFFFF;
      border-radius: 16px;
      border: 1px solid #E2E8F0;
      box-shadow: 0 10px 25px -5px rgba(0,0,0,0.05), 0 8px 10px -6px rgba(0,0,0,0.05);
      padding: 36px;
      box-sizing: border-box;
      text-align: center;
    }
    .header-logo {
      display: flex;
      flex-direction: column;
      align-items: center;
      margin-bottom: 28px;
    }
    .logo-text {
      font-size: 13px;
      font-weight: 800;
      color: #1B2E58;
      letter-spacing: 0.15em;
      margin-top: 10px;
      text-transform: uppercase;
    }
    .content-box {
      background: #F8FAFC;
      border-radius: 12px;
      border: 1px solid #E2E8F0;
      padding: 20px 24px;
      margin: 24px 0;
    }
    h1 {
      font-size: 22px;
      color: #1B2E58;
      margin: 0 0 10px;
      font-weight: 700;
      letter-spacing: -0.02em;
    }
    p {
      color: #6B7280;
      line-height: 1.5;
      font-size: 14px;
      margin: 0;
    }
    .num-label {
      font-size: 11px;
      font-weight: 700;
      color: #0D84E7;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      margin-bottom: 4px;
    }
    .num {
      font-size: 28px;
      font-weight: 800;
      color: #0D1117;
      margin: 0;
    }
    .row {
      display: flex;
      gap: 12px;
      margin-top: 24px;
    }
    button {
      flex: 1;
      padding: 14px 0;
      border: 0;
      border-radius: 10px;
      font-size: 14px;
      font-weight: 600;
      cursor: pointer;
      transition: all 0.2s ease;
      box-shadow: 0 1px 2px rgba(0,0,0,0.05);
    }
    button:hover {
      filter: brightness(0.92);
      transform: translateY(-1px);
    }
    button:active {
      transform: translateY(1px);
      filter: brightness(0.85);
    }
    button:disabled {
      opacity: 0.4;
      pointer-events: none;
    }
    .ok {
      background: #27AE60;
      color: #FFFFFF;
    }
    .no {
      background: #D46A6A;
      color: #FFFFFF;
    }
    .muted {
      color: #6B7280;
      font-size: 12px;
      margin-top: 24px;
      line-height: 1.4;
    }
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 8px 16px;
      border-radius: 20px;
      font-weight: 700;
      font-size: 13px;
      margin-top: 16px;
    }
    .badge.ok {
      background: #E4F5EC;
      color: #27AE60;
    }
    .badge.no {
      background: #FDECEC;
      color: #D46A6A;
    }
  </style>
</head>
<body>
  <div class="container">
    <div class="card">
      <div class="header-logo">
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
          <defs>
            <linearGradient id="brand-grad" x1="0%" y1="0%" x2="100%" y2="100%">
              <stop offset="0%" stop-color="#0D84E7" />
              <stop offset="100%" stop-color="#1B2E58" />
            </linearGradient>
          </defs>
          <path fill="url(#brand-grad)" d="M20,8.5 L31,31.5 H26.4 L20,17.6 L13.6,31.5 H9 Z M16.4,25.4 H23.6 L25.1,28.4 H14.9 Z" />
        </svg>
        <span class="logo-text">Grupo Alquitel</span>
      </div>
      ${body}
    </div>
  </div>
  <script type="text/javascript">
    // <![CDATA[
    async function send(action){
      document.querySelectorAll('button').forEach(b => b.disabled = true);
      await fetch(location.href, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ action })
      });
      location.reload();
    }
    const btnApprove = document.getElementById('btn-approve');
    if (btnApprove) btnApprove.onclick = () => send('approve');
    const btnReject = document.getElementById('btn-reject');
    if (btnReject) btnReject.onclick = () => send('reject');
    // ]]>
  </script>
</body>
</html>`;

  return new Response(content, {
    status,
    headers: {
      "Content-Type": "text/HTML; charset=utf-8",
    },
  });
}

Deno.serve(async (req) => {
  const url = new URL(req.url);
  const token = url.searchParams.get("token") ?? "";
  if (!UUID_RE.test(token)) {
    return html(`<h1>Link inválido</h1><p>El link de aprobación no es válido o está incompleto.</p>`, 400);
  }

  const { data: approval, error } = await supabase
    .from("OrderApprovals")
    .select("Id, OrderId, Status")
    .eq("Token", token)
    .maybeSingle();

  if (error || !approval) {
    return html(`<h1>Link no encontrado</h1><p>Este link de aprobación no existe o fue dado de baja.</p>`, 404);
  }

  const { data: order } = await supabase
    .from("Orders")
    .select("BudgetNumber, Status")
    .eq("Id", approval.OrderId)
    .maybeSingle();

  const budget = escapeHtml(order?.BudgetNumber ?? "—");

  if (req.method === "GET") {
    if (approval.Status === APPROVAL_APPROVED) {
      return html(`<h1>Aprobación Confirmada</h1>
        <div class="content-box">
          <div class="num-label">Presupuesto</div>
          <div class="num">${budget}</div>
        </div>
        <span class="badge ok">✔ Ya aprobado</span>
        <p class="muted">Este presupuesto ya fue aprobado. ¡Muchas gracias!</p>`);
    }
    if (approval.Status === APPROVAL_REJECTED) {
      return html(`<h1>Presupuesto Rechazado</h1>
        <div class="content-box">
          <div class="num-label">Presupuesto</div>
          <div class="num">${budget}</div>
        </div>
        <span class="badge no">✘ Rechazado</span>
        <p class="muted">Este presupuesto fue rechazado. Si cambió de opinión, contáctenos.</p>`);
    }
    return html(`<h1>Decisión del Presupuesto</h1>
      <p>Le acercamos el presupuesto solicitado. Por favor indíquenos su decisión:</p>
      <div class="content-box">
        <div class="num-label">Presupuesto</div>
        <div class="num">${budget}</div>
      </div>
      <div class="row">
        <button id="btn-approve" class="ok">Aprobar</button>
        <button id="btn-reject" class="no">Rechazar</button>
      </div>
      <p class="muted">Al hacer clic queda registrada su respuesta con fecha y hora.</p>`);
  }

  if (req.method === "POST") {
    if (approval.Status !== APPROVAL_PENDING) {
      return html(`<h1>Acción Duplicada</h1>
        <div class="content-box">
          <div class="num-label">Presupuesto</div>
          <div class="num">${budget}</div>
        </div>
        <p class="muted">Este link de aprobación ya fue utilizado previamente.</p>`, 409);
    }

    let action = "";
    try { action = (await req.json()).action; } catch { /* body inválido */ }
    if (action !== "approve" && action !== "reject") {
      return html(`<h1>Solicitud inválida</h1>`, 400);
    }

    const approved = action === "approve";
    const clientIp = req.headers.get("x-forwarded-for")?.split(",")[0]?.trim() ?? null;

    const { error: upErr } = await supabase
      .from("OrderApprovals")
      .update({
        Status: approved ? APPROVAL_APPROVED : APPROVAL_REJECTED,
        RespondedAt: new Date().toISOString(),
        ClientIp: clientIp,
      })
      .eq("Id", approval.Id)
      .eq("Status", APPROVAL_PENDING); // idempotencia ante doble clic

    if (!upErr) {
      await supabase
        .from("Orders")
        .update({ Status: approved ? ORDER_STATUS_APPROVED : ORDER_STATUS_REJECTED })
        .eq("Id", approval.OrderId);
    }

    return html(`<h1>¡Muchas Gracias!</h1>
      <div class="content-box">
        <div class="num-label">Presupuesto</div>
        <div class="num">${budget}</div>
      </div>
      <span class="badge ${approved ? "ok" : "no"}">${approved ? "✔ Aprobado" : "✘ Rechazado"}</span>
      <p class="muted">Su respuesta quedó registrada con éxito.</p>`);
  }

  return html(`<h1>Método no soportado</h1>`, 405);
});
