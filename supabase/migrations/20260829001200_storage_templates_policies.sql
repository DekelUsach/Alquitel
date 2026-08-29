-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829001200_storage_templates_policies
--
-- HALLAZGO (verificado el 2026-08-29 contra el proyecto real):
--
--   storage.buckets → templates, public = false, límite 10 MB, solo .docx
--   pg_policies (schema storage) → UNA sola política:
--       templates_read_anon | {anon} | SELECT | bucket_id = 'templates'
--
-- El bucket está marcado como privado, pero la única política que tiene concede
-- lectura al rol `anon`. Es decir: cualquiera que tenga la AnonKey puede
-- descargar todas las plantillas. Y la AnonKey no es un secreto — está en
-- appsettings.json, viaja dentro del instalador y está en el repositorio por
-- diseño. En la práctica el bucket es público para cualquiera que haya visto el
-- binario una vez.
--
-- Las plantillas .docx no son catastróficas si se filtran, pero son documentos
-- internos: membrete, estructura de precios, condiciones comerciales y textos
-- legales de la empresa. No hay ninguna razón para que sean descargables sin
-- estar autenticado.
--
-- Además hoy PUBLICAR una plantilla requiere la service role key en la máquina
-- del Admin (SupabaseTemplateStorageService). Esa key tiene BYPASSRLS sobre todo
-- el proyecto: usarla para subir un .docx es como usar la llave maestra del
-- edificio para abrir un cajón. Con las políticas de acá, un Admin puede subir
-- con su propio JWT y la key deja de ser necesaria en el cliente.
--
-- Cambio requerido del lado cliente (documentado en
-- docs/SUPABASE_MIGRATION_CONTRACT.md): SupabaseTemplateStorageService debe usar
-- el access token del usuario logueado en vez de AnonKey/ServiceKey.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── 1. Se va la lectura anónima ──────────────────────────────────────────────
DROP POLICY IF EXISTS templates_read_anon ON storage.objects;

-- ── 2. Leen los empleados activos, cualquiera sea su rol ─────────────────────
-- Operaciones también necesita las plantillas de OT, así que no se restringe por
-- rol: alcanza con ser un usuario de la aplicación vigente. app.is_active_user()
-- devuelve false para cuentas archivadas o desactivadas, así que dar de baja a
-- alguien también le corta el acceso a las plantillas.

DROP POLICY IF EXISTS templates_read_app ON storage.objects;
CREATE POLICY templates_read_app ON storage.objects
    FOR SELECT TO authenticated
    USING (bucket_id = 'templates' AND app.is_active_user());

-- ── 3. Publica solo el Admin, con su propia identidad ────────────────────────
-- Antes esto exigía la service role key. Ahora es el JWT del Admin, que además
-- deja rastro de quién subió qué en storage.objects.owner.

DROP POLICY IF EXISTS templates_write_admin ON storage.objects;
CREATE POLICY templates_write_admin ON storage.objects
    FOR INSERT TO authenticated
    WITH CHECK (bucket_id = 'templates' AND app.has_role('admin'));

DROP POLICY IF EXISTS templates_update_admin ON storage.objects;
CREATE POLICY templates_update_admin ON storage.objects
    FOR UPDATE TO authenticated
    USING      (bucket_id = 'templates' AND app.has_role('admin'))
    WITH CHECK (bucket_id = 'templates' AND app.has_role('admin'));

DROP POLICY IF EXISTS templates_delete_admin ON storage.objects;
CREATE POLICY templates_delete_admin ON storage.objects
    FOR DELETE TO authenticated
    USING (bucket_id = 'templates' AND app.has_role('admin'));

-- ── 4. El bucket sigue restringido a .docx y a 10 MB ─────────────────────────
-- Ya estaba así; se reafirma acá para que el baseline lo reproduzca en un
-- proyecto nuevo. Sin el filtro de MIME, el bucket sería un lugar cómodo para
-- alojar un ejecutable y repartir el link.
UPDATE storage.buckets
SET    public = false,
       file_size_limit = 10485760,
       allowed_mime_types = ARRAY['application/vnd.openxmlformats-officedocument.wordprocessingml.document']
WHERE  id = 'templates';

-- ── Verificación ─────────────────────────────────────────────────────────────
-- SELECT policyname, roles, cmd FROM pg_policies WHERE schemaname='storage';
--   → ninguna política con {anon}
-- Descarga con la AnonKey:
--   curl -s -o /dev/null -w '%{http_code}\n' \
--     "$URL/storage/v1/object/templates/<archivo>.docx" -H "apikey: $ANON"
--   → 400/403, ya no 200.
