# Portal de vídeos

Frontend estático en `src/FyxBody.Videos.Api/wwwroot/portal/`, servido por la API en `/portal/`. Usa HTML, CSS y JavaScript nativo sin compilación, dependencias, CDN ni fuentes externas. Su integración y despliegue corresponden a la aplicación .NET.

## Acceso y seguridad

- Autenticación personal mediante las cookies HttpOnly del backend. La interfaz no maneja claves API ni guarda credenciales en localStorage/sessionStorage.
- La sesión se obtiene de `GET /portal/api/session`. Toda escritura envía `X-CSRF-TOKEN`; la sesión se refresca después del login y del cambio de contraseña.
- El cambio de contraseña temporal es obligatorio antes de mostrar otras pantallas. Los permisos y el aislamiento de clientes los aplica siempre el backend.
- Las contraseñas temporales generadas al crear/restablecer usuarios se muestran en un diálogo una sola vez. Se vacía su contenido al cerrarlo; la copia al portapapeles requiere acción expresa.
- Los valores del servidor se insertan como texto y nodos DOM, sin `innerHTML`. No hay scripts, estilos ni manejadores de eventos inline. Las escrituras no se reintentan automáticamente.

## Funciones

Administrador: resumen con conteos reales, edición de ejercicios e ID de origen, registro y edición de metadatos de archivos de Azure, clientes, licencias, asignación de catálogo, usuarios y consumo mensual con CSV.

Cliente: consulta de su catálogo y sus metadatos disponibles, consumo de sus licencias y cambio de contraseña. No se muestran rutas Azure ni se inicia reproducción desde el portal.

El catálogo carga la biblioteca paginada y mantiene una selección independiente de la búsqueda/página visible. Guardar exige confirmación y sustituye la asignación completa de esa licencia. Se bloquea el guardado con más de 1.000 ejercicios. El catálogo vacío tiene una confirmación explícita de revocación.

Las licencias se editan y muestran en la zona horaria local; se envían en UTC. El mes de consumo es UTC. Se distingue entre sesiones autorizadas (cuota) e inicios/finalizaciones recibidos.

## Comprobaciones

Comprobación de sintaxis: `node --check src/FyxBody.Videos.Api/wwwroot/portal/app.js`.

La interfaz contempla estados vacíos, carga, errores con referencia de solicitud, navegación por teclado, etiquetas de campos, diálogos nativos con devolución de foco, tablas con desplazamiento horizontal, tamaños móviles y preferencia de movimiento reducido. Estos mecanismos no sustituyen una auditoría con lector de pantalla o pruebas en dispositivos físicos.

La comprobación en navegador requiere el backend integrado y una base de datos configurada. No se incluyen datos ficticios ni credenciales en los archivos estáticos.

## Administrador inicial y alta de partners

En Azure TEST existe el usuario inicial `admin`. La contraseña temporal se entregó fuera del repositorio y el primer acceso exige cambiarla. No hay contraseñas predeterminadas ni una página pública de registro.

Después de cambiar la contraseña: crear el cliente en Clientes; definir su licencia y asignar catálogo; en Usuarios crear el acceso con rol Cliente y seleccionar su organización. Si se deja la contraseña vacía, se genera una temporal que solo se muestra una vez. El partner deberá cambiarla antes de consultar catálogo/consumo. Para recuperar el acceso, el administrador usa Restablecer contraseña; esto invalida sesiones previas.

Las cuentas del portal son independientes de las claves API de integración. Desactivar usuario o cliente invalida su acceso. No es posible desactivar la cuenta propia ni dejar el sistema sin administradores activos.

## Bootstrap para una instalación nueva

Solo para una base sin administradores, proporcionar por entorno/gestor de secretos `PortalBootstrap__Username`, `PortalBootstrap__Password` y opcionalmente `PortalBootstrap__DisplayName`, y ejecutar:

```bash
dotnet run --project src/FyxBody.Videos.Api -- --bootstrap-admin
```

