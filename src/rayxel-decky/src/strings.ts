// The panel's copy, in the five languages Rayxel ships, with the same words as the sender's
// window on a PC (src/Rayxel.Send.App/Strings.cs). A key one language lacks falls back to
// English, so a gap shows English rather than nothing.

type Table = Record<string, string>;

const en: Table = {
  notRunning: "Not running",
  notRunningDetail: "Switch it on to start the sender",
  stOff: "Off",
  stWaiting: "Ready",
  stDiscoverable: "Discoverable",
  stPairing: "Pairing",
  stStreaming: "Streaming",
  dOff: "Nobody can find it or connect",
  dWaiting: "Waiting for a paired PC",
  dWaitingNone: "No PC is paired yet. Make it discoverable, then pick it in Rayxel on your PC",
  dDiscoverable: "Rayxel on your PC can find it now",
  dPairing: "Pairing with {0}",
  watching: "{0} is watching",
  fps: "fps",
  mbit: "Mbit/s",
  encodeMs: "encode {0} ms",
  instead: "{0} instead of {1}",
  inputOn: "PC controls on",

  senderOn: "Sender on",
  makeDiscoverable: "Make discoverable",
  discoverableFor: "Discoverable for {0} · Stop",

  pairWants: "{0} wants to connect",
  pairTypePin: "Type this PIN into Rayxel on the PC",
  pairExchanging: "Exchanging keys…",
  pairExpires: "Expires in {0}",
  cancel: "Cancel",

  pairedTitle: "Paired PCs",
  pairedNone: "None yet.",
  lastSeen: "last seen {0}",
  neverSeen: "has not connected yet",
  watchingNow: "watching now",
  justNow: "just now",
  today: "today, {0}",
  forget: "Forget",
  forgetTitle: "Forget {0}?",
  forgetBody: "It will have to pair again before it can connect.",

  optionsTitle: "Options",
  allowInput: "Allow input from the PC",
  allowInputHint: "The PC's controller, keyboard and mouse play here",
  autostart: "Start with the Deck",
  autostartHint: "Starts in the background when the Deck turns on",

  stopSender: "Stop sender",
  stopHint: "Switching off is enough to stop streaming. This ends the program itself.",
  stopCaptureTitle: "The game may close",
  stopCaptureBody:
    "The sender holds the game's picture. Stopping it now can crash gamescope, and the running game with it. Switching it off is safe.",
  stopNoAnswerBody:
    "The sender does not answer, so it cannot say whether it holds the game's picture. Stopping it may close the game. Switching it off is safe.",
  stopAnyway: "Stop anyway",

  toastBody: "PIN {0}",

  errNoAnswer: "The sender did not answer",
  errNoBinary: "The sender is missing from the plugin's bin folder",
  errStart: "The sender did not start: {0}",
  errRefused: "The sender said: {0}",
};

const ru: Table = {
  notRunning: "Не запущен",
  notRunningDetail: "Включите, чтобы запустить отправитель",
  stOff: "Выключен",
  stWaiting: "Готов",
  stDiscoverable: "Виден в сети",
  stPairing: "Сопряжение",
  stStreaming: "Трансляция",
  dOff: "Его не найти и к нему не подключиться",
  dWaiting: "Ждёт сопряжённый ПК",
  dWaitingNone: "Сопряжённых ПК пока нет. Сделайте Deck видимым и выберите его в Rayxel на ПК",
  dDiscoverable: "Rayxel на ПК сейчас может его найти",
  dPairing: "Сопряжение с {0}",
  watching: "Смотрит {0}",
  fps: "к/с",
  mbit: "Мбит/с",
  encodeMs: "кодер {0} мс",
  instead: "{0} вместо {1}",
  inputOn: "управление с ПК включено",

  senderOn: "Отправитель включён",
  makeDiscoverable: "Сделать видимым",
  discoverableFor: "Виден ещё {0} · Скрыть",

  pairWants: "{0} хочет подключиться",
  pairTypePin: "Введите этот PIN в Rayxel на ПК",
  pairExchanging: "Обмен ключами…",
  pairExpires: "Осталось {0}",
  cancel: "Отмена",

  pairedTitle: "Сопряжённые ПК",
  pairedNone: "Пока нет.",
  lastSeen: "был на связи {0}",
  neverSeen: "ещё не подключался",
  watchingNow: "смотрит сейчас",
  justNow: "только что",
  today: "сегодня, {0}",
  forget: "Забыть",
  forgetTitle: "Забыть {0}?",
  forgetBody: "Чтобы снова подключиться, ему придётся пройти сопряжение заново.",

  optionsTitle: "Параметры",
  allowInput: "Разрешить управление с ПК",
  allowInputHint: "Геймпад, клавиатура и мышь ПК работают здесь",
  autostart: "Запускать вместе с Deck",
  autostartHint: "Запускается в фоне при включении Deck",

  stopSender: "Остановить отправитель",
  stopHint: "Чтобы прекратить трансляцию, достаточно выключить. Эта кнопка завершает саму программу.",
  stopCaptureTitle: "Игра может закрыться",
  stopCaptureBody:
    "Отправитель держит картинку игры. Если остановить его сейчас, gamescope может упасть, а с ним и запущенная игра. Выключить его безопасно.",
  stopNoAnswerBody:
    "Отправитель не отвечает, поэтому нельзя узнать, держит ли он картинку игры. Остановка может закрыть игру. Выключить его безопасно.",
  stopAnyway: "Всё равно остановить",

  toastBody: "PIN {0}",

  errNoAnswer: "Отправитель не ответил",
  errNoBinary: "В папке bin плагина нет отправителя",
  errStart: "Отправитель не запустился: {0}",
  errRefused: "Ответ отправителя: {0}",
};

