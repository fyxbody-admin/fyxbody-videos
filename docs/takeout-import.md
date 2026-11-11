# Importación local desde Google Takeout

Extrae el archivo de Google en un directorio privado. El planificador lee el CSV original y prepara un informe por fila. Una coincidencia requiere el ID exacto en el nombre del archivo o en un JSON contiguo con un ID o URL de YouTube reconocible. Cuando el nombre solo contiene el título, revisa la correspondencia antes de añadirla manualmente al manifiesto.

```bash
python3 examples/plan-takeout.py ejercicios.csv /ruta/Takeout coincidencias.csv \
  --write-manifest --batch-directory lotes-takeout --existing calidades-listas.csv
```

El argumento opcional `--existing` usa el CSV de solo lectura generado con `examples/takeout-ready-inventory.sql` en la base de destino. Omite los ejercicios que ya tienen todas las calidades pedidas para el mismo ID de origen.

El manifiesto `youtube-id-map.json` contiene pares de ID de YouTube y ruta relativa. Usa una sola ruta por ID. El informe separa coincidencias, ambigüedades, URLs inválidas y archivos sin identificar. Los lotes conservan la referencia original `Order#Id`, el título y las calidades. Los archivos y las referencias se mantienen en el equipo.

Arranca el worker local con `VideoImports__Enabled=true`, `VideoImports__TakeoutOnly=true` y `VideoImports__TakeoutDirectory` apuntando al directorio extraído. Usa la misma base y el mismo contenedor que la importación original. El worker reclama trabajos existentes que tengan archivo local y conserva su ID. Para filas aún sin trabajo, valida y encola los lotes preparados. Usa una sola fila activa por ID de YouTube y espera a que finalice antes de encolar otra referencia de ese mismo origen. Antes de encolar, coteja el informe con los trabajos y las referencias existentes y conserva únicamente las filas que aún necesiten procesamiento. Los trabajos con una importación anterior pueden reutilizar las calidades ya disponibles.

Prueba primero con unas pocas filas pendientes, comprueba el cruce por referencia y valida las calidades disponibles. Los vídeos ambiguos quedan fuera hasta que se resuelva su identificación. El origen local conserva la resolución del archivo como límite y se publica en Azure mediante el mismo proceso de importación.
