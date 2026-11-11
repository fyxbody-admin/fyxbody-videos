# Administración de FYXBODY Videos

API de backend; no hay interfaz administrativa. El contrato ejecutable se genera desde los handlers tipados: documento `admin` de OpenAPI (`/openapi/admin.json` solo en Development) y exportaciones generadas y validadas en `docs/openapi`.

## Autenticación y configuración

Cada petición a `/admin/v1` requiere `X-Admin-Key`. Configurar `Administration__ApiKeySha256` con SHA-256 hexadecimal de una clave aleatoria de al menos 32 caracteres almacenada en el gestor de secretos del operador. No existe contraseña por defecto ni bypass de desarrollo; sin hash válido el acceso está cerrado. La clave administrativa no se entrega a partners ni se usa desde navegadores. Utilizar HTTPS.

```sh
curl "$VIDEOS_API/admin/v1/partners?page=1&pageSize=25" \
  -H "X-Admin-Key: $VIDEOS_ADMIN_KEY"
```

Las listas devuelven `{items,page,pageSize,total}`; `page` comienza en 1, `pageSize` por defecto 25 y máximo 100. El orden es estable por identificador externo/código o fecha más UUID; paginación por offset, con posibles desplazamientos si hay escrituras concurrentes. Fechas ISO 8601 UTC (`Z` o `+00:00`), propiedades JSON camelCase, IDs UUID. Los `PUT` requieren el objeto completo. Errores Problem Details con `status`, `title`, `code`, `requestId`; `X-Request-Id` permite correlacionarlos.

## Partners y credenciales

| Método y ruta | Cuerpo o resultado |
|---|---|
| `POST /admin/v1/partners` | `{code,name,status}` → 201 partner |
| `GET /admin/v1/partners` | Lista paginada |
| `GET /admin/v1/partners/{partnerId}` | Partner |
| `PUT /admin/v1/partners/{partnerId}` | `{code,name,status}` → partner actualizado |
| `POST /admin/v1/partners/{partnerId}/credentials` | `{expiresAt:null}` o fecha UTC futura → 201 `{credential,apiKey}` |
| `GET /admin/v1/partners/{partnerId}/credentials` | Metadatos paginados |
| `DELETE /admin/v1/partners/{partnerId}/credentials/{credentialId}` | Metadatos con `revokedAt`; repetir conserva la fecha |

Partner: `id,code,name,status,createdAt`. `code` único, 1–64 caracteres, minúsculas/números/`_`/`-`, comienzo alfanumérico; `name` 1–200. Estados `active`, `suspended`, `revoked`. Suspender o revocar impide autenticar nuevas peticiones del partner.

Credencial: `id,keyId,createdAt,expiresAt,revokedAt`. `apiKey` tiene forma `keyId.secret` y se devuelve **una única vez** al crearla. Solo se almacena hash del secreto; ninguna consulta permite recuperarlo. Guardar inmediatamente en el gestor de secretos del backend integrador. Rotación: crear nueva credencial, actualizar backend del partner, verificar tráfico y revocar la anterior. Si se pierde la respuesta de creación, revisar metadatos y revocar la credencial no instalada antes de crear otra; no reintentar ciegamente el POST.

## Licencias y catálogos

| Método y ruta | Cuerpo o resultado |
|---|---|
| `POST /admin/v1/partners/{partnerId}/licenses` | LicenseInput → 201 licencia |
| `GET /admin/v1/partners/{partnerId}/licenses` | Licencias paginadas del partner |
| `GET /admin/v1/partners/{partnerId}/licenses/{licenseId}` | Licencia del partner |
| `PUT /admin/v1/partners/{partnerId}/licenses/{licenseId}` | LicenseInput → licencia actualizada |
| `GET /admin/v1/partners/{partnerId}/licenses/{licenseId}/catalogue` | `{videoIds:[...]}` actualmente asignados |
| `PUT /admin/v1/partners/{partnerId}/licenses/{licenseId}/catalogue` | `{videoIds:[...]}` reemplaza el catálogo completo |
| `GET /admin/v1/partners/{partnerId}/catalogue` | Vídeos efectivos, paginados |

```json
{"name":"Plan anual","status":"active","startsAt":"2026-11-01T00:00:00Z","endsAt":"2027-11-01T00:00:00Z","monthlySessionQuota":10000}
```

