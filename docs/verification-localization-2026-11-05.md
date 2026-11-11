# Verificación de idiomas y material de presentación · 2026-11-05

## Resultado

Portal de administración y de partners en español, catalán, inglés y árabe. Selector accesible, preferencia guardada con respaldo por cookie, textos reactivos sin reconstruir formularios, plurales y formatos Intl. Árabe con RTL real, fuente local y campos técnicos LTR. Sin nuevas migraciones ni cambios en Azure.

## Comprobaciones

- Backend: 155/155 pruebas correctas, incluidas 25 nuevas de localización; PostgreSQL temporal separado en 55440, retirado tras verificar. La base local del portal en 55439 se conserva.
- Frontend: 17/17 pruebas correctas: paridad de 350 mensajes por idioma, argumentos, seis plurales árabes, negociación, formatos, datos Unicode, bindings DOM, encabezados, almacenamiento/cookie, altura dinámica de menú y selector mensual independiente del idioma del sistema.
- API local real: 11 combinaciones de negociación (cuatro idiomas, regiones, pesos, exclusiones y fallback), ocho errores de validación diferenciados y cuatro lecturas de trabajos. Códigos y cabeceras estables; no se crearon registros en estas comprobaciones.
- Navegador: login en los cuatro idiomas; admin en ES/CA/EN/AR; catálogo y consumo de partner en AR/EN/ES. Cambiar idioma mantuvo búsqueda y selección sin guardar en el editor de catálogo. La selección temporal se revirtió sin guardar.
- Árabe: fuente disponible, RTL y ausencia de desbordamiento de página; URL de YouTube LTR. Móvil 390×844: el menú empieza en 123 px tras una cabecera de 122,17 px, sin taparla. Tamaño restablecido al terminar.
- Preferencia AR conservada al recargar el navegador integrado mediante respaldo por cookie. Panel de pruebas restaurado a español; pestañas propias cerradas.
- Docker final `fyxbody-videos:local-multilingual`: build Release correcto, usuario 1654, respuestas de los cuatro idiomas y fuentes frontend finales verificadas. Sin push ni despliegue.

Los controles nativos del navegador o del sistema mantienen su propio idioma. El contenido introducido por usuarios se conserva; no hay traducción automática ni doblaje. Véanse las guías de localización para mantenimiento y alcance.

## Presentación

Vídeo local con datos de ejemplo, 1920×1080, 30 fps y 22,5 segundos, creado con brag/Hyperframes. Original español y variante inglesa separados en brag-output, excluidos del contexto Docker y del repositorio. Música y atribuciones en cada entrega. Tres capturas reales de interfaz en inglés se entregan aparte para el PDF de propuesta; los datos de demostración y los títulos de contenido conservan sus nombres originales.

Material final en inglés: brag-output/brag-en.mp4 (22,500 s, 1920×1080, 30 fps) y tres JPEG: vista general, selección de catálogo por licencia y consumo del partner. Mes del consumo verificado como November en EN y نوفمبر en AR, conservando 2026-11. Docker actualizado después de este ajuste; 10/10 recursos JS/CSS finales coinciden con fuente.