const uk: Table = {
  notRunning: "Не запущений",
  notRunningDetail: "Увімкніть, щоб запустити відправника",
  stOff: "Вимкнено",
  stWaiting: "Готовий",
  stDiscoverable: "Видимий у мережі",
  stPairing: "Сполучення",
  stStreaming: "Трансляція",
  dOff: "Його не знайти і до нього не під'єднатися",
  dWaiting: "Чекає на сполучений ПК",
  dWaitingNone: "Сполучених ПК поки немає. Зробіть Deck видимим і оберіть його в Rayxel на ПК",
  dDiscoverable: "Rayxel на ПК зараз може його знайти",
  dPairing: "Сполучення з {0}",
  watching: "Дивиться {0}",
  fps: "к/с",
  mbit: "Мбіт/с",
  encodeMs: "кодер {0} мс",
  instead: "{0} замість {1}",
  inputOn: "керування з ПК увімкнено",

  senderOn: "Відправник увімкнений",
  makeDiscoverable: "Зробити видимим",
  discoverableFor: "Видимий ще {0} · Сховати",

  pairWants: "{0} хоче під'єднатися",
  pairTypePin: "Введіть цей PIN у Rayxel на ПК",
  pairExchanging: "Обмін ключами…",
  pairExpires: "Лишилося {0}",
  cancel: "Скасувати",

  pairedTitle: "Сполучені ПК",
  pairedNone: "Поки немає.",
  lastSeen: "був на зв'язку {0}",
  neverSeen: "ще не під'єднувався",
  watchingNow: "дивиться зараз",
  justNow: "щойно",
  today: "сьогодні, {0}",
  forget: "Забути",
  forgetTitle: "Забути {0}?",
  forgetBody: "Щоб знову під'єднатися, йому доведеться пройти сполучення заново.",

  optionsTitle: "Параметри",
  allowInput: "Дозволити керування з ПК",
  allowInputHint: "Геймпад, клавіатура і миша ПК працюють тут",
  autostart: "Запускати разом із Deck",
  autostartHint: "Запускається у фоні під час увімкнення Deck",

  stopSender: "Зупинити відправника",
  stopHint: "Щоб припинити трансляцію, досить вимкнути. Ця кнопка завершує саму програму.",
  stopCaptureTitle: "Гра може закритися",
  stopCaptureBody:
    "Відправник тримає картинку гри. Якщо зупинити його зараз, gamescope може впасти, а з ним і запущена гра. Вимкнути його безпечно.",
  stopNoAnswerBody:
    "Відправник не відповідає, тож не можна дізнатися, чи тримає він картинку гри. Зупинка може закрити гру. Вимкнути його безпечно.",
  stopAnyway: "Все одно зупинити",

  toastBody: "PIN {0}",

  errNoAnswer: "Відправник не відповів",
  errNoBinary: "У теці bin плагіна немає відправника",
  errStart: "Відправник не запустився: {0}",
  errRefused: "Відповідь відправника: {0}",
};

