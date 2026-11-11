# Integración backend de partners

El portal del partner incluye la sección **Documentación** (`/portal/#documentation`) con esta integración guiada en ES/CA/EN/AR, ejemplos copiables y descargas. La navegación requiere iniciar sesión como partner. La guía utiliza datos de ejemplo y no solicita ni muestra credenciales reales.

El backend del partner llama a FYXBODY con `Authorization: Bearer keyId.secret`. Esa credencial permite acceder al catálogo licenciado de toda la cuenta: guardarla en el gestor de secretos del servidor. El navegador recibe únicamente una URL de reproducción temporal después de que el backend del partner autentique al usuario y autorice el ejercicio. No incluir la credencial en JavaScript, URLs, aplicaciones distribuidas ni logs. No enviar nombres, emails o identificadores de pacientes a esta API.

El contrato se deriva de los handlers y DTOs: documento OpenAPI `partner` (`/openapi/partner.json` solo en Development) y exportaciones generadas en `docs/openapi`. Base de rutas `/v1`; JSON camelCase, fechas ISO 8601 UTC, IDs UUID.

## 1. Consultar catálogo

```sh
curl "$VIDEOS_API/v1/videos?page=1&pageSize=25" \
  -H "Authorization: Bearer $VIDEOS_PARTNER_KEY"
```

200:

```json
{"page":1,"pageSize":25,"total":1,"items":[{"id":"11111111-1111-4111-8111-111111111111","externalExerciseId":"EX-001","title":"Ejercicio de ejemplo","assets":[{"id":"22222222-2222-4222-8222-222222222222","language":"es","rendition":"original","contentType":"video/mp4","sizeBytes":7823491,"durationMs":32000}]}]}
```

`GET /v1/videos/{videoId}` devuelve el mismo objeto individual. `page` empieza en 1, `pageSize` por defecto 25, máximo 100. Las páginas usan offset y pueden desplazarse si el catálogo cambia; deduplicar por UUID al sincronizar. Catálogo efectivo: partner activo, licencia activa y vigente, grant no revocado, vídeo activo y asset ready. No se exponen rutas Blob ni SAS al listar. La disponibilidad del catálogo no asegura saldo de cuota.

## 2. Emitir sesión desde el backend

```sh
curl "$VIDEOS_API/v1/videos/$VIDEO_ID/playback-sessions" \
  -X POST -H "Authorization: Bearer $VIDEOS_PARTNER_KEY" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $PLAYBACK_ATTEMPT_ID" \
  --data '{"videoAssetId":"22222222-2222-4222-8222-222222222222"}'
```

200: `{sessionId,videoId,videoAssetId,expiresAt,url,replayed}`. `url` es HTTPS con permiso de lectura para un blob y expiración corta (5 minutos por defecto; el servidor puede configurar hasta 15 y limita por vigencia de licencia). Mantener la respuesta fuera de cachés y logs. Devolver al navegador solo los datos necesarios y `Cache-Control: no-store`.

Generar un `Idempotency-Key` de 1–128 caracteres por intento lógico de reproducción. Un UUID sin datos personales es adecuado. Si hay timeout o 503, repetir la misma ruta, asset y clave. La repetición válida devuelve la misma sesión y fecha de expiración con `replayed:true`, sin consumir otra cuota. Reutilizar la clave con distinto vídeo/asset devuelve 409; una sesión ya caducada también devuelve 409. Si se acorta la vigencia de la licencia por debajo de la expiración original, la repetición devuelve 409 `session_license_changed`; solicitar una nueva sesión con otra clave. Para un nuevo intento tras caducar, generar otra clave. No reutilizar una clave para varios usuarios o intentos distintos.

La autorización actual se comprueba también al repetir: revocar credencial, partner, licencia, grant, vídeo o asset puede impedir la repetición. No se extiende una SAS por repetir una petición. Una URL ya entregada es reutilizable hasta `expiresAt`, incluso tras revocar acceso; la SAS es un bearer temporal. La API no implementa DRM, URL de un solo uso ni corte instantáneo de un stream ya emitido. No sobrescribir el blob de un asset con sesiones. Una sesión emitida incrementa el contador aunque el usuario nunca reproduzca el vídeo y aunque la licencia sea ilimitada; repetir la misma sesión no vuelve a contar.

