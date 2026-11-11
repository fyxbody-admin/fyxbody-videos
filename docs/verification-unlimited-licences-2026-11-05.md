# Licencias con sesiones ilimitadas · 2026-11-05

## Comportamiento

Las licencias incluyen `unlimitedSessions`, false por defecto. El administrador elige Cuota mensual o Sesiones ilimitadas. Una licencia ilimitada normaliza la cuota a cero y registra cada nueva sesión sin tope mensual. Las limitadas conservan su comportamiento: cero bloquea emisiones. El catálogo, estado, vigencia, expiración SAS y protección contra abuso se mantienen.

La selección prioriza una licencia ilimitada válida que incluya el vídeo antes que una limitada superpuesta. La emisión incrementa el contador atómicamente dentro de la misma transacción que la sesión; un replay idempotente no duplica consumo y un fallo de firma revierte ambos cambios. Cambiar de modalidad o cuota no reinicia contadores.

El flag opcional en POST crea una licencia limitada si se omite/null. En PUT, omitido/null conserva la modalidad actual para clientes anteriores. Las respuestas administrativas y de consumo del partner incluyen el flag. CSV conserva sus ocho columnas anteriores y añade unlimited_sessions como novena. El panel muestra Sin límite y omite el medidor de cuota para ilimitadas.

## Comprobaciones

- 130/130 pruebas automatizadas correctas, sin omisiones; once casos nuevos de licencias ilimitadas.
- SQL 005 ejecutado dos veces correctamente en PostgreSQL temporal 55440. Contenedor de prueba retirado.
- Revisión crítica independiente sin bloqueos en ámbito de acceso, concurrencia, contadores, compatibilidad, migración, JSON, CSV y frontend.
- Prueba HTTP real: licencia QA temporal ilimitada con cuota cero emitió dos sesiones sobre un vídeo real de Azure; repetir la primera petición devolvió su sesión y mantuvo el contador en dos.
- Vídeo real ajeno al catálogo de esa licencia rechazado con 403. Cambiar a limitada con cuota cero rechazó una nueva emisión con 429; volver a ilimitada conservó el contador.
- PUT sin flag conservó el modo ilimitado. Consumo admin/partner y CSV confirmaron flag true y dos sesiones. No se guardaron URLs SAS en los resultados.
- Navegador: selector, campo de cuota oculto/deshabilitado, edición a limitada75 y regreso a ilimitada, tabla Ilimitadas y Consumo con Sin límite sin medidor, conservando dos sesiones.
- Sintaxis JavaScript y contratos OpenAPI administrativos/partner/portal actualizados.

La licencia QA, sus dos sesiones y contador se retiraron al finalizar mediante su UUID y nombre exactos. Las licencias y contadores originales se conservaron. Resultado detallado sin secretos bajo .local/verification/unlimited-check.json. Captura de la prueba guardada por separado.

## Datos y despliegue

Migración `20261105183903_UnlimitedLicenseSessions` aplicada a PostgreSQL local 127.0.0.1:55439/fyxbody_videos tras copia SQL. El campo es NOT NULL DEFAULT false, por lo que no convierte licencias anteriores. Copias antes/después en .local/backups con permisos 0600.

La API local sigue en http://127.0.0.1:5234/portal/. PostgreSQL Azure y App Service no se modificaron. Para despliegue aplicar la migración 005 con el ejecutor habitual antes de activar esta versión del servicio. La modalidad está preparada para el partner interno FYXBODY; sus registros comerciales se configurarán con el catálogo y vigencia correspondientes.
