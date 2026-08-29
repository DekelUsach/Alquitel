// Edge Function "aprobar" — portal público de aprobación de presupuestos.
//
// GET  /aprobar?token=<uuid>  → página HTML con el presupuesto y botones Aprobar/Rechazar.
// POST /aprobar?token=<uuid>  → body {"action":"approve"|"reject"}.
//
// ─────────────────────────────────────────────────────────────────────────────
// SEGURIDAD — qué cambió respecto de la versión anterior
//
// 1. Ya NO usa la service role key. Esta función corre con la clave pública
//    (anon) y toda la lógica vive en dos RPC SECURITY DEFINER de la base:
//    public.get_approval_page() y public.respond_approval(). El secreto
//    administrativo del proyecto deja de estar en el entorno de la función.
//
// 2. Ya NO decide nada acá. Antes esta función leía y escribía tablas sueltas
//    con PostgREST y decidía en TypeScript si el token valía. La consecuencia
//    fue una carrera real: un UPDATE condicional que no matchea ninguna fila NO
//    devuelve error en PostgREST — devuelve 0 filas — y el código igual entraba
//    a la rama de éxito y pisaba el estado de la orden con el veredicto
//    contrario. Ahora el consumo del token, el cambio de estado de la orden y el
//    registro en la bitácora ocurren en UNA transacción, con el número de filas
//    afectadas verificado. Ver 20260829000800_approval_rpc_atomic.sql.
//
// 3. El token del link no se guarda en la base: se guarda su SHA-256. Acá se
//    manda en claro (es lo que el cliente tiene) y la base lo hashea para
//    buscarlo. Nunca se loguea, nunca se interpola en el HTML y nunca se incluye
//    en un mensaje de error.
//
// 4. Los errores no exponen nada interno: el RPC devuelve un código de resultado
//    acotado y acá se traduce a una página. Ningún SQLERRM, ningún nombre de
//    tabla, ningún connection string llega al navegador.
//
// 5. La página no expone datos internos: la selección de columnas está en el
//    RPC (parte del esquema, revisable en code review), no en este archivo.
//    InternalNotes, SpecialDiscountPercent, Cost, AdminName y CreatedByUserId
//    no salen nunca.
//
// Deploy (una vez, desde la máquina del Admin):
//   supabase functions deploy aprobar --project-ref <ref> --no-verify-jwt
// (--no-verify-jwt: el cliente final no tiene sesión de Supabase; el token del
// link es la autorización.)
//
// Variables de entorno necesarias: SUPABASE_URL y SUPABASE_ANON_KEY.
// Las inyecta Supabase automáticamente. NO configurar SUPABASE_SERVICE_ROLE_KEY.
// ─────────────────────────────────────────────────────────────────────────────

import { createClient } from "jsr:@supabase/supabase-js@2";

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,
  // Clave pública. Las dos RPC que se invocan son SECURITY DEFINER y validan el
  // token, el vencimiento, la revocación y el límite de intentos del lado base.
  Deno.env.get("SUPABASE_ANON_KEY")!,
);

const APPROVAL_PENDING = 0;
const APPROVAL_APPROVED = 1;
const APPROVAL_REJECTED = 2;

const VAT_RATE = 0.21;

const UUID_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HEX_COLOR_RE = /^#[0-9a-f]{6}$/i;

// ─────────────────────────────── Helpers de texto ───────────────────────────────

function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!)
  );
}

interface TextSegment {
  text: string;
  color: string;
  bold: boolean;
  italic: boolean;
  underline: boolean;
}

// Port fiel de Alquitel.Core/Parsing/TagParser.cs: descompone el BBCode de las
// descripciones ([b], [i], [u], [red], [blue], ...) en segmentos con estilo.
function parseTags(text: string): TextSegment[] {
  const result: TextSegment[] = [];
  let color = "#000000";
  let bold = false, italic = false, underline = false;
  const stack: Array<[string, boolean, boolean, boolean]> = [];

  let i = 0;
  let buf = "";
  const flush = () => {
    if (buf.length === 0) return;
    result.push({ text: buf, color, bold, italic, underline });
    buf = "";
  };

  while (i < text.length) {
    if (text[i] === "[") {
      const close = text.indexOf("]", i + 1);
      if (close > i) {
        const tag = text.substring(i + 1, close).trim().toLowerCase();
        const isClose = tag.startsWith("/");
        const name = isClose ? tag.substring(1) : tag;
        const newColor = ({
          red: "#FF0000",
          green: "#006600",
          darkred: "#C00000",
          blue: "#1F68C7",
          white: "#FFFFFF",
          black: "#000000",
        } as Record<string, string>)[name] ?? null;
        const isStyle = name === "b" || name === "i" || name === "u";

        if (newColor !== null || isStyle) {
          flush();
          if (!isClose) {
            stack.push([color, bold, italic, underline]);
            if (newColor !== null) color = newColor;
            if (name === "b") bold = true;
            if (name === "i") italic = true;
            if (name === "u") underline = true;
          } else if (stack.length > 0) {
            [color, bold, italic, underline] = stack.pop()!;
          }
          i = close + 1;
          continue;
        }
      }
    }
    buf += text[i];
    i++;
  }
  flush();
  return result;
}

