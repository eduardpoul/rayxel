namespace Rayxel.Ui;

/// <summary>
/// The interface copy, in the five languages the design ships. The table itself is
/// generated from the handoff prototype so the wording stays the designer's.
/// </summary>
public static partial class Strings
{
    /// <summary>
    /// Copy the handoff does not cover. Kept apart from the generated table so regenerating
    /// that from the designer's deck never overwrites these, and so it stays obvious which
    /// wording is theirs and which is ours.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Additions = new()
    {
        ["ru"] = new()
        {
            ["audio"] = "Звук",
            ["audioIn"] = "вход",
            ["audioOut"] = "выход",
            ["audioDefault"] = "по умолчанию",
            ["audioNoInput"] = "у этой карты не найден аудиовход HDMI",
            ["videoBright"] = "Яркость видео",
            ["videoBrightDriver"] = "PQ производит фильтр драйвера",
            ["quit"] = "Выйти",
            ["quitTitle"] = "Выйти из Rayxel?",
            ["quitBody"] = "Видео остановится. Настройки сохранятся.",
            ["cancel"] = "Отмена",
            ["thDelay"] = "мс",
            ["tenBitCost"] = "10 бит — вдвое больше данных и рывки",
            ["capNote"] = "канал {0} MiB/s",
            // "масштабатор" is not a word anyone says out loud.
            ["upscaler"] = "Апскейлер",
            ["whyComposed"] = "окно рисует композитор Windows — минус целый кадр, F11",
            ["whyCodec"] = "в тракте кодек — картинку разжимают за нас",
            ["whyBudget"] = "не укладываемся в бюджет кадра",
            ["whyDropped"] = "кадры теряются",
            ["whyNoSignal"] = "источник молчит",

            // The bottom bar, its flyouts and the restyled screens.
            ["hz"] = "Гц",
            ["ms"] = "мс",
            ["nits"] = "нит",
            ["budgetOf"] = "{0} из {1} мс",
            ["modeTitle"] = "Режим захвата",
            ["rawGroup"] = "Без сжатия",
            ["codecGroup"] = "Через сжатие — дольше и с потерей качества",
            ["modesShown"] = "{0} из {1}",
            ["closeM"] = "M — закрыть",
            ["scalerTitle"] = "Масштабирование",
            ["perFrame"] = "цена за кадр",
            ["dNone"] = "Картинка как есть",
            ["dNis"] = "Масштаб и резкость, почти бесплатно",
            ["dVsr"] = "Нейросетевое увеличение в драйвере NVIDIA",
            ["dVsrIntel"] = "Увеличение в драйвере Intel",
            ["scaleUp"] = "Увеличение ×{0}: {1} → {2} строк · клавиши 1, 2, 3",
            ["scaleInert"] = "Кадр не меньше окна: увеличивать нечего, {0} будет простаивать",
            ["device"] = "Устройство",
            ["audioSend"] = "Передавать звук карты",
            ["uiBright"] = "Яркость интерфейса в HDR",
            ["window"] = "Окно",
            ["wWindowed"] = "В окне",
            ["wBorderless"] = "Без рамки",
            ["hotkeys"] = "Esc — панель · T — телеметрия · M — режимы · 1, 2, 3 — масштаб · F11 — экран",
            ["ttMode"] = "Режим захвата · M",
            ["ttScaler"] = "Масштабирование · 1, 2, 3",
            ["ttAudioOn"] = "Звук карты включён",
            ["ttAudioOff"] = "Звук карты выключен",
            ["ttTele"] = "Телеметрия · T",
            ["ttFull"] = "Полный экран без рамки · F11",
            ["ttWindow"] = "Вернуть окно · F11",
            ["ttBudget"] = "GPU {0} + CPU {1} из {2} мс, запас {3} мс",
            ["infoComposedTitle"] = "Кадр проходит через композитор Windows",
            ["infoComposedBody"] = "Это +{0} мс, целый кадр — больше, чем RTX VSR и HDR вместе. В полноэкранном режиме без рамки вывод снова прямой.",
            ["infoBudgetTitle"] = "Обработка не укладывается в кадр",
            ["infoBudgetBody"] = "Она занимает больше, чем длится кадр, и кадры опаздывают. Выберите NIS или режим с меньшей частотой.",
            ["tGpu"] = "GPU",
            ["tHead"] = "запас",
            ["tAge"] = "возраст",
            ["tFrames"] = "кадра",
            ["tJitter"] = "дрожание",
            ["tDropped"] = "потеряно",
            ["tLate"] = "опоздали",
            ["nsTitle"] = "Нет сигнала",
            ["nsBody"] = "Устройство подключено, но кадры не приходят. Обычно консоль спит или отключён HDMI.",
            ["nsWait"] = "Ждём сигнал — картинка вернётся сама, параметры сохранены",
            ["busyTitle"] = "Устройство занято",
            ["busyBody"] = "{0} уже читает другое приложение, например OBS или вкладка браузера. Закройте его или выберите другое устройство.",
            ["lostTitle"] = "Устройство недоступно",
            ["lostBody"] = "Связь с {0} потеряна. Проверьте кабель или выберите другое устройство.",
            ["otherDevice"] = "Другое устройство",
            ["dBusyNow"] = "занято другим приложением",
            ["splashStatus"] = "Читаем режимы захвата…",
            ["retryNow"] = "Повторить",
            ["warnFix"] = "Нажмите на значок, чтобы перейти без рамки, или F11.",
        },
        ["en"] = new()
        {
            ["audio"] = "Audio",
            ["audioIn"] = "input",
            ["audioOut"] = "output",
            ["audioDefault"] = "default",
            ["audioNoInput"] = "no HDMI audio input found on this card",
            ["videoBright"] = "Video brightness",
            ["videoBrightDriver"] = "the driver filter produces the PQ values",
            ["quit"] = "Quit",
            ["quitTitle"] = "Quit Rayxel?",
            ["quitBody"] = "The video stops. Settings are kept.",
            ["cancel"] = "Cancel",
            ["thDelay"] = "ms",
            ["tenBitCost"] = "10-bit — twice the data, and it stutters",
            ["capNote"] = "{0} MiB/s link",
            ["whyComposed"] = "the Windows compositor draws the window — a whole frame, F11",
            ["whyCodec"] = "a codec is in the path — someone is decompressing for us",
            ["whyBudget"] = "over the frame budget",
            ["whyDropped"] = "frames are being dropped",
            ["whyNoSignal"] = "the source is silent",

            ["hz"] = "Hz",
            ["ms"] = "ms",
            ["nits"] = "nits",
            ["budgetOf"] = "{0} of {1} ms",
            ["modeTitle"] = "Capture mode",
            ["rawGroup"] = "Uncompressed",
            ["codecGroup"] = "Compressed — slower, and it costs quality",
            ["modesShown"] = "{0} of {1}",
            ["closeM"] = "M closes",
            ["scalerTitle"] = "Scaling",
            ["perFrame"] = "cost per frame",
            ["dNone"] = "The picture as it arrives",
            ["dNis"] = "Scaling and sharpening, nearly free",
            ["dVsr"] = "Neural upscaling in the NVIDIA driver",
            ["dVsrIntel"] = "Upscaling in the Intel driver",
            ["scaleUp"] = "×{0} up: {1} → {2} lines · keys 1, 2, 3",
            ["scaleInert"] = "The frame is not smaller than the window: nothing to upscale, {0} would idle",
            ["device"] = "Device",
            ["audioSend"] = "Play the card's audio",
            ["uiBright"] = "Interface brightness in HDR",
            ["window"] = "Window",
            ["wWindowed"] = "Windowed",
            ["wBorderless"] = "Borderless",
            ["hotkeys"] = "Esc bar · T telemetry · M modes · 1, 2, 3 scaling · F11 screen",
            ["ttMode"] = "Capture mode · M",
            ["ttScaler"] = "Scaling · 1, 2, 3",
            ["ttAudioOn"] = "Card audio on",
            ["ttAudioOff"] = "Card audio off",
            ["ttTele"] = "Telemetry · T",
            ["ttFull"] = "Borderless full screen · F11",
            ["ttWindow"] = "Back to a window · F11",
            ["ttBudget"] = "GPU {0} + CPU {1} of {2} ms, {3} ms headroom",
            ["infoComposedTitle"] = "Frames go through the Windows compositor",
            ["infoComposedBody"] = "That is +{0} ms, a whole frame — more than RTX VSR and HDR together. Borderless full screen makes the output direct again.",
            ["infoBudgetTitle"] = "Processing does not fit in a frame",
            ["infoBudgetBody"] = "It takes longer than a frame lasts, so frames arrive late. Choose NIS or a lower-rate mode.",
            ["tGpu"] = "GPU",
            ["tHead"] = "headroom",
            ["tAge"] = "age",
            ["tFrames"] = "frames",
            ["tJitter"] = "jitter",
            ["tDropped"] = "dropped",
            ["tLate"] = "late",
            ["nsTitle"] = "No signal",
            ["nsBody"] = "The device is connected but no frames are arriving. Usually the console is asleep or HDMI is unplugged.",
            ["nsWait"] = "Waiting for a signal — the picture comes back on its own, settings are kept",
            ["busyTitle"] = "Device in use",
            ["busyBody"] = "Another application is already reading {0} — OBS or a browser tab, for example. Close it or choose another device.",
            ["lostTitle"] = "Device unavailable",
            ["lostBody"] = "The connection to {0} was lost. Check the cable or choose another device.",
            ["otherDevice"] = "Another device",
            ["dBusyNow"] = "in use by another application",
            ["splashStatus"] = "Reading capture modes…",
            ["retryNow"] = "Retry",
            ["warnFix"] = "Click the badge to go borderless, or press F11.",
        },
        ["uk"] = new()
        {
            ["audio"] = "Звук",
            ["audioIn"] = "вхід",
            ["audioOut"] = "вихід",
            ["audioDefault"] = "за умовчанням",
            ["audioNoInput"] = "у цієї карти не знайдено аудіовхід HDMI",
            ["videoBright"] = "Яскравість відео",
            ["videoBrightDriver"] = "PQ виробляє фільтр драйвера",
            ["quit"] = "Вийти",
            ["quitTitle"] = "Вийти з Rayxel?",
            ["quitBody"] = "Відео зупиниться. Налаштування збережуться.",
            ["cancel"] = "Скасувати",
            ["thDelay"] = "мс",
            ["tenBitCost"] = "10 біт — удвічі більше даних і ривки",
            ["capNote"] = "канал {0} MiB/s",
            ["upscaler"] = "Апскейлер",
            ["whyComposed"] = "вікно малює композитор Windows — мінус цілий кадр, F11",
            ["whyCodec"] = "у тракті кодек — картинку розтискають за нас",
            ["whyBudget"] = "не вкладаємось у бюджет кадра",
            ["whyDropped"] = "кадри губляться",
            ["whyNoSignal"] = "джерело мовчить",

            ["hz"] = "Гц",
            ["ms"] = "мс",
            ["nits"] = "ніт",
            ["budgetOf"] = "{0} з {1} мс",
            ["modeTitle"] = "Режим захоплення",
            ["rawGroup"] = "Без стиснення",
            ["codecGroup"] = "Через стиснення — довше і з втратою якості",
            ["modesShown"] = "{0} з {1}",
            ["closeM"] = "M — закрити",
            ["scalerTitle"] = "Масштабування",
            ["perFrame"] = "ціна за кадр",
            ["dNone"] = "Картинка як є",
            ["dNis"] = "Масштаб і різкість, майже безкоштовно",
            ["dVsr"] = "Нейромережеве збільшення в драйвері NVIDIA",
            ["dVsrIntel"] = "Збільшення в драйвері Intel",
            ["scaleUp"] = "Збільшення ×{0}: {1} → {2} рядків · клавіші 1, 2, 3",
            ["scaleInert"] = "Кадр не менший за вікно: збільшувати нічого, {0} простоюватиме",
            ["device"] = "Пристрій",
            ["audioSend"] = "Передавати звук карти",
            ["uiBright"] = "Яскравість інтерфейсу в HDR",
            ["window"] = "Вікно",
            ["wWindowed"] = "У вікні",
            ["wBorderless"] = "Без рамки",
            ["hotkeys"] = "Esc — панель · T — телеметрія · M — режими · 1, 2, 3 — масштаб · F11 — екран",
            ["ttMode"] = "Режим захоплення · M",
            ["ttScaler"] = "Масштабування · 1, 2, 3",
            ["ttAudioOn"] = "Звук карти увімкнено",
            ["ttAudioOff"] = "Звук карти вимкнено",
            ["ttTele"] = "Телеметрія · T",
            ["ttFull"] = "Повний екран без рамки · F11",
            ["ttWindow"] = "Повернути вікно · F11",
            ["ttBudget"] = "GPU {0} + CPU {1} з {2} мс, запас {3} мс",
            ["infoComposedTitle"] = "Кадр проходить через композитор Windows",
            ["infoComposedBody"] = "Це +{0} мс, цілий кадр — більше, ніж RTX VSR і HDR разом. У повноекранному режимі без рамки вивід знову прямий.",
            ["infoBudgetTitle"] = "Обробка не вкладається в кадр",
            ["infoBudgetBody"] = "Вона триває довше за кадр, і кадри запізнюються. Оберіть NIS або режим із меншою частотою.",
            ["tGpu"] = "GPU",
            ["tHead"] = "запас",
            ["tAge"] = "вік",
            ["tFrames"] = "кадру",
            ["tJitter"] = "тремтіння",
            ["tDropped"] = "втрачено",
            ["tLate"] = "запізнилися",
            ["nsTitle"] = "Немає сигналу",
            ["nsBody"] = "Пристрій підключено, але кадри не надходять. Зазвичай консоль спить або від'єднано HDMI.",
            ["nsWait"] = "Чекаємо на сигнал — картинка повернеться сама, налаштування збережено",
            ["busyTitle"] = "Пристрій зайнятий",
            ["busyBody"] = "Інша програма вже читає {0}, наприклад OBS або вкладка браузера. Закрийте її або оберіть інший пристрій.",
            ["lostTitle"] = "Пристрій недоступний",
            ["lostBody"] = "Зв'язок із {0} втрачено. Перевірте кабель або оберіть інший пристрій.",
            ["otherDevice"] = "Інший пристрій",
            ["dBusyNow"] = "зайнятий іншою програмою",
            ["splashStatus"] = "Читаємо режими захоплення…",
            ["retryNow"] = "Повторити",
            ["warnFix"] = "Натисніть на значок, щоб перейти без рамки, або F11.",
        },
        ["de"] = new()
        {
            ["audio"] = "Ton",
            ["audioIn"] = "Eingang",
            ["audioOut"] = "Ausgang",
            ["audioDefault"] = "Standard",
            ["audioNoInput"] = "kein HDMI-Audioeingang an dieser Karte gefunden",
            ["videoBright"] = "Videohelligkeit",
            ["videoBrightDriver"] = "die PQ-Werte erzeugt der Treiberfilter",
            ["quit"] = "Beenden",
            ["quitTitle"] = "Rayxel beenden?",
            ["quitBody"] = "Das Video stoppt. Einstellungen bleiben erhalten.",
            ["cancel"] = "Abbrechen",
            ["thDelay"] = "ms",
            ["tenBitCost"] = "10 Bit — doppelte Datenmenge, und es ruckelt",
            ["capNote"] = "Leitung {0} MiB/s",
            ["whyComposed"] = "der Windows-Compositor zeichnet das Fenster — ein ganzes Bild, F11",
            ["whyCodec"] = "ein Codec liegt im Pfad — es wird für uns dekomprimiert",
            ["whyBudget"] = "über dem Bildbudget",
            ["whyDropped"] = "Bilder gehen verloren",
            ["whyNoSignal"] = "die Quelle schweigt",

            ["hz"] = "Hz",
            ["ms"] = "ms",
            ["nits"] = "Nits",
            ["budgetOf"] = "{0} von {1} ms",
            ["modeTitle"] = "Aufnahmemodus",
            ["rawGroup"] = "Unkomprimiert",
            ["codecGroup"] = "Komprimiert — langsamer und mit Qualitätsverlust",
            ["modesShown"] = "{0} von {1}",
            ["closeM"] = "M schließt",
            ["scalerTitle"] = "Skalierung",
            ["perFrame"] = "Kosten pro Bild",
            ["dNone"] = "Das Bild, wie es ankommt",
            ["dNis"] = "Skalierung und Schärfe, fast kostenlos",
            ["dVsr"] = "Neuronale Hochskalierung im NVIDIA-Treiber",
            ["dVsrIntel"] = "Hochskalierung im Intel-Treiber",
            ["scaleUp"] = "×{0} größer: {1} → {2} Zeilen · Tasten 1, 2, 3",
            ["scaleInert"] = "Das Bild ist nicht kleiner als das Fenster: nichts zu vergrößern, {0} liefe leer",
            ["device"] = "Gerät",
            ["audioSend"] = "Ton der Karte wiedergeben",
            ["uiBright"] = "Helligkeit der Oberfläche in HDR",
            ["window"] = "Fenster",
            ["wWindowed"] = "Im Fenster",
            ["wBorderless"] = "Rahmenlos",
            ["hotkeys"] = "Esc Leiste · T Telemetrie · M Modi · 1, 2, 3 Skalierung · F11 Bildschirm",
            ["ttMode"] = "Aufnahmemodus · M",
            ["ttScaler"] = "Skalierung · 1, 2, 3",
            ["ttAudioOn"] = "Ton der Karte an",
            ["ttAudioOff"] = "Ton der Karte aus",
            ["ttTele"] = "Telemetrie · T",
            ["ttFull"] = "Rahmenloses Vollbild · F11",
            ["ttWindow"] = "Zurück ins Fenster · F11",
            ["ttBudget"] = "GPU {0} + CPU {1} von {2} ms, {3} ms Reserve",
            ["infoComposedTitle"] = "Bilder laufen durch den Windows-Compositor",
            ["infoComposedBody"] = "Das sind +{0} ms, ein ganzes Bild — mehr als RTX VSR und HDR zusammen. Im rahmenlosen Vollbild ist die Ausgabe wieder direkt.",
            ["infoBudgetTitle"] = "Die Verarbeitung passt nicht in ein Bild",
            ["infoBudgetBody"] = "Sie dauert länger als ein Bild, also kommen Bilder zu spät. Wählen Sie NIS oder einen Modus mit niedrigerer Rate.",
            ["tGpu"] = "GPU",
            ["tHead"] = "Reserve",
            ["tAge"] = "Alter",
            ["tFrames"] = "Bilder",
            ["tJitter"] = "Jitter",
            ["tDropped"] = "verloren",
            ["tLate"] = "verspätet",
            ["nsTitle"] = "Kein Signal",
            ["nsBody"] = "Das Gerät ist verbunden, aber es kommen keine Bilder. Meist schläft die Konsole oder HDMI ist abgezogen.",
            ["nsWait"] = "Warte auf Signal — das Bild kommt von selbst zurück, Einstellungen bleiben erhalten",
            ["busyTitle"] = "Gerät belegt",
            ["busyBody"] = "Eine andere Anwendung liest bereits {0}, zum Beispiel OBS oder ein Browser-Tab. Schließen Sie sie oder wählen Sie ein anderes Gerät.",
            ["lostTitle"] = "Gerät nicht verfügbar",
            ["lostBody"] = "Die Verbindung zu {0} ist abgebrochen. Prüfen Sie das Kabel oder wählen Sie ein anderes Gerät.",
            ["otherDevice"] = "Anderes Gerät",
            ["dBusyNow"] = "von einer anderen Anwendung belegt",
            ["splashStatus"] = "Aufnahmemodi werden gelesen…",
            ["retryNow"] = "Erneut versuchen",
            ["warnFix"] = "Auf das Symbol klicken, um rahmenlos zu wechseln, oder F11.",
        },
        ["es"] = new()
        {
            ["audio"] = "Sonido",
            ["audioIn"] = "entrada",
            ["audioOut"] = "salida",
            ["audioDefault"] = "predeterminado",
            ["audioNoInput"] = "no se encontró entrada de audio HDMI en esta tarjeta",
            ["videoBright"] = "Brillo del vídeo",
            ["videoBrightDriver"] = "el filtro del controlador produce los valores PQ",
            ["quit"] = "Salir",
            ["quitTitle"] = "¿Salir de Rayxel?",
            ["quitBody"] = "El vídeo se detiene. Los ajustes se conservan.",
            ["cancel"] = "Cancelar",
            ["thDelay"] = "ms",
            ["tenBitCost"] = "10 bits — el doble de datos, y da tirones",
            ["capNote"] = "enlace {0} MiB/s",
            ["whyComposed"] = "el compositor de Windows dibuja la ventana — un fotograma entero, F11",
            ["whyCodec"] = "hay un códec en la ruta — nos lo descomprimen",
            ["whyBudget"] = "por encima del presupuesto de fotograma",
            ["whyDropped"] = "se están perdiendo fotogramas",
            ["whyNoSignal"] = "la fuente está en silencio",

            ["hz"] = "Hz",
            ["ms"] = "ms",
            ["nits"] = "nits",
            ["budgetOf"] = "{0} de {1} ms",
            ["modeTitle"] = "Modo de captura",
            ["rawGroup"] = "Sin compresión",
            ["codecGroup"] = "Con compresión — más lento y con pérdida de calidad",
            ["modesShown"] = "{0} de {1}",
            ["closeM"] = "M cierra",
            ["scalerTitle"] = "Escalado",
            ["perFrame"] = "coste por fotograma",
            ["dNone"] = "La imagen tal como llega",
            ["dNis"] = "Escalado y nitidez, casi gratis",
            ["dVsr"] = "Escalado neuronal en el controlador de NVIDIA",
            ["dVsrIntel"] = "Escalado en el controlador de Intel",
            ["scaleUp"] = "Ampliación ×{0}: {1} → {2} líneas · teclas 1, 2, 3",
            ["scaleInert"] = "El fotograma no es menor que la ventana: no hay nada que ampliar, {0} quedaría inactivo",
            ["device"] = "Dispositivo",
            ["audioSend"] = "Reproducir el audio de la tarjeta",
            ["uiBright"] = "Brillo de la interfaz en HDR",
            ["window"] = "Ventana",
            ["wWindowed"] = "En ventana",
            ["wBorderless"] = "Sin bordes",
            ["hotkeys"] = "Esc barra · T telemetría · M modos · 1, 2, 3 escalado · F11 pantalla",
            ["ttMode"] = "Modo de captura · M",
            ["ttScaler"] = "Escalado · 1, 2, 3",
            ["ttAudioOn"] = "Audio de la tarjeta activado",
            ["ttAudioOff"] = "Audio de la tarjeta desactivado",
            ["ttTele"] = "Telemetría · T",
            ["ttFull"] = "Pantalla completa sin bordes · F11",
            ["ttWindow"] = "Volver a ventana · F11",
            ["ttBudget"] = "GPU {0} + CPU {1} de {2} ms, margen {3} ms",
            ["infoComposedTitle"] = "Los fotogramas pasan por el compositor de Windows",
            ["infoComposedBody"] = "Son +{0} ms, un fotograma entero: más que RTX VSR y HDR juntos. En pantalla completa sin bordes la salida vuelve a ser directa.",
            ["infoBudgetTitle"] = "El procesado no cabe en un fotograma",
            ["infoBudgetBody"] = "Tarda más de lo que dura un fotograma, así que llegan tarde. Elija NIS o un modo de menor frecuencia.",
            ["tGpu"] = "GPU",
            ["tHead"] = "margen",
            ["tAge"] = "edad",
            ["tFrames"] = "fotogramas",
            ["tJitter"] = "jitter",
            ["tDropped"] = "perdidos",
            ["tLate"] = "tarde",
            ["nsTitle"] = "Sin señal",
            ["nsBody"] = "El dispositivo está conectado, pero no llegan fotogramas. Normalmente la consola está en reposo o el HDMI desconectado.",
            ["nsWait"] = "Esperando señal: la imagen volverá sola, los ajustes se conservan",
            ["busyTitle"] = "Dispositivo en uso",
            ["busyBody"] = "Otra aplicación ya está leyendo {0}, por ejemplo OBS o una pestaña del navegador. Ciérrela o elija otro dispositivo.",
            ["lostTitle"] = "Dispositivo no disponible",
            ["lostBody"] = "Se perdió la conexión con {0}. Revise el cable o elija otro dispositivo.",
            ["otherDevice"] = "Otro dispositivo",
            ["dBusyNow"] = "en uso por otra aplicación",
            ["splashStatus"] = "Leyendo modos de captura…",
            ["retryNow"] = "Reintentar",
            ["warnFix"] = "Haga clic en el icono para pasar a sin bordes, o pulse F11.",
        },
    };