LicenseInput es el JSON anterior. Respuesta añade `id,partnerId,createdAt,unlimitedSessions`. Nombre 1–200, estado `active`/`suspended`/`revoked`, `endsAt > startsAt`, cuota entera no negativa. Vigencia `[startsAt, endsAt)`. Con `unlimitedSessions:false`, cuota 0 no permite nuevas sesiones. No hay borrado físico de licencias: las sesiones y consumo mantienen referencias históricas.

`unlimitedSessions` es un booleano opcional: al crear, omitido o null significa false; al actualizar mediante PUT, omitido o null conserva el modo actual para clientes anteriores. Enviar true habilita sesiones sin tope mensual y normaliza `monthlySessionQuota` a 0; se exige igualmente un entero no negativo en el payload y se ignoran sus valores positivos en ese modo. Enviar false con una cuota no negativa vuelve al modo limitado. No se usan valores centinela como -1 o números máximos.

Toda sesión nueva incrementa el contador, también en licencias ilimitadas; un replay válido no vuelve a incrementarlo. Cambiar modo o cuota no reinicia el consumo existente: si una licencia ilimitada con 100 sesiones pasa a limitada con cuota 50, queda agotada para nuevas sesiones ese mes. Las licencias ilimitadas se eligen antes que las limitadas que cubren el mismo vídeo; dentro de cada modo, vence primero la de menor endsAt y después ID. No alteran vigencia, revocación, catálogo, permisos, límites de peticiones ni expiración SAS. Una sesión repetida conserva la licencia original.

Reemplazar catálogo admite entre 0 y 1000 UUID existentes, sin duplicados; `[]` retira todos los grants. Se permiten vídeos draft para preparar una licencia antes de activarlos. Los grants retirados se marcan revocados y los existentes se reactivan al reasignarlos, preservando las sesiones históricas. El catálogo efectivo exige partner y licencia activos, vigencia actual, grant no revocado, vídeo `active` y al menos un asset `ready`. Agrega las licencias del partner sin duplicar vídeos. No filtra por saldo de cuota: la emisión comprueba la cuota.

Mutaciones de partners, credenciales, licencias y grants serializan mediante bloqueo de la fila partner dentro de una transacción `ReadCommitted`, compartido con la emisión de sesiones. Un recurso de otra cuenta nunca se modifica por una ruta del partner actual.

## Vídeos y assets

| Método y ruta | Cuerpo o resultado |
|---|---|
| `POST /admin/v1/videos` | `{externalExerciseId,title,status}` → 201 vídeo |
| `GET /admin/v1/videos` | Vídeos paginados |
| `GET /admin/v1/videos/{videoId}` | Vídeo |
| `PUT /admin/v1/videos/{videoId}` | `{externalExerciseId,title,status}` → vídeo actualizado |
| `POST /admin/v1/videos/{videoId}/assets` | AssetInput → 201 asset |
| `POST /admin/v1/videos/{videoId}/assets/upload` | Multipart `{file,language,rendition,durationMs}` → 201 asset ready |
| `GET /admin/v1/videos/{videoId}/assets` | Assets paginados |
| `GET /admin/v1/videos/{videoId}/assets/{assetId}` | Asset del vídeo |
| `PUT /admin/v1/videos/{videoId}/assets/{assetId}` | AssetInput → asset actualizado |
| `POST /admin/v1/videos/{videoId}/assets/{assetId}/preview` | Sin cuerpo → `{url,expiresAt}` |

Vídeo: `id,externalExerciseId,title,status,createdAt,updatedAt`. Identificador externo único 1–128; título 1–300; estados `draft`, `active`, `archived`.

```json
{"language":"es","rendition":"original","blobContainer":"videos","blobName":"exercises/EX-001/es-original.mp4","contentType":"video/mp4","sizeBytes":7823491,"durationMs":32000,"status":"ready"}
```

AssetInput es el JSON anterior. Respuesta añade `id,videoId,createdAt`. Estados `pending`, `ready`, `disabled`; idioma hasta 35 caracteres con etiqueta como `es`/`en-US`; rendition hasta 50. `sizeBytes` y `durationMs` no negativos, y ambos positivos para `ready`; contenido `video/mp4`. Unicidad por vídeo/idioma/rendition y por container/blob. `videoId` no se cambia.

`blobContainer` debe respetar el formato Azure (3–63 caracteres minúsculos/números/guiones, sin guiones consecutivos o extremos) y figurar en `Storage:AllowedContainers` (por defecto `videos`). `blobName` es una ruta relativa de hasta 1024 caracteres; se rechazan URLs, query, fragmentos, barras inversas, codificación porcentual y segmentos vacíos/de navegación. Estos campos son metadatos internos, no URLs públicas.