// BBCode → HTML seguro. Negro y blanco se tratan como "color del tema" (la página
// tiene modo claro y oscuro; un negro fijo sería ilegible en oscuro).
function bbToHtml(text: string | null | undefined): string {
  if (!text) return "";
  return parseTags(text)
    .map((s) => {
      const t = escapeHtml(s.text).replace(/\r?\n/g, "<br/>");
      const styles: string[] = [];
      const c = s.color.toUpperCase();
      if (HEX_COLOR_RE.test(s.color) && c !== "#000000" && c !== "#FFFFFF") {
        styles.push(`color:${s.color}`);
      }
      if (s.bold) styles.push("font-weight:700");
      if (s.italic) styles.push("font-style:italic");
      if (s.underline) styles.push("text-decoration:underline");
      return styles.length ? `<span style="${styles.join(";")}">${t}</span>` : t;
    })
    .join("");
}

// ─────────────────────────────── Formateadores ───────────────────────────────

const moneyFmt = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 0,
  maximumFractionDigits: 2,
});
const fmtMoney = (n: number) => moneyFmt.format(n);

const MONTHS_ES = [
  "enero", "febrero", "marzo", "abril", "mayo", "junio",
  "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
];
// Formatea la parte de fecha del timestamp SIN pasar por Date: los timestamps de la
// base son naive (hora local de la app de escritorio) y convertirlos a la zona del
// runtime de la Edge Function correría el día (ej. 14T00:00 → "13 de agosto").
function fmtDate(iso: string | null | undefined): string | null {
  const m = iso?.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (!m) return null;
  const month = MONTHS_ES[parseInt(m[2], 10) - 1];
  return month ? `${parseInt(m[3], 10)} de ${month} de ${m[1]}` : null;
}

function eventRange(start: string | null, end: string | null): string | null {
  const s = fmtDate(start);
  if (!s) return null;
  const e = fmtDate(end);
  return e && e !== s ? `Del ${s} al ${e}` : s;
}

// ─────────────────────── Forma del payload que devuelve el RPC ───────────────────────
// Espejo de public.get_approval_page(). La lista de campos vive en la base; acá
// solo se tipa lo que llega.

interface PageItem {
  quantity: number;
  unit_price: number;
  dias: number;
  technical_notes: string | null;
  custom_fields_json: string | null;
  requested_measure: string | null;
  description: string;
}

interface PagePayload {
  outcome: string;
  detail_visible?: boolean;
  approval_status?: number;
  responded_at?: string | null;
  budget_number?: string;
  created_date?: string | null;
  event_date?: string | null;
  event_end_date?: string | null;
  comments?: string | null;
  discount_percent?: number | null;
  discount_amount?: number | null;
  add_vat?: boolean | null;
  client?: {
    company_name: string | null;
    cuit: string | null;
    contact_name: string | null;
    email: string | null;
    phone: string | null;
  } | null;
  location?: string | null;
  items?: PageItem[];
  max_age_days?: number;
}

interface CustomField {
  Label?: string;
  Value?: string;
  IsBold?: boolean;
  IsUnderline?: boolean;
  ColorHex?: string;
}

// ─────────────────────────────── Render de secciones ───────────────────────────────