El backend del partner debe comprobar quién solicita la reproducción, su acceso al ejercicio, límites propios y CSRF cuando use cookies. El secreto de cada credencial tiene 256 bits de entropía; FYXBODY conserva únicamente su hash SHA-256. La API no conoce esos usuarios y no reemplaza esa autorización.

## 3. Reproductor y eventos

Ejemplo documental en el frontend **del partner**, detrás de su propio backend autenticado:

```js
const response = await fetch(`/api/exercises/${exerciseId}/playback`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
  body: JSON.stringify({ attemptId: crypto.randomUUID() })
});
if (!response.ok) throw new Error('No se pudo abrir el vídeo');
const { url, sessionId, expiresAt } = await response.json();
videoElement.src = url;
// Enviar started/progress/completed/failed al backend propio con sessionId.
// El backend valida pertenencia de sessionId y reenvía el lote a FYXBODY.
```

Este fragmento ilustra la integración; este proyecto no incluye el frontend propio del partner ni ese endpoint. El partner debe mapear `exerciseId` a vídeo/asset del catálogo en el servidor y mantener el mismo `attemptId` al reintentar. Renovar mediante una nueva sesión cuando el producto lo necesite y la anterior haya expirado.

```sh
curl "$VIDEOS_API/v1/playback-events" -X POST \
  -H "Authorization: Bearer $VIDEOS_PARTNER_KEY" -H 'Content-Type: application/json' \
  --data '{"events":[{"sessionId":"33333333-3333-4333-8333-333333333333","eventKey":"attempt-started-1","kind":"started","positionMs":0,"occurredAt":"2026-11-05T12:00:00Z"}]}'
```

Lote obligatorio de 1–50 eventos. Tipos `started`, `progress`, `completed`, `failed`; posición entera no negativa y dentro de duración del asset, timestamp ISO 8601 (enviar UTC) entre creación y expiración de la sesión, sin más de 30 segundos de adelanto respecto al servidor. Los eventos pueden recibirse hasta 24 horas tras la expiración de la sesión. `eventKey` 1–128, único por sesión: mantenerlo al reintentar el mismo evento. 200 `{accepted,duplicates}`; un duplicado idéntico no suma. Reutilizar clave con contenido diferente devuelve 409 y rechaza el lote completo. El backend del partner conserva y reintenta el lote; evitar llamar directamente desde el navegador con la credencial.

Los eventos reflejan telemetría reportada por el integrador, no una prueba independiente de visualización real. No añadir campos de pacientes.

## 4. Consultar consumo mensual

```sh
curl "$VIDEOS_API/v1/usage?month=2026-11" \
  -H "Authorization: Bearer $VIDEOS_PARTNER_KEY"
```

200: `{month,licenses:[{licenseId,monthlySessionQuota,sessionsIssued,licenseName,unlimitedSessions}],startedSessions,completedSessions}`. `month` usa `YYYY-MM`, por defecto el mes UTC actual. La cuota se aplica por licencia y mes UTC al emitir sesiones cuando `unlimitedSessions` es false. En ese modo, cero impide emitir. Con `unlimitedSessions:true`, no hay tope mensual y `monthlySessionQuota` es 0; el contador sessionsIssued sigue aumentando por cada sesión nueva. Un replay válido no lo incrementa. Cambiar modo o cuota no reinicia el consumo. `startedSessions`/`completedSessions` cuentan sesiones distintas con esos eventos en el mes de `occurredAt` UTC, no el número de eventos. Pueden diferir del mes en que se emitió la sesión. La revocación de un grant no borra sesiones ni consumo histórico.

## Errores y límites

Los errores usan Problem Details (`application/problem+json`), con `status,title,code,requestId`; el header `X-Request-Id` facilita soporte. Ejemplo:

