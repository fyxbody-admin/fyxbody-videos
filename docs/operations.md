# Operación y despliegue

## Configuración

| Variable | Uso |
|---|---|
| ConnectionStrings__Videos | PostgreSQL, base exacta fyxbody_videos; secreto/Key Vault |
| Administration__ApiKeySha256 | SHA-256 hexadecimal de clave administrativa aleatoria de al menos 32 caracteres |
| Storage__AccountName | Nombre de cuenta Blob, 3–24 letras minúsculas/números |
| Storage__AllowedContainers__0 | Primer contenedor autorizado; por defecto videos |
| Playback__SessionLifetimeSeconds | Duración de sesión/SAS, 300 por defecto, rango 30–900 |
| ASPNETCORE_ENVIRONMENT | Production en App Service; Development local |

Conexión de ejemplo con marcadores, nunca una contraseña real versionada:

```text
Host=psql-fyxbody2-test-zpfyqf.postgres.database.azure.com;Database=fyxbody_videos;Username=<runtime-role>;Password=<secret>;SSL Mode=VerifyFull;Timeout=15;Command Timeout=30
```

La API y la factory EF rechazan otro nombre de base y exigen validación TLS completa para Azure. No se ejecutan migraciones al arrancar. `Database:Host/Name` en appsettings solo documentan el destino; la conexión real procede de ConnectionStrings:Videos.

Sin clave administrativa configurada, administración está cerrada. Generar y custodiar una clave de operador de alta entropía; el hash hexadecimal se obtiene con SHA-256 sobre los bytes UTF-8 de la clave. El secreto original va al gestor del operador y el hash a configuración. No entregar a partners. Las claves partner se generan a través de la administración y se devuelven solo una vez.

La clave administrativa compartida es para operadores de confianza y esta primera versión; no proporciona identidad individual de cada operador. Mantener trazabilidad del acceso en la plataforma y limitar el acceso administrativo por red cuando se despliegue. Las rotaciones se hacen cambiando la clave/hash sin modificar credenciales partner.

## App Service y PostgreSQL

Crear posteriormente aplicación Linux .NET 10 LTS independiente en el plan acordado. Publicación:

```bash
dotnet publish src/FyxBody.Videos.Api --configuration Release --output ./artifacts/publish
```

Compartir plan comparte CPU/memoria; medir especialmente memoria antes del piloto. Cada instancia tiene sus propios límites de peticiones y caché de clave de delegación SAS; las cuotas e idempotencia están en PostgreSQL y se coordinan entre instancias.

Health Check: `/health/ready`. La liveness `/health/live` comprueba proceso. La readiness actual comprueba conexión y migraciones pendientes; no certifica almacenamiento Blob ni detecta toda modificación manual del esquema.

Configurar HTTPS Only en la plataforma y la terminación TLS del proxy. Si se utiliza un proxy adicional, configurar sus forwarded headers únicamente para proxies confiables; la aplicación no confía en X-Forwarded-For arbitrarios. No habilita CORS para navegadores: el partner llama desde su backend. El límite previo a autenticación usa la IP de conexión observada; verificar agrupación de IPs detrás de proxy al desplegar.

Usuario PostgreSQL de ejecución separado del ejecutor de migración. Conceder únicamente CONNECT a la base, USAGE al esquema, SELECT/INSERT/UPDATE a las nueve tablas de negocio y SELECT al historial EF; sin permisos DDL ni de otras bases. No se han creado roles nuevos ni ampliado permisos en esta entrega. Las futuras migraciones deben revisar grants de tablas nuevas. Mantener copia/restauración según política del servidor.

## Azure Blob Storage