const de: Table = {
  notRunning: "Läuft nicht",
  notRunningDetail: "Einschalten startet den Sender",
  stOff: "Aus",
  stWaiting: "Bereit",
  stDiscoverable: "Sichtbar",
  stPairing: "Kopplung",
  stStreaming: "Überträgt",
  dOff: "Niemand findet ihn oder verbindet sich",
  dWaiting: "Wartet auf einen gekoppelten PC",
  dWaitingNone: "Noch kein PC gekoppelt. Machen Sie das Deck sichtbar und wählen Sie es in Rayxel auf dem PC",
  dDiscoverable: "Rayxel auf dem PC kann es jetzt finden",
  dPairing: "Kopplung mit {0}",
  watching: "{0} schaut zu",
  fps: "fps",
  mbit: "Mbit/s",
  encodeMs: "Encoder {0} ms",
  instead: "{0} statt {1}",
  inputOn: "Steuerung vom PC an",

  senderOn: "Sender an",
  makeDiscoverable: "Sichtbar machen",
  discoverableFor: "Noch {0} sichtbar · Beenden",

  pairWants: "{0} möchte sich verbinden",
  pairTypePin: "Geben Sie diese PIN in Rayxel auf dem PC ein",
  pairExchanging: "Schlüssel werden ausgetauscht…",
  pairExpires: "Läuft ab in {0}",
  cancel: "Abbrechen",

  pairedTitle: "Gekoppelte PCs",
  pairedNone: "Noch keine.",
  lastSeen: "zuletzt {0}",
  neverSeen: "noch nie verbunden",
  watchingNow: "schaut gerade zu",
  justNow: "gerade eben",
  today: "heute, {0}",
  forget: "Vergessen",
  forgetTitle: "{0} vergessen?",
  forgetBody: "Er muss neu gekoppelt werden, bevor er sich wieder verbinden kann.",

  optionsTitle: "Optionen",
  allowInput: "Eingaben vom PC erlauben",
  allowInputHint: "Controller, Tastatur und Maus des PCs wirken hier",
  autostart: "Mit dem Deck starten",
  autostartHint: "Startet im Hintergrund, wenn das Deck angeht",

  stopSender: "Sender beenden",
  stopHint: "Zum Beenden der Übertragung reicht Ausschalten. Dies beendet das Programm selbst.",
  stopCaptureTitle: "Das Spiel kann sich schließen",
  stopCaptureBody:
    "Der Sender hält das Bild des Spiels. Wird er jetzt beendet, kann gamescope abstürzen und das laufende Spiel mit ihm. Ausschalten ist sicher.",
  stopNoAnswerBody:
    "Der Sender antwortet nicht und kann daher nicht sagen, ob er das Bild des Spiels hält. Beenden kann das Spiel schließen. Ausschalten ist sicher.",
  stopAnyway: "Trotzdem beenden",

  toastBody: "PIN {0}",

  errNoAnswer: "Der Sender hat nicht geantwortet",
  errNoBinary: "Im bin-Ordner des Plugins fehlt der Sender",
  errStart: "Der Sender ist nicht gestartet: {0}",
  errRefused: "Antwort des Senders: {0}",
};

