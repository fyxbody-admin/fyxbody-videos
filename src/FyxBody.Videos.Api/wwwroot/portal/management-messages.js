const rows = [
  ['subpartners', 'Subpartners', 'Subpartners', 'Subpartners', 'الشركاء الفرعيون'],
  ['subpartnersDescription', 'Gestiona los profesionales, sus licencias y los vídeos que pueden reproducir.', 'Gestiona els professionals, les llicències i els vídeos que poden reproduir.', 'Manage professionals, their licences and the videos they can play.', 'أدر المحترفين وتراخيصهم والفيديوهات التي يمكنهم تشغيلها.'],
  ['noDelegation', 'Tu licencia no permite gestionar subpartners.', 'La teva llicència no permet gestionar subpartners.', 'Your licence does not allow subpartner management.', 'ترخيصك لا يسمح بإدارة الشركاء الفرعيين.'],
  ['createChild', 'Crear subpartner', 'Crea un subpartner', 'Create subpartner', 'إنشاء شريك فرعي'],
  ['editChild', 'Editar subpartner', 'Edita el subpartner', 'Edit subpartner', 'تعديل الشريك الفرعي'],
  ['noChildren', 'Todavía no hay subpartners.', 'Encara no hi ha subpartners.', 'There are no subpartners yet.', 'لا يوجد شركاء فرعيون بعد.'],
  ['backToChildren', 'Volver a subpartners', 'Torna als subpartners', 'Back to subpartners', 'العودة إلى الشركاء الفرعيين'],
  ['sourceLicense', 'Licencia de origen', 'Llicència d’origen', 'Source licence', 'الترخيص المصدر'],
  ['chooseSource', 'Elegir vídeos', 'Tria els vídeos', 'Choose videos', 'اختيار الفيديوهات'],
  ['noSources', 'No hay licencias activas con permiso para delegar vídeos.', 'No hi ha llicències actives amb permís per delegar vídeos.', 'No active licences allow video delegation.', 'لا توجد تراخيص نشطة تسمح بتفويض الفيديوهات.'],
  ['snapshot', 'Guardar aplica la selección a esta licencia. Los vídeos que se incorporen después deben asignarse en otra edición.', 'Desar aplica la selecció a aquesta llicència. Els vídeos que s’incorporin després s’han d’assignar en una altra edició.', 'Saving applies this selection to the licence. Assign any later additions in a subsequent edit.', 'يطبق الحفظ هذا الاختيار على الترخيص. عيّن الفيديوهات المضافة لاحقًا في تعديل آخر.'],
  ['allCurrent', 'Asignar todos los vídeos actuales', 'Assigna tots els vídeos actuals', 'Assign all current videos', 'تعيين جميع الفيديوهات الحالية'],
  ['searchVideos', 'Buscar por título o ID de origen', 'Cerca per títol o ID d’origen', 'Search by title or source ID', 'البحث حسب العنوان أو معرّف المصدر'],
  ['selected', '{count} vídeos seleccionados', '{count} vídeos seleccionats', '{count} videos selected', 'عدد الفيديوهات المختارة: {count}'],
  ['selectedColumn', 'Incluir', 'Inclou', 'Include', 'تضمين'],
  ['selectionLimit', 'Selecciona como máximo 1.000 vídeos o usa la opción para asignarlos todos.', 'Selecciona com a màxim 1.000 vídeos o fes servir l’opció per assignar-los tots.', 'Select at most 1,000 videos or use the option to assign all current videos.', 'اختر ما يصل إلى ١٠٠٠ فيديو أو استخدم خيار تعيين جميع الفيديوهات الحالية.'],
  ['privateVideos', 'Vídeos privados', 'Vídeos privats', 'Private videos', 'الفيديوهات الخاصة'],
  ['privateDescription', 'Sube y administra los vídeos que pertenecen a tu catálogo privado.', 'Puja i administra els vídeos que pertanyen al teu catàleg privat.', 'Upload and manage the videos in your private catalogue.', 'ارفع وأدر فيديوهات كتالوجك الخاص.'],
  ['privateUploads', 'Permitir subidas privadas', 'Permet pujades privades', 'Allow private uploads', 'السماح بالرفع الخاص'],
  ['allowManagement', 'Permitir gestionar subpartners', 'Permet gestionar subpartners', 'Allow subpartner management', 'السماح بإدارة الشركاء الفرعيين'],
  ['allowDelegation', 'Permitir delegar el catálogo', 'Permet delegar el catàleg', 'Allow catalogue delegation', 'السماح بتفويض الكتالوج'],
  ['uploadQuota', 'Cuota de subidas privadas', 'Quota de pujades privades', 'Private upload quota', 'حصة الرفع الخاص'],
  ['quotaStorage', 'Por espacio (GB)', 'Per espai (GB)', 'By storage (GB)', 'حسب المساحة (GB)'],
  ['quotaVideos', 'Por número de vídeos', 'Per nombre de vídeos', 'By video count', 'حسب عدد الفيديوهات'],
  ['quotaLimit', 'Límite de la cuota', 'Límit de la quota', 'Quota limit', 'حد الحصة'],
  ['gbValue', '{count} GB', '{count} GB', '{count} GB', '{count} GB'],
  ['videoLimit', '{count} vídeos', '{count} vídeos', '{count} videos', '{count} فيديو'],
  ['usage', 'Uso actual: {bytes} GB y {count} vídeos.', 'Ús actual: {bytes} GB i {count} vídeos.', 'Current usage: {bytes} GB and {count} videos.', 'الاستخدام الحالي: {bytes} GB وعدد الفيديوهات {count}.'],
  ['partnerReference', 'Referencia propia', 'Referència pròpia', 'Partner reference', 'مرجع الشريك'],
  ['upload', 'Subir vídeo privado', 'Puja un vídeo privat', 'Upload private video', 'رفع فيديو خاص'],
  ['noPrivate', 'Esta licencia aún no tiene vídeos privados.', 'Aquesta llicència encara no té vídeos privats.', 'This licence has no private videos yet.', 'لا توجد فيديوهات خاصة لهذا الترخيص بعد.'],
  ['delete', 'Eliminar vídeo', 'Suprimeix el vídeo', 'Delete video', 'حذف الفيديو'],
  ['deleteWarning', 'Se eliminará «{title}» y se retirará de los catálogos que lo tengan asignado.', 'Se suprimirà «{title}» i es retirarà dels catàlegs que el tinguin assignat.', '“{title}” will be deleted and removed from any catalogues it was assigned to.', 'سيتم حذف «{title}» وإزالته من أي كتالوج عُيّن له.'],
  ['saved', 'Cambios guardados.', 'Canvis desats.', 'Changes saved.', 'تم حفظ التغييرات.']
];
const locales = ['es', 'ca', 'en', 'ar'];
export const managementMessages = Object.fromEntries(locales.map((locale, index) => [
  locale, Object.fromEntries(rows.map(([key, ...translations]) => [`management.${key}`, translations[index]]))
]));
export const managementDescriptions = Object.fromEntries(rows.map(([key]) => [
  `management.${key}`, 'Partner management, delegated catalogue or private upload control.'
]));
