# Verificación del portal — 2026-11-05

## Entregado

Panel estático integrado en ASP.NET Core, sin compilación frontend ni dependencias externas. Roles administrador y partner; gestión de ejercicios/archivos, clientes, licencias, catálogo y usuarios; consulta de consumo y CSV. Las claves API no llegan al frontend. Las cuentas personales usan PasswordHasher de Identity, cookie HttpOnly, CSRF, cambio obligatorio, bloqueo y revocación por marca de seguridad.

## Comprobaciones

- Build: cero advertencias y cero errores.
- 49 pruebas .NET correctas, cero omitidas, con PostgreSQL 16 local real.
- Tres pruebas Node correctas y sintaxis JS comprobada.
- Conservados los flujos API por clave y añadidos flujos por cookie.
- Probados CSRF, cambio obligatorio, aislamiento real de catálogos/consumo de dos partners, revocación tras cambio/reset/desactivación, bloqueo por fallos, acceso administrativo y prohibición de claves API para administrar usuarios personales.
- Probada revalidación de SecurityStamp en el servicio partner y recursos estáticos con CSP.
- Revisión de seguridad independiente, sin bloqueadores pendientes. Corregidas la ventana de revalidación de stamp y la ruta inicial para evitar un bucle de redirección.
- Navegador: login admin, edición guardada, retirada y guardado de un ejercicio de catálogo, logout, login partner, catálogo reducido correctamente y consumo propio. Datos de prueba locales.
- Móvil 390×844: navegación colapsable, catálogo y consumo sin desbordamiento de página; tablas con scroll interior. No se hizo auditoría con lector de pantalla ni dispositivo físico.
- OpenAPI partner/admin/portal exportados desde proceso actual y validados como 3.1.1.
- Publicación Release incluye wwwroot/portal/index.html, app.js y styles.css.

## Azure TEST

Aplicada PortalUsers en fyxbody_videos/esquema videos. Historial con tres migraciones y diez tablas de negocio. Creado admin inicial activo, con hash de contraseña aleatoria y cambio obligatorio. Contraseña entregada en archivo local privado 0600 fuera del repositorio; no se incluye en este informe. No se crearon partners ni ejercicios ficticios en Azure, no se alteraron roles PostgreSQL ni otras bases.

## Pendiente

App Service no desplegado, repo remoto no creado y Blob real sin configurar. Los usuarios pueden utilizar el panel cuando se publique con conexión PostgreSQL de mínimo privilegio y protección persistente de cookies. Los ejemplos/fixtures y capturas locales no representan datos comerciales. No hay subida de vídeos desde el panel: se registran archivos ya alojados en Azure.
