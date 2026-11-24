using System.Globalization;
using Avalonia.Markup.Xaml;

namespace Rayxel.Send.App;

/// <summary>
/// The window's copy, in the five languages the viewer ships. Picked once, from the system's
/// interface language; anything else falls back to English, and so does a key one language
/// lacks, so a missing translation shows English rather than nothing.
/// </summary>
public static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        ["en"] = new()
        {
            ["title"] = "Rayxel Send",
            ["on"] = "On",
            ["off"] = "Off",

            ["stOff"] = "Off",
            ["stWaiting"] = "Ready",
            ["stDiscoverable"] = "Discoverable",
            ["stPairing"] = "Pairing",
            ["stStreaming"] = "Streaming",
            ["dOff"] = "Switched off: nobody can find it or connect",
            ["dWaiting"] = "Waiting for a paired computer",
            ["dWaitingNone"] = "No computer is paired yet. Make it discoverable, then pick it in Rayxel on your PC",
            ["dDiscoverable"] = "Rayxel on your PC can find it now",
            ["dPairing"] = "Pairing with {0}",
            ["watching"] = "{0} is watching",
            ["fps"] = "fps",
            ["mbit"] = "Mbit/s",
            ["encodeMs"] = "encode {0} ms",
            ["instead"] = "{0} instead of {1}",
            ["inputOn"] = "input on",

            ["makeDiscoverable"] = "Make discoverable",
            ["discoverableFor"] = "Discoverable for {0} · Stop",
            ["switchOnFirst"] = "Switch it on first",

            ["pairWants"] = "{0} wants to connect",
            ["pairTypePin"] = "Type this PIN into Rayxel on that computer",
            ["pairExchanging"] = "Exchanging keys…",
            ["pairExpires"] = "Expires in {0}",
            ["cancel"] = "Cancel",

            ["pairedTitle"] = "Paired computers",
            ["pairedNone"] = "None yet.",
            ["lastSeen"] = "last seen {0}",
            ["neverSeen"] = "has not connected yet",
            ["watchingNow"] = "watching now",
            ["justNow"] = "just now",
            ["today"] = "today, {0}",
            ["forget"] = "Forget",
            ["forgetTitle"] = "Forget {0}?",
            ["forgetBody"] = "It will have to pair again before it can connect.",

            ["codecsTitle"] = "Codecs",
            ["codecReady"] = "available",
            ["codecNot"] = "not available",

            ["settings"] = "Settings",
            ["sName"] = "Name",
            ["sNameHint"] = "What Rayxel on the other computer shows",
            ["sNameEmpty"] = "This computer's name",
            ["sAllowInput"] = "Allow input",
            ["sAllowInputHint"] = "The controller, keyboard and mouse of the computer watching work here",
            ["sDiscoverableMinutes"] = "Stay discoverable for, minutes",
            ["sStartEnabled"] = "Switched on when the sender starts",
            ["sAllowUnpaired"] = "Allow unpaired viewers",
            ["sAllowUnpairedHint"] = "For older Rayxel builds: unencrypted, and never with input",
            ["sSource"] = "Source",
            ["sSourceHint"] = "Empty: gamescope on a Steam Deck, the first display on Windows",
            ["sSourcePlaceholder"] = "default",
            ["sPort"] = "Port",
            ["sPortHint"] = "Takes effect the next time the sender starts",
            ["sBuffer"] = "Rate buffer, frames",
            ["sBufferHint"] = "How many frames' worth of bits the rate control may borrow",
            ["sMaxFrame"] = "Largest frame, times the average",
            ["sMaxFrameHint"] = "0: no cap",
            ["sGop"] = "Keyframe every, frames",
            ["sGopHint"] = "0: only when the viewer asks for one",
            ["save"] = "Save",
            ["saved"] = "Saved",

            ["notRunning"] = "The sender is not running",
            ["notRunningBody"] = "Until it runs, Rayxel on other computers cannot find this one or connect to it.",
            ["start"] = "Start",
            ["starting"] = "Starting…",
            ["startNoAnswer"] = "It did not answer after starting. Try again, or run rayxel-send by hand to see why.",
            ["noDaemon"] = "{0} is not next to this app",
            ["startFailed"] = "Could not start it: {0}",
            ["retrying"] = "Looking for it every few seconds",

            ["quitSender"] = "Quit sender",
            ["quitHint"] = "Closing this window leaves the sender running. To stop it answering, switch it off.",
            ["quitTitle"] = "Quit the sender?",
            ["quitStreamingBody"] = "{0} is watching. The stream stops, and nobody can connect until the sender is started again.",
            ["quitCaptureTitle"] = "The game may close",
            ["quitCaptureBody"] = "The sender holds the game's picture. Ending it now can take the compositor down (gamescope on a Steam Deck), and the running game with it. Switching it off is safe.",
            ["quitAnyway"] = "Quit anyway",

            ["noAnswer"] = "The sender did not answer",
            ["refused"] = "The sender said: {0}",
        },
        ["ru"] = new()
        {
            ["title"] = "Rayxel Send",
            ["on"] = "Вкл",
            ["off"] = "Выкл",

            ["stOff"] = "Выключен",
            ["stWaiting"] = "Готов",
            ["stDiscoverable"] = "Виден в сети",
            ["stPairing"] = "Сопряжение",
            ["stStreaming"] = "Трансляция",
            ["dOff"] = "Выключен: его не найти и к нему не подключиться",
            ["dWaiting"] = "Ждёт сопряжённый компьютер",
            ["dWaitingNone"] = "Сопряжённых компьютеров пока нет. Сделайте его видимым и выберите в Rayxel на ПК",
            ["dDiscoverable"] = "Rayxel на ПК сейчас может его найти",
            ["dPairing"] = "Сопряжение с {0}",
            ["watching"] = "Смотрит {0}",
            ["fps"] = "к/с",
            ["mbit"] = "Мбит/с",
            ["encodeMs"] = "кодер {0} мс",
            ["instead"] = "{0} вместо {1}",
            ["inputOn"] = "управление включено",

            ["makeDiscoverable"] = "Сделать видимым",
            ["discoverableFor"] = "Виден ещё {0} · Скрыть",
            ["switchOnFirst"] = "Сначала включите",

            ["pairWants"] = "{0} хочет подключиться",
            ["pairTypePin"] = "Введите этот PIN в Rayxel на том компьютере",
            ["pairExchanging"] = "Обмен ключами…",
            ["pairExpires"] = "Осталось {0}",
            ["cancel"] = "Отмена",

            ["pairedTitle"] = "Сопряжённые компьютеры",
            ["pairedNone"] = "Пока нет.",
            ["lastSeen"] = "был на связи {0}",
            ["neverSeen"] = "ещё не подключался",
            ["watchingNow"] = "смотрит сейчас",
            ["justNow"] = "только что",
            ["today"] = "сегодня, {0}",
            ["forget"] = "Забыть",
            ["forgetTitle"] = "Забыть {0}?",
            ["forgetBody"] = "Чтобы снова подключиться, ему придётся пройти сопряжение заново.",

            ["codecsTitle"] = "Кодеки",
            ["codecReady"] = "доступен",
            ["codecNot"] = "недоступен",

            ["settings"] = "Настройки",
            ["sName"] = "Имя",
            ["sNameHint"] = "Так его покажет Rayxel на другом компьютере",
            ["sNameEmpty"] = "Имя этого компьютера",
            ["sAllowInput"] = "Разрешить управление",
            ["sAllowInputHint"] = "Геймпад, клавиатура и мышь компьютера, на котором смотрят, работают здесь",
            ["sDiscoverableMinutes"] = "Сколько минут быть видимым",
            ["sStartEnabled"] = "Включён сразу при запуске",
            ["sAllowUnpaired"] = "Пускать без сопряжения",
            ["sAllowUnpairedHint"] = "Для старых версий Rayxel: без шифрования и никогда с управлением",
            ["sSource"] = "Источник",
            ["sSourceHint"] = "Пусто — gamescope на Steam Deck, первый дисплей в Windows",
            ["sSourcePlaceholder"] = "по умолчанию",
            ["sPort"] = "Порт",
            ["sPortHint"] = "Вступит в силу при следующем запуске отправителя",
            ["sBuffer"] = "Буфер битрейта, кадров",
            ["sBufferHint"] = "Сколько кадров битов регулятор битрейта может занять вперёд",
            ["sMaxFrame"] = "Предел кадра, во сколько раз больше среднего",
            ["sMaxFrameHint"] = "0 — без предела",
            ["sGop"] = "Опорный кадр каждые, кадров",
            ["sGopHint"] = "0 — только когда его попросит приёмник",
            ["save"] = "Сохранить",
            ["saved"] = "Сохранено",

            ["notRunning"] = "Отправитель не запущен",
            ["notRunningBody"] = "Пока он не запущен, Rayxel на других компьютерах не найдёт этот и не подключится к нему.",
            ["start"] = "Запустить",
            ["starting"] = "Запускается…",
            ["startNoAnswer"] = "После запуска он не ответил. Попробуйте ещё раз или запустите rayxel-send вручную, чтобы увидеть причину.",
            ["noDaemon"] = "{0} не найден рядом с этим приложением",
            ["startFailed"] = "Не удалось запустить: {0}",
            ["retrying"] = "Проверяем каждые несколько секунд",

            ["quitSender"] = "Завершить отправитель",
            ["quitHint"] = "Если закрыть это окно, отправитель продолжит работать. Чтобы он перестал отвечать, выключите его.",
            ["quitTitle"] = "Завершить отправитель?",
            ["quitStreamingBody"] = "Смотрит {0}. Трансляция остановится, и никто не подключится, пока отправитель не запустят снова.",
            ["quitCaptureTitle"] = "Игра может закрыться",
            ["quitCaptureBody"] = "Отправитель держит картинку игры. Если завершить его сейчас, может упасть композитор (gamescope на Steam Deck), а с ним и запущенная игра. Выключить его безопасно.",
            ["quitAnyway"] = "Всё равно завершить",

            ["noAnswer"] = "Отправитель не ответил",
            ["refused"] = "Ответ отправителя: {0}",
        },
        ["uk"] = new()
        {
            ["title"] = "Rayxel Send",
            ["on"] = "Увімк",
            ["off"] = "Вимк",

            ["stOff"] = "Вимкнено",
            ["stWaiting"] = "Готовий",
            ["stDiscoverable"] = "Видимий у мережі",
            ["stPairing"] = "Сполучення",
            ["stStreaming"] = "Трансляція",
            ["dOff"] = "Вимкнено: його не знайти і до нього не під'єднатися",
            ["dWaiting"] = "Чекає на сполучений комп'ютер",
            ["dWaitingNone"] = "Сполучених комп'ютерів поки немає. Зробіть його видимим і оберіть у Rayxel на ПК",
            ["dDiscoverable"] = "Rayxel на ПК зараз може його знайти",
            ["dPairing"] = "Сполучення з {0}",
            ["watching"] = "Дивиться {0}",
            ["fps"] = "к/с",
            ["mbit"] = "Мбіт/с",
            ["encodeMs"] = "кодер {0} мс",
            ["instead"] = "{0} замість {1}",
            ["inputOn"] = "керування увімкнено",

            ["makeDiscoverable"] = "Зробити видимим",
            ["discoverableFor"] = "Видимий ще {0} · Сховати",
            ["switchOnFirst"] = "Спершу увімкніть",

            ["pairWants"] = "{0} хоче під'єднатися",
            ["pairTypePin"] = "Введіть цей PIN у Rayxel на тому комп'ютері",
            ["pairExchanging"] = "Обмін ключами…",
            ["pairExpires"] = "Лишилося {0}",
            ["cancel"] = "Скасувати",

            ["pairedTitle"] = "Сполучені комп'ютери",
            ["pairedNone"] = "Поки немає.",
            ["lastSeen"] = "був на зв'язку {0}",
            ["neverSeen"] = "ще не під'єднувався",
            ["watchingNow"] = "дивиться зараз",
            ["justNow"] = "щойно",
            ["today"] = "сьогодні, {0}",
            ["forget"] = "Забути",
            ["forgetTitle"] = "Забути {0}?",
            ["forgetBody"] = "Щоб знову під'єднатися, йому доведеться пройти сполучення заново.",

            ["codecsTitle"] = "Кодеки",
            ["codecReady"] = "доступний",
            ["codecNot"] = "недоступний",

            ["settings"] = "Налаштування",
            ["sName"] = "Ім'я",
            ["sNameHint"] = "Так його покаже Rayxel на іншому комп'ютері",
            ["sNameEmpty"] = "Ім'я цього комп'ютера",
            ["sAllowInput"] = "Дозволити керування",
            ["sAllowInputHint"] = "Геймпад, клавіатура і миша комп'ютера, на якому дивляться, працюють тут",
            ["sDiscoverableMinutes"] = "Скільки хвилин бути видимим",
            ["sStartEnabled"] = "Увімкнений одразу під час запуску",
            ["sAllowUnpaired"] = "Пускати без сполучення",
            ["sAllowUnpairedHint"] = "Для старих версій Rayxel: без шифрування і ніколи з керуванням",
            ["sSource"] = "Джерело",
            ["sSourceHint"] = "Порожньо — gamescope на Steam Deck, перший дисплей у Windows",
            ["sSourcePlaceholder"] = "за умовчанням",
            ["sPort"] = "Порт",
            ["sPortHint"] = "Набуде чинності під час наступного запуску відправника",
            ["sBuffer"] = "Буфер бітрейту, кадрів",
            ["sBufferHint"] = "Скільки кадрів бітів регулятор бітрейту може позичити наперед",
            ["sMaxFrame"] = "Межа кадру, у скільки разів більше за середній",
            ["sMaxFrameHint"] = "0 — без межі",
            ["sGop"] = "Опорний кадр кожні, кадрів",
            ["sGopHint"] = "0 — лише коли його попросить приймач",
            ["save"] = "Зберегти",
            ["saved"] = "Збережено",

            ["notRunning"] = "Відправник не запущений",
            ["notRunningBody"] = "Поки він не запущений, Rayxel на інших комп'ютерах не знайде цей і не під'єднається до нього.",
            ["start"] = "Запустити",
            ["starting"] = "Запускається…",
            ["startNoAnswer"] = "Після запуску він не відповів. Спробуйте ще раз або запустіть rayxel-send вручну, щоб побачити причину.",
            ["noDaemon"] = "{0} не знайдено поруч із цією програмою",
            ["startFailed"] = "Не вдалося запустити: {0}",
            ["retrying"] = "Перевіряємо кожні кілька секунд",

            ["quitSender"] = "Завершити відправника",
            ["quitHint"] = "Якщо закрити це вікно, відправник працюватиме далі. Щоб він перестав відповідати, вимкніть його.",
            ["quitTitle"] = "Завершити відправника?",
            ["quitStreamingBody"] = "Дивиться {0}. Трансляція зупиниться, і ніхто не під'єднається, доки відправника не запустять знову.",
            ["quitCaptureTitle"] = "Гра може закритися",
            ["quitCaptureBody"] = "Відправник тримає картинку гри. Якщо завершити його зараз, може впасти композитор (gamescope на Steam Deck), а з ним і запущена гра. Вимкнути його безпечно.",
            ["quitAnyway"] = "Все одно завершити",

            ["noAnswer"] = "Відправник не відповів",
            ["refused"] = "Відповідь відправника: {0}",
        },
        ["de"] = new()
        {
            ["title"] = "Rayxel Send",
            ["on"] = "Ein",
            ["off"] = "Aus",

            ["stOff"] = "Aus",
            ["stWaiting"] = "Bereit",
            ["stDiscoverable"] = "Sichtbar",
            ["stPairing"] = "Kopplung",
            ["stStreaming"] = "Überträgt",
            ["dOff"] = "Ausgeschaltet: niemand findet ihn oder verbindet sich",
            ["dWaiting"] = "Wartet auf einen gekoppelten Computer",
            ["dWaitingNone"] = "Noch kein Computer gekoppelt. Machen Sie ihn sichtbar und wählen Sie ihn in Rayxel auf dem PC",
            ["dDiscoverable"] = "Rayxel auf dem PC kann ihn jetzt finden",
            ["dPairing"] = "Kopplung mit {0}",
            ["watching"] = "{0} schaut zu",
            ["fps"] = "fps",
            ["mbit"] = "Mbit/s",
            ["encodeMs"] = "Encoder {0} ms",
            ["instead"] = "{0} statt {1}",
            ["inputOn"] = "Eingaben an",

            ["makeDiscoverable"] = "Sichtbar machen",
            ["discoverableFor"] = "Noch {0} sichtbar · Beenden",
            ["switchOnFirst"] = "Zuerst einschalten",

            ["pairWants"] = "{0} möchte sich verbinden",
            ["pairTypePin"] = "Geben Sie diese PIN in Rayxel auf jenem Computer ein",
            ["pairExchanging"] = "Schlüssel werden ausgetauscht…",
            ["pairExpires"] = "Läuft ab in {0}",
            ["cancel"] = "Abbrechen",

            ["pairedTitle"] = "Gekoppelte Computer",
            ["pairedNone"] = "Noch keine.",
            ["lastSeen"] = "zuletzt {0}",
            ["neverSeen"] = "noch nie verbunden",
            ["watchingNow"] = "schaut gerade zu",
            ["justNow"] = "gerade eben",
            ["today"] = "heute, {0}",
            ["forget"] = "Vergessen",
            ["forgetTitle"] = "{0} vergessen?",
            ["forgetBody"] = "Er muss neu gekoppelt werden, bevor er sich wieder verbinden kann.",

            ["codecsTitle"] = "Codecs",
            ["codecReady"] = "verfügbar",
            ["codecNot"] = "nicht verfügbar",

            ["settings"] = "Einstellungen",
            ["sName"] = "Name",
            ["sNameHint"] = "So zeigt ihn Rayxel auf dem anderen Computer",
            ["sNameEmpty"] = "Name dieses Computers",
            ["sAllowInput"] = "Eingaben erlauben",
            ["sAllowInputHint"] = "Controller, Tastatur und Maus des zuschauenden Computers wirken hier",
            ["sDiscoverableMinutes"] = "Wie lange sichtbar, Minuten",
            ["sStartEnabled"] = "Beim Start des Senders eingeschaltet",
            ["sAllowUnpaired"] = "Ungekoppelte Viewer zulassen",
            ["sAllowUnpairedHint"] = "Für ältere Rayxel-Versionen: unverschlüsselt und nie mit Eingaben",
            ["sSource"] = "Quelle",
            ["sSourceHint"] = "Leer: gamescope auf einem Steam Deck, der erste Bildschirm unter Windows",
            ["sSourcePlaceholder"] = "Standard",
            ["sPort"] = "Port",
            ["sPortHint"] = "Gilt ab dem nächsten Start des Senders",
            ["sBuffer"] = "Ratenpuffer, Bilder",
            ["sBufferHint"] = "Wie viele Bilder an Bits die Ratensteuerung vorab leihen darf",
            ["sMaxFrame"] = "Größtes Bild, Vielfaches des Mittels",
            ["sMaxFrameHint"] = "0: keine Grenze",
            ["sGop"] = "Schlüsselbild alle, Bilder",
            ["sGopHint"] = "0: nur wenn der Viewer eines anfordert",
            ["save"] = "Speichern",
            ["saved"] = "Gespeichert",

            ["notRunning"] = "Der Sender läuft nicht",
            ["notRunningBody"] = "Solange er nicht läuft, kann Rayxel auf anderen Computern diesen weder finden noch sich mit ihm verbinden.",
            ["start"] = "Starten",
            ["starting"] = "Wird gestartet…",
            ["startNoAnswer"] = "Nach dem Start kam keine Antwort. Versuchen Sie es erneut oder starten Sie rayxel-send von Hand, um den Grund zu sehen.",
            ["noDaemon"] = "{0} liegt nicht neben dieser App",
            ["startFailed"] = "Start fehlgeschlagen: {0}",
            ["retrying"] = "Wird alle paar Sekunden gesucht",

            ["quitSender"] = "Sender beenden",
            ["quitHint"] = "Wenn Sie dieses Fenster schließen, läuft der Sender weiter. Damit er nicht mehr antwortet, schalten Sie ihn aus.",
            ["quitTitle"] = "Sender beenden?",
            ["quitStreamingBody"] = "{0} schaut zu. Die Übertragung stoppt, und niemand kann sich verbinden, bis der Sender wieder gestartet wird.",
            ["quitCaptureTitle"] = "Das Spiel kann sich schließen",
            ["quitCaptureBody"] = "Der Sender hält das Bild des Spiels. Wird er jetzt beendet, kann der Compositor abstürzen (gamescope auf einem Steam Deck) und das laufende Spiel mit ihm. Ausschalten ist sicher.",
            ["quitAnyway"] = "Trotzdem beenden",

            ["noAnswer"] = "Der Sender hat nicht geantwortet",
            ["refused"] = "Antwort des Senders: {0}",
        },
        ["es"] = new()
        {
            ["title"] = "Rayxel Send",
            ["on"] = "Activado",
            ["off"] = "Desactivado",

            ["stOff"] = "Apagado",
            ["stWaiting"] = "Listo",
            ["stDiscoverable"] = "Visible",
            ["stPairing"] = "Emparejando",
            ["stStreaming"] = "Transmitiendo",
            ["dOff"] = "Apagado: nadie puede encontrarlo ni conectarse",
            ["dWaiting"] = "Esperando a un equipo emparejado",
            ["dWaitingNone"] = "Aún no hay equipos emparejados. Hágalo visible y elíjalo en Rayxel en el PC",
            ["dDiscoverable"] = "Rayxel en el PC puede encontrarlo ahora",
            ["dPairing"] = "Emparejando con {0}",
            ["watching"] = "{0} está mirando",
            ["fps"] = "fps",
            ["mbit"] = "Mbit/s",
            ["encodeMs"] = "codificación {0} ms",
            ["instead"] = "{0} en lugar de {1}",
            ["inputOn"] = "control activado",

            ["makeDiscoverable"] = "Hacer visible",
            ["discoverableFor"] = "Visible {0} más · Ocultar",
            ["switchOnFirst"] = "Enciéndalo primero",

            ["pairWants"] = "{0} quiere conectarse",
            ["pairTypePin"] = "Escriba este PIN en Rayxel en ese equipo",
            ["pairExchanging"] = "Intercambiando claves…",
            ["pairExpires"] = "Caduca en {0}",
            ["cancel"] = "Cancelar",

            ["pairedTitle"] = "Equipos emparejados",
            ["pairedNone"] = "Ninguno todavía.",
            ["lastSeen"] = "última vez {0}",
            ["neverSeen"] = "aún no se ha conectado",
            ["watchingNow"] = "mirando ahora",
            ["justNow"] = "hace un momento",
            ["today"] = "hoy, {0}",
            ["forget"] = "Olvidar",
            ["forgetTitle"] = "¿Olvidar {0}?",
            ["forgetBody"] = "Tendrá que emparejarse de nuevo para poder conectarse.",

            ["codecsTitle"] = "Códecs",
            ["codecReady"] = "disponible",
            ["codecNot"] = "no disponible",

            ["settings"] = "Ajustes",
            ["sName"] = "Nombre",
            ["sNameHint"] = "Así lo muestra Rayxel en el otro equipo",
            ["sNameEmpty"] = "El nombre de este equipo",
            ["sAllowInput"] = "Permitir el control",
            ["sAllowInputHint"] = "El mando, el teclado y el ratón del equipo que mira funcionan aquí",
            ["sDiscoverableMinutes"] = "Minutos visible",
            ["sStartEnabled"] = "Encendido al iniciar el emisor",
            ["sAllowUnpaired"] = "Permitir visores sin emparejar",
            ["sAllowUnpairedHint"] = "Para versiones antiguas de Rayxel: sin cifrado y nunca con control",
            ["sSource"] = "Fuente",
            ["sSourceHint"] = "Vacío: gamescope en una Steam Deck, la primera pantalla en Windows",
            ["sSourcePlaceholder"] = "predeterminada",
            ["sPort"] = "Puerto",
            ["sPortHint"] = "Se aplica la próxima vez que se inicie el emisor",
            ["sBuffer"] = "Búfer de tasa, fotogramas",
            ["sBufferHint"] = "Cuántos fotogramas de bits puede tomar prestados el control de tasa",
            ["sMaxFrame"] = "Fotograma máximo, veces la media",
            ["sMaxFrameHint"] = "0: sin límite",
            ["sGop"] = "Fotograma clave cada, fotogramas",
            ["sGopHint"] = "0: solo cuando el visor lo pida",
            ["save"] = "Guardar",
            ["saved"] = "Guardado",

            ["notRunning"] = "El emisor no está en marcha",
            ["notRunningBody"] = "Mientras no esté en marcha, Rayxel en otros equipos no puede encontrar este ni conectarse a él.",
            ["start"] = "Iniciar",
            ["starting"] = "Iniciando…",
            ["startNoAnswer"] = "No respondió tras iniciarse. Inténtelo de nuevo o ejecute rayxel-send a mano para ver por qué.",
            ["noDaemon"] = "{0} no está junto a esta aplicación",
            ["startFailed"] = "No se pudo iniciar: {0}",
            ["retrying"] = "Se busca cada pocos segundos",

            ["quitSender"] = "Cerrar el emisor",
            ["quitHint"] = "Cerrar esta ventana deja el emisor en marcha. Para que deje de responder, apáguelo.",
            ["quitTitle"] = "¿Cerrar el emisor?",
            ["quitStreamingBody"] = "{0} está mirando. La transmisión se detiene y nadie podrá conectarse hasta que el emisor se inicie de nuevo.",
            ["quitCaptureTitle"] = "El juego puede cerrarse",
            ["quitCaptureBody"] = "El emisor tiene la imagen del juego. Cerrarlo ahora puede hacer caer el compositor (gamescope en una Steam Deck), y con él el juego en curso. Apagarlo es seguro.",
            ["quitAnyway"] = "Cerrar de todos modos",

            ["noAnswer"] = "El emisor no respondió",
            ["refused"] = "Respuesta del emisor: {0}",
        },
    };

    /// <summary>The language in use: "en", "ru", "uk", "de" or "es".</summary>
    public static string Current { get; } = Pick();

    /// <summary>
    /// Numbers and dates in the language's own notation. The user's own culture when it is
    /// in the same language (so an English UI on a British system writes British dates),
    /// else the language's default.
    /// </summary>
    public static CultureInfo Culture { get; } = PickCulture();

    private static string Pick()
    {
        // LANGUAGE is how Linux desktops say which interface language comes first; .NET
        // reads only LC_ALL, LC_MESSAGES and LANG, so look at it first.
        string? language = Environment.GetEnvironmentVariable("LANGUAGE");
        if (!string.IsNullOrEmpty(language))
        {
            foreach (string entry in language.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string code = entry.Split('_', '-', '.')[0].ToLowerInvariant();
                if (Table.ContainsKey(code)) return code;
            }
        }
        for (var culture = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            if (Table.ContainsKey(culture.TwoLetterISOLanguageName)) return culture.TwoLetterISOLanguageName;
        }
        return "en";
    }

    private static CultureInfo PickCulture()
    {
        var own = CultureInfo.CurrentCulture;
        if (own.TwoLetterISOLanguageName == Current) return own;
        try
        {
            return CultureInfo.GetCultureInfo(Current);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    public static string T(string key) =>
        Table[Current].TryGetValue(key, out var value) ? value
        : Table["en"].TryGetValue(key, out var english) ? english
        : key;

    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);

    public static string Num(double value, string format) => value.ToString(format, Culture);

    public static string State(string state) => state switch
    {
        "off" => T("stOff"),
        "waiting" => T("stWaiting"),
        "discoverable" => T("stDiscoverable"),
        "pairing" => T("stPairing"),
        "streaming" => T("stStreaming"),
        _ => state,
    };

    /// <summary>A codec as people write it, whatever case the sender sends it in.</summary>
    public static string Codec(string name) => name.ToLowerInvariant() switch
    {
        "hevc" or "h265" or "h.265" => "HEVC",
        "av1" => "AV1",
        "h264" or "h.264" or "avc" => "H.264",
        "pyrowave" => "PyroWave",
        _ => name,
    };

    /// <summary>Time left as "4:59", or "1:04:59" past an hour. Rounded up, so it starts on the full minute.</summary>
    public static string Countdown(long milliseconds)
    {
        long seconds = Math.Max(0, (milliseconds + 999) / 1000);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>"last seen just now", "last seen today, 14:03", "last seen 21.11.2026 9:15".</summary>
    public static string LastSeen(long lastSeenMs, long nowMs)
    {
        if (lastSeenMs <= 0) return T("neverSeen");
        if (nowMs - lastSeenMs < 120_000) return F("lastSeen", T("justNow"));
        var when = DateTimeOffset.FromUnixTimeMilliseconds(lastSeenMs).ToLocalTime();
        string text = when.Date == DateTime.Today
            ? F("today", when.ToString("t", Culture))
            : when.ToString("g", Culture);
        return F("lastSeen", text);
    }

    /// <summary>"123 456": six digits are read and typed more surely in two threes.</summary>
    public static string Pin(string pin) => pin.Length == 6 ? $"{pin[..3]} {pin[3..]}" : pin;
}

/// <summary>
/// <c>{app:T key}</c> in XAML: the string for <c>key</c> in the current language. The
/// language is fixed for the life of the process, so the lookup happens once, when the
/// window is built.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.T(Key);
}