- Cuenta y contenedores privados, sin acceso anónimo; no servir ficheros desde App Service.
- Identidad administrada propia del nuevo App Service. Lectura del contenedor mediante Storage Blob Data Reader y obtención de claves de delegación mediante Storage Blob Delegator a nivel de cuenta (o rol personalizado equivalente de mínimo privilegio).
- DefaultAzureCredential usa la identidad del entorno; localmente puede usar identidad Azure CLI autorizada. No configurar claves de cuenta para partners.
- Cada asset debe apuntar a un MP4 real, con Content-Type video/mp4 y tamaño idéntico al registrado. La emisión hace HEAD antes de firmar.
- No sobrescribir objetos ya utilizados: crear otra ruta/asset para otra versión. Si el reproductor usa fetch/crossorigin, configurar CORS del almacenamiento solo para los orígenes del partner y métodos/headers necesarios.

Se genera SAS de delegación de usuario, solo lectura, un único blob, HTTPS y expiración corta, limitada por licencia. La clave de delegación se cachea hasta una hora. Las URLs ya entregadas pueden utilizarse hasta su expiración pese a revocar credencial/licencia/grant. La revocación bloquea nuevas emisiones y replays; no corta enlaces ya emitidos. No hay DRM ni garantía contra copias.

La subida y la lectura privadas se han verificado desde el entorno local contra stfyxbody2testzpfyqf/video el 2026-11-05; ver verification-video-upload-2026-11-05.md. El futuro App Service aún necesita su propia identidad y permisos. El modo normal falla con 503 si no hay configuración/identidad/blob válido; no existe modo de prueba que devuelva enlaces públicos.

## Métricas y soporte

La API entrega X-Request-Id y errores Problem Details. No habilitar logs de cuerpos, Authorization, X-Admin-Key ni URLs SAS en API, proxy, reproductor o herramientas de analítica. No registrar datos de pacientes. Configurar recogida de ILogger en la plataforma, alertas de errores/latencia, uso de PostgreSQL, capacidad del plan y transferencia Blob. No se ha creado un recurso de monitorización adicional.

Los eventos son telemetría del integrador; las sesiones son autorizaciones consumidas, no visualizaciones probadas. El tráfico facturado se obtiene de Azure, no de esos contadores. Los informes mensuales muestran cuota actual y no guardan una tarifa o cuota histórica inmutable. Los agregados de eventos son consultas; no hay trabajos en segundo plano.

La retención de sesiones/eventos no se elimina automáticamente en esta versión para preservar historial e idempotencia. Antes de activar un plazo de retención, definir archivo y conciliación, y respetar FK/eventos, ventanas de reintento e historial de facturación. Vigilar crecimiento e índices a medida que aumente el volumen.

## Contratos y ejemplos

La fuente de contrato son endpoints/DTOs tipados. Tras cambiar la API, regenerar docs/openapi/partner.json y admin.json desde `/openapi/{document}.json` en Development y validarlos con OpenAPI 3.1. No generar esos contratos desde datos reales. El cliente Node de examples es un ejemplo backend sin dependencias; impide enviar la credencial fuera del origen y no reintenta automáticamente operaciones ambiguas.

## Panel, cookies y claves de protección

El panel `/portal/` se sirve desde wwwroot dentro del mismo App Service. No necesita otro hosting, Node runtime ni CORS. Acceso personal por usuario/contraseña; la clave administrativa API sigue disponible para automatizaciones de confianza, pero no administra usuarios del portal. No pegar claves API en el navegador.

Sesiones PortalCookie: HttpOnly, SameSite Strict, ocho horas sin renovación deslizante; Secure y prefijo __Host- en producción. CSRF obligatorio en login y escrituras autenticadas por cookie; token desde /portal/api/session. Cambio obligatorio de contraseñas temporales. Login limitado por IP y bloqueo persistente por usuario tras cinco fallos durante quince minutos. Las claves de integración siguen independientes.

Data Protection usa ApplicationName FyxBody.Videos.Portal. Al desplegar varias instancias, persistir y compartir el mismo key ring únicamente entre instancias de esta aplicación, protegerlo en reposo y revisar permisos. El almacenamiento local del perfil de desarrollo no constituye una configuración de claves de producción. No borrar claves activas al desplegar. Configurar HTTPS Only y validar cookies/CSRF a través del proxy real antes de publicar.