La contraseña debe tener 12–128 caracteres y no se registra. El proceso crea el administrador con cambio obligatorio y termina sin arrancar HTTP. Se deshabilita si ya existe cualquier administrador; no restablece cuentas existentes. No incluir el secreto en scripts versionados ni argumentos del proceso.

## Validación realizada

49 pruebas .NET sin omisiones y tres del cliente Node correctas. Revisión manual en navegador: login de administrador, edición de ejercicio, selección/guardado de catálogo, logout, login partner, reflejo de la retirada en el catálogo y estadísticas propias. Vista móvil de 390×844 comprobada para navegación, catálogo y consumo. Datos ficticios usados únicamente en PostgreSQL local desechable, no en Azure.

## Sistema visual FYXBODY

Aplicado a partir de `FYXBODY-sistema-visual.pdf` facilitado por el usuario. Base negra `#000`, paneles `#0B0B0B → #060606` con borde `#222` y radio de 12 px; celdas `#0F0F0F` con borde `#1A1A1A`, radio de 6 px y espaciado compacto. Texto secundario `#A5A5A5` como mínimo para información funcional.

IBM Plex Mono 400/500 se utiliza en contenido, navegación, módulos y formularios; Inter 900 se reserva para los grandes titulares, cifras y el wordmark tipográfico existente. Las fuentes son WOFF2 locales cargadas desde `/portal/fonts/fonts.css`, con sus licencias en la misma carpeta. No se genera un nuevo logotipo o isotipo.

El cian `#00D4FF` identifica navegación e información. El naranja `#FF8C1A` se reserva a la acción primaria y a estados pendientes; verde, ámbar y rojo distinguen disponibilidad completada, advertencias y alertas. Los estados usan contorno y texto explícito, sin depender exclusivamente del color. Los formularios y diálogos conservan una sola acción primaria; al abrir un diálogo la acción de fondo pierde el acento.

Las cifras de Resumen y Consumo se agrupan en un panel con celdas y fichas de dígitos partidas visualmente. Cada cifra anuncia el valor completo mediante `aria-label`; las fichas decorativas se ocultan a la accesibilidad. Los números largos se distribuyen en varias líneas sin recortarse. Las tablas y los módulos incluyen numeración visual.

Los iconos de navegación se construyen con SVG de geometría fija en una rejilla de 16×16, creados mediante DOM con un único color. El marco de pantalla aplica fondo radial, líneas de barrido y brillo; el dibujo no lleva degradados. Solo el panel de módulos de trabajo tiene un resplandor fijo. Se omite la animación decorativa para mantener una lectura estable; el modo de movimiento reducido anula animaciones y transiciones.

La aplicación de esta guía cambia la presentación del portal. Conserva sus rutas, permisos, contratos API, formularios, confirmaciones y selección de catálogo. No incorpora pantallas de entrada, tests de reacción ni otras funcionalidades descritas como ejemplos en el PDF.

La aplicación del sistema visual oficial, sus tokens y las comprobaciones están documentadas en [visual-system.md](visual-system.md).


### Vista previa para el administrador

En **Ejercicios y vídeos → Ver vídeo** se abre un reproductor MP4 dentro del panel. Si hay varias versiones, se puede elegir idioma y resolución. También se puede abrir un archivo concreto desde **Archivos → Ver vídeo**. Los archivos pendientes o deshabilitados no permiten reproducir.

El panel solicita un enlace de lectura temporal de diez minutos al backend, sin claves de Azure en el navegador. No se crean sesiones de partner ni se descuenta su cuota. La transferencia de datos de Azure sí sigue teniendo su coste habitual. Al cerrar la ventana se detiene el vídeo y se retira el enlace del reproductor; cuando caduca se puede renovar. El cierre no revoca un enlace ya emitido, que caduca por sí solo.

Se necesita un MP4 real en Azure Blob Storage y la configuración/identidad de almacenamiento indicada en operaciones. Los registros ficticios del entorno local no contienen archivos reales; en ese caso se muestra un error de disponibilidad, sin simular una reproducción.


