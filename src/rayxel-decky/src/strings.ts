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
  dOff: "No computer can find it or connect",
  dWaiting: "Waiting for a paired computer",
  dWaitingNone: "No computer is paired yet. Press Make discoverable, then Pair a Steam Deck in Rayxel on the computer",
  dDiscoverable: "In Rayxel on the computer, press Pair a Steam Deck",
  dPairing: "Pairing with {0}",
  watching: "{0} is watching",
  fps: "fps",
  mbit: "Mbit/s",
  encodeMs: "encode {0} ms",
  instead: "{0} instead of {1}",
  inputOn: "controls from the computer on",

  senderOn: "Sender on",
  makeDiscoverable: "Make discoverable",
  discoverableFor: "Hide · {0} left",
  pairTitle: "Pairing",

  pairWants: "{0} wants to pair",
  pairTypePin: "Type this code into Rayxel on the computer",
  pairExchanging: "Exchanging keys…",
  pairExpires: "Expires in {0}",
  cancel: "Cancel",

  pairedTitle: "Paired computers",
  pairedNone: "None yet.",
  lastSeen: "last seen {0}",
  neverSeen: "has not connected yet",
  watchingNow: "watching now",
  justNow: "just now",
  today: "today, {0}",
  forget: "Forget",
  forgetTitle: "Forget {0}?",
  forgetBody: "To watch this Deck again, that computer will have to be paired again.",

  optionsTitle: "Options",
  allowInput: "Controls from the computer",
  allowInputHint: "Its controller, keyboard and mouse play here",
  autostart: "Start with the Deck",
  autostartHint: "The sender starts in the background when the Deck turns on",

  toastBody: "Code {0}",

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
  stDiscoverable: "Поиск открыт",
  stPairing: "Связывание",
  stStreaming: "Трансляция",
  dOff: "Компьютеры его не найдут и не подключатся",
  dWaiting: "Ждёт связанный компьютер",
  dWaitingNone: "Связанных компьютеров пока нет. Нажмите «Разрешить поиск», затем в Rayxel на компьютере — «Связать Steam Deck…»",
  dDiscoverable: "В Rayxel на компьютере нажмите «Связать Steam Deck…»",
  dPairing: "Связывание с {0}",
  watching: "Смотрит {0}",
  fps: "к/с",
  mbit: "Мбит/с",
  encodeMs: "кодер {0} мс",
  instead: "{0} вместо {1}",
  inputOn: "управление с компьютера включено",

  senderOn: "Отправитель включён",
  makeDiscoverable: "Разрешить поиск",
  discoverableFor: "Закрыть поиск · {0}",
  pairTitle: "Связывание",

  pairWants: "{0} хочет связаться",
  pairTypePin: "Введите этот код в Rayxel на компьютере",
  pairExchanging: "Обмен ключами…",
  pairExpires: "Осталось {0}",
  cancel: "Отмена",

  pairedTitle: "Связанные компьютеры",
  pairedNone: "Пока ни одного.",
  lastSeen: "был на связи {0}",
  neverSeen: "ещё не подключался",
  watchingNow: "смотрит сейчас",
  justNow: "только что",
  today: "сегодня, {0}",
  forget: "Забыть",
  forgetTitle: "Забыть {0}?",
  forgetBody: "Чтобы снова смотреть этот Дек, компьютер придётся связать заново.",

  optionsTitle: "Параметры",
  allowInput: "Управление с компьютера",
  allowInputHint: "Его геймпад, клавиатура и мышь работают здесь",
  autostart: "Запускать вместе с Деком",
  autostartHint: "Отправитель стартует в фоне при включении Дека",

  toastBody: "Код {0}",

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
  stDiscoverable: "Пошук відкрито",
  stPairing: "Зв’язування",
  stStreaming: "Трансляція",
  dOff: "Комп’ютери його не знайдуть і не під’єднаються",
  dWaiting: "Чекає на зв’язаний комп’ютер",
  dWaitingNone: "Зв’язаних комп’ютерів поки немає. Натисніть «Дозволити пошук», потім у Rayxel на комп’ютері — «Зв’язати Steam Deck…»",
  dDiscoverable: "У Rayxel на комп’ютері натисніть «Зв’язати Steam Deck…»",
  dPairing: "Зв’язування з {0}",
  watching: "Дивиться {0}",
  fps: "к/с",
  mbit: "Мбіт/с",
  encodeMs: "кодер {0} мс",
  instead: "{0} замість {1}",
  inputOn: "керування з комп’ютера увімкнено",

  senderOn: "Відправник увімкнений",
  makeDiscoverable: "Дозволити пошук",
  discoverableFor: "Закрити пошук · {0}",
  pairTitle: "Зв’язування",

  pairWants: "{0} хоче зв’язатися",
  pairTypePin: "Введіть цей код у Rayxel на комп’ютері",
  pairExchanging: "Обмін ключами…",
  pairExpires: "Лишилося {0}",
  cancel: "Скасувати",

  pairedTitle: "Зв’язані комп’ютери",
  pairedNone: "Поки жодного.",
  lastSeen: "був на зв’язку {0}",
  neverSeen: "ще не під’єднувався",
  watchingNow: "дивиться зараз",
  justNow: "щойно",
  today: "сьогодні, {0}",
  forget: "Забути",
  forgetTitle: "Забути {0}?",
  forgetBody: "Щоб знову дивитися цей Дек, комп’ютер доведеться зв’язати заново.",

  optionsTitle: "Параметри",
  allowInput: "Керування з комп’ютера",
  allowInputHint: "Його геймпад, клавіатура і миша працюють тут",
  autostart: "Запускати разом із Деком",
  autostartHint: "Відправник стартує у фоні під час увімкнення Дека",

  toastBody: "Код {0}",

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
  dOff: "Kein Computer findet es oder verbindet sich",
  dWaiting: "Wartet auf einen gekoppelten Computer",
  dWaitingNone: "Noch kein Computer gekoppelt. Tippen Sie auf Sichtbar machen, dann in Rayxel am Computer auf Steam Deck koppeln…",
  dDiscoverable: "In Rayxel am Computer auf Steam Deck koppeln… tippen",
  dPairing: "Kopplung mit {0}",
  watching: "{0} schaut zu",
  fps: "fps",
  mbit: "Mbit/s",
  encodeMs: "Encoder {0} ms",
  instead: "{0} statt {1}",
  inputOn: "Steuerung vom Computer an",

  senderOn: "Sender an",
  makeDiscoverable: "Sichtbar machen",
  discoverableFor: "Verbergen · noch {0}",
  pairTitle: "Kopplung",

  pairWants: "{0} möchte koppeln",
  pairTypePin: "Geben Sie diesen Code in Rayxel am Computer ein",
  pairExchanging: "Schlüssel werden ausgetauscht…",
  pairExpires: "Läuft ab in {0}",
  cancel: "Abbrechen",

  pairedTitle: "Gekoppelte Computer",
  pairedNone: "Noch keiner.",
  lastSeen: "zuletzt {0}",
  neverSeen: "noch nie verbunden",
  watchingNow: "schaut gerade zu",
  justNow: "gerade eben",
  today: "heute, {0}",
  forget: "Vergessen",
  forgetTitle: "{0} vergessen?",
  forgetBody: "Um dieses Deck wieder anzusehen, muss der Computer neu gekoppelt werden.",

  optionsTitle: "Optionen",
  allowInput: "Steuerung vom Computer",
  allowInputHint: "Sein Controller, seine Tastatur und Maus wirken hier",
  autostart: "Mit dem Deck starten",
  autostartHint: "Der Sender startet im Hintergrund, wenn das Deck angeht",

  toastBody: "Code {0}",

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
  stPairing: "Vinculando",
  stStreaming: "Transmitiendo",
  dOff: "Ningún equipo puede encontrarla ni conectarse",
  dWaiting: "Esperando a un equipo vinculado",
  dWaitingNone: "Aún no hay equipos vinculados. Pulse Hacer visible y luego Vincular un Steam Deck… en Rayxel en el equipo",
  dDiscoverable: "En Rayxel en el equipo, pulse Vincular un Steam Deck…",
  dPairing: "Vinculando con {0}",
  watching: "{0} está mirando",
  fps: "fps",
  mbit: "Mbit/s",
  encodeMs: "codificación {0} ms",
  instead: "{0} en lugar de {1}",
  inputOn: "control desde el equipo activado",

  senderOn: "Emisor encendido",
  makeDiscoverable: "Hacer visible",
  discoverableFor: "Ocultar · {0} más",
  pairTitle: "Vinculación",

  pairWants: "{0} quiere vincularse",
  pairTypePin: "Escriba este código en Rayxel en el equipo",
  pairExchanging: "Intercambiando claves…",
  pairExpires: "Caduca en {0}",
  cancel: "Cancelar",

  pairedTitle: "Equipos vinculados",
  pairedNone: "Ninguno todavía.",
  lastSeen: "última vez {0}",
  neverSeen: "aún no se ha conectado",
  watchingNow: "mirando ahora",
  justNow: "hace un momento",
  today: "hoy, {0}",
  forget: "Olvidar",
  forgetTitle: "¿Olvidar {0}?",
  forgetBody: "Para volver a ver esta Deck, habrá que vincular ese equipo de nuevo.",

  optionsTitle: "Opciones",
  allowInput: "Control desde el equipo",
  allowInputHint: "Su mando, teclado y ratón funcionan aquí",
  autostart: "Iniciar con la Deck",
  autostartHint: "El emisor se inicia en segundo plano al encender la Deck",

  toastBody: "Código {0}",

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
