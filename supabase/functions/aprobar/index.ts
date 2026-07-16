// Edge Function "aprobar" — portal público de aprobación de presupuestos.
//
// GET  /aprobar?token=<uuid>  → página HTML con el presupuesto COMPLETO (cliente,
//                                evento, ítems, totales) y botones Aprobar/Rechazar.
// POST /aprobar?token=<uuid>  → body {"action":"approve"|"reject"}; registra la respuesta
//                                y actualiza el estado de la orden.
//
// Seguridad: el token uuid ES la autorización (link secreto por presupuesto). La función
// corre con la service role key (secret del proyecto, nunca viaja al navegador) y toca
// solo la fila de OrderApprovals del token + el Status de su orden.
// Todo dato de la base se escapa antes de interpolar en el HTML (XSS almacenado);
// los colores de estilos dinámicos se validan contra /^#[0-9a-f]{6}$/i.
// Nunca se exponen campos internos: InternalNotes, SpecialDiscountPercent, Cost.
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

// ─────────────────────────────── Tipos de filas ───────────────────────────────

interface OrderRow {
  Id: string;
  BudgetNumber: string;
  ClientId: string;
  LocationId: string;
  CreatedDate: string;
  EventDate: string | null;
  EventEndDate: string | null;
  Status: number;
  Comments: string | null;
  DiscountPercent: number | null;
  DiscountAmount: number | null;
  AddVat: boolean | null;
}

