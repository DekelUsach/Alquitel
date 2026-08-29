// ─────────────────────────────────────────────────────────────────────────────
// Prueba de concurrencia real del portal de aprobación.
//
// La suite SQL (01_security_suite.sql) corre en una sola sesión: puede verificar
// el invariante (el segundo pedido no pisa el veredicto del primero) pero no la
// carrera de verdad, que necesita DOS conexiones simultáneas. Esto es eso.
//
// Reproduce el escenario que rompía la versión anterior de la Edge Function:
// dos peticiones que llegan al mismo tiempo con acciones CONTRARIAS sobre el
// mismo token. Con el código viejo, el UPDATE condicional del segundo afectaba
// 0 filas, no devolvía error, y el código igual pisaba Orders.Status con el
// veredicto contrario.
//
// ⚠️  ESCRIBE Y COMMITEA datos de prueba (dos conexiones no pueden compartir una
//     transacción sin commitear). Correr SOLO contra una base descartable: una
//     rama de Supabase, un Postgres local o una copia. NUNCA contra producción.
//     Al final limpia lo que creó, incluso si una prueba falla.
//
// Requisitos:
//     npm install pg
//     export SUPABASE_DB_URL="postgresql://postgres:<pass>@<host>:5432/postgres"
//     node supabase/tests/02_approval_concurrency.mjs
//
// Requiere aplicadas las migraciones 20260829000100 … 20260829000900.
// ─────────────────────────────────────────────────────────────────────────────

import pg from "pg";

const CONN = process.env.SUPABASE_DB_URL;
if (!CONN) {
  console.error("Falta SUPABASE_DB_URL. Ver el encabezado de este archivo.");
  process.exit(2);
}
if (!process.env.ALQUITEL_ALLOW_WRITES) {
  console.error(
    "Esta prueba escribe en la base. Confirmá que NO es producción exportando\n" +
    "  ALQUITEL_ALLOW_WRITES=1"
  );
  process.exit(2);
}

const ID = {
  client:   "ccccccc2-0000-0000-0000-000000000001",
  location: "11111111-0000-0000-0000-0000000000f2",
  product:  "bbbbbbb2-0000-0000-0000-000000000001",
  order:    "00000011-0000-0000-0000-000000000001",
  item:     "00000012-0000-0000-0000-000000000001",
  approval: "00000014-0000-0000-0000-000000000001",
  token:    "cccccccc-cccc-cccc-cccc-cccccccccccc",
};

const resultados = [];
const chk = (nombre, ok, detalle = "") => resultados.push({ nombre, ok, detalle });

async function conectar() {
  const c = new pg.Client({ connectionString: CONN });
  await c.connect();
  return c;
}

async function sembrar(c) {
  await c.query(`
    INSERT INTO public."Clients" ("Id","CompanyName","Cuit") VALUES ($1,'ZZ CONC Cliente','30711111122')
      ON CONFLICT ("Id") DO NOTHING;
    INSERT INTO public."Locations" ("Id","Name") VALUES ($2,'ZZ CONC Predio')
      ON CONFLICT ("Id") DO NOTHING;
    INSERT INTO public."Products" ("Id","Description","Category","BasePrice") VALUES ($3,'ZZ CONC Producto','Visuales',1000)
      ON CONFLICT ("Id") DO NOTHING;
  `, [ID.client, ID.location, ID.product]);

  await c.query(`
    INSERT INTO public."Orders" ("Id","BudgetNumber","ClientId","LocationId","CreatedDate","Status")
    VALUES ($1,'ZZ-CONC-0001',$2,$3,(now() AT TIME ZONE 'UTC'),0)
      ON CONFLICT ("Id") DO UPDATE SET "Status"=0;
  `, [ID.order, ID.client, ID.location]);

  await c.query(`
    INSERT INTO public."OrderItems" ("Id","OrderId","ProductId","Quantity","UnitPrice","Dias")
    VALUES ($1,$2,$3,1,1000,1) ON CONFLICT ("Id") DO NOTHING;
  `, [ID.item, ID.order, ID.product]);

  await c.query(`DELETE FROM public."OrderApprovals" WHERE "Id" = $1;`, [ID.approval]);
  await c.query(`
    INSERT INTO public."OrderApprovals" ("Id","OrderId","Token","Status","CreatedAt")
    VALUES ($1,$2,$3,0,(now() AT TIME ZONE 'UTC'));
  `, [ID.approval, ID.order, ID.token]);

  // El rate limit es por token y por IP: se limpia para que la carrera no choque
  // con el límite en vez de con el bloqueo de fila, que es lo que se quiere medir.
  await c.query(`DELETE FROM app.approval_rate_limit;`);
}

