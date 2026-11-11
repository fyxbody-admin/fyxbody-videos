# Importación de vídeos de YouTube mediante CSV

La sección administrativa **Importar YouTube** permite seleccionar un CSV, validar sus filas y confirmar su incorporación a la cola de importaciones. La importación individual por URL sigue disponible.

## Formato recomendado

Guardar como **CSV UTF-8**, con las columnas separadas por comas y las calidades separadas por punto y coma dentro de su celda:

```csv
Referencia,Titulo,URL,Calidades
EXAMPLE-REF-001,"Sentadilla, con apoyo",https://www.youtube.com/watch?v=REPLACE_WITH_ID,1080p;720p;480p
,,https://www.youtube.com/watch?v=REPLACE_WITH_ID,720p;480p;360p
```

Los enlaces de esta plantilla son marcadores: sustituir cada uno por la URL real de un vídeo antes de validar.

| Columna | Obligatoria | Comportamiento |
|---|---|---|
| `Referencia` | No | Se utiliza como **ID de origen** del ejercicio. Si está vacía, se utiliza `youtube:ID_DEL_VIDEO`. Máximo 128 caracteres sin controles. |
| `Titulo` | No | Título del ejercicio nuevo. Si está vacío, se obtiene de YouTube cuando se procesa el trabajo. Máximo 300 caracteres sin controles. |
| `URL` | Sí | URL HTTPS de un vídeo de YouTube admitido por el importador. |
| `Calidades` | Sí | Una o varias de `1080p`, `720p`, `480p`, `360p`, separadas por `;`. |

Ejemplos de `Calidades`: `720p`, `1080p;720p`, `1080p;720p;480p;360p`. También se admiten los números sin `p`; el formato recomendado conserva la `p`.

Se puede descargar una plantilla desde el propio importador. Sus cabeceras se mantienen como campos de formato aunque cambie el idioma del portal.

### CSV exportado con punto y coma como separador

También se admite este separador de columnas. En ese caso, la celda que contiene varias calidades debe ir entre comillas:

```csv
Referencia;Titulo;URL;Calidades
EXAMPLE-REF-001;Sentadilla con apoyo;https://www.youtube.com/watch?v=REPLACE_WITH_ID;"1080p;720p;480p"
```

Se admiten comillas CSV estándar, finales de línea habituales y marca BOM de UTF-8. Si un título contiene una coma, encerrar la celda entre comillas; para una comilla dentro de una celda entrecomillada, escribirla dos veces. Las columnas opcionales pueden estar vacías o no incluirse; `URL` y `Calidades` deben existir y tener valor en cada fila. No utilizar separador de coma dentro de `Calidades`.

## Uso

1. Abrir **Importar YouTube → Importar CSV** como administrador.
2. Seleccionar el archivo, de hasta **1 MiB y 500 filas de vídeos**.
3. Pulsar el botón de validación/vista previa. La vista previa comprueba el formato, los datos y los conflictos, sin descargar vídeos ni crear trabajos.
4. Revisar las referencias, títulos, URLs, calidades y errores indicados por línea.
5. Si hay algún error, corregir el CSV y volver a validarlo. No se encola ninguna fila mientras el lote tenga errores.
6. Confirmar la importación. El servidor vuelve a validar y registra el lote completo en la cola. El resultado muestra los identificadores de los trabajos y los que ya estaban en curso y se han reutilizado.
7. Seguir el procesamiento de cada vídeo en el historial de importaciones.

La validación no consulta YouTube: la disponibilidad real del vídeo, su título y las resoluciones de origen se comprueban al procesar el trabajo. Las descargas se ejecutan en segundo plano; un error de disponibilidad de un vídeo no anula las importaciones independientes que ya se hayan completado.

## Reimportaciones y conflictos

La referencia identifica el ejercicio. Utilizar la misma referencia al añadir calidades a un ejercicio importado previamente.

- Se detectan referencias y vídeos repetidos dentro del CSV.
- Un trabajo activo compatible puede reutilizarse; una petición con metadatos o calidades incompatibles se señala como conflicto.
- Una referencia existente no se reasigna silenciosamente a otro vídeo de YouTube.
- Los títulos de ejercicios existentes se conservan. Para añadir calidades, dejar `Titulo` vacío o usar el título actual; un título explícito diferente se informa como conflicto.
- Los archivos ya disponibles se reutilizan cuando corresponden al mismo ejercicio y origen.
- El prefijo `youtube:` representa la referencia canónica de un vídeo y debe coincidir con el identificador de su URL.

El importador mantiene los límites de duración y tamaño de la importación individual. No aumenta artificialmente la resolución original; puede generar una versión de la resolución disponible cuando todas las solicitadas son superiores a la fuente, siguiendo el comportamiento del importador existente.

## API administrativa

Las operaciones usan los permisos administrativos y la protección CSRF existente del portal:

- `POST /admin/v1/video-imports/csv/preview`: validación y vista previa.
- `POST /admin/v1/video-imports/csv`: validación e incorporación completa a la cola.

Ambas reciben JSON con un campo `csv` que contiene el texto del archivo. El CSV está limitado a 1 MiB de UTF-8 y el cuerpo HTTP a 2 MiB. Los errores de filas se devuelven en un informe con `rows`, `errors`, `canImport`, `queued`, `totalRows`, `validRows` e `invalidRows`; un informe con errores no crea trabajos. Los errores de autenticación, formato HTTP o servicio siguen el contrato habitual de la API.

El informe de cada fila incluye `rowNumber`, `reference`, `title`, `url`, `heights`, `youtubeId`, `effectiveReference`, `errors`, `jobId` y `reused`. Los mensajes siguen el idioma solicitado mediante `Accept-Language`; los nombres de campo y códigos permanecen estables.
