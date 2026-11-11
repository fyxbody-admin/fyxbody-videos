# Importación automática desde YouTube · 2026-11-05

## Funcionalidad

El administrador introduce la URL en Importar YouTube, selecciona resoluciones y añade un trabajo a una cola PostgreSQL. El servidor descarga, verifica, convierte y sube los MP4 al contenedor privado configurado; crea el ejercicio con el título de YouTube o reutiliza su identificador de origen `youtube:{id}`. Las calidades disponibles se conservan sin sobrescribir objetos. Resoluciones predeterminadas: 1080p, 720p y 480p; 360p opcional. No amplía la resolución de origen.

Los trabajos se consultan en el historial con su estado, fase, porcentaje, resoluciones y error/reintento. Cerrar el panel no cancela un trabajo del servidor. Si la instancia se reinicia, se requiere reintentar el trabajo interrumpido; se conservan las versiones ya registradas.

## Validación automatizada y revisión

- Suite completa: 118 pruebas superadas, incluidas las de PostgreSQL aislado y procesamiento multimedia simulado.
- Tras ajustar las solicitudes de resoluciones adicionales durante un trabajo activo: 32 pruebas de importación superadas. Mismas calidades o subconjunto devuelven el trabajo activo; calidades adicionales devuelven 409 para evitar solicitudes ignoradas.
- Revisión especializada sin bloqueos en SSRF, ejecución de procesos, leases, compensación y sondeo de UI. Corregida la resolución original impar hacia el par inferior sin ampliar.
- Sintaxis JavaScript comprobada. OpenAPI administrativo regenerado desde el servidor actualizado.
- Imagen Docker final construida. Herramientas probadas como UID 1654, con red deshabilitada: yt-dlp 2026.10.26, Node 22.23.4, FFmpeg 6.1.1 y codificación H.264 real. No se publicó ni desplegó la imagen.

## Prueba real desde el navegador

Desde el formulario se encoló `https://www.youtube.com/watch?v=WhpknPP0xKU` solicitando 360p. El vídeo 41 Toca la pelota ya existía con 1080p/720p/480p, por lo que se reutilizó el ejercicio. La API ejecutó la consulta y descarga reales de YouTube, generó un MP4 H.264/AAC de 360p y lo subió a Azure.

El historial se actualizó automáticamente desde En cola a Completada, 100 %, y mostró Disponible 360p. El botón Ver ejercicio mostró las cuatro versiones. El nuevo archivo pesa 2.169.779 bytes y dura 44,35 s. La emisión de vista previa comprobó tamaño y Content-Type de Azure; una petición Range devolvió 206 y cabecera MP4 ftyp. El navegador cargó el nuevo archivo con readyState 4, 640×360, duración 44,350113 s y reproducción activa sin errores. Se corrigió y comprobó también el regreso al historial desde Ver ejercicio pulsando la navegación del mismo hash.

Trabajo: `465cbf07-f212-426b-8411-8d6aac89e500`. Resultado detallado sin URLs SAS en `.local/imports/automatic-youtube-2026-11-05.json`. Copias SQL locales antes de la migración y después de la prueba, con permisos 0600.

## Entorno

API local: http://127.0.0.1:5234/portal/#imports. PostgreSQL local: 127.0.0.1:55439/fyxbody_videos, esquema videos. Migración `20261105170851_YouTubeImportJobs` aplicada únicamente a esta base, después de copia de seguridad. Las pruebas PostgreSQL usaron otro contenedor en 55440, ya retirado.

Almacenamiento real: stfyxbody2testzpfyqf/video, privado. Se reutiliza la identidad local y el permiso de datos autorizado previamente; no se cambian roles ni acceso público en esta entrega. No se modificó PostgreSQL Azure ni se desplegó App Service. Para desplegar se necesita la imagen con herramientas, identidad administrada, configuración, migración y Always On descritos en operaciones.
