using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// The full-stage screens: start-up, the device choice, and the state where the card is
/// there but the console is not.
/// </summary>
public static class Screens
{
    public const double SplashDurationMs = 1700;

    /// <summary>
    /// The stage background. The design asks for a radial gradient, which a draw list
    /// cannot express directly; concentric rounded rectangles are close enough at these
    /// alphas and cost nothing.
    /// </summary>
    private static void Stage(Vector2 size)
    {
        var draw = ImGui.GetBackgroundDrawList();
        draw.AddRectFilled(Vector2.Zero, size, Theme.U32(Theme.Base));

        var centre = new Vector2(size.X * 0.5f, size.Y * 0.45f);
        for (int i = 8; i >= 1; i--)
        {
            float t = i / 8f;
            float radius = Math.Max(size.X, size.Y) * 0.55f * t;
            draw.AddRectFilled(centre - new Vector2(radius), centre + new Vector2(radius),
                Theme.U32(new Vector4(0.066f, 0.09f, 0.145f, 0.16f * (1f - t) + 0.04f)), radius);
        }
    }

    private static void FullStage(string id, Vector2 size, Action body)
    {
        Stage(size);
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (ImGui.Begin(id, flags)) body();
        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>Centres the next block of the given width and returns its left edge.</summary>
    private static float CentreColumn(float width, float contentHeight)
    {
        var avail = ImGui.GetWindowSize();
        ImGui.SetCursorPos(new Vector2((avail.X - width) * 0.5f, (avail.Y - contentHeight) * 0.5f));
        return (avail.X - width) * 0.5f;
    }

    public static void Splash(AppModel model, Fonts fonts)
    {
        var size = new Vector2(model.Renderer.Width, model.Renderer.Height);
        FullStage("##splash", size, () =>
        {
            float mark = Layout.S(60), markGap = Layout.S(18);
            float left = CentreColumn(Layout.S(280), Layout.S(150) + mark + markGap);

            // Drawn onto the list and claimed with one Dummy of the exact height, rather
            // than laid out as an item and spaced with another: ImGui puts its item spacing
            // either side of a Dummy, and the 18px the design asks for would arrive as 18
            // plus two helpings of it.
            var markAt = ImGui.GetCursorScreenPos() + new Vector2((Layout.S(280) - mark) * 0.5f, 0);
            Brand.Mark(ImGui.GetWindowDrawList(), markAt, mark);
            ImGui.Dummy(new Vector2(Layout.S(280),
                Math.Max(1f, mark + markGap - ImGui.GetStyle().ItemSpacing.Y)));

            var wordmark = Widgets.Measure(fonts.Wordmark, "Rayxel");
            ImGui.SetCursorPosX(left + (Layout.S(280) - wordmark.X) * 0.5f);
            Widgets.Text(fonts.Wordmark, Theme.TextPrimary, "Rayxel");

            ImGui.Dummy(new Vector2(0, Layout.S(9)));
            var tag = Strings.T("splashTag");
            var tagSize = Widgets.Measure(fonts.Body, tag);
            ImGui.SetCursorPosX(left + (Layout.S(280) - tagSize.X) * 0.5f);
            Widgets.Text(fonts.Body, Theme.Alpha(Theme.TextPrimary, 0.45f), tag);

            ImGui.Dummy(new Vector2(0, Layout.S(26)));
            ImGui.SetCursorPosX(left);

            // 1.6 s of travel, eased. The bar is honest about progress in the sense that
            // matters: it finishes when the app is ready to move on.
            float t = (float)Math.Clamp(model.SplashElapsedMs / 1600.0, 0, 1);
            float eased = 1f - MathF.Pow(1f - t, 3f);
            var barPos = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            float barW = Layout.S(280), barH = Math.Max(1f, Layout.S(2));
            draw.AddRectFilled(barPos, barPos + new Vector2(barW, barH),
                Theme.U32(new Vector4(1, 1, 1, 0.08f)), 99f);
            if (eased > 0.001f)
                draw.AddRectFilled(barPos, barPos + new Vector2(barW * eased, barH), Theme.U32(Theme.Accent), 99f);
            ImGui.Dummy(new Vector2(barW, barH));

            ImGui.Dummy(new Vector2(0, Layout.S(9)));
            ImGui.SetCursorPosX(left);
            Widgets.Text(fonts.MonoSmall, Theme.Alpha(Theme.TextPrimary, 0.35f), "enumerating capture modes…");
            const string stack = "D3D11 · .NET 10";
            float sw = Widgets.Measure(fonts.MonoSmall, stack).X;
            ImGui.SameLine(left + Layout.S(280) - sw);
            Widgets.Text(fonts.MonoSmall, Theme.Alpha(Theme.TextPrimary, 0.35f), stack);
        });
    }

    public static void DevicePicker(AppModel model, Fonts fonts)
    {
        var size = new Vector2(model.Renderer.Width, model.Renderer.Height);
        FullStage("##devices", size, () =>
        {
            float column = Math.Min(Layout.S(540), size.X * 0.92f);
            float height = Layout.S(130) + model.Devices.Count * Layout.S(64);
            float left = CentreColumn(column, height);

            ImGui.SetCursorPosX(left);
            Widgets.Text(fonts.Title, Theme.TextPrimary, Strings.T("chooseDevice"));

            ImGui.Dummy(new Vector2(0, Layout.S(8)));
            ImGui.SetCursorPosX(left);
            ImGui.PushTextWrapPos(left + column);
            Widgets.Text(fonts.Body, Theme.Alpha(Theme.TextPrimary, 0.48f), Strings.T("chooseHint"));
            ImGui.PopTextWrapPos();

            ImGui.Dummy(new Vector2(0, Layout.S(20)));

            foreach (var device in model.Devices)
            {
                ImGui.SetCursorPosX(left);
                DeviceRow(model, fonts, device, column);
                ImGui.Dummy(new Vector2(0, Layout.S(7)));
            }

            ImGui.SetCursorPosX(left);
            Widgets.Text(fonts.MonoSmall, Theme.Alpha(Theme.TextPrimary, 0.3f), Strings.T("oneOwner"));
        });
    }

    private static void DeviceRow(AppModel model, Fonts fonts, CaptureDeviceInfo device, float width)
    {
        model.Probed.TryGetValue(device.SymbolicLink, out var probe);

        // A device we have never opened is shown as exactly that. Busy cannot be known
        // without trying, so the row does not claim to know.
        var (colour, note, count, selectable) = probe switch
        {
            { Availability: DeviceAvailability.Ready } p =>
                (Theme.Good, Strings.T("dAvail"), Strings.Modes(p.Modes.Count), true),
            { Availability: DeviceAvailability.Busy } =>
                (Theme.Bad, Strings.T("dBusy"), "", false),
            { Availability: DeviceAvailability.Unusable } p =>
                (Theme.Warn, Strings.T("dOffline"), Strings.Modes(p.Modes.Count), false),
            { } p => (Theme.Warn, p.Problem ?? Strings.T("dOffline"), "", false),
            _ => (Theme.TextMuted, NotChecked(device), "", true)
        };

        var start = ImGui.GetCursorScreenPos();
        float height = Layout.S(56);
        bool clicked = ImGui.InvisibleButton($"##dev{device.Ordinal}", new Vector2(width, height)) && selectable;
        bool hovered = ImGui.IsItemHovered() && selectable;

        var background = !selectable ? new Vector4(1, 1, 1, 0.025f)
                       : hovered ? Theme.CardHover
                       : new Vector4(1, 1, 1, 0.05f);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), Theme.U32(background), Layout.S(Theme.RadiusPopover));