### Subida de vídeos desde el panel

En **Ejercicios y vídeos → Archivos → Subir vídeo**, el administrador puede seleccionar un MP4 de su equipo, revisar su vista previa local y elegir idioma/etiqueta de resolución. El navegador calcula la duración y las dimensiones; no se realiza transcodificación. El máximo es 200 MiB por archivo.

La barra muestra el envío al servidor. Al llegar al 100 % se espera a que termine la escritura en Azure y el registro en PostgreSQL. El resultado queda disponible en la tabla para **Ver vídeo**. El archivo pasa por la API autenticada: el navegador no recibe claves de la cuenta ni permisos de escritura en Azure, y el contenedor se mantiene privado. Si se interrumpe o se pierde la conexión, revisar la lista antes de repetir.

La ruta se asigna en el backend: `ejercicios/{videoId}/{language}/{rendition}/{assetId}.mp4` dentro del contenedor configurado. No hay que crear carpetas manualmente. Para un idioma/resolución ya registrado, elegir otra etiqueta de versión o gestionar el registro existente; no se sobrescribe el blob.


### Importar desde YouTube

El administrador dispone de **Importar YouTube** en la navegación y desde **Ejercicios y vídeos**. La página consulta las capacidades del servidor y muestra si el servicio está habilitado, sus límites y el historial paginado (25 trabajos). **Importar desde YouTube** acepta un enlace y permite elegir 1080p, 720p, 480p y 360p; por defecto se solicitan 1080p, 720p y 480p según las capacidades. El servidor valida el enlace, la disponibilidad y los límites. Solo se admite contenido público o no listado accesible que se tenga permiso para importar.

Tras **Añadir a la cola**, el trabajo persiste en el servidor y continúa aunque se cierre el portal. El historial muestra cola, ejecución, comprobación, descarga, conversión, subida, progreso, resoluciones solicitadas/disponibles y errores. No se amplía la resolución original: el servidor puede generar versiones menores según el origen. Se reutiliza el ejercicio identificado por YouTube y no se sobrescriben archivos existentes. **Ver ejercicio** abre sus archivos; **Reintentar** en trabajos fallidos crea una nueva solicitud con el mismo enlace y resoluciones, revisable antes de enviarla.

Se actualiza el historial cada cinco segundos cuando hay trabajos activos en la página visible; no se solapan lecturas y la navegación, cierre de sesión y salida de la página retiran el sondeo. **Actualizar** permite consultar manualmente cualquier página. Los enlaces y mensajes se representan como texto; no se cargan miniaturas, iframes ni recursos de YouTube en el navegador. Toda la comunicación usa las rutas autenticadas `/admin/v1/video-imports`, la cookie del portal y el CSRF existente. La subida manual de MP4 y la vista previa administrativa siguen disponibles.


### Licencias con sesiones ilimitadas

En **Licencias y catálogo → Nueva licencia / Editar → Tipo de sesiones**, elegir **Cuota mensual** o **Sesiones ilimitadas**. Con cuota mensual se solicita un entero mayor o igual a cero: **0 bloquea nuevas sesiones**, no significa ilimitadas. Al elegir sesiones ilimitadas se oculta y deshabilita el campo de cuota; la petición envía `unlimitedSessions: true` y `monthlySessionQuota: 0`. Una licencia sin el indicador se interpreta como limitada, conservando el comportamiento anterior.

La tabla muestra **Ilimitadas** para estas licencias. En **Consumo**, tanto el administrador como el cliente ven las sesiones autorizadas reales, **Sin límite** en cuota/disponible y ningún medidor de cuota para las ilimitadas. La vigencia, el estado y el catálogo siguen aplicándose.