function renderCustomFields(json: string | null): string {
  if (!json) return "";
  let fields: CustomField[];
  try {
    fields = JSON.parse(json);
    if (!Array.isArray(fields)) return "";
  } catch {
    return "";
  }
  const rows = fields
    .filter((f) => (f.Label ?? "").trim() !== "" || (f.Value ?? "").trim() !== "")
    .map((f) => {
      const styles: string[] = [];
      if (f.IsBold) styles.push("font-weight:700");
      if (f.IsUnderline) styles.push("text-decoration:underline");
      const c = (f.ColorHex ?? "").toUpperCase();
      if (HEX_COLOR_RE.test(c) && c !== "#FFFFFF" && c !== "#000000") {
        styles.push(`color:${c}`);
      }
      const styleAttr = styles.length ? ` style="${styles.join(";")}"` : "";
      return `<div class="spec"><span class="spec-k">${escapeHtml(f.Label ?? "")}</span><span${styleAttr}>${escapeHtml(f.Value ?? "")}</span></div>`;
    });
  return rows.length ? `<div class="specs">${rows.join("")}</div>` : "";
}

// Fila de la tabla de ítems, estilo remito: descripción (con especificaciones y
// notas debajo) + columnas numéricas alineadas a la derecha.
function renderItemRow(item: PageItem): string {
  const desc = bbToHtml(item.description);
  const total = item.quantity * item.unit_price * item.dias;

  const extras: string[] = [];
  if (item.requested_measure?.trim()) {
    extras.push(`<div class="item-measure">${escapeHtml(item.requested_measure)}</div>`);
  }
  if (item.technical_notes?.trim()) {
    extras.push(`<div class="item-notes">${bbToHtml(item.technical_notes)}</div>`);
  }

  return `<tr>
    <td class="c-desc">
      <div class="item-name">${desc}</div>
      ${renderCustomFields(item.custom_fields_json)}
      ${extras.join("")}
    </td>
    <td class="c-num">${item.quantity}</td>
    <td class="c-num">${item.dias}</td>
    <td class="c-num col-unit">${fmtMoney(item.unit_price)}</td>
    <td class="c-num c-total">${fmtMoney(total)}</td>
  </tr>`;
}

function renderStamp(status: number | undefined, respondedAt: string | null | undefined): string {
  if (status === APPROVAL_APPROVED || status === APPROVAL_REJECTED) {
    const when = fmtDate(respondedAt);
    const ok = status === APPROVAL_APPROVED;
    return `<div class="stamp-row"><span class="stamp ${ok ? "ok" : "no"}">${
      ok ? "Aprobado" : "Rechazado"}${when ? ` · ${when}` : ""}</span></div>`;
  }
  return "";
}

function renderHeader(budget: string, created: string | null): string {
  return `<header class="letterhead">
      <div class="brand">
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="34" height="34" class="brand-mark" aria-hidden="true">
          <path fill="currentColor" d="M20,8.5 L31,31.5 H26.4 L20,17.6 L13.6,31.5 H9 Z M16.4,25.4 H23.6 L25.1,28.4 H14.9 Z"/>
        </svg>
        <span class="brand-name">Grupo Alquitel</span>
      </div>
      <div class="doc-id">
        <div class="k">Presupuesto</div>
        <div class="doc-num">N.º ${budget}</div>
        ${created ? `<div class="doc-date">Emitido el ${created}</div>` : ""}
      </div>
    </header>`;
}