        float fade = selectable ? 1f : 0.55f;
        float dotR = Layout.S(8) / 2f;
        var dotCentre = new Vector2(start.X + Layout.S(17) + dotR, start.Y + height / 2f);
        draw.AddCircleFilled(dotCentre, dotR * 2.6f, Theme.U32(Theme.Alpha(colour, 0.12f * fade)));
        draw.AddCircleFilled(dotCentre, dotR, Theme.U32(Theme.Alpha(colour, fade)));

        float textX = start.X + Layout.S(17) + dotR * 2 + Layout.S(10);
        float nameH = Widgets.Measure(fonts.Name, device.FriendlyName).Y;
        float noteH = Widgets.Measure(fonts.Label, note).Y;
        float blockTop = start.Y + (height - nameH - noteH - Layout.S(2)) / 2f;

        bool pushed = PushFont(fonts.Name);
        draw.AddText(new Vector2(textX, blockTop),
            Theme.U32(Theme.Alpha(Theme.TextPrimary, fade)), device.FriendlyName);
        if (pushed) ImGui.PopFont();

        pushed = PushFont(fonts.Label);
        draw.AddText(new Vector2(textX, blockTop + nameH + Layout.S(2)),
            Theme.U32(Theme.Alpha(colour, 0.85f * fade)), note);
        if (pushed) ImGui.PopFont();