```json
{"type":"about:blank","title":"Valid partner credentials are required.","status":401,"code":"invalid_credentials","requestId":"example-request"}
```

| HTTP | Acción del integrador |
|---|---|
| 400 | Corregir cuerpo, UUID, paginación, mes, evento o clave de idempotencia |
| 401 | Revisar credencial, expiración y estado del partner; no reintentar indefinidamente |
| 403/404 | Revisar licencia, vigencia y acceso al vídeo/asset; no revelar datos de otras cuentas |
| 409 | Conflicto de idempotencia, sesión expirada o clave de evento con otro contenido |
| 429 | Respetar `Retry-After`, añadir backoff y jitter |
| 503 | Fallo temporal de base de datos/almacenamiento: reintentar de forma limitada; conservar claves de sesión/evento |

El límite actual de partner es 300 peticiones/minuto por cuenta, por instancia, adicional al global de 600/minuto por IP e instancia. Estos límites de peticiones se mantienen en memoria y no se comparten entre réplicas; la cuota mensual se aplica globalmente mediante PostgreSQL. El agotamiento de cuota mensual también puede producir 429; revisar su `code` y consumo antes de reintentar. `Retry-After` se devuelve en el límite de peticiones y en el agotamiento de cuota mensual; para esta última indica el tiempo hasta el siguiente mes UTC. No asumir que un reintento rápido resuelve cuota mensual. Las respuestas no incluyen actualmente headers de saldo de rate limit. No reintentar automáticamente todos los 4xx. Usar timeout y reintentos acotados con la misma clave únicamente cuando la operación lo permita.

Ejemplo de cliente backend sin dependencias: [examples/backend-client.mjs](../examples/backend-client.mjs). Las credenciales y SAS de los ejemplos deben ser valores locales de prueba y nunca publicarse.

Las licencias ilimitadas tienen prioridad sobre licencias limitadas superpuestas del mismo partner y catálogo, conservando vigencia, revocación, rate limits e idempotencia. /usage refleja el modo actual, no una instantánea histórica.

## Idioma de las respuestas

Enviar `Accept-Language: en`, `es`, `ca` o `ar` para elegir el idioma de los mensajes humanos; se admiten variantes regionales y preferencias con pesos. La API confirma la elección en `Content-Language` y añade `Vary: Accept-Language`. Sin una preferencia compatible, utiliza español. Gestionar los errores por `code` y estado HTTP: los códigos, claves JSON, estados, IDs, fechas ISO y CSV permanecen estables. Los nombres y títulos del catálogo son contenido guardado; esta cabecera no traduce ni dobla los vídeos. Véase [localización HTTP](backend-localization.md).

## Descargas del portal y mantenimiento

En producción, el portal sirve un contrato estático para el partner en `/portal/integration/partner-openapi.json` y el cliente backend de ejemplo en `/portal/integration/backend-client.mjs.txt` (descargado como `.mjs`). El contrato solo contiene operaciones `/v1`; su servidor relativo `/` usa el origen del entorno que lo sirve. Al importarlo desde un archivo en una herramienta externa, configurar la URL de API proporcionada por FYXBODY. No depende de los endpoints dinámicos `/openapi`, que solo se habilitan en Development.

Al cambiar el contrato, regenerar `docs/openapi/partner.json` desde el documento real y actualizar la copia del portal normalizando únicamente `servers` a `[{"url":"/"}]`. Si cambia `examples/backend-client.mjs`, sincronizar la copia descargable. `node --test tests/partner-doc-downloads.test.mjs` comprueba ambas copias y evita introducir operaciones administrativas.


## Find a video by exercise reference

Call `GET /v1/videos?externalExerciseId=EX-001` with your partner bearer token.
URL-encode the exercise reference when building the query string. The match is
exact and case-sensitive. The response keeps the catalogue format, with one item
if the video is available under your licence, or `total: 0` and `items: []` otherwise.
Use the returned item’s `id` to create a playback session.