function renderBudgetPage(d: PagePayload): string {
  const budget = escapeHtml(d.budget_number || "—");
  const created = fmtDate(d.created_date);
  const stamp = renderStamp(d.approval_status, d.responded_at);

  // Retención: pasado el plazo del comprobante, la página se reduce al sello.
  // El plazo lo decide la base (app.approval_detail_days), no este archivo.
  if (d.detail_visible === false) {
    return `${renderHeader(budget, created)}
      ${stamp}
      <p>Este presupuesto ya fue respondido. Por privacidad, el detalle completo
      dejó de mostrarse en esta página; sigue disponible en nuestros registros.
      Si necesitás una copia, respondé el correo por el que recibiste este link.</p>`;
  }

  const items = d.items ?? [];

  // Totales — misma aritmética que Order.cs (Total/DiscountValue/NetTotal/VatValue/GrandTotal)
  const subtotal = items.reduce((acc, i) => acc + i.quantity * i.unit_price * i.dias, 0);
  const pct = Math.min(Math.max(Number(d.discount_percent ?? 0), 0), 100);
  const rawDisc = subtotal * pct / 100 + Math.max(0, Number(d.discount_amount ?? 0));
  const discount = Math.min(rawDisc, subtotal);
  const net = subtotal - discount;
  const addVat = d.add_vat === true;
  const vat = addVat ? Math.round(net * VAT_RATE * 100) / 100 : 0;
  const grand = net + vat;

  const c = d.client;
  const clientLines = [
    `<div class="name">${escapeHtml(c?.company_name ?? "—")}</div>`,
    c?.cuit ? `<div class="line">CUIT ${escapeHtml(c.cuit)}</div>` : "",
    c?.contact_name ? `<div class="line">${escapeHtml(c.contact_name)}</div>` : "",
    c?.email ? `<div class="line">${escapeHtml(c.email)}</div>` : "",
    c?.phone ? `<div class="line">${escapeHtml(c.phone)}</div>` : "",
  ].join("");

  const range = eventRange(d.event_date ?? null, d.event_end_date ?? null);
  const eventLines = [
    range ? `<div class="name">${escapeHtml(range)}</div>` : "",
    d.location ? `<div class="line">${escapeHtml(d.location)}</div>` : "",
  ].join("");

  const meta = `<section class="meta">
    <div class="meta-block">
      <div class="k">Preparado para</div>
      ${clientLines}
    </div>
    ${eventLines.trim() ? `<div class="meta-block"><div class="k">Evento</div>${eventLines}</div>` : ""}
  </section>`;

  const comments = d.comments?.trim()
    ? `<section class="comments">
         <div class="k">Comentarios</div>
         <p>${bbToHtml(d.comments)}</p>
       </section>`
    : "";

  const itemRows = items.length
    ? items.map(renderItemRow).join("")
    : `<tr><td class="c-desc" colspan="5"><span class="empty">Este presupuesto no tiene ítems cargados.</span></td></tr>`;

  const totalsRows = [
    `<div class="t-row"><span>Subtotal</span><span class="v">${fmtMoney(subtotal)}</span></div>`,
    discount > 0
      ? `<div class="t-row"><span>Descuento${pct > 0 ? ` (${pct}%${Number(d.discount_amount ?? 0) > 0 ? " + fijo" : ""})` : ""}</span><span class="v">−${fmtMoney(discount)}</span></div>`
      : "",
    addVat
      ? `<div class="t-row"><span>Neto</span><span class="v">${fmtMoney(net)}</span></div>
         <div class="t-row"><span>IVA 21%</span><span class="v">${fmtMoney(vat)}</span></div>`
      : "",
    `<div class="t-row grand"><span>Total</span><span class="v">${fmtMoney(grand)}</span></div>`,
    !addVat ? `<div class="t-note">Precios finales, IVA no discriminado.</div>` : "",
  ].join("");

  const actions = d.approval_status === APPROVAL_PENDING
    ? `<section class="decision">
         <p class="decision-q">¿Confirma este presupuesto?</p>
         <div class="decision-btns">
           <button id="btn-approve" class="btn btn-approve">Aprobar presupuesto</button>
           <button id="btn-reject" class="btn btn-reject">Rechazar</button>
         </div>
         <p class="fine">Al confirmar, su respuesta queda registrada con fecha y hora.</p>
       </section>`
    : "";

  return `${renderHeader(budget, created)}
    ${stamp}
    ${meta}
    ${comments}
    <table class="items">
      <thead>
        <tr>
          <th>Detalle</th>
          <th>Cant.</th>
          <th>Días</th>
          <th class="col-unit">Unitario</th>
          <th>Importe</th>
        </tr>
      </thead>
      <tbody>${itemRows}</tbody>
    </table>
    <section class="totals">${totalsRows}</section>
    ${actions}`;
}

// ─────────────────────────────── Shell HTML ───────────────────────────────