    /// <summary>
    /// First letter up. The panel used to lower-case its labels for every language except
    /// German; sentence case reads better and is one rule rather than two.
    /// </summary>
    public static string Sentence(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// A number in the current language's notation. The process runs with invariant
    /// globalization, so this is done by hand: every language shipped except English
    /// writes a decimal comma, and "4.95 мс" reads as a typo in Russian.
    /// </summary>
    public static string Num(double value, string format)
    {
        var s = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        return Current == "en" ? s : s.Replace('.', ',');
    }

    /// <summary>A value with the current language's millisecond unit: "4,95 мс".</summary>
    public static string Ms(double value, string format = "0.00") => $"{Num(value, format)} {T("ms")}";

    static Strings()
    {
        foreach (var (language, entries) in Additions)
            if (Table.TryGetValue(language, out var target))
                foreach (var (key, value) in entries)
                    target[key] = value;
    }

    public static readonly string[] Languages = ["ru", "en", "uk", "de", "es"];

    public static readonly string[] LanguageNames = ["Русский", "English", "Українська", "Deutsch", "Español"];

    /// <summary>Russian first: that is the language this is used in.</summary>
    public static string Current { get; private set; } = "ru";

    public static int CurrentIndex => Math.Max(0, Array.IndexOf(Languages, Current));

    public static void Use(string language)
    {
        if (Table.ContainsKey(language)) Current = language;
    }

    public static void Use(int index)
    {
        if (index >= 0 && index < Languages.Length) Current = Languages[index];
    }

    /// <summary>
    /// Looks a key up in the current language. A missing key returns the key itself rather
    /// than an empty string — an untranslated label is a visible bug, a blank one is not.
    /// </summary>
    public static string T(string key) =>
        Table.TryGetValue(Current, out var lang) && lang.TryGetValue(key, out var value) ? value : key;

    /// <summary>
    /// "241 режим", "86 режимов", "54 режима".
    /// <para>
    /// Slavic languages pick one of three forms from the number, and the handoff is
    /// explicit that a fixed word must not be printed. English and German have two forms;
    /// the table simply repeats one of them, so the same rule covers every language.
    /// </para>
    /// </summary>
    public static string Modes(int count)
    {
        if (!PluralForms.TryGetValue(Current, out var forms) || forms.Length < 3)
            return $"{count} {T("modesN")}";

        return $"{count} {forms[FormIndex(count)]}";
    }

    /// <summary>0 = one, 1 = few, 2 = many. The Russian/Ukrainian rule; Latin ones only ever hit 0 and 2.</summary>
    private static int FormIndex(int n)
    {
        if (Current is not ("ru" or "uk")) return n == 1 ? 0 : 2;

        int mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return 2;
        return (n % 10) switch { 1 => 0, 2 or 3 or 4 => 1, _ => 2 };
    }
}
