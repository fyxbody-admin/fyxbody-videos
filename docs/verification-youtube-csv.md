# Verificación de importación CSV de YouTube

- Backend: 177/177 pruebas correctas (155 previas y 22 nuevas), sin omisiones, PostgreSQL temporal separado 55440 retirado al terminar. Incluye parser, encolado atómico, metadatos persistidos, conflictos, concurrencia y permisos.
- Revisión independiente: sin bloqueadores pendientes. Corregido el límite Kestrel específico de CSV; verificado en servidor real un cuerpo válido de 600060 bytes, un chunked de más de 2 MiB devuelve 413 y el resto de endpoints conserva su límite anterior.
- Frontend y clientes: 39/39 pruebas correctas, incluidos lectura UTF-8, BOM, plantilla, límites, snapshots, respuestas tardías, doble envío y rechazo de lotes inválidos.
- Entorno local: copia pg_dump privada en .local/backups antes de migrar. Aplicada únicamente en PostgreSQL local 55439 la migración aditiva 20261105224719_CsvImportMetadata; Azure no modificado. API reiniciada en 5234 y readiness 200.
- HTTP local real: preview válido con referencia/título personalizados y segunda fila opcional; commit de CSV inválido no encola; origen legacy youtube: compatible; partner recibe 403 en endpoints administrativos.
- Navegador real: seleccionar CSV, ver errores por línea y confirmación bloqueada; sustituir por CSV válido y comprobar referencia/title fallback/calidades y confirmación habilitada. No se pulsó la confirmación de datos de prueba. ES/CA/EN/AR comprobados; RTL móvil 390×844 sin desbordamiento. Vista de escritorio ampliada para las siete columnas.
- El historial local sigue con sus dos trabajos previos, sin nuevos trabajos ni descargas de YouTube o subidas a Azure durante estas comprobaciones.
- Plantilla disponible desde el portal y como archivo de entrega; sus URLs son marcadores que deben sustituirse. Guía de formato en youtube-csv-import.md.
- Imagen local fyxbody-videos:youtube-csv compilada Release, seis recursos del portal verificados contra las fuentes. Sin push ni despliegue.
- OpenAPI administrativo regenerado desde el servidor; contrato partner y sus descargas conservados.
