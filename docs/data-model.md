# Modelo de datos v1

Todas las tablas viven en el esquema PostgreSQL `videos`. Los IDs de entidades son UUID generados por .NET. Fechas de instantes en `timestamptz` y UTC; períodos mensuales en `date`, siempre primer día del mes UTC. Los nombres físicos son snake_case.

| Tabla | Propósito | Restricciones principales |
|---|---|---|
| videos | Referencia al ejercicio original, título y estado | Referencia externa única; título/referencia no vacíos |
| video_assets | Archivo por idioma y calidad | Vídeo/idioma/calidad únicos; ruta Blob única; tamaños no negativos |
| partners | Cliente externo | Código único; estados active/suspended/revoked |
| partner_credentials | Identificador público y hash irreversible del secreto | KeyId único; fechas de expiración/revocación coherentes |
| licenses | Vigencia y cuota por cliente | Fin posterior a inicio; cuota no negativa |
| license_videos | Catálogo explícito de licencia | PK compuesta licencia/vídeo |
| playback_sessions | Autorización e idempotencia | Cliente/clave idempotente únicos; hash de petición SHA-256; mes consistente |
| playback_events | Analítica enviada por reproductor | Sesión/clave de evento únicos; posición no negativa |
| usage_monthly | Sesiones emitidas por licencia y mes | PK cliente/licencia/mes; contador no negativo |

Las claves foráneas compuestas impiden asociar una sesión a la licencia de otro cliente, a un vídeo no incluido en esa licencia o al archivo de otro vídeo. Los borrados están restringidos para conservar el historial: suspender o revocar mediante estados. La política de retención se implementará junto con analítica; no hay borrados automáticos.

El esquema no contiene pacientes, programas ni datos personales de usuarios finales. `external_exercise_id` conserva el ID original como texto; no existe conexión ni dependencia operativa de MongoDB.

## Reglas de negocio implementadas

La base garantiza integridad estructural. La API valida estados, vigencia de licencia, asset listo, expiración de sesión y límites. Emisión de sesión y reserva de cuota se ejecutan en una transacción con bloqueo de partner e incremento condicional. Se comparan hashes de petición para rechazar una clave idempotente reutilizada con otro contenido. El vídeo se bloquea FOR SHARE durante la emisión; administración usa FOR UPDATE para editar vídeo o asset.

Una sesión autorizada no acredita una reproducción. Eventos de inicio/progreso/finalización dependen del reproductor y no equivalen a bytes facturados por Azure. Se consultan sesiones distintas iniciadas/completadas por mes de ocurrido el evento; la cuota cuenta sesiones emitidas por mes UTC. La transferencia Azure se concilia externamente.

Los secretos partner aleatorios de 256 bits se guardan como sha256:hex; no son contraseñas humanas. No se han generado credenciales comerciales en Azure ni se almacenan URLs SAS. Las rutas Blob son identificadores de objetos, no permisos de acceso.

La migración se versiona en código y su historial queda en `videos.__EFMigrationsHistory`. No se modifica `public` ni otras bases del servidor. No hay seed de datos comerciales.

## Revocación de catálogo

RevokeCatalogGrants añade license_videos.revoked_at nullable y una restricción de fechas. Reemplazar catálogo revoca/reactiva filas sin borrarlas, de modo que las sesiones antiguas conservan sus FK. Una licencia retirada o acortada bloquea nuevas emisiones; el replay no extiende expiración ni supera una nueva vigencia más corta.

## Usuarios del portal

PortalUsers añade videos.portal_users con nombre normalizado único, nombre visible, rol admin/partner, vínculo de cliente obligatorio solo para rol partner, estado, hash de contraseña Identity/PBKDF2, marca de seguridad UUID y bloqueo tras cinco fallos. La contraseña humana nunca usa el hash SHA-256 de claves API. Las sesiones cifradas en cookies incluyen marca de seguridad, validada contra PostgreSQL en cada petición. Cambio/restablecimiento de contraseña o desactivación invalidan sesiones anteriores. La revalidación del servicio partner comprueba también esa marca tras esperar bloqueos.

## video_import_jobs

Historial durable de importaciones YouTube administrativas: ID y URL canónica de la fuente, título nullable, status/stage, porcentaje aproximado, vídeo nullable, arrays de alturas solicitadas/confirmadas, fechas, error sanitizado y lease temporal con identificador de propietario. Índice único parcial sobre youtube_id para queued/running; índices de status/created_at y lease_expires_at running. RequestedHeights se limita a 1080/720/480/360; CompletedHeights conserva alturas reales positivas, incluido fallback inferior. No contiene permisos SAS, rutas temporales ni secretos.

El claim usa una única sentencia UPDATE con selección FOR UPDATE SKIP LOCKED. Las confirmaciones por asset bloquean primero la fila del trabajo y comprueban su lease, después la del vídeo, y guardan el asset y completed_heights en la misma transacción. No hay bloqueos de base de datos durante el procesamiento de medios o la subida. Los trabajos fallidos no se reencolan automáticamente y las versiones ya confirmadas permanecen.

## Modo ilimitado de licencias

`licenses.unlimited_sessions` es booleano no nullable, default false para filas y clientes anteriores. La API representa las ilimitadas con monthly_session_quota=0; en limitadas, 0 bloquea nuevas sesiones. El contador usage_monthly sigue registrando sesiones emitidas en ambos modos. La condición atómica de incremento admite unlimited_sessions o saldo inferior a cuota, con incremento y sesión dentro de la transacción original; replays no incrementan. No se usan cuotas centinela. Cambiar modo/cuota conserva contadores existentes y la selección prioriza ilimitadas únicamente entre grants válidos del mismo partner y vídeo.