Tras existir cualquier sesión del asset, cambiar `blobContainer`, `blobName`, `language` o `rendition` devuelve 409 `asset_in_use`. Para sustituir un archivo se crea un asset con nueva rendition y se deshabilita el anterior, manteniendo su blob hasta que caduquen sus URLs. Deshabilitar evita nuevas sesiones; una SAS ya entregada puede seguir funcionando hasta su expiración. Modificaciones de vídeo/assets bloquean la fila vídeo, igual que la emisión; también actualizan `updatedAt`.

## Vista previa administrativa

`POST /admin/v1/videos/{videoId}/assets/{assetId}/preview` requiere la autenticación administrativa habitual (clave de backend o cookie de administrador con `X-CSRF-TOKEN`) y comparte su límite de peticiones. Permite revisar vídeos `draft`, `active` y `archived` cuando el asset pertenece al vídeo, está `ready`, es MP4 y tiene tamaño y duración positivos. Devuelve 404 `not_found` si no existe en ese vídeo, y 503 `video_unavailable` si no está preparado. El firmador comprueba el blob real y su tamaño/tipo; los problemas de cuenta, permisos o disponibilidad devuelven 503 `video_storage_unavailable`.

La respuesta contiene únicamente `url` (SAS HTTPS de lectura) y `expiresAt` (UTC, diez minutos desde la emisión), con `Cache-Control: no-store`. No crea sesiones ni eventos, no descuenta cuota ni registra consumo facturable de partners. La transferencia del vídeo sí puede generar costes de Azure. La URL permite leer el blob hasta caducar; no registrarla, compartirla ni persistirla. Reintentar genera otra URL corta. El portal permite medios del origen propio, URLs `blob:` para inspeccionar archivos locales y de la cuenta Azure configurada, validada sin comodines.

## Subir un MP4 desde el portal

`POST /admin/v1/videos/{videoId}/assets/upload` acepta `multipart/form-data` con un único campo de archivo `file` y los campos `language`, `rendition` y `durationMs`, sin campos adicionales ni duplicados. Requiere administrador, comparte el límite de 60 peticiones/minuto y exige `X-CSRF-TOKEN` para cookies de navegador. Devuelve 201 `AssetView` y `Location` del asset creado, con `Cache-Control: no-store`. El vídeo puede estar draft, active o archived.

Límite fijo: **200 MiB (209715200 bytes)** por archivo y 64 KiB adicionales para el multipart. El servidor modifica el límite Kestrel únicamente para esta petición antes de leerla; el resto de endpoints conserva su límite de 256 KiB. Lee con `MultipartReader` y copia por bloques de 64 KiB a un archivo temporal privado, que elimina al terminar o fallar. No carga el MP4 completo en memoria. Admite dos subidas simultáneas por instancia; una tercera recibe 429. El proceso tiene diez minutos para recibir, subir y registrar el archivo, más un plazo acotado para compensación si falla.

Se exige extensión `.mp4`, MIME `video/mp4` o `application/octet-stream`, firma inicial `ftyp`, tamaño real positivo y metadatos válidos. `durationMs` es un entero entre 1 y 2147483647; el portal lo obtiene del vídeo local y lo redondea a milisegundos. **La duración procede del cliente**: el backend no decodifica el archivo ni verifica codecs, duración real o reproducción completa. Idioma sigue las reglas del asset; para las nuevas subidas `rendition` se limita a un segmento de 1–50 letras ASCII, números, `_` o `-`.

El backend elige el contenedor de `Storage:UploadContainer`, que debe figurar en `Storage:AllowedContainers`, y genera exclusivamente la ruta `ejercicios/{videoId}/{language}/{rendition}/{assetId}.mp4`. El nombre original del archivo no forma parte de la ruta. Sube con `Content-Type: video/mp4`, identidad de backend y condición de no sobrescritura. No crea contenedores, no emite permisos de escritura al navegador y no necesita CORS. Al terminar registra el asset ready; una combinación vídeo/idioma/rendition ya existente devuelve 409. Para reemplazar un vídeo se usa otra rendition y se deshabilita la anterior.

Si falla la escritura de base de datos, se comprueba si el asset llegó a persistirse antes de eliminar el blob nuevo. Si no puede confirmarse, se conserva y se registra únicamente el AssetId y tipo de error para conciliación del operador. Los fallos al compensar no ocultan el error original; puede quedar un blob huérfano que deba retirar el operador. El archivo local se elimina siempre. Una respuesta ambigua requiere consultar los assets antes de repetir; no hay idempotencia de subida. No crea sesiones, eventos ni consumo de cuota de partners; almacenamiento y transferencia de Azure sí pueden generar costes.