// wide=true → página de presupuesto (trae su propio membrete).
// wide=false → páginas cortas (errores, respuesta de POST): se antepone la marca.
function html(body: string, status = 200, wide = false): Response {
  const miniBrand = wide ? "" : `<header class="letterhead compact">
      <div class="brand">
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="30" height="30" class="brand-mark" aria-hidden="true">
          <path fill="currentColor" d="M20,8.5 L31,31.5 H26.4 L20,17.6 L13.6,31.5 H9 Z M16.4,25.4 H23.6 L25.1,28.4 H14.9 Z"/>
        </svg>
        <span class="brand-name">Grupo Alquitel</span>
      </div>
    </header>`;

  const content = `<!DOCTYPE html>
<html lang="es">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <meta name="robots" content="noindex, nofollow"/>
  <!-- Refuerzo del header Referrer-Policy para el caso de que un intermediario
       lo quite: el token viaja en la query string y no debe salir en el Referer. -->
  <meta name="referrer" content="no-referrer"/>
  <title>Grupo Alquitel — Presupuesto</title>
  <style>
    /* Paleta corporativa Alquitel, derivada del azul de marca #1F68C7:
       neutros fríos con tinte azul + tinta azul-negra + acento de marca. */
    :root {
      --paper: #edf2f9;
      --sheet: #fdfeff;
      --ink: #16253c;
      --muted: #5b6b82;
      --rule: #d9e2ef;
      --brand: #1F68C7;
      --ok: #2e7d4f;
      --no: #a84848;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --paper: #0d1522;
        --sheet: #152030;
        --ink: #e2eaf5;
        --muted: #8ba0bc;
        --rule: #2b3b52;
        --brand: #6ea8e8;
        --ok: #63b285;
        --no: #d08c8c;
      }
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--paper);
      color: var(--ink);
      font: 14px/1.55 "Segoe UI", system-ui, -apple-system, sans-serif;
      -webkit-font-smoothing: antialiased;
    }
    .container { max-width: ${wide ? "740px" : "520px"}; margin: 0 auto; padding: 40px 16px 48px; }
    .sheet {
      background: var(--sheet);
      border: 1px solid var(--rule);
      border-radius: 3px;
      padding: 52px 56px;
      box-shadow: 0 1px 3px rgba(0,0,0,0.05);
    }
    @media (max-width: 600px) {
      .container { padding: 10px 8px 32px; }
      .sheet { padding: 28px 20px; }
    }

    /* ── Membrete ── */
    .letterhead {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 24px;
      padding-bottom: 20px;
      border-bottom: 2px solid var(--ink);
      margin-bottom: 26px;
    }
    .letterhead.compact { padding-bottom: 14px; margin-bottom: 22px; }
    .brand { display: flex; align-items: center; gap: 10px; }
    .brand-mark { color: var(--brand); flex-shrink: 0; }
    .brand-name {
      font-size: 13px;
      font-weight: 800;
      letter-spacing: 0.18em;
      text-transform: uppercase;
      color: var(--brand);
    }
    .doc-id { text-align: right; }
    .doc-num { font-family: Georgia, "Times New Roman", serif; font-size: 30px; line-height: 1.15; margin-top: 2px; }
    .doc-date { font-size: 12px; color: var(--muted); margin-top: 3px; }
    @media (max-width: 600px) {
      .letterhead { flex-direction: column; gap: 14px; }
      .doc-id { text-align: left; }
    }

    /* ── Etiquetas y tipografía ── */
    .k {
      font-size: 10.5px;
      font-weight: 700;
      letter-spacing: 0.15em;
      text-transform: uppercase;
      color: var(--muted);
      margin-bottom: 6px;
    }
    h1 { font-family: Georgia, "Times New Roman", serif; font-size: 24px; font-weight: 700; margin: 0 0 10px; }
    p { color: var(--muted); line-height: 1.55; margin: 0; }

    /* ── Sello de estado ── */
    .stamp-row { margin: 0 0 24px; }
    .stamp {
      display: inline-block;
      border: 2px solid currentColor;
      border-radius: 2px;
      padding: 5px 14px;
      font-size: 11.5px;
      font-weight: 800;
      letter-spacing: 0.14em;
      text-transform: uppercase;
      transform: rotate(-1.2deg);
    }
    .stamp.ok { color: var(--ok); }
    .stamp.no { color: var(--no); }

    /* ── Datos de cliente y evento ── */
    .meta { display: grid; grid-template-columns: 1fr 1fr; gap: 28px; margin-bottom: 26px; }
    @media (max-width: 600px) { .meta { grid-template-columns: 1fr; gap: 18px; } }
    .meta .name { font-size: 15px; font-weight: 700; }
    .meta .line { color: var(--muted); font-size: 13px; }

    .comments { margin: 0 0 26px; padding: 4px 0 4px 16px; border-left: 3px solid var(--rule); }
    .comments p { font-family: Georgia, "Times New Roman", serif; font-style: italic; font-size: 14.5px; color: var(--ink); }
    .comments .k { margin-bottom: 4px; }

    /* ── Tabla de ítems ── */
    table.items { width: 100%; border-collapse: collapse; }
    .items th {
      font-size: 10px;
      font-weight: 700;
      letter-spacing: 0.14em;
      text-transform: uppercase;
      color: var(--muted);
      text-align: right;
      padding: 0 0 8px 14px;
      border-bottom: 2px solid var(--ink);
      white-space: nowrap;
    }
    .items th:first-child { text-align: left; padding-left: 0; }
    .items td {
      border-bottom: 1px solid var(--rule);
      padding: 14px 0 14px 14px;
      vertical-align: top;
      text-align: right;
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
      font-size: 13.5px;
    }
    .items td.c-desc { text-align: left; padding-left: 0; white-space: normal; width: 100%; }
    .item-name { font-size: 14.5px; font-weight: 600; line-height: 1.4; }
    .c-total { font-weight: 700; }
    .specs { margin-top: 7px; }
    .spec { font-size: 12.5px; line-height: 1.65; }
    .spec-k { color: var(--muted); }
    .spec-k::after { content: "\\2009\\2014\\2009"; color: var(--muted); }
    .item-measure { margin-top: 7px; font-size: 12.5px; font-weight: 600; }
    .item-notes { margin-top: 6px; font-size: 12.5px; color: var(--muted); }
    @media (max-width: 600px) { .col-unit { display: none; } }
    .empty { color: var(--muted); }

    /* ── Totales ── */
    .totals { margin: 16px 0 0 auto; max-width: 320px; }
    .t-row { display: flex; justify-content: space-between; gap: 20px; padding: 4px 0; font-size: 13.5px; color: var(--muted); }
    .t-row .v { color: var(--ink); font-variant-numeric: tabular-nums; }
    .t-row.grand {
      border-top: 2px solid var(--ink);
      margin-top: 8px;
      padding-top: 10px;
      color: var(--ink);
      font-weight: 700;
      font-size: 15px;
      align-items: baseline;
    }
    .t-row.grand .v { font-family: Georgia, "Times New Roman", serif; font-size: 21px; color: var(--brand); }
    .t-note { font-size: 11px; color: var(--muted); text-align: right; margin-top: 4px; }

    /* ── Decisión ── */
    .decision { border-top: 1px solid var(--rule); margin-top: 32px; padding-top: 26px; }
    .decision-q { font-size: 14.5px; font-weight: 600; color: var(--ink); margin: 0 0 14px; }
    .decision-btns { display: flex; gap: 10px; }
    @media (max-width: 480px) { .decision-btns { flex-direction: column; } }
    .btn {
      font: inherit;
      font-weight: 600;
      font-size: 14px;
      padding: 13px 26px;
      border-radius: 2px;
      cursor: pointer;
      transition: background 0.15s ease, color 0.15s ease, border-color 0.15s ease;
    }
    .btn-approve { background: var(--ink); color: var(--sheet); border: 1px solid var(--ink); }
    .btn-approve:hover { background: var(--brand); border-color: var(--brand); }
    .btn-reject { background: transparent; color: var(--muted); border: 1px solid var(--rule); }
    .btn-reject:hover { color: var(--no); border-color: var(--no); }
    .btn:disabled { opacity: 0.4; pointer-events: none; }
    #btn-approve.confirm { background: var(--ok); border-color: var(--ok); color: #fff; }
    #btn-reject.confirm { background: var(--no); border-color: var(--no); color: #fff; }
    .fine { font-size: 11.5px; color: var(--muted); margin-top: 12px; }

    /* ── Páginas cortas (errores / respuesta) ── */
    .receipt { padding: 8px 0 4px; }
    .receipt .doc-num { font-size: 26px; }
    .receipt .stamp-row { margin-top: 18px; }

    footer {
      text-align: center;
      font-size: 11px;
      color: var(--muted);
      margin-top: 20px;
      line-height: 1.6;
      opacity: 0.85;
    }
  </style>
</head>
<body>
  <div class="container">
    <main class="sheet">
      ${miniBrand}
      ${body}
    </main>
    <footer>Documento generado por el sistema de gestión de Grupo Alquitel.<br/>Si tiene consultas sobre este presupuesto, responda el correo por el que recibió este link.</footer>
  </div>
  <script>
    // Doble paso: el primer clic pide confirmación en el mismo botón; el segundo envía.
    let armed = null;
    function arm(btn, action, confirmLabel) {
      if (armed === action) { send(action); return; }
      document.querySelectorAll('.decision button').forEach(function(b) {
        b.classList.remove('confirm');
        if (b.dataset.original) b.textContent = b.dataset.original;
      });
      armed = action;
      btn.dataset.original = btn.dataset.original || btn.textContent;
      btn.textContent = confirmLabel;
      btn.classList.add('confirm');
    }
    async function send(action) {
      document.querySelectorAll('button').forEach(function(b) { b.disabled = true; });
      try {
        // El token va en la URL, no en el body: el servidor lo lee de la query.
        // La respuesta del POST se descarta y se recarga por GET, que vuelve a
        // renderizar la página del lado servidor. Es a propósito: nada de HTML
        // recibido por fetch se inyecta en este documento.
        await fetch(location.href, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ action: action }),
          cache: 'no-store'
        });
      } finally {
        location.reload();
      }
    }
    var btnApprove = document.getElementById('btn-approve');
    if (btnApprove) {
      btnApprove.dataset.original = btnApprove.textContent;
      btnApprove.onclick = function() { arm(btnApprove, 'approve', '¿Confirmar aprobación?'); };
    }
    var btnReject = document.getElementById('btn-reject');
    if (btnReject) {
      btnReject.dataset.original = btnReject.textContent;
      btnReject.onclick = function() { arm(btnReject, 'reject', '¿Confirmar rechazo?'); };
    }
  </script>
</body>
</html>`;

  return new Response(content, {
    status,
    headers: {
      // OJO: "text/HTML" (mayúsculas) a propósito. El gateway de Supabase reescribe
      // "text/html" a "text/plain" en GET (anti-phishing) con match case-sensitive;
      // los MIME types son case-insensitive para el navegador, así que renderiza igual.
      // No "corregir" a minúsculas: rompe la página (se ve el código fuente).
      "Content-Type": "text/HTML; charset=utf-8",
      // La página contiene datos personales e importes de un tercero: no debe
      // quedar en ninguna caché intermedia ni en la del navegador.
      "Cache-Control": "no-store, no-cache, must-revalidate, private",
      "Pragma": "no-cache",
      // El token viaja en la query string: sin esto, cualquier navegación saliente se
      // lo lleva puesto en el header Referer hacia un tercero.
      "Referrer-Policy": "no-referrer",
      "X-Content-Type-Options": "nosniff",
      "X-Frame-Options": "DENY",
      "X-Robots-Tag": "noindex, nofollow, noarchive",
      // La página es autocontenida: no carga nada de afuera y no debe poder hacerlo.
      // 'unsafe-inline' es necesario porque el <style> y el <script> van embebidos.
      "Content-Security-Policy": [
        "default-src 'none'",
        "style-src 'unsafe-inline'",
        "script-src 'unsafe-inline'",
        "img-src data:",
        "connect-src 'self'",
        "base-uri 'none'",
        "form-action 'none'",
        "frame-ancestors 'none'",
      ].join("; "),
    },
  });
}