const es: Table = {
  notRunning: "Detenido",
  notRunningDetail: "Enciéndalo para iniciar el emisor",
  stOff: "Apagado",
  stWaiting: "Listo",
  stDiscoverable: "Visible",
  stPairing: "Emparejando",
  stStreaming: "Transmitiendo",
  dOff: "Nadie puede encontrarlo ni conectarse",
  dWaiting: "Esperando a un PC emparejado",
  dWaitingNone: "Aún no hay ningún PC emparejado. Haga visible la Deck y elíjala en Rayxel en el PC",
  dDiscoverable: "Rayxel en el PC puede encontrarla ahora",
  dPairing: "Emparejando con {0}",
  watching: "{0} está mirando",
  fps: "fps",
  mbit: "Mbit/s",
  encodeMs: "codificación {0} ms",
  instead: "{0} en lugar de {1}",
  inputOn: "control desde el PC activado",

  senderOn: "Emisor encendido",
  makeDiscoverable: "Hacer visible",
  discoverableFor: "Visible {0} más · Ocultar",

  pairWants: "{0} quiere conectarse",
  pairTypePin: "Escriba este PIN en Rayxel en el PC",
  pairExchanging: "Intercambiando claves…",
  pairExpires: "Caduca en {0}",
  cancel: "Cancelar",

  pairedTitle: "PC emparejados",
  pairedNone: "Ninguno todavía.",
  lastSeen: "última vez {0}",
  neverSeen: "aún no se ha conectado",
  watchingNow: "mirando ahora",
  justNow: "hace un momento",
  today: "hoy, {0}",
  forget: "Olvidar",
  forgetTitle: "¿Olvidar {0}?",
  forgetBody: "Tendrá que emparejarse de nuevo para poder conectarse.",

  optionsTitle: "Opciones",
  allowInput: "Permitir el control desde el PC",
  allowInputHint: "El mando, el teclado y el ratón del PC funcionan aquí",
  autostart: "Iniciar con la Deck",
  autostartHint: "Se inicia en segundo plano al encender la Deck",

  stopSender: "Detener el emisor",
  stopHint: "Para dejar de transmitir basta con apagarlo. Esto cierra el propio programa.",
  stopCaptureTitle: "El juego puede cerrarse",
  stopCaptureBody:
    "El emisor tiene la imagen del juego. Detenerlo ahora puede hacer caer gamescope, y con él el juego en curso. Apagarlo es seguro.",
  stopNoAnswerBody:
    "El emisor no responde, así que no puede decir si tiene la imagen del juego. Detenerlo puede cerrar el juego. Apagarlo es seguro.",
  stopAnyway: "Detener de todos modos",

  toastBody: "PIN {0}",

  errNoAnswer: "El emisor no respondió",
  errNoBinary: "Falta el emisor en la carpeta bin del complemento",
  errStart: "El emisor no se inició: {0}",
  errRefused: "Respuesta del emisor: {0}",
};

const tables: Record<string, Table> = { en, ru, uk, de, es };

// Steam names its languages in words; the client's own choice comes first, then the
// browser's, which in Steam's UI follows the client's too.
const steamLanguages: Record<string, string> = {
  english: "en",
  russian: "ru",
  ukrainian: "uk",
  german: "de",
  spanish: "es",
  latam: "es",
};

function pickLanguage(): string {
  const steam = (window as unknown as { LocalizationManager?: { m_rgLocalesToUse?: string[] } })
    .LocalizationManager?.m_rgLocalesToUse?.[0];
  if (steam && steamLanguages[steam]) return steamLanguages[steam];
  for (const tag of [...(navigator.languages ?? []), navigator.language]) {
    const code = tag?.split("-")[0]?.toLowerCase();
    if (code && tables[code]) return code;
  }
  return "en";
}

export const language = pickLanguage();

export function t(key: string): string {
  return tables[language][key] ?? en[key] ?? key;
}

export function f(key: string, ...args: (string | number)[]): string {
  return t(key).replace(/\{(\d+)\}/g, (_, index: string) => String(args[Number(index)] ?? ""));
}

export function num(value: number, digits: number): string {
  return value.toLocaleString(language, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

/** Time left as "4:59", or "1:04:59" past an hour. Rounded up, so it starts on the full minute. */
export function countdown(milliseconds: number): string {
  const seconds = Math.max(0, Math.ceil(milliseconds / 1000));
  const two = (n: number) => String(n).padStart(2, "0");
  return seconds >= 3600
    ? `${Math.floor(seconds / 3600)}:${two(Math.floor(seconds / 60) % 60)}:${two(seconds % 60)}`
    : `${Math.floor(seconds / 60)}:${two(seconds % 60)}`;
}

export function lastSeen(lastSeenMs: number, nowMs: number): string {
  if (!lastSeenMs || lastSeenMs <= 0) return t("neverSeen");
  if (nowMs - lastSeenMs < 120_000) return f("lastSeen", t("justNow"));
  const when = new Date(lastSeenMs);
  const now = new Date(nowMs);
  const sameDay = when.toDateString() === now.toDateString();
  const text = sameDay
    ? f("today", when.toLocaleTimeString(language, { hour: "2-digit", minute: "2-digit" }))
    : when.toLocaleString(language, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  return f("lastSeen", text);
}

/** A codec as people write it, whatever case the sender sends it in. */
export function codecName(name: string): string {
  switch (name.toLowerCase()) {
    case "hevc":
    case "h265":
    case "h.265":
      return "HEVC";
    case "av1":
      return "AV1";
    case "h264":
    case "h.264":
    case "avc":
      return "H.264";
    case "pyrowave":
      return "PyroWave";
    default:
      return name;
  }
}

/** "123 456": six digits are read and typed more surely in two threes. */
export function pinText(pin: string): string {
  return pin.length === 6 ? `${pin.slice(0, 3)} ${pin.slice(3)}` : pin;
}