Errores: 400 `invalid_request` (multipart, archivo o metadatos), 404 `not_found` (vídeo), 409 `resource_conflict` (combinación ya registrada), 413 `upload_too_large`, 429 `rate_limit_exceeded`, 503 `video_storage_unavailable` y 504 `upload_timeout`. La configuración local y permisos Azure se detallan en [operaciones](operations.md#subidas-administrativas-a-azure).

## Carga y activación

La ruta `/assets` registra blobs existentes; `/assets/upload` permite cargarlos desde el portal. No crean recursos Azure. Procedimiento del operador para el registro manual:

1. Cargar MP4 con la herramienta de carga del equipo en la cuenta `Storage:AccountName`, contenedor privado permitido y ruta inmutable; no habilitar acceso anónimo.
2. Comprobar existencia, tamaño real, `Content-Type: video/mp4`, duración y reproducción. Obtener duración con una herramienta de medios; no deducirla del nombre.
3. Registrar vídeo y asset (`pending` durante preparación), rellenar tamaño/duración verificados y pasar a `ready`.
4. Asignar catálogo y activar vídeo/licencia cuando corresponda. Probar emisión y reproducción con el backend autorizado del partner.

La emisión comprueba mediante HEAD que el blob existe, su tipo es MP4 y su tamaño coincide; registrar `ready` por sí solo no valida el almacenamiento. El firmador utiliza identidad administrada/DefaultAzureCredential y SAS de delegación de usuario con permiso de lectura y duración corta. Configurar permisos de lectura y delegación según el despliegue; no guardar claves de cuenta en partners.

## Errores, reintentos y operación

400 `invalid_request`: datos/fechas/paginación inválidos. 401 `invalid_credentials`: clave ausente o incorrecta. 404 `not_found`: recurso inexistente o no perteneciente al partner. 409 `resource_conflict`: código externo o combinación única ya registrada. 409 `asset_in_use`: locator congelado por sesiones. 422 `invalid_resource`: restricción persistente. 429 `rate_limit_exceeded`: seguir `Retry-After`. 503 `database_unavailable`: fallo temporal.

Los POST administrativos no implementan `Idempotency-Key`: antes de repetir un alta tras un resultado ambiguo consultar por metadatos/listas; en credenciales revocar la creación no instalada. PUT expresa reemplazo y puede repetirse; DELETE de credencial es revocación idempotente. El límite administrativo actual es 60 peticiones/minuto por identidad, por instancia, adicional al límite global de 600/minuto por IP e instancia. Los límites de peticiones son en memoria y no se comparten entre réplicas; las cuotas mensuales sí se coordinan en PostgreSQL. No se registran claves, secretos ni SAS en respuestas de listado; tratar el secreto de creación como confidencial y evitar logs de cuerpos.

## Consumo y exportación administrativa

`GET /admin/v1/partners/{partnerId}/usage?month=2026-11` devuelve `{month,licenses:[{licenseId,monthlySessionQuota,sessionsIssued,licenseName,unlimitedSessions}],startedSessions,completedSessions}` sin necesitar la clave partner. Incluye licencias históricas o suspendidas y cero para contadores ausentes. `month` es `YYYY-MM` (año anterior a 9999); por defecto mes UTC actual. Sesiones emitidas se asignan al mes UTC de emisión; started/completed cuentan sesiones distintas por mes UTC de `occurredAt`. La cuota y el modo ilimitado devueltos son la configuración actual de licencia, no una instantánea histórica.

`GET /admin/v1/partners/{partnerId}/usage.csv?month=2026-11` descarga CSV UTF-8 con el mismo ámbito, sin nombres, secretos, blobs ni URLs de reproducción. Columnas `record_type,month,partner_id,license_id,monthly_session_quota,sessions_issued,started_sessions,completed_sessions,unlimited_sessions`: la columna nueva `unlimited_sessions` se añade al final conservando las ocho columnas previas y contiene true/false para una fila `license`; la fila de eventos la deja vacía. Una fila `license` por licencia lleva cuota/sesiones y una única fila `partner_events` lleva los totales de eventos del partner (columnas no aplicables vacías). No sumar el total de eventos por licencia. Celdas escapadas con comillas dobles; solo se exportan IDs, mes validado y contadores generados por el servidor, sin texto libre que pueda convertirse en fórmula de hoja de cálculo.

Los informes usan varias consultas de lectura y pueden reflejar escrituras concurrentes entre consultas; para conciliación final, exportar una vez cerrado el periodo. Los contadores de sesiones y los eventos miden conceptos distintos.

## Importaciones de YouTube en segundo plano

Estas rutas son exclusivamente administrativas, usan la autenticación y límite de peticiones habituales y devuelven `Cache-Control: no-store`. Las cookies requieren `X-CSRF-TOKEN` para el POST.

| Método y ruta | Resultado |
|---|---|
| `GET /admin/v1/video-imports/capabilities` | `{enabled,availableHeights,defaultHeights,maxDurationSeconds,maxFileBytes}` |
| `POST /admin/v1/video-imports` | `{url,heights?:[1080,720,480]}` → 202 job y `Location` |
| `GET /admin/v1/video-imports` | Lista paginada `{items,page,pageSize,total}` |
| `GET /admin/v1/video-imports/{importId}` | Estado del trabajo |

`heights` acepta entre una y cuatro alturas distintas: 1080, 720, 480 y 360. Si se omite, usa 1080/720/480. La URL debe ser HTTPS de `youtube.com`, `www.youtube.com`, `m.youtube.com` o `youtu.be`, con un único ID de once caracteres en watch?v=ID, shorts/ID, embed/ID o youtu.be/ID. Se rechazan credenciales, puertos explícitos, hosts alternativos, fragmentos y listas. Los parámetros de seguimiento `si`, `feature`, `t` y `start` se aceptan y descartan; cualquier otro parámetro ajeno a `v` en watch se rechaza. La cola conserva solo `https://www.youtube.com/watch?v=ID` y el procesador recibe únicamente el ID validado.

Job: `id,youtubeId,sourceUrl,title,status,stage,progressPercent,videoId,requestedHeights,completedHeights,createdAt,updatedAt,errorCode,errorMessage`. Título, vídeo y errores pueden ser nulos. Estados: queued, running, completed y failed. Etapas: queued, probing, downloading, transcoding, uploading, completed y failed. El porcentaje representa avance aproximado; no es porcentaje exacto de bytes. `completedHeights` describe resoluciones reales ya listas, que pueden incluir la altura original inferior a las solicitadas (ajustada al entero par inferior para vídeo compatible), sin ampliarla artificialmente.

Repetir POST mientras existe un trabajo queued/running para ese ID devuelve el mismo trabajo si sus alturas solicitadas incluyen todas las de la nueva petición. Si se piden resoluciones adicionales devuelve 409 `import_in_progress`: esperar a que termine antes de solicitarlas; no amplía el trabajo en curso. La unicidad se protege también en PostgreSQL. Un fallo no se reintenta automáticamente: un nuevo POST crea otro trabajo; conserva y reutiliza assets ya listos y prepara solo las resoluciones pendientes.

El trabajador busca el ejercicio por `externalExerciseId = youtube:ID`; reutiliza el existente y conserva su título. Para un nuevo ejercicio usa el título obtenido del medio y comienza como draft. Cada versión se guarda como `es`/`{altura}p` en un blob privado nuevo con ruta `ejercicios/{videoId}/es/{altura}p/{assetId}.mp4`. No sobrescribe objetos ni duplica ejercicios; una versión pending/disabled existente con el mismo idioma/rendition produce `import_asset_conflict`. El vídeo se activa al guardar el primer asset ready. Assets y completedHeights se confirman juntos; si falla una versión posterior, las anteriores permanecen disponibles.

Desactivado por defecto (`VideoImports:Enabled=false`). POST devuelve 503 `video_imports_disabled` cuando está desactivado y 400 `invalid_request` para URL/alturas inválidas. Los fallos posteriores se consultan en el job (respuesta GET 200): códigos como import_interrupted, import_timeout, video_storage_unavailable, import_asset_conflict y errores de medios. Los mensajes son breves y no incluyen comandos, archivos locales, credenciales ni salida de herramientas. Un ID de trabajo inexistente devuelve 404 `not_found`.

Los trabajos son durables en PostgreSQL. Cada instancia procesa uno a la vez, con claim atómico `FOR UPDATE SKIP LOCKED` y lease de treinta minutos; una identidad de lease comprueba la propiedad antes de publicar versiones. El trabajo tiene como máximo veinte minutos, con cancelación propagada a descargas, procesos y Azure. Al apagar el host intenta guardar failed; si no puede hacerlo, un trabajador posterior marca failed la lease caducada, sin reencolarla. No crea sesiones de reproducción, eventos ni consumo de cuota de partners; las descargas, almacenamiento y transferencias de Azure sí tienen costes.
