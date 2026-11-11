# Verificación de catálogo e integración — 2026-11-05

Esta entrega amplía la base documentada en verification-2026-11-05.md.

## Implementado

- Administración de clientes, claves, licencias, catálogo, vídeos y assets.
- Autenticación separada admin/partner sin credenciales predeterminadas ni bypass.
- Catálogo efectivo por cliente; unión de licencias vigentes sin duplicar vídeos.
- Revocación de grants conservando referencias históricas.
- Sesiones con cuotas transaccionales, idempotencia, SHA-256 de payload y permisos temporales Azure.
- Firma SAS de delegación de usuario, lectura/HTTPS/expiración; HEAD del blob antes de firmar.
- Eventos por lotes deduplicados, estadísticas mensuales y exportación CSV administrativa.
- Límites por IP previos a autenticación y por identidad después de autorización.
- Documentación de partner/administración, contratos OpenAPI y ejemplo backend Node.

## Evidencia ejecutada

- Build: cero advertencias y cero errores.
- 40 pruebas .NET correctas; cero omitidas. PostgreSQL 16 local real; firmador simulado.
- Tres pruebas Node correctas: no fuga cross-origin, idempotencia/reintentos explícitos y Retry-After.
- Pruebas de concurrencia: diez solicitudes con cuota tres no la superan; misma clave concurrente consume una; dos clientes pueden firmar simultáneamente el mismo vídeo.
- Fallo de firmador revierte sesión y consumo.
- Histórico conservado tras retirada y reasignación del catálogo; assets con sesiones no pueden cambiar de locator.
- Revocación y límites por cliente; evento ajeno rechazado; precisión de siete dígitos normalizada a microsegundos PostgreSQL para reintentos.
- Licencia acortada impide replay que exceda su nuevo vencimiento.
- Peticiones anónimas no agotan la partición administrativa.
- Flujo HTTP completo: alta cliente/clave/vídeo/asset/licencia, asignación, consulta, sesión, replay, cuota, eventos, informe y revocación.
- OpenAPI partner: cinco rutas/operaciones. Administración: catorce rutas/veinticuatro operaciones. Ambos contratos 3.1.1 exportados desde el proceso local y validados con openapi-spec-validator.
- NuGet sin vulnerabilidades conocidas en las fuentes consultadas.
- Publicación Release correcta en carpeta temporal; no desplegada.

## Revisión independiente

Revisión de seguridad y concurrencia detectó y se corrigieron: límite administrativo previo a autenticación efectiva, replay con licencia acortada, bloqueo exclusivo global de vídeo y duplicación de cabecera OpenAPI. Se añadieron pruebas de regresión y validación del contrato generado.

## Azure TEST

Se aplicó 20261105145124_RevokeCatalogGrants usando la credencial de migración previamente autorizada, exclusivamente en fyxbody_videos/esquema videos. Historial verificado con InitialVideoLicensing y RevokeCatalogGrants. Se mantienen nueve tablas de negocio; ninguna tabla de usuario fuera del esquema. No se cambiaron usuarios/permisos ni otras bases. No se cargaron vídeos ni se crearon clientes comerciales. Credenciales solo en memoria, fuera del proyecto.

## Límites y siguiente despliegue

No hay frontend, App Service nuevo, cuenta Blob configurada, vídeos cargados ni repositorio remoto. La firma mediante SDK está implementada, pero la reproducción contra Blob real y los permisos de identidad administrada requieren validación cuando se provisionen esos recursos. Los tests usan URLs ficticias, no demuestran una reproducción real de Azure.

Los límites de peticiones son locales a cada instancia; las cuotas y sesiones se coordinan en PostgreSQL. Una SAS ya entregada permanece válida hasta expirar. Telemetría del partner no prueba visualización real. No hay política automática de purga ni historial de tarifas/cuotas inmutable. Configuración y requisitos de operación en operations.md.