async function limpiar(c) {
  await c.query(`DELETE FROM public."OrderAuditEvents" WHERE "OrderId" = $1;`, [ID.order]);
  await c.query(`DELETE FROM public."OrderApprovals"  WHERE "OrderId" = $1;`, [ID.order]);
  await c.query(`DELETE FROM public."OrderItems"      WHERE "OrderId" = $1;`, [ID.order]);
  await c.query(`DELETE FROM public."Orders"          WHERE "Id" = $1;`, [ID.order]);
  await c.query(`DELETE FROM public."Products"        WHERE "Id" = $1;`, [ID.product]);
  await c.query(`DELETE FROM public."Locations"       WHERE "Id" = $1;`, [ID.location]);
  await c.query(`DELETE FROM public."Clients"         WHERE "Id" = $1;`, [ID.client]);
  await c.query(`DELETE FROM app.approval_rate_limit;`);
}

async function main() {
  const admin = await conectar();
  const a = await conectar();
  const b = await conectar();

  try {
    await sembrar(admin);

    // ── Carrera: aprobar y rechazar disparados a la vez sobre el mismo token ──
    // Las dos conexiones son independientes; el orden en que el planificador las
    // atienda no importa. Lo que se verifica es que el resultado sea COHERENTE:
    // exactamente una gana, la otra recibe already_other, y el estado de la orden
    // se corresponde con la ganadora.
    const [r1, r2] = await Promise.all([
      a.query(`SELECT public.respond_approval($1,'approve','198.51.100.10') AS r`, [ID.token]),
      b.query(`SELECT public.respond_approval($1,'reject','198.51.100.11')  AS r`, [ID.token]),
    ]);

    const o1 = r1.rows[0].r.outcome;
    const o2 = r2.rows[0].r.outcome;
    const ganadoras = [o1, o2].filter((o) => o === "ok").length;

    chk("carrera · exactamente una petición consume el token",
        ganadoras === 1, `resultados: ${o1} / ${o2}`);

    chk("carrera · la perdedora recibe already_other, no un éxito falso",
        [o1, o2].some((o) => o === "already_other"), `resultados: ${o1} / ${o2}`);

    const est = await admin.query(
      `SELECT o."Status" AS orden, a."Status" AS aprobacion
         FROM public."Orders" o JOIN public."OrderApprovals" a ON a."OrderId" = o."Id"
        WHERE o."Id" = $1`, [ID.order]);
    const { orden, aprobacion } = est.rows[0];

    // 1 Approved / 2 Rejected en OrderApprovals; 1 Approved / 5 Rejected en Orders.
    const coherente = (aprobacion === 1 && orden === 1) || (aprobacion === 2 && orden === 5);
    chk("carrera · el estado de la orden coincide con el veredicto guardado",
        coherente, `orden=${orden} aprobacion=${aprobacion} (con el código anterior podían contradecirse)`);

    const audit = await admin.query(
      `SELECT count(*)::int AS n FROM public."OrderAuditEvents"
        WHERE "OrderId" = $1 AND "EventType" LIKE '%por el cliente'`, [ID.order]);
    chk("carrera · se registra exactamente un veredicto en la bitácora",
        audit.rows[0].n === 1, `eventos: ${audit.rows[0].n}`);

    // ── Diez peticiones simultáneas de la MISMA acción ──────────────────────
    // Idempotencia bajo carga: ninguna debe fallar ni escribir de nuevo.
    const conns = [];
    for (let i = 0; i < 10; i++) conns.push(await conectar());
    try {
      const rs = await Promise.all(conns.map((c, i) =>
        c.query(`SELECT public.respond_approval($1,'approve',$2) AS r`,
                [ID.token, `198.51.100.${20 + i}`])));
      const outs = rs.map((r) => r.rows[0].r.outcome);
      chk("idempotencia · 10 peticiones simultáneas no producen errores ni escrituras nuevas",
          outs.every((o) => ["ok", "already_same", "already_other"].includes(o)),
          `resultados: ${[...new Set(outs)].join(", ")}`);

      const audit2 = await admin.query(
        `SELECT count(*)::int AS n FROM public."OrderAuditEvents"
          WHERE "OrderId" = $1 AND "EventType" LIKE '%por el cliente'`, [ID.order]);
      chk("idempotencia · la bitácora sigue con un solo veredicto",
          audit2.rows[0].n === 1, `eventos: ${audit2.rows[0].n}`);
    } finally {
      for (const c of conns) await c.end().catch(() => {});
    }
  } finally {
    await limpiar(admin).catch((e) => console.error("Fallo la limpieza:", e.message));
    await Promise.all([admin.end(), a.end(), b.end()].map((p) => p.catch(() => {})));
  }

  let fallan = 0;
  for (const r of resultados) {
    if (!r.ok) fallan++;
    console.log(`${r.ok ? "PASA        " : "*** FALLA ***"} ${r.nombre}${r.detalle ? `  — ${r.detalle}` : ""}`);
  }
  console.log(`\n${resultados.length - fallan}/${resultados.length} pruebas pasan.`);
  process.exit(fallan === 0 ? 0 : 1);
}

main().catch((e) => { console.error(e); process.exit(1); });