Comprobación manual con backend integrado: crear una licencia ilimitada con fechas y catálogo válidos; volver a editar y comprobar que conserva el modo; consultar Consumo como administrador y cliente y confirmar el recuento de sesiones sin medidor ni saldo numérico; cambiar a cuota mensual, introducir una cuota y verificar que vuelve a mostrarse el límite. Comprobar también que una licencia limitada con cuota 0 conserva el 0. Este flujo no crea por sí mismo un cliente FYXBODY ni asigna fechas o catálogo.

## Idiomas y dirección de lectura

El portal permite elegir **Español, Català, English y العربية** desde el login y la cabecera autenticada. Guarda solo el código de idioma en `localStorage['fyxbody.portal.locale']` y en una cookie de preferencia del mismo nombre; las credenciales siguen fuera de este almacenamiento. Lee primero una preferencia válida de localStorage, después la cookie y por último `navigator.languages`; las variantes regionales usan su idioma base y el fallback es español. La cookie es de host, limitada a `/portal/`, `SameSite=Lax`, dura un año y añade `Secure` en HTTPS. Cuando localStorage no está disponible permite conservar la elección al recargar. Si ambos mecanismos están bloqueados, el cambio funciona durante la pestaña actual.

El cambio actualiza los textos y atributos accesibles existentes sin reconstruir formularios, selección de catálogo, búsqueda, página visible o subidas. Los diálogos nativos impiden utilizar el selector exterior mientras están abiertos. Las solicitudes posteriores, incluida la subida XHR y la descarga CSV, envían `Accept-Language`. Los errores del servidor ya recibidos conservan el idioma de su solicitud hasta su siguiente consulta.

Árabe establece `lang="ar"` y `dir="rtl"` en el documento. Navegación y bordes usan propiedades lógicas; las cifras conservan su orden dentro de contenedores LTR aislados y reconocen dígitos Unicode. Los datos propios del cliente (nombres, títulos y códigos) conservan su contenido; los campos técnicos, rutas y contraseñas se aíslan en LTR. El idioma del portal no traduce los títulos de origen ni modifica el audio de los vídeos.