El usuario PostgreSQL de ejecución necesita también SELECT/INSERT/UPDATE sobre videos.portal_users; esos permisos no se han concedido en esta entrega. No usar la cuenta administrativa de migración como identidad de ejecución. El alta inicial admin se realizó por el comando bootstrap autorizado; credencial temporal entregada fuera del repositorio.

## Subidas administrativas a Azure

Para cargar MP4 desde el portal, configurar en el proceso del backend:

```sh
Storage__AccountName=stfyxbody2testzpfyqf
Storage__AllowedContainers__0=video
Storage__UploadContainer=video
```

El contenedor `video` debe existir y mantenerse privado. No habilitar acceso público ni guardar claves de cuenta o SAS en frontend. El nombre configurado se valida y el backend genera rutas inmutables como `ejercicios/{videoId}/{language}/{rendition}/{assetId}.mp4`. Las configuraciones genéricas existentes con `videos` siguen siendo válidas para el registro manual; configurar explícitamente UploadContainer para habilitar subidas.

La identidad que ejecuta el backend necesita **Storage Blob Data Contributor** sobre el ámbito de almacenamiento apropiado para escribir, leer y compensar eliminando únicamente blobs nuevos. La vista previa con SAS de delegación también exige la acción de generación de clave de delegación al ámbito adecuado de cuenta (por ejemplo, Storage Blob Delegator sobre la cuenta si Contributor se asigna solo al contenedor). Usar identidad administrada en despliegues y `DefaultAzureCredential`; asignar roles mediante el procedimiento habitual de infraestructura. El endpoint no crea cuentas ni contenedores.

Para desarrollo local con Azure CLI, seleccionar la sesión y tenant de FYXBODY correctos antes de arrancar. Puede establecerse `AZURE_TENANT_ID` al tenant correspondiente y `AZURE_TOKEN_CREDENTIALS=AzureCliCredential` para que DefaultAzureCredential use la identidad CLI local. No modificar silenciosamente la suscripción predeterminada global; comprobar acceso a la suscripción de FYXBODY explícitamente. Una sesión válida en otro tenant no garantiza acceso a este almacenamiento. Si Azure CLI tiene varias cuentas o una suscripción predeterminada de otro tenant, establecer `Storage__AzureCliSubscription` al identificador o nombre de la suscripción FYXBODY **solo en Development**. Con esta opción, ambos clientes (subida y firma de lectura) usan explícitamente AzureCliCredential con la suscripción indicada, equivalente a `az account get-access-token --subscription ...`, sin cambiar la suscripción global de CLI. La suscripción selecciona el tenant; no se combina TenantId en AzureCliCredential porque CLI no acepta ambos parámetros. Fuera de Development, o con el valor vacío, se conserva DefaultAzureCredential y su configuración habitual mediante `AZURE_TENANT_ID`/identidad administrada. Esta opción no modifica la identidad administrada de producción.