function shortPage(title: string, body: string, status: number): Response {
  return html(`<h1>${title}</h1><p>${body}</p>`, status);
}

function receiptPage(budget: string, approved: boolean, extra = ""): Response {
  return html(`<div class="receipt"><h1>Muchas gracias</h1>
    <div class="k">Presupuesto</div>
    <div class="doc-num">N.º ${escapeHtml(budget)}</div>
    <div class="stamp-row"><span class="stamp ${approved ? "ok" : "no"}">${approved ? "Aprobado" : "Rechazado"}</span></div>
    <p class="fine">Su respuesta quedó registrada con éxito.${extra}</p></div>`);
}

// Traducción de los códigos que devuelve el RPC a páginas. Es la ÚNICA superficie
// de error del portal: nada de lo que pase adentro de la base se filtra acá.
function pageForOutcome(outcome: string, maxAgeDays?: number): Response | null {
  switch (outcome) {
    case "not_found":
      return shortPage("Link no encontrado",
        "Este link de aprobación no existe o fue dado de baja.", 404);
    case "revoked":
      return shortPage("Link reemplazado",
        "Se emitió un presupuesto actualizado y este link quedó sin efecto. " +
        "Usá el último correo que recibiste, o respondelo para que te enviemos uno nuevo.", 410);
    case "expired":
      return shortPage("Link vencido",
        `Este link de aprobación venció a los ${maxAgeDays ?? 30} días de emitido. ` +
        "Escribinos respondiendo el correo por el que lo recibiste y te enviamos uno nuevo.", 410);
    case "rate_limited":
      return shortPage("Demasiados intentos",
        "Recibimos muchas solicitudes seguidas desde tu conexión. Esperá unos minutos y volvé a intentar.", 429);
    case "order_missing":
      return shortPage("Presupuesto no disponible",
        "No pudimos cargar los datos de este presupuesto. Contactanos y lo resolvemos.", 404);
    default:
      return null;
  }
}

