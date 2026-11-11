# FYXBODY Videos API

API independiente en ASP.NET Core 10 LTS, EF Core 10 y PostgreSQL 16 para licenciar un catálogo de vídeos a aplicaciones externas. Incluye un panel de administración y acceso de partners en `/portal/`.

## Funcionalidades

- Panel web con usuarios administrador y partner, contraseña y sesiones protegidas.
- Gestión visual de ejercicios, clientes, licencias, asignación de catálogo y accesos.
- Área de partner con catálogo y estadísticas propias.

- Administración de clientes, credenciales, licencias, vídeos y archivos.
- Catálogo explícito por licencia y catálogo efectivo por cliente, sin duplicados.
- Revocación de asignaciones conservando sesiones e históricos.
- Credenciales de partner aleatorias, almacenadas mediante hash; autenticación administrativa separada.
- Reproducción con SAS de Azure Blob, HTTPS y solo lectura; identidad administrada y comprobación del archivo.
- Cuotas mensuales PostgreSQL y sesiones idempotentes, con protección ante concurrencia.
- Eventos de reproducción deduplicados, informes mensuales y exportación administrativa CSV.
- Errores Problem Details, límites de peticiones y comprobaciones de salud.

## Documentación

- [Panel y usuarios](docs/portal-ui.md): acceso, gestión y perfiles.
- [Idiomas ES/CA/EN/AR y RTL](docs/localization.md): selección, traducciones, formatos y mantenimiento.
- [Verificación de idiomas](docs/verification-localization-2026-11-05.md): pruebas HTTP, navegador, móvil y Docker.
- [Localización de la API](docs/backend-localization.md): cabeceras, errores y compatibilidad de contratos.
- [Partner Integration Guide — English](docs/FYXBODY-partner-integration-EN.md): guía independiente para el integrador, sin arquitectura interna ni contenido comercial.
- [Integración del partner](docs/partner-integration.md): autenticación, catálogo, reproducción, eventos, cuotas y reintentos. También disponible en el menú Documentación del portal del partner, en los cuatro idiomas.
- [Administración](docs/admin-api.md): altas, licencias, asignación/revocación y consumo.
- [OpenAPI partner](docs/openapi/partner.json) y [OpenAPI administración](docs/openapi/admin.json): contratos 3.1.1 generados desde la API y validados.
- [Cliente Node de ejemplo](examples/backend-client.mjs): uso exclusivo en el backend del partner.
- [Operación y despliegue](docs/operations.md), [modelo](docs/data-model.md) y [migraciones](migrations/README.md).
- [Verificación del portal](docs/verification-portal-2026-11-05.md) y [fase API](docs/verification-catalog-api-2026-11-05.md).

- [Subida e importación real de vídeos](docs/verification-video-upload-2026-11-05.md): resultados y límites del entorno local con almacenamiento Azure.

- [Importación automática desde YouTube](docs/verification-youtube-imports-2026-11-05.md): URL, cola persistente, resoluciones y prueba real.

- [Licencias con sesiones ilimitadas](docs/verification-unlimited-licences-2026-11-05.md): selector, estadísticas y comprobaciones de acceso y consumo.

- [Importación de YouTube por CSV](docs/youtube-csv-import.md): columnas, calidades, validación y procesamiento por lotes.

## Estructura

```text
src/FyxBody.Videos.Api/      API, autenticación, administración y partner
src/FyxBody.Videos.Data/     Entidades, DbContext y migraciones
src/FyxBody.Videos.Api/wwwroot/portal/  Interfaz del panel
tests/FyxBody.Videos.Tests/  Pruebas HTTP y PostgreSQL real
migrations/                 SQL idempotente y procedimiento
docs/                       Guías, contratos y evidencia
examples/                   Cliente backend y pruebas Node
```

## Arranque local

Requiere SDK .NET 10 compatible con global.json. Mantener SDK/runtime actualizados antes de desplegar.

```bash
dotnet tool restore
dotnet restore
dotnet build
dotnet run --project src/FyxBody.Videos.Api --launch-profile http
```

Puerto local: `5234`. `GET /health/live` no depende de PostgreSQL; `GET /health/ready` devuelve 200 solo si conecta y no hay migraciones pendientes. Sin conexión configurada, readiness devuelve 503. `/openapi/partner.json`, `/openapi/admin.json` y `/openapi/v1.json` están disponibles solo en Development. El panel está en `/portal/`; no hay interfaz Swagger.

Configuración sensible exclusivamente mediante entorno/Key Vault. Variables y requisitos completos en `docs/operations.md`.

## Pruebas

```bash
docker run --rm -d --name fyxbody-videos-pg-tests -e POSTGRES_DB=fyxbody_videos -e POSTGRES_USER=videos_test -e POSTGRES_PASSWORD=local-test-only -p 127.0.0.1:55440:5432 postgres:16-alpine
export VIDEOS_TEST_CONNECTION='Host=127.0.0.1;Port=55440;Database=fyxbody_videos;Username=videos_test;Password=local-test-only'
dotnet test
node --test examples/backend-client.test.mjs
node --test tests/portal-i18n/*.test.mjs
docker stop fyxbody-videos-pg-tests
```

Usar una base desechable separada de la del portal: las pruebas PostgreSQL modifican sus datos. El ejemplo reserva el puerto `55440`; el portal de demostración utiliza `55439`.

`local-test-only` es exclusivamente la contraseña de la base desechable local. Sin VIDEOS_TEST_CONNECTION, las pruebas PostgreSQL se omiten explícitamente; las pruebas de integración rechazan servidores remotos. No usar datos reales. La verificación de esta entrega ejecutó todas las pruebas sin omisiones.

## Estado de despliegue

Base TEST: `fyxbody_videos` en `psql-fyxbody2-test-zpfyqf.postgres.database.azure.com`, esquema `videos`. Migraciones aplicadas: InitialVideoLicensing, RevokeCatalogGrants y PortalUsers. Existe el administrador inicial `admin`, con cambio obligatorio de contraseña temporal. No se han cargado vídeos ni clientes comerciales.

La API y el panel se han validado localmente; no se ha desplegado App Service ni creado un repositorio remoto. Las subidas, importaciones de YouTube y vistas previas utilizan el contenedor privado `video` del almacenamiento Azure existente; el catálogo y las licencias de demostración permanecen en PostgreSQL local. Las migraciones posteriores de importaciones y sesiones ilimitadas están aplicadas localmente y pendientes de aplicar en Azure antes del despliegue. El panel y sus recursos estáticos se publican junto con la API. Las pruebas automatizadas de reproducción usan un firmador simulado; la verificación manual también ha probado Blob real. Las credenciales administrativas PostgreSQL nunca se incorporaron al proyecto.