El archivo se recibe por API (máximo 200 MiB más 64 KiB multipart). Ajustar proxy/ingress/IIS a un límite de petición al menos **209780736 bytes** y a un timeout compatible con los diez minutos del endpoint; un límite anterior en el proxy no puede ampliarlo Kestrel. Mantener HTTPS y el resto de rutas con sus límites habituales. No registrar cuerpos multipart, credenciales ni URLs SAS. Ver [límites por petición de ASP.NET Core](https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads#per-request-body-size-limits).

Reservar espacio en disco temporal para dos archivos de 200 MiB por instancia, más margen; `ASPNETCORE_TEMP` puede indicar un directorio existente privado, fuera de wwwroot y sin ejecución. Los archivos temporales usan permisos de propietario y se eliminan al disponerlos; después de una terminación abrupta del proceso revisar residuos `fyxbody-video-*.tmp` en ese directorio. No borrar archivos de procesos activos. La transferencia Azure usa bloques máximos de 4 MiB con concurrencia 2 y reintentos de SDK limitados; el timeout de red es 30 segundos y el plazo global de subida diez minutos. La compensación tiene un plazo separado de 30 segundos y puede requerir conciliación manual si falla.

Usar un MP4 real y comprobar su reproducción. La validación de extensión, MIME y ftyp no verifica codecs ni duración real: durationMs sigue siendo metadato del navegador. Un usuario administrador debe comprobar la vista previa antes de activar un vídeo nuevo. Las pruebas automáticas usan almacenamiento simulado y PostgreSQL temporal aislado; no suben archivos a Azure.

## Trabajador de importaciones YouTube

Aplicar primero la migración `20261105170851_YouTubeImportJobs` mediante un único ejecutor. La API nunca ejecuta migraciones al arrancar. `migrations/004_youtube_import_jobs.sql` es acumulativo e idempotente; no se ha aplicado a Azure como parte del desarrollo de esta funcionalidad. Hacer copia y revisar el destino antes de aplicarlo a una base con datos.

Activación explícita del proceso API:

```sh
VideoImports__Enabled=true
VideoImports__MaxConcurrentDownloads=1
VideoImports__MaxConcurrentTranscodes=1
VideoImports__MaxJobMinutes=20
VideoImports__MaxDurationSeconds=600
```

Enabled es false por defecto. MaxJobMinutes admite 1–20 y MaxDurationSeconds 1–600; las salidas tienen el límite fijo de 200 MiB por archivo. Configurar también Storage y credenciales como en las subidas administrativas. Las herramientas locales `yt-dlp`, `ffmpeg` y `ffprobe` deben estar disponibles para el usuario del proceso; pueden elegirse mediante `VideoImports__YtDlpPath`, `VideoImports__FfmpegPath` y `VideoImports__FfprobePath`. `VideoImports__TemporaryDirectory` debe ser un directorio existente privado fuera de wwwroot. Consultar el Dockerfile del servicio para una imagen con herramientas incluidas y usuario sin privilegios.

Reservar al menos 1 GiB de espacio temporal de importación por instancia más el margen de subidas manuales y las herramientas. El procesador limita el espacio de cada trabajo y elimina sus temporales al completarlo, fallar o cancelarse; revisar residuos después de una caída abrupta. Un reinicio no reanuda automáticamente el trabajo interrumpido. El usuario debe volver a enviar la importación fallida; las versiones confirmadas permanecen y se reutilizan.

La cola reclama filas con SKIP LOCKED: cada instancia trabaja secuencialmente y varias instancias reclaman trabajos diferentes. La lease de treinta minutos no se renueva y supera el plazo máximo del trabajo. No reencolar ni editar manualmente una lease running para forzar una segunda ejecución. Los trabajos caducados se marcan failed antes de reclamar otros; las escrituras del trabajador comprueban el identificador de lease para impedir que una ejecución antigua publique después de perderla. Un fallo de PostgreSQL durante el cierre puede dejar el trabajo running hasta que caduque su lease.

Las transacciones de catálogo y confirmación de versiones son breves; no mantienen bloqueos durante descargas, transcodificación o transferencias Azure. Si la confirmación falla, se verifica si el asset llegó a persistir antes de compensar el blob nuevo. Una comprobación o eliminación fallida puede dejar objetos que requieran conciliación del operador; los logs indican ImportId/AssetId y códigos, sin guardar URLs SAS ni salida de procesos. Conservar los assets confirmados en fallos parciales.

No se configura cuota comercial ni facturación de partners mediante estos trabajos. Usar únicamente fuentes que el administrador esté autorizado a importar y comprobar las versiones antes de concederlas a un catálogo.


### Herramientas de importación y App Service

La imagen del Dockerfile incluye .NET 10, FFmpeg, Python con `yt-dlp[default]` fijado a 2026.10.26 y Node 22 para resolver los requisitos JavaScript del extractor. Ejecuta el servicio con el usuario sin privilegios de la imagen base. Las variables de rutas y `VideoImports__JavaScriptRuntime=node:/usr/local/bin/node` están incluidas en la imagen; `VideoImports__Enabled` sigue desactivado hasta configurarlo explícitamente. No se instala ni actualiza software durante una petición. Mantener las herramientas actualizadas mediante una nueva construcción y pruebas del flujo, porque YouTube puede cambiar.

Activa Always On en el App Service para mantener la cola activa aunque nadie visite el portal. Mantén una instancia del worker en TEST para controlar el uso de CPU y disco compartidos con los otros servicios del plan.

`VideoImports__MaxConcurrentDownloads` y `VideoImports__MaxConcurrentTranscodes` admiten valores de 1 a 5 y ambos se inician en 1. Con esos valores se puede descargar el siguiente vídeo mientras se convierte o se sube el anterior. Para probar más capacidad, aumenta primero las descargas a 3 y deja la conversión en 1. FFmpeg usa dos hilos por conversión.

Los trabajos y la pausa de cinco minutos se guardan en PostgreSQL. Si YouTube limita las descargas, la cola espera y libera una sola prueba de descarga al vencer el plazo. Para trasladar la ejecución a Azure, detén el worker anterior y arranca el nuevo con la misma base. No vuelvas a enviar el lote que ya está en cola.

Los procesos de medios no reciben las variables de conexión de PostgreSQL ni las credenciales Azure de la API. No utilizan shell, ignoran configuración y plugins locales de yt-dlp y solo reciben la URL canónica de YouTube. FFmpeg/ffprobe procesan archivos locales con protocolos file/pipe. El stdout/stderr está acotado; no se publica la salida de herramientas, URLs de descarga firmadas ni datos temporales en los errores del portal.

Construcción local, sin publicación:

```sh
docker build -t fyxbody-videos:youtube-import .
```

La imagen se ha construido y sus herramientas se han comprobado como UID 1654. El futuro despliegue requiere aplicar también la migración 004 y permisos de lectura/escritura sobre `videos.video_import_jobs` a la identidad de base de datos de ejecución. No se han aplicado estos cambios a PostgreSQL Azure.

## Licencias con sesiones ilimitadas

La migración `20261105183903_UnlimitedLicenseSessions` añade `licenses.unlimited_sessions` booleano no nullable con default false; las licencias anteriores continúan limitadas sin cambiar sus cuotas ni contadores. `migrations/005_unlimited_license_sessions.sql` es acumulativo e idempotente. Aplicar con un único ejecutor, tras copia y verificación del destino; no hay migración automática en el arranque. La validación del desarrollo se ejecuta únicamente contra PostgreSQL local temporal y no aplica esta migración a Azure.

Habilitar por API administrativa con `unlimitedSessions:true`; el servicio guarda cuota 0 como representación explícita. Para volver al modo limitado, enviar false y una cuota no negativa. Los PUT de clientes anteriores que omiten el flag mantienen el modo existente. Un cambio no reinicia counters: comprobar sessionsIssued del mes actual antes de reducir cuota o pasar a limitado. Exportaciones JSON y CSV incluyen el modo actual; CSV conserva sus ocho columnas anteriores y agrega unlimited_sessions al final. No interpretar cuota 0 como bloqueo sin leer el flag.

Las sesiones ilimitadas siguen contándose y las operaciones concurrentes se coordinan en PostgreSQL con la misma transacción, idempotencia y rollback que las limitadas. Si falla la firma SAS, no se confirma sesión ni incremento. Una licencia ilimitada no amplía el catálogo ni la vigencia y no elimina rate limits, costes de Azure o requisitos de autenticación. No se crean partners o licencias comerciales automáticamente; sus fechas y catálogo se configuran explícitamente cuando se disponga de las condiciones correspondientes.

Antes de revertir esta migración, detener emisiones y convertir las licencias ilimitadas a condiciones limitadas explícitas y revisadas; eliminar el flag deja cuota 0 y podría bloquear nuevas sesiones. Preferir una corrección hacia delante en bases con datos y conservar copia del modo y consumo histórico.