interface ItemRow {
  Id: string;
  ProductId: string;
  Quantity: number;
  UnitPrice: number;
  Dias: number;
  TechnicalNotes: string | null;
  CustomFieldsJson: string | null;
  DescriptionSnapshot: string | null;
  RequestedMeasure: string | null;
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
function renderItemRow(item: ItemRow, fallbackDescription: string): string {
  const desc = item.DescriptionSnapshot?.trim()
    ? bbToHtml(item.DescriptionSnapshot)
    : escapeHtml(fallbackDescription);
  const total = item.Quantity * item.UnitPrice * item.Dias;

  const extras: string[] = [];
  if (item.RequestedMeasure?.trim()) {
    extras.push(`<div class="item-measure">${escapeHtml(item.RequestedMeasure)}</div>`);
  }
  if (item.TechnicalNotes?.trim()) {
    extras.push(`<div class="item-notes">${bbToHtml(item.TechnicalNotes)}</div>`);
  }

  return `<tr>
    <td class="c-desc">
      <div class="item-name">${desc}</div>
      ${renderCustomFields(item.CustomFieldsJson)}
      ${extras.join("")}
    </td>
    <td class="c-num">${item.Quantity}</td>
    <td class="c-num">${item.Dias}</td>
    <td class="c-num col-unit">${fmtMoney(item.UnitPrice)}</td>
    <td class="c-num c-total">${fmtMoney(total)}</td>
  </tr>`;
}

interface PageData {
  approvalStatus: number;
  order: OrderRow;
  clientName: string;
  clientCuit: string;
  clientContact: string | null;
  clientEmail: string | null;
  clientPhone: string | null;
  locationName: string | null;
  items: ItemRow[];
  productNames: Map<string, string>;
  respondedAt: string | null;
}

function renderBudgetPage(d: PageData): string {
  const o = d.order;
  const budget = escapeHtml(o.BudgetNumber || "—");

  // Totales — misma aritmética que Order.cs (Total/DiscountValue/NetTotal/VatValue/GrandTotal)
  const subtotal = d.items.reduce((acc, i) => acc + i.Quantity * i.UnitPrice * i.Dias, 0);
  const pct = Math.min(Math.max(o.DiscountPercent ?? 0, 0), 100);
  const rawDisc = subtotal * pct / 100 + Math.max(0, o.DiscountAmount ?? 0);
  const discount = Math.min(rawDisc, subtotal);
  const net = subtotal - discount;
  const addVat = o.AddVat === true;
  const vat = addVat ? Math.round(net * VAT_RATE * 100) / 100 : 0;
  const grand = net + vat;

  // Sello de estado (cuando ya se respondió)
  let stamp = "";
  if (d.approvalStatus === APPROVAL_APPROVED) {
    const when = fmtDate(d.respondedAt);
    stamp = `<div class="stamp-row"><span class="stamp ok">Aprobado${when ? ` · ${when}` : ""}</span></div>`;
  } else if (d.approvalStatus === APPROVAL_REJECTED) {
    const when = fmtDate(d.respondedAt);
    stamp = `<div class="stamp-row"><span class="stamp no">Rechazado${when ? ` · ${when}` : ""}</span></div>`;
  }

  const clientLines = [
    `<div class="name">${escapeHtml(d.clientName)}</div>`,
    d.clientCuit ? `<div class="line">CUIT ${escapeHtml(d.clientCuit)}</div>` : "",
    d.clientContact ? `<div class="line">${escapeHtml(d.clientContact)}</div>` : "",
    d.clientEmail ? `<div class="line">${escapeHtml(d.clientEmail)}</div>` : "",
    d.clientPhone ? `<div class="line">${escapeHtml(d.clientPhone)}</div>` : "",
  ].join("");
  const range = eventRange(o.EventDate, o.EventEndDate);
  const eventLines = [
    range ? `<div class="name">${escapeHtml(range)}</div>` : "",
    d.locationName ? `<div class="line">${escapeHtml(d.locationName)}</div>` : "",
  ].join("");

  const meta = `<section class="meta">
    <div class="meta-block">
      <div class="k">Preparado para</div>
      ${clientLines}
    </div>
    ${eventLines.trim() ? `<div class="meta-block"><div class="k">Evento</div>${eventLines}</div>` : ""}
  </section>`;

  const comments = o.Comments?.trim()
    ? `<section class="comments">
         <div class="k">Comentarios</div>
         <p>${bbToHtml(o.Comments)}</p>
       </section>`
    : "";

  const itemRows = d.items.length
    ? d.items.map((i) => renderItemRow(i, d.productNames.get(i.ProductId) ?? "Producto")).join("")
    : `<tr><td class="c-desc" colspan="5"><span class="empty">Este presupuesto no tiene ítems cargados.</span></td></tr>`;

  const totalsRows = [
    `<div class="t-row"><span>Subtotal</span><span class="v">${fmtMoney(subtotal)}</span></div>`,
    discount > 0
      ? `<div class="t-row"><span>Descuento${pct > 0 ? ` (${pct}%${(o.DiscountAmount ?? 0) > 0 ? " + fijo" : ""})` : ""}</span><span class="v">−${fmtMoney(discount)}</span></div>`
      : "",
    addVat
      ? `<div class="t-row"><span>Neto</span><span class="v">${fmtMoney(net)}</span></div>
         <div class="t-row"><span>IVA 21%</span><span class="v">${fmtMoney(vat)}</span></div>`
      : "",
    `<div class="t-row grand"><span>Total</span><span class="v">${fmtMoney(grand)}</span></div>`,
    !addVat ? `<div class="t-note">Precios finales, IVA no discriminado.</div>` : "",
  ].join("");

  const actions = d.approvalStatus === APPROVAL_PENDING
    ? `<section class="decision">
         <p class="decision-q">¿Confirma este presupuesto?</p>
         <div class="decision-btns">
           <button id="btn-approve" class="btn btn-approve">Aprobar presupuesto</button>
           <button id="btn-reject" class="btn btn-reject">Rechazar</button>
         </div>
         <p class="fine">Al confirmar, su respuesta queda registrada con fecha y hora.</p>
       </section>`
    : "";

  const created = fmtDate(o.CreatedDate);

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
    </header>
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
        await fetch(location.href, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ action: action })
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
      "Cache-Control": "no-store",
    },
  });
}

// ─────────────────────────────── Handler ───────────────────────────────

