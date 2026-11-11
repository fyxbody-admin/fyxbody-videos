# Sistema visual FYXBODY aplicado al portal

Referencia aportada por el usuario: FYXBODY-sistema-visual.pdf, versión 1, octubre de 2026. Se aplica al panel de vídeos, no añade los módulos ni el test de reacción de la página de inicio general de FYXBODY.

## Tokens

| Función | Valor |
|---|---|
| Fondo | #000000 |
| Panel | #0B0B0B → #060606; borde #222222; radio 12px |
| Celda | #0F0F0F; borde #1A1A1A; radio 6px |
| Texto secundario funcional | #A5A5A5 como mínimo sobre negro |
| Información/navegación | #00D4FF |
| Foco/iluminación | #BFF3FF |
| Acción principal/pendiente | #FF8C1A |
| Completado | #3DDC97 |
| Aviso | #FFC23D |
| Alerta | #FF4D5E |

IBM Plex Mono 400/500 para texto, etiquetas y títulos de módulo. Inter 900 para titulares grandes, marca tipográfica existente y cifras. Fuentes WOFF2 Latin/Latin Extended reutilizadas del proyecto FYXBODY y servidas localmente; licencias OFL incluidas en wwwroot/portal/fonts. No CDN.

## Componentes

- Panel por sección, con celdas compactas y separadores uniformes.
- Cifras en fichas partidas; mínimo dos cifras visibles y lectura accesible del valor real completo.
- Iconos SVG de píxel en rejilla 16×16, un color, con pantalla de fósforo y líneas de barrido.
- Una acción principal naranja por contexto; navegación cian y acciones secundarias de contorno.
- Estados de contorno, en mayúsculas y con espaciado mono.
- Resplandor de interacción también al navegar por teclado. Un panel de módulos mantiene resplandor fijo en el resumen.
- Movimiento reducido respetado. No se han añadido parpadeos, introducciones ni animaciones decorativas.

## Validación

Comprobados login y resumen de administración, tabla y edición/guardado de un ejercicio ficticio, menú móvil y consumo a 390×844. Selector de mes ajustado a fila completa en móvil; sin desbordamiento horizontal de página. Tipografías cargadas comprobadas en navegador. Tablas conservan scroll interior.

Sintaxis JavaScript correcta y publicación Release con fuentes/recursos. No se han cambiado endpoints, autenticación, permisos ni datos Azure. No se han añadido pruebas que repliquen cambios de CSS; se ha realizado revisión visual y comprobación funcional local. Capturas con datos ficticios locales.