// ─────────────────────────────── Handler ───────────────────────────────

Deno.serve(async (req) => {
  const url = new URL(req.url);
  const token = url.searchParams.get("token") ?? "";

  // Validación de forma antes de tocar la base: recorta el barrido trivial.
  // El token NUNCA se registra ni se devuelve en un mensaje.
  if (!UUID_RE.test(token)) {
    return shortPage("Link inválido",
      "El link de aprobación no es válido o está incompleto.", 400);
  }

  const clientIp = req.headers.get("x-forwarded-for")?.split(",")[0]?.trim() ?? null;

  if (req.method === "GET") {
    const { data, error } = await supabase.rpc("get_approval_page", {
      p_token: token,
      p_client_ip: clientIp,
    });

    if (error) {
      // No se propaga `error` al navegador: puede traer detalles del backend.
      console.error("get_approval_page falló", { code: error.code, msg: error.message });
      return shortPage("No pudimos abrir el presupuesto",
        "Hubo un problema al cargar la página. Volvé a intentar en un momento.", 502);
    }

    const payload = data as PagePayload;
    const errPage = pageForOutcome(payload?.outcome, payload?.max_age_days);
    if (errPage) return errPage;
    if (payload?.outcome !== "ok") {
      return shortPage("No pudimos abrir el presupuesto",
        "Hubo un problema al cargar la página. Volvé a intentar en un momento.", 502);
    }

    return html(renderBudgetPage(payload), 200, true);
  }

  if (req.method === "POST") {
    let action = "";
    try { action = (await req.json()).action; } catch { /* body inválido */ }

    const { data, error } = await supabase.rpc("respond_approval", {
      p_token: token,
      p_action: action,
      p_client_ip: clientIp,
    });

    if (error) {
      console.error("respond_approval falló", { code: error.code, msg: error.message });
      return shortPage("No pudimos registrar tu respuesta",
        "Hubo un problema al guardar. Volvé a intentar en un momento; tu link sigue siendo válido.", 502);
    }

    const r = data as { outcome: string; status?: number; budget_number?: string; max_age_days?: number };
    const budget = r?.budget_number ?? "—";

    switch (r?.outcome) {
      case "ok":
        return receiptPage(budget, r.status === APPROVAL_APPROVED);

      // Idempotencia: repetir la MISMA acción devuelve el mismo comprobante, sin
      // volver a escribir. Es lo que ve el cliente que hace doble clic o recarga.
      case "already_same":
        return receiptPage(budget, r.status === APPROVAL_APPROVED,
          " Ya lo habíamos registrado antes.");

      // La acción contraria a un veredicto ya emitido no lo pisa. Antes de este
      // cambio, dos pedidos simultáneos podían dejar la orden en un estado que
      // contradecía la respuesta guardada.
      case "already_other":
        return html(`<div class="receipt"><h1>Ya respondido</h1>
          <div class="k">Presupuesto</div>
          <div class="doc-num">N.º ${escapeHtml(budget)}</div>
          ${renderStamp(r.status, null)}
          <p class="fine">Este presupuesto ya había sido respondido y se conserva la primera
          respuesta. Si necesitás cambiarla, respondé el correo por el que recibiste el link.</p></div>`, 409);

      case "invalid_action":
        return shortPage("Solicitud inválida", "La acción solicitada no es válida.", 400);

      case "order_state_conflict":
        return shortPage("El presupuesto ya avanzó",
          "Este presupuesto ya pasó a producción y no admite una respuesta por este medio. " +
          "Escribinos respondiendo el correo y lo vemos.", 409);

      case "approval_not_consumed":
        return shortPage("No pudimos registrar tu respuesta",
          "Hubo un conflicto al guardar. Volvé a intentar; tu link sigue siendo válido.", 409);

      default: {
        const errPage = pageForOutcome(r?.outcome, r?.max_age_days);
        if (errPage) return errPage;
        return shortPage("No pudimos registrar tu respuesta",
          "Hubo un problema al guardar. Volvé a intentar en un momento.", 502);
      }
    }
  }

  return shortPage("Método no soportado", "Esta página solo responde a GET y POST.", 405);
});