Deno.serve(async (req) => {
  const url = new URL(req.url);
  const token = url.searchParams.get("token") ?? "";
  if (!UUID_RE.test(token)) {
    return html(`<h1>Link inválido</h1><p>El link de aprobación no es válido o está incompleto.</p>`, 400);
  }

  const { data: approval, error } = await supabase
    .from("OrderApprovals")
    .select("Id, OrderId, Status, RespondedAt")
    .eq("Token", token)
    .maybeSingle();

  if (error || !approval) {
    return html(`<h1>Link no encontrado</h1><p>Este link de aprobación no existe o fue dado de baja.</p>`, 404);
  }

  if (req.method === "GET") {
    // Carga completa del presupuesto: orden + cliente + lugar + ítems + nombres
    // de producto (fallback para ítems legados sin DescriptionSnapshot).
    const { data: order } = await supabase
      .from("Orders")
      .select("Id, BudgetNumber, ClientId, LocationId, CreatedDate, EventDate, EventEndDate, Status, Comments, DiscountPercent, DiscountAmount, AddVat")
      .eq("Id", approval.OrderId)
      .maybeSingle();

    if (!order) {
      return html(`<h1>Presupuesto no disponible</h1><p>No pudimos cargar los datos de este presupuesto. Contáctenos.</p>`, 404);
    }

    const [clientRes, locationRes, itemsRes] = await Promise.all([
      supabase.from("Clients")
        .select("CompanyName, Cuit, ContactName, Email, Phone")
        .eq("Id", order.ClientId).maybeSingle(),
      supabase.from("Locations")
        .select("Name")
        .eq("Id", order.LocationId).maybeSingle(),
      supabase.from("OrderItems")
        .select("Id, ProductId, Quantity, UnitPrice, Dias, TechnicalNotes, CustomFieldsJson, DescriptionSnapshot, RequestedMeasure")
        .eq("OrderId", order.Id),
    ]);

    const items = (itemsRes.data ?? []) as ItemRow[];

    const productNames = new Map<string, string>();
    const missingIds = [...new Set(
      items.filter((i) => !i.DescriptionSnapshot?.trim()).map((i) => i.ProductId),
    )];
    if (missingIds.length > 0) {
      const { data: products } = await supabase
        .from("Products")
        .select("Id, Description")
        .in("Id", missingIds);
      for (const p of products ?? []) {
        // El catálogo también usa BBCode en la descripción: se limpia para el fallback.
        productNames.set(p.Id, (p.Description ?? "").replace(/\[\/?[a-zA-Z]+\]/g, ""));
      }
    }

    const page = renderBudgetPage({
      approvalStatus: approval.Status,
      order: order as OrderRow,
      clientName: clientRes.data?.CompanyName ?? "—",
      clientCuit: clientRes.data?.Cuit ?? "",
      clientContact: clientRes.data?.ContactName ?? null,
      clientEmail: clientRes.data?.Email ?? null,
      clientPhone: clientRes.data?.Phone ?? null,
      locationName: locationRes.data?.Name ?? null,
      items,
      productNames,
      respondedAt: approval.RespondedAt,
    });
    return html(page, 200, true);
  }

  if (req.method === "POST") {
    const { data: order } = await supabase
      .from("Orders")
      .select("BudgetNumber")
      .eq("Id", approval.OrderId)
      .maybeSingle();
    const budget = escapeHtml(order?.BudgetNumber ?? "—");

    if (approval.Status !== APPROVAL_PENDING) {
      return html(`<div class="receipt"><h1>Acción duplicada</h1>
        <div class="k">Presupuesto</div>
        <div class="doc-num">N.º ${budget}</div>
        <p class="fine">Este link de aprobación ya fue utilizado previamente.</p></div>`, 409);
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

    return html(`<div class="receipt"><h1>Muchas gracias</h1>
      <div class="k">Presupuesto</div>
      <div class="doc-num">N.º ${budget}</div>
      <div class="stamp-row"><span class="stamp ${approved ? "ok" : "no"}">${approved ? "Aprobado" : "Rechazado"}</span></div>
      <p class="fine">Su respuesta quedó registrada con éxito.</p></div>`);
  }

  return html(`<h1>Método no soportado</h1>`, 405);
});
