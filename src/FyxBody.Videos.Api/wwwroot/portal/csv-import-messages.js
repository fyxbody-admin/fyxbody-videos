// CSV importer messages. Canonical CSV column names and quality values are never translated.
const entries = [
  [
    "csv.title",
    "CSV dialog title/action. Admin only.",
    "Importar CSV",
    "Importa CSV",
    "Import CSV",
    "استيراد CSV"
  ],
  [
    "csv.download",
    "Download inert CSV template, no API calls.",
    "Descargar plantilla CSV",
    "Descarrega la plantilla CSV",
    "Download CSV template",
    "تنزيل نموذج CSV"
  ],
  [
    "csv.intro",
    "Preview before explicit confirmation; permission and all-or-none policy.",
    "Selecciona un CSV con vídeos que tengas permiso para importar. Revisa todas las filas antes de confirmar; si alguna tiene errores, no se añade ningún trabajo.",
    "Selecciona un CSV amb vídeos que tinguis permís per importar. Revisa totes les files abans de confirmar; si alguna té errors, no s’afegeix cap treball.",
    "Choose a CSV containing videos you have permission to import. Review every row before confirming; if any row has errors, no jobs are added.",
    "اختر ملف CSV يتضمن فيديوهات تملك إذن استيرادها. راجع جميع الصفوف قبل التأكيد؛ لن تُضاف أي مهام إذا احتوى أي صف على أخطاء."
  ],
  [
    "csv.columns",
    "Fixed canonical Spanish headers and optional field behavior.",
    "Columnas: Referencia, Titulo, URL, Calidades. URL y Calidades son obligatorias. Referencia es el ID de origen; si está vacía se usa youtube:ID_DEL_VIDEO. Titulo vacío usa el título de YouTube al procesar.",
    "Columnes: Referencia, Titulo, URL, Calidades. URL i Calidades són obligatòries. Referencia és l’ID d’origen; si és buida s’utilitza youtube:ID_DEL_VIDEO. Titulo buit utilitza el títol de YouTube en processar.",
    "Columns: Referencia, Titulo, URL, Calidades. URL and Calidades are required. Referencia is the source ID; if empty, youtube:VIDEO_ID is used. An empty Titulo uses the YouTube title during processing.",
    "الأعمدة: Referencia, Titulo, URL, Calidades. حقلا URL وCalidades إلزاميان. يمثّل Referencia معرّف المصدر؛ إذا كان فارغًا تُستخدم الصيغة youtube:VIDEO_ID. عند ترك Titulo فارغًا يُستخدم عنوان YouTube أثناء المعالجة."
  ],
  [
    "csv.qualityHint",
    "Technical CSV values must remain ASCII, supported qualities/no upscale.",
    "Separa las calidades con punto y coma: 1080p;720p;480p;360p. No se amplía la resolución original. Se aceptan columnas separadas por coma o punto y coma; con punto y coma, escribe las calidades entre comillas.",
    "Separa les qualitats amb punt i coma: 1080p;720p;480p;360p. No s’amplia la resolució original. S’accepten columnes separades per coma o punt i coma; amb punt i coma, escriu les qualitats entre cometes.",
    "Separate qualities with semicolons: 1080p;720p;480p;360p. Source resolution is never increased. Columns may use commas or semicolons; with semicolon columns, quote the qualities cell.",
    "افصل الجودات بفاصلة منقوطة: 1080p;720p;480p;360p. لا تُرفع دقة المصدر. يمكن فصل الأعمدة بفواصل أو فواصل منقوطة؛ عند استخدام الفواصل المنقوطة ضع خلية الجودات بين علامتي اقتباس."
  ],
  [
    "csv.templateHint",
    "Template uses invalid placeholders requiring real URLs, no accidental enqueue.",
    "La plantilla contiene ejemplos: sustituye REPLACE_WITH_ID por el ID real o pega la URL completa de cada vídeo antes de validar.",
    "La plantilla conté exemples: substitueix REPLACE_WITH_ID per l’ID real o enganxa l’URL completa de cada vídeo abans de validar.",
    "The template contains examples: replace REPLACE_WITH_ID with the real ID or paste each full video URL before validating.",
    "يتضمن النموذج أمثلة: استبدل REPLACE_WITH_ID بالمعرّف الحقيقي أو الصق رابط كل فيديو كاملًا قبل التحقق."
  ],
  [
    "csv.fileLabel",
    "CSV file field label.",
    "Archivo CSV",
    "Fitxer CSV",
    "CSV file",
    "ملف CSV"
  ],
  [
    "csv.fileHint",
    "CSV encoding, byte and record limits.",
    "UTF-8 (con o sin BOM), máximo 1 MiB y 500 filas de datos.",
    "UTF-8 (amb BOM o sense), màxim 1 MiB i 500 files de dades.",
    "UTF-8 (with or without BOM), up to 1 MiB and 500 data rows.",
    "UTF-8 مع علامة BOM أو بدونها، بحد أقصى 1 MiB و500 صف بيانات."
  ],
  [
    "csv.chooseFile",
    "Custom localized file picker.",
    "Seleccionar CSV",
    "Selecciona CSV",
    "Choose CSV",
    "اختيار CSV"
  ],
  [
    "csv.preview",
    "Explicit read-only validation action.",
    "Validar y previsualizar",
    "Valida i previsualitza",
    "Validate and preview",
    "التحقق والمعاينة"
  ],
  [
    "csv.confirm",
    "Explicit enqueue action after valid preview.",
    "Confirmar y añadir a la cola",
    "Confirma i afegeix a la cua",
    "Confirm and add to queue",
    "التأكيد والإضافة إلى قائمة الانتظار"
  ],
  [
    "csv.confirmHint",
    "Reference/title existing metadata never overwritten and deduplication.",
    "La confirmación vuelve a validar el mismo archivo. Se reutilizan trabajos coincidentes sin duplicarlos; los conflictos de referencia o título se muestran como errores y no se sobrescriben datos.",
    "La confirmació torna a validar el mateix fitxer. Es reutilitzen treballs coincidents sense duplicar-los; els conflictes de referència o títol es mostren com a errors i no se sobreescriuen dades.",
    "Confirmation validates the same file again. Matching jobs are reused without duplicates; reference or title conflicts are shown as errors and existing data is not overwritten.",
    "يعيد التأكيد التحقق من الملف نفسه. تُعاد استخدام المهام المطابقة دون تكرار؛ تُعرض تعارضات المرجع أو العنوان كأخطاء ولا تُستبدل البيانات الموجودة."
  ],
  [
    "csv.reading",
    "CSV file is being decoded locally.",
    "Leyendo archivo…",
    "S’està llegint el fitxer…",
    "Reading file…",
    "جارٍ قراءة الملف…"
  ],
  [
    "csv.previewing",
    "CSV preview request pending.",
    "Validando filas…",
    "S’estan validant les files…",
    "Validating rows…",
    "جارٍ التحقق من الصفوف…"
  ],
  [
    "csv.queuing",
    "CSV enqueue request pending.",
    "Añadiendo trabajos a la cola…",
    "S’estan afegint treballs a la cua…",
    "Adding jobs to queue…",
    "جارٍ إضافة المهام إلى قائمة الانتظار…"
  ],
  [
    "csv.selected",
    "Selection is inert until explicit preview.",
    "Archivo preparado. Pulsa Validar y previsualizar para revisar sus filas.",
    "Fitxer preparat. Prem Valida i previsualitza per revisar-ne les files.",
    "File ready. Select Validate and preview to review its rows.",
    "الملف جاهز. اختر التحقق والمعاينة لمراجعة صفوفه."
  ],
  [
    "csv.queued",
    "Completed enqueue live status, dialog remains open.",
    "Solicitud guardada. El historial muestra el progreso de los trabajos.",
    "Sol·licitud desada. L’historial mostra el progrés dels treballs.",
    "Request saved. The history shows job progress.",
    "تم حفظ الطلب. يعرض السجل تقدم المهام."
  ],
  [
    "csv.tooLarge",
    "File size validation, max bytes inclusive.",
    "El CSV supera el máximo de 1 MiB.",
    "El CSV supera el màxim d’1 MiB.",
    "The CSV exceeds the 1 MiB limit.",
    "يتجاوز ملف CSV الحد الأقصى البالغ 1 MiB."
  ],
  [
    "csv.encoding",
    "Fatal UTF-8 decoding error.",
    "No se pudo leer como UTF-8. Guarda el archivo en CSV UTF-8 y vuelve a seleccionarlo.",
    "No s’ha pogut llegir com a UTF-8. Desa el fitxer en CSV UTF-8 i torna’l a seleccionar.",
    "The file could not be read as UTF-8. Save it as CSV UTF-8 and select it again.",
    "تعذّرت قراءة الملف بترميز UTF-8. احفظه بتنسيق CSV UTF-8 واختره مرة أخرى."
  ],
  [
    "csv.empty",
    "Empty file local validation.",
    "El archivo CSV está vacío.",
    "El fitxer CSV és buit.",
    "The CSV file is empty.",
    "ملف CSV فارغ."
  ],
  [
    "csv.summary",
    "Preview counts, preformatted localized number parameters.",
    "Filas: {total} · Válidas: {valid} · Con errores: {invalid}",
    "Files: {total} · Vàlides: {valid} · Amb errors: {invalid}",
    "Rows: {total} · Valid: {valid} · With errors: {invalid}",
    "الصفوف: {total} · الصالحة: {valid} · التي بها أخطاء: {invalid}"
  ],
  [
    "csv.ready",
    "No jobs created by successful preview.",
    "Validación correcta. Revisa la tabla y confirma para añadir los trabajos a la cola.",
    "Validació correcta. Revisa la taula i confirma per afegir els treballs a la cua.",
    "Validation passed. Review the table and confirm to add jobs to the queue.",
    "نجح التحقق. راجع الجدول وأكّد لإضافة المهام إلى قائمة الانتظار."
  ],
  [
    "csv.fixErrors",
    "All-or-none error report next action.",
    "No se ha añadido ningún trabajo. Corrige los errores en el CSV, vuelve a seleccionarlo y valida de nuevo.",
    "No s’ha afegit cap treball. Corregeix els errors al CSV, torna’l a seleccionar i valida’l de nou.",
    "No jobs were added. Correct the CSV errors, select the file again and validate it.",
    "لم تُضف أي مهام. صحّح أخطاء CSV ثم اختر الملف وتحقّق منه مرة أخرى."
  ],
  [
    "csv.uncertain",
    "A failed write response may still have queued jobs.",
    "No se pudo confirmar el resultado. Revisa el historial con Actualizar antes de repetir. Se requiere una nueva validación.",
    "No s’ha pogut confirmar el resultat. Revisa l’historial amb Actualitza abans de repetir. Cal una nova validació.",
    "The result could not be confirmed. Refresh the history before trying again. A new validation is required.",
    "تعذّر تأكيد النتيجة. حدّث السجل قبل المحاولة مجددًا. يلزم إجراء تحقق جديد."
  ],
  [
    "csv.line",
    "CSV physical line heading, distinct from visual table row count.",
    "Línea CSV",
    "Línia CSV",
    "CSV line",
    "سطر CSV"
  ],
  [
    "csv.reference",
    "Effective source reference heading.",
    "Referencia / ID de origen",
    "Referencia / ID d’origen",
    "Referencia / Source ID",
    "Referencia / معرّف المصدر"
  ],
  [
    "csv.url",
    "Technical source URL column.",
    "URL de YouTube",
    "URL de YouTube",
    "YouTube URL",
    "رابط YouTube"
  ],
  [
    "csv.qualities",
    "Requested resolution column.",
    "Calidades",
    "Qualitats",
    "Qualities",
    "الجودات"
  ],
  [
    "csv.result",
    "Per row validation/enqueue result column.",
    "Resultado",
    "Resultat",
    "Result",
    "النتيجة"
  ],
  [
    "csv.valid",
    "Per row successful preview only.",
    "Válida",
    "Vàlida",
    "Valid",
    "صالح"
  ],
  [
    "csv.reused",
    "Existing matching job reused, no extra job.",
    "Trabajo existente reutilizado",
    "Treball existent reutilitzat",
    "Existing job reused",
    "أُعيد استخدام مهمة موجودة"
  ],
  [
    "csv.sourceTitle",
    "Empty title is resolved during worker metadata probe.",
    "Título de YouTube al procesar",
    "Títol de YouTube en processar",
    "YouTube title during processing",
    "عنوان YouTube أثناء المعالجة"
  ]
];
export const csvImportMessages = Object.fromEntries(['es', 'ca', 'en', 'ar'].map((locale, index) => [locale, Object.fromEntries(entries.map(([key, , ...values]) => [key, values[index]]))]));
export const csvImportDescriptions = Object.fromEntries(entries.map(([key, description]) => [key, description]));
