# Subida, vista previa e importación · 2026-11-05

## Resultado real

Tres vídeos de YouTube autorizados por el usuario están en el contenedor privado `video` de `stfyxbody2testzpfyqf`, grupo `rg-fyxbody2-spaincentral`. Se generaron nueve MP4: 1080p, 720p y 480p por vídeo; total 40.243.609 bytes. No se han sobrescrito objetos existentes ni habilitado acceso público.

| Fuente | Título | Duración | Resoluciones |
|---|---|---|---|
| znWpziD4Vvs | 183 Gatear con obstáculos I | 33,228 s | 1080p, 720p, 480p |
| mDa8SFe0zLI | 119 Toca con la CADERA en distintas posiciones | 54,451 s | 1080p, 720p, 480p |
| WhpknPP0xKU | 41 Toca la pelota | 44,350 s | 1080p, 720p, 480p |

Origen a máxima calidad disponible de hasta 1080p con H.264 y audio AAC. 1080p se remultiplexó sin recodificar; 720p y 480p se generaron con FFmpeg H.264 y audio AAC, MP4 con faststart. Todos se verificaron con ffprobe.

Las rutas las generó la API: `video/ejercicios/{videoId}/es/{rendition}/{assetId}.mp4`. El identificador de origen del catálogo es `youtube:{youtubeId}`, editable cuando se conozca el identificador canónico del ejercicio.

## Validación

- Suite de backend de subida: 72 pruebas superadas con PostgreSQL temporal 55440 y almacenamiento simulado. Recursos temporales de prueba retirados.
- Tras añadir el selector de suscripción local: 14 pruebas focalizadas superadas; cuatro de PostgreSQL se omitieron en esa ejecución al no activar la base temporal.
- Prueba Kestrel real: multipart de 400 KB procesado hasta la validación de metadatos; petición excesiva rechazada con 413 antes de leer el cuerpo.
- Revisión especializada sin bloqueos sobre autorización, CSRF, límites, archivos temporales, rutas, compensación de Azure y cancelación del frontend.
- Primer archivo real (183, 1080p) subido desde el formulario del navegador: metadatos detectados, progreso, registro ready y tabla actualizada. Los ocho restantes usaron el mismo endpoint administrativo.
- Los nueve archivos superaron la emisión real de enlace privado: HEAD verifica tipo y tamaño; GET con Range devolvió 206 y cabecera MP4 ftyp. No se guardaron ni imprimieron los enlaces SAS.
- Reproductor web real a 1080p: readyState 4, 1920×1080, duración 33,227755 s y reproducción activa sin error. Selector con las tres versiones y cambio a 720p comprobados.
- Sintaxis JavaScript validada con node --check.

## Entorno y permisos

La API sigue en `http://127.0.0.1:5234/portal/` y el catálogo se guarda en PostgreSQL **local**, `127.0.0.1:55439/fyxbody_videos`, esquema `videos`. Los MP4 sí están en Azure. No se han escrito estos registros en PostgreSQL Azure ni publicado un App Service. No se modificaron licencias de partners para asignar estos ejercicios.

El usuario autorizó expresamente asignar Storage Blob Data Contributor a su identidad local, limitado al contenedor video. Se aplicó ese rol persistente. La identidad ya tenía Owner heredado. No se modificó la suscripción global de Azure CLI: en Development se usa `Storage:AzureCliSubscription` para elegir la cuenta FYXBODY correcta. El despliegue deberá usar su propia identidad administrada.

Los MP4, manifiesto con rutas/UUID/tamaños y copia SQL del catálogo se conservan bajo `.local/imports/youtube-2026-11-05/` y `.local/backups/`, excluidos de Git. La copia SQL tiene permisos 0600. Los tres registros DEMO previos siguen siendo ficticios.