        if (count.Length > 0)
        {
            var text = count;
            float w = Widgets.Measure(fonts.MonoSmall, text).X;
            pushed = PushFont(fonts.MonoSmall);
            draw.AddText(new Vector2(start.X + width - w - Layout.S(17), start.Y + (height - Widgets.Measure(fonts.MonoSmall, text).Y) / 2f),
                Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.3f * fade)), text);
            if (pushed) ImGui.PopFont();
        }

        if (clicked)
        {
            model.SwitchDevice(device);
            model.Screen = Screen.App;
        }

        static bool PushFont(ImFontPtr font)
        {
            unsafe { if (font.NativePtr == null) return false; }
            ImGui.PushFont(font);
            return true;
        }
    }

    private static string Free() => Strings.Current switch
    {
        "ru" => "свободно, {0} режимов",
        "uk" => "вільно, {0} режимів",
        "de" => "frei, {0} Modi",
        "es" => "libre, {0} modos",
        _ => "free, {0} modes"
    };

    private static string NotChecked(CaptureDeviceInfo d)
    {
        if (d.IsVirtual)
            return Strings.Current switch
            {
                "ru" => "программное устройство",
                "uk" => "програмний пристрій",
                "de" => "Softwaregerät",
                "es" => "dispositivo por software",
                _ => "software device"
            };

        return Strings.Current switch
        {
            "ru" => "не проверено — занятость видна только при открытии",
            "uk" => "не перевірено — зайнятість видно лише при відкритті",
            "de" => "nicht geprüft — Belegung zeigt sich erst beim Öffnen",
            "es" => "sin comprobar — la ocupación solo se ve al abrir",
            _ => "not checked — busy only shows on opening"
        };
    }

    /// <summary>
    /// Device present, no picture. Every measured value goes to n/a while this is up:
    /// the numbers would be about a frame that is not arriving.
    /// </summary>
    public static void NoSignal(AppModel model, Fonts fonts)
    {
        var draw = ImGui.GetBackgroundDrawList();
        draw.AddRectFilled(Vector2.Zero, new Vector2(model.Renderer.Width, model.Renderer.Height),
            Theme.U32(Theme.Base));

        const float cardWidth = 420f;
        var centre = new Vector2(model.Renderer.Width * 0.5f, model.Renderer.Height * 0.5f);

        ImGui.SetNextWindowPos(centre, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(cardWidth, 0), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Alpha(new Vector4(0.078f, 0.106f, 0.157f, 1f), 0.85f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(26, 24));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Theme.RadiusOverlay);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin("##nosignal", flags))
        {
            // 1.8 s pulse. Slow on purpose: this is a recoverable state, not an alarm.
            float pulse = 0.35f + 0.55f * (0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * MathF.PI / 0.9f));
            Widgets.StatusDot(Theme.Alpha(Theme.Bad, pulse));
            ImGui.SameLine(0, 9);
            Widgets.Text(fonts.Tiny, Theme.Bad, Strings.T("stNoSignal"));

            ImGui.Dummy(new Vector2(0, 6));
            Widgets.Text(fonts.Title, Theme.TextPrimary, Strings.T("nosignalTitle"));

            ImGui.Dummy(new Vector2(0, 8));
            ImGui.PushTextWrapPos(cardWidth - 52);
            Widgets.Text(fonts.Label, Theme.TextSecondary, Strings.T("nosignalBody"));
            ImGui.PopTextWrapPos();

            if (model.Pump.StateDetail is { } detail)
            {
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.PushTextWrapPos(cardWidth - 52);
                Widgets.Text(fonts.Micro, Theme.Alpha(Theme.Warn, 0.8f), detail);
                ImGui.PopTextWrapPos();
            }

            ImGui.Dummy(new Vector2(0, 14));
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.Alpha(Theme.Accent, 0.18f));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.AccentText);
            if (ImGui.Button(Strings.T("retry"), new Vector2(0, 32)))
            {
                if (model.Active is { } active) model.SwitchDevice(active.Device);
            }
            ImGui.PopStyleColor(2);

            ImGui.SameLine(0, 10);
            Widgets.Text(fonts.MonoTiny, Theme.TextMuted, Strings.T("retryIn"));
        }
        ImGui.End();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }
}
