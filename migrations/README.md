# Procedimiento de migración

Destino autorizado de esta fase: base `fyxbody_videos` en PostgreSQL TEST. El historial está en `videos.__EFMigrationsHistory`.

1. Revisar el SQL y confirmar host/base y estado previo.
2. Para bases con datos, preparar copia/restauración antes de cambios. La migración inicial crea tablas nuevas en una base que se verificó vacía.
3. Configurar `ConnectionStrings__Videos` sin escribir secretos en el proyecto. Usar una identidad de migración con permisos DDL, separada del usuario de ejecución cuando se despliegue la API.
4. Aplicar con un único ejecutor: `dotnet ef database update --project src/FyxBody.Videos.Data --startup-project src/FyxBody.Videos.Data`. Alternativamente ejecutar `001_initial_video_licensing.sql` con parada ante error. No combinar ambos ejecutores simultáneamente.
5. Verificar historial, nueve tablas de negocio y readiness HTTP. Repetir la actualización no debe aplicar cambios.

No ejecutar migraciones en el arranque de la API. El SQL idempotente crea el esquema/historial si faltan y protege cada operación mediante el historial EF. No repara deriva manual de tablas o restricciones.

## Reversión

Para una base **desechable local** se comprobó la reversión completa y reaplicación mediante:

```bash
dotnet ef database update 0 --project src/FyxBody.Videos.Data --startup-project src/FyxBody.Videos.Data
dotnet ef database update --project src/FyxBody.Videos.Data --startup-project src/FyxBody.Videos.Data
```

La reversión elimina tablas y datos del esquema de negocio. No usarla sobre TEST con datos o producción sin revisar impacto y recuperación. Para despliegues con datos, preferir migraciones correctivas hacia delante.

## Estado verificado de TEST

Migración inicial aplicada el 2026-11-05 con credencial de migración autorizada. Verificados nueve tablas de negocio e historial EF; segunda ejecución sin cambios. No se modificaron permisos de usuarios ni otras bases. Consultar `docs/verification-2026-11-05.md`.

## Gestión de catálogo por cliente

Migración adicional `20261105145124_RevokeCatalogGrants`: columna nullable revoked_at y CHECK de fechas en license_videos. Conserva datos y referencias históricas. `002_catalog_revocation.sql` es un script idempotente acumulativo que incluye la migración inicial y la adicional; puede ejecutarse contra una base vacía o ya inicializada mediante un único ejecutor. Su reversión elimina la marca de revocación: no usarla sobre datos comerciales sin revisar permisos efectivos.

## Usuarios del panel

`20261105152349_PortalUsers` crea videos.portal_users e índices/restricciones. Aplicada y verificada en Azure TEST el 2026-11-05; diez tablas de negocio más historial EF. `003_portal_users.sql` es acumulativo e idempotente. La creación de usuarios no forma parte de la migración: el administrador inicial se provisiona mediante bootstrap explícito. No revertir esta migración en una base con usuarios sin plan de recuperación, pues se eliminarían cuentas.

## Cola de importaciones YouTube

`20261105170851_YouTubeImportJobs` añade únicamente la tabla `videos.video_import_jobs`, sus restricciones, la referencia nullable al vídeo y los índices de cola/lease y unicidad del ID YouTube en estados queued/running. No modifica ni rellena vídeos, assets, usuarios o datos de consumo. `004_youtube_import_jobs.sql` es acumulativo e idempotente.

La funcionalidad permanece desactivada por defecto. Desplegar/aplicar la migración antes de habilitar VideoImports:Enabled; no hay ejecutor automático al arrancar. Las pruebas de esta migración se realizan en PostgreSQL local temporal; la aplicación al PostgreSQL Azure no forma parte de esta entrega. Su reversión elimina el historial de importaciones y debe realizarse solo con el trabajador detenido y tras copia de seguridad; conserva los vídeos y assets importados. Preferir correcciones hacia delante en bases con datos.

## Sesiones ilimitadas explícitas

`20261105183903_UnlimitedLicenseSessions` añade únicamente `videos.licenses.unlimited_sessions`, booleano no nullable con default false. Las licencias existentes conservan su cuota y permanecen limitadas. `005_unlimited_license_sessions.sql` es acumulativo e idempotente; validar en una base temporal antes de aplicar con un único ejecutor y copia del destino. No se ha aplicado a Azure en esta tarea.

Desplegar código que entiende el flag antes de habilitar el modo ilimitado; un proceso antiguo interpreta cuota 0 como bloqueo y no debe convivir atendiendo emisiones ilimitadas. La ampliación de esquema es compatible con las filas y peticiones anteriores. No revertir eliminando el flag sin convertir y revisar antes las licencias afectadas: se perdería el significado de ilimitada y la cuota normalizada 0 bloquearía emisiones. No se reinician contadores ni se borran sesiones.

## Subpartners, delegated catalogues and private upload quotas

`007_partner_catalogues_and_upload_quotas.sql` adds partner hierarchy, licence capabilities and quotas, private video ownership and delegated grant provenance. Existing catalogue grants retain direct access and upload and delegation capabilities default to disabled. The script is cumulative and idempotent. Apply it before starting the new API build. The local integration suite uses a separate PostgreSQL database.
