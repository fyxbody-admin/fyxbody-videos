# Verificación inicial — 2026-11-05

Registro histórico de la fase base. Para el estado actual, consultar verification-catalog-api-2026-11-05.md.

## Completado

- Solución independiente net10.0, SDK local 10.0.400.
- Compilación con cero advertencias y cero errores.
- 11 pruebas correctas, sin omisiones, incluyendo PostgreSQL 16 real en Docker.
- Integridad entre cliente/licencia, catálogo autorizado y archivo del vídeo; deduplicación de sesiones y eventos; cuotas no negativas y mes UTC coherente.
- Migración `20261105143156_InitialVideoLicensing` aplicada y revertida localmente; reaplicada correctamente.
- SQL idempotente ejecutado dos veces sobre la base local sin errores.
- EF no detecta diferencias entre modelo y migración.
- Publicación Release correcta; endpoints raíz, liveness, readiness y OpenAPI verificados con HTTP 200 contra PostgreSQL local.
- Consulta de vulnerabilidades NuGet sin paquetes vulnerables conocidos en las fuentes consultadas.

## Azure TEST: migración aplicada y verificada

Se verificó que `fyxbody_videos` existía vacía en `psql-fyxbody2-test-zpfyqf.postgres.database.azure.com`, PostgreSQL 16.15. El primer intento con la credencial de la videoteca se detuvo por falta de permisos CREATE SCHEMA y no creó tablas.

Tras autorización explícita del usuario para localizar y utilizar una credencial de migración de TEST, se resolvió la credencial administrativa existente en Key Vault únicamente en memoria. La conexión utilizó TLS VerifyFull y el nombre exacto de base fyxbody_videos. No se cambiaron permisos, usuarios ni otras bases. No se guardaron contraseñas en el proyecto.

Se aplicó `20261105143156_InitialVideoLicensing` y se verificaron las nueve tablas de negocio en el esquema videos: videos, video_assets, partners, partner_credentials, licenses, license_videos, playback_sessions, playback_events y usage_monthly. El historial EF contiene exactamente esa migración. No hay tablas de usuario fuera del esquema videos en esta base.

Una segunda ejecución de la actualización devolvió: "No migrations were applied. The database is already up to date."

La credencial administrativa se utilizó solo para la migración y verificación; no se ha configurado como conexión de ejecución de una API. La creación/configuración de una identidad PostgreSQL limitada para el futuro App Service queda pendiente en la fase de despliegue.

## Fuera de esta entrega

No se creó App Service, almacenamiento Blob, repositorio remoto, credenciales de partners ni catálogo comercial. Tampoco se implementaron autenticación B2B, firma SAS, endpoints comerciales, reserva transaccional de cuotas o recolección de analítica. Son los siguientes módulos sobre este modelo.
