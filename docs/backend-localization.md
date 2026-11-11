# Idiomas del backend

La API admite español (`es`), catalán (`ca`), inglés (`en`) y árabe (`ar`). El idioma predeterminado es español. Se negocia exclusivamente mediante `Accept-Language`, respetando pesos `q`; las preferencias con q=0 se excluyen. Se prueban hasta diez preferencias ordenadas por calidad y se permite la cultura padre: `ar-SA` → `ar`, `ca-ES` → `ca`, `en-GB` → `en`. Una cabecera vacía, inválida o sin idioma compatible vuelve a español. Los parámetros de URL y las cookies de cultura no sustituyen la cabecera. La elección del portal se conserva en localStorage y se envía en sus fetch/XHR; no se guarda un idioma en el usuario ni se añade una migración.

RequestLocalization se ejecuta antes del manejador de excepciones y de autenticación. Cada respuesta publica `Content-Language` con el idioma base y añade `Accept-Language` a Vary, conservando valores existentes. También lo hacen las respuestas de error. Se mantienen las reglas de Cache-Control: no-store de las rutas privadas y errores. Cambiar idioma no requiere iniciar otra sesión ni modifica las comprobaciones de cookies, CSRF o permisos.

## Mensajes y mantenimiento

`src/FyxBody.Videos.Api/Localization/messages.json` es un catálogo UTF-8 embebido con **134 IDs semánticos y cuatro traducciones por ID**. Contiene mensajes distintos para autenticación, límites, licencias, recursos, validaciones de cada campo, multipart, Azure e importaciones. Las plantillas conservan parámetros como `{field}`, `{maximum}`, `{states}`, `{resource}` y `{maxDuration}`; los nombres de campos y valores de protocolo permanecen estables.

`ApiMessageCatalog` resuelve el texto fuente de ApiException/ApiErrors en el límite HTTP, incluye alias de mensajes existentes y genera el título localizado sin modificar la excepción original. Los mensajes de validación con el mismo code mantienen sus detalles: por ejemplo una cuota negativa, un código de cliente inválido y un nombre vacío no se convierten en el mismo mensaje genérico. Los mensajes desconocidos usan una alternativa segura y localizada por código, sin mostrar diagnósticos ni texto arbitrario. Los Problem Details generados por el framework también pasan por el catálogo.

Al introducir un mensaje humano nuevo, añadir un ID semántico, su texto fuente o alias y las cuatro traducciones; conservar los mismos parámetros en todos los idiomas. El catálogo es parte del ensamblado y se publica también en Docker. Las pruebas comprueban que no faltan traducciones, que los parámetros coinciden y que los mensajes explícitos de la API tienen entrada; las traducciones no implican una revisión de hablantes nativos.

## Trabajos persistidos

`YouTubeImportQueue.View` traduce ErrorMessage al proyectar el DTO, según la cabecera de esa petición. No reescribe el historial almacenado ni depende del idioma del trabajador. Los literales conocidos se traducen; mensajes antiguos genéricos o desconocidos se resuelven por su ErrorCode. Un texto persistido desconocido no se usa como plantilla ni se devuelve como diagnóstico, incluso si contiene rutas o tokens. Dos clientes pueden leer el mismo trabajo en idiomas diferentes conservando el mismo texto y código en PostgreSQL.

## Contrato estable

No se traducen códigos de error, IDs, claves JSON, rutas HTTP, estados/etapas de protocolo, etiquetas de idioma de assets, valores numéricos, fechas ISO UTC ni encabezados CSV. Los títulos de ejercicios, clientes, nombres de usuarios y licencias son datos del usuario: se conservan, sin traducción automática ni doblaje de vídeos. Los mensajes de health, logs internos y descripciones técnicas de OpenAPI no forman parte de la interfaz de negocio traducida. El árabe se devuelve en UTF-8; la dirección RTL y las fuentes del portal pertenecen al frontend.

## Validación

BackendLocalizationTests comprueba los cuatro idiomas, culturas regionales, pesos q, preferencias excluidas y fallback; títulos de autenticación y validaciones diferenciadas; cabeceras en éxito/error; paridad y cobertura del catálogo; lectura de errores de trabajo en varios idiomas sin mutación de PostgreSQL; protección frente a diagnósticos antiguos desconocidos; y sesión/CSRF al cambiar idioma. Las pruebas con datos se ejecutan únicamente en PostgreSQL local temporal separado. No se aplica ninguna migración de idioma a Azure ni a la base local de demostración.

La configuración sigue el [middleware de localización de ASP.NET Core](https://learn.microsoft.com/aspnet/core/fundamentals/localization/select-language-culture?view=aspnetcore-10.0); se usa un proveedor acotado que descarta q=0 antes de delegar la resolución de culturas al framework.