IBM Plex Sans Arabic 400/500/600/700 se sirve desde `portal/fonts/arabic.css`; los TTF y su licencia OFL se incluyen localmente. Se obtuvieron de la hoja oficial de [Google Fonts](https://fonts.google.com/specimen/IBM+Plex+Sans+Arabic) y del [repositorio oficial de fuentes y licencia](https://github.com/google/fonts/tree/main/ofl/ibmplexsansarabic). No se consultan fuentes/CDN en tiempo de ejecución. Inter y IBM Plex Mono se conservan para la identidad y los datos técnicos.

Los mensajes, arquitectura y pruebas se describen en [localization.md](localization.md). Comprobación frontend: `node --test tests/portal-i18n/*.test.mjs`; sintaxis: `node --check src/FyxBody.Videos.Api/wwwroot/portal/app.js` y `node --check src/FyxBody.Videos.Api/wwwroot/portal/i18n.js`. Las pruebas no sustituyen revisión lingüística nativa, lectores de pantalla ni dispositivos físicos.

## Documentación de integración para partners

La navegación de cliente incluye **Documentación**, ruta `#documentation`. Es una guía estática disponible tras el cambio de contraseña obligatorio; no genera credenciales ni sesiones y no ejecuta solicitudes de ejemplo. Distingue el usuario del portal, la clave API de backend entregada por FYXBODY y la licencia que autoriza automáticamente el catálogo. Cubre entorno/origen, paginación y selección de archivo, sesiones e idempotencia, reproductor mediante backend propio, eventos, consumo UTC, errores, reintentos y límites de las URLs temporales.

Los enlaces del índice (`#documentation/catalogue`, etc.) llevan el foco al encabezado correspondiente y conservan el DOM al saltar dentro de la página. El código se representa como texto LTR independiente de ICU, sin controles de aislamiento copiados; los bloques tienen desplazamiento local y copia explícita con selección manual si el portapapeles no está disponible. El selector existente actualiza textos, atributos y el `Accept-Language` de los ejemplos sin reconstruir la página. Los valores técnicos, UUID, fechas ISO y código conservan ASCII incluso en árabe.

Se muestra `location.origin` como entorno actual y se pide confirmar la URL productiva con FYXBODY. No hay campos para introducir secretos ni botones de reproducción. Los recursos descargables están limitados al OpenAPI de partner y al cliente Node.js de backend; nunca deben distribuirse claves API al navegador. La guía usa ejemplos ficticios y no sustituye pruebas de integración con la licencia y el backend del partner.

Archivos: `documentation.js`, `documentation-messages.js`; mensajes integrados en los cuatro catálogos y sus descripciones. Comprobar con `node --test tests/portal-i18n/*.test.mjs tests/portal-documentation/*.test.mjs` y `node --check` de los módulos modificados. Las pruebas de shell sustituyen `curl` por un grabador local: validan JSON, cabeceras y conservación de claves sin usar red ni credenciales reales. Revisión manual requerida: cuatro idiomas, navegación por teclado, enlaces del índice, copia y móvil/RTL. No implican despliegue ni cambios de backend.

El botón «Descargar guía · Markdown» de la cabecera genera un `.md` UTF-8 en el idioma elegido en el momento de pulsarlo (es/ca/en/ar), con nombre `fyxbody-integration-guide-IDIOMA.md`. Comparte la estructura, los mensajes y los ejemplos con la página; incluye errores, checklist y enlaces absolutos a recursos, sin datos de cuenta ni credenciales. No ejecuta solicitudes ni emite sesiones.


### Importación de YouTube por CSV

En **Importar YouTube**, **Descargar plantilla CSV** genera localmente un archivo UTF-8 con BOM y cabecera `Referencia,Titulo,URL,Calidades`. Incluye ejemplos que deben sustituirse: `REPLACE_WITH_ID` no identifica un vídeo importable. La descarga no contacta con YouTube ni crea trabajos. **Importar CSV** abre un diálogo con selector localizado, validación explícita y tabla por línea. Máximo: 1 MiB y 500 filas de datos; se exige UTF-8 válido con o sin BOM. Las calidades se separan por punto y coma, por ejemplo `1080p;720p;480p`; no se amplía la resolución original. Si se utiliza punto y coma como separador de columnas, la celda de calidades debe ir entre comillas.

`URL` y `Calidades` son obligatorias. `Referencia` define el ID de origen y, cuando está vacía, se utiliza el ID de YouTube según la normalización del servidor. `Titulo` vacío toma el título de YouTube durante el procesamiento. La tabla muestra el ID de origen efectivo, la URL, las calidades, errores por línea y, después de confirmar, los identificadores de trabajo y las solicitudes existentes reutilizadas. No se sobrescriben metadatos en conflicto.

**Validar y previsualizar** envía el contenido a `POST /admin/v1/video-imports/csv/preview` sin encolar trabajos. **Confirmar y añadir a la cola** exige una previsualización válida y envía exactamente el mismo contenido a `POST /admin/v1/video-imports/csv`; el servidor vuelve a validarlo. Si una fila tiene errores, se bloquea todo el lote. Cambiar de archivo elimina el informe anterior y exige validación nueva; durante las solicitudes se bloquean el selector y el doble envío. Al cerrar se descarta el contenido del diálogo y los resultados pendientes no modifican otra pantalla. El historial se refresca tras un resultado de encolado correcto y conserva su sondeo existente. Si se pierde la respuesta a una escritura, se indica revisar el historial y se exige previsualizar de nuevo antes de reenviar.

Etiquetas, ayuda, estados y botones están disponibles en español, catalán, inglés y árabe; referencias, URLs y UUID conservan aislamiento LTR. Los mensajes recibidos del servidor mantienen el idioma de su solicitud. Las peticiones JSON utilizan el helper existente con cookies, CSRF y `Accept-Language`. Pruebas frontend: `node --test tests/portal-csv/*.test.mjs tests/portal-i18n/*.test.mjs tests/portal-documentation/*.test.mjs`.
