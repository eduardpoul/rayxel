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
        },
    };

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
