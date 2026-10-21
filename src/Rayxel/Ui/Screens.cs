using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// The full-stage screens: start-up, the device choice, and the states where the card is
/// there but no picture is — the console asleep, or the card held by another application.
/// </summary>
public static class Screens
{
    public const double SplashDurationMs = 1700;

    private const ImGuiWindowFlags CardFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav;

    /// <summary>
    /// The stage behind these screens: flat. The radial glow it used to have was the most
    /// decorative thing in the app and said nothing.
    /// </summary>
    private static void Stage(AppModel model) =>
        ImGui.GetBackgroundDrawList().AddRectFilled(Vector2.Zero,
            new Vector2(model.Renderer.Width, model.Renderer.Height), Theme.U32(Theme.Stage));

    /// <summary>A window centred on the screen, at a fixed width and whatever height it needs.</summary>
    private static bool BeginCentred(AppModel model, string id, float width, Vector2 padding, Vector4 background)
    {
        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width, model.Renderer.Height) * 0.5f,
                               ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(4)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, background.W > 0 ? Theme.SurfaceStroke : default);
        return ImGui.Begin(id, CardFlags);
    }

    private static void EndCentred()
    {
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    /// <summary>Text centred in the current window.</summary>
    private static void Centred(ImFontPtr font, Vector4 colour, string text)
    {
        float w = Widgets.Measure(font, text).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) * 0.5f);
        Widgets.Text(font, colour, text);
    }

    public static void Splash(AppModel model)
    {
        var f = Widgets.F;
        Stage(model);

        if (BeginCentred(model, "##splash", Layout.S(320), Vector2.Zero, default))
        {
            float mark = Layout.S(56);
            var at = ImGui.GetCursorScreenPos() + new Vector2((ImGui.GetWindowWidth() - mark) * 0.5f, 0);
            Brand.Mark(ImGui.GetWindowDrawList(), at, mark);
            ImGui.Dummy(new Vector2(mark, mark + Layout.S(12)));

            Centred(f.Title, Theme.TextPrimary, "Rayxel");
            ImGui.Dummy(new Vector2(0, Layout.S(20)));

            // Fluent's progress bar: a hairline track with a 3px accent indicator on it.
            // The splash is a real wait — capture modes are being read behind it — and it
            // finishes when the app is ready to move on.
            float t = (float)Math.Clamp(model.SplashElapsedMs / 1600.0, 0, 1);
            float eased = 1f - MathF.Pow(1f - t, 3f);
            var pos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X, bar = Layout.S(3);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(new Vector2(pos.X, pos.Y + bar / 2f - 0.5f), new Vector2(pos.X + width, pos.Y + bar / 2f + 0.5f),
                               Theme.U32(Theme.ControlStrong));
            if (eased > 0.001f)
                draw.AddRectFilled(pos, pos + new Vector2(width * eased, bar), Theme.U32(Theme.Accent), bar / 2f);
            ImGui.Dummy(new Vector2(width, bar));

            ImGui.Dummy(new Vector2(0, Layout.S(8)));
            Centred(f.Caption, Theme.TextTertiary, Strings.T("splashStatus"));
        }
        EndCentred();
    }

    public static void DevicePicker(AppModel model)
    {
        var f = Widgets.F;
        Stage(model);

        float width = Math.Min(Layout.S(480), model.Renderer.Width * 0.92f);
        if (BeginCentred(model, "##devices", width, new Vector2(Layout.S(16), Layout.S(20)), Theme.Card))
        {
            ImGui.Indent(Layout.S(8));
            Widgets.Text(f.Subtitle, Theme.TextPrimary, Strings.T("chooseDevice"));
            ImGui.PushTextWrapPos(Widgets.ContentRight() - Layout.S(8));
            Widgets.Text(f.Caption, Theme.TextTertiary, Strings.T("chooseHint"));
            ImGui.PopTextWrapPos();
            ImGui.Unindent(Layout.S(8));
            ImGui.Dummy(new Vector2(0, Layout.S(8)));

            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, Layout.S(2)));
            foreach (var device in model.Devices) DeviceRow(model, device);
            ImGui.PopStyleVar();
        }
        EndCentred();
    }

    private static void DeviceRow(AppModel model, CaptureDeviceInfo device)
    {
        var f = Widgets.F;
        model.Probed.TryGetValue(device.SymbolicLink, out var probe);

        // A device never opened is shown as exactly that. Busy cannot be known without
        // trying, so the row does not claim to know.
        var (status, colour, detail, selectable) = probe switch
        {
            { Availability: DeviceAvailability.Ready } p =>
                (Strings.T("dAvail"), Theme.Success, Details(p), true),
            { Availability: DeviceAvailability.Busy } =>
                (Strings.T("dBusyNow"), Theme.Caution, "", false),
            { Availability: DeviceAvailability.Unusable } p =>
                (Strings.T("dOffline"), Theme.Caution, Strings.Modes(p.Modes.Count), false),
            { } p => (p.Problem ?? Strings.T("dOffline"), Theme.Caution, "", false),
            _ => (NotChecked(device), Theme.TextTertiary, "", true)
        };

        bool current = model.Pump.Device.Is(device) && model.Pump.State == CaptureState.Streaming;
        if (Widgets.ListRow($"##dev{device.Ordinal}", Layout.S(60), current, selectable, out var min, out var max))
        {
            model.SwitchDevice(device);
            model.Screen = Screen.App;
        }

        var draw = ImGui.GetWindowDrawList();
        float inset = Layout.S(12), icon = Layout.S(20);
        float height = max.Y - min.Y;
        Icons.Draw(draw, device.IsVirtual ? Icon.MonitorOff : Icon.Camera,
                   new Vector2(min.X + inset, min.Y + (height - icon) / 2f), icon,
                   selectable ? Theme.TextSecondary : Theme.TextDisabled);

        float x = min.X + inset + icon + Layout.S(12);
        var name = Widgets.Measure(f.Body, device.FriendlyName);
        var line = Widgets.Measure(f.Caption, status);
        float top = min.Y + (height - name.Y - line.Y - Layout.S(1)) / 2f;

        Widgets.DrawText(draw, f.Body, new Vector2(x, top), selectable ? Theme.TextPrimary : Theme.TextDisabled, device.FriendlyName);
        Widgets.DrawText(draw, f.Caption, new Vector2(x, top + name.Y + Layout.S(1)), colour, status);
        if (detail.Length > 0)
            Widgets.DrawText(draw, f.Caption, new Vector2(x + line.X, top + name.Y + Layout.S(1)), Theme.TextTertiary, " · " + detail);
    }

    private static string Details(DeviceProbe p)
    {
        var modes = Strings.Modes(p.Modes.Count);
        return !p.Device.IsVirtual && p.Link.Known ? $"{modes} · {p.Link.LinkClass}" : modes;
    }

    private static string NotChecked(CaptureDeviceInfo d)
    {
        if (d.IsVirtual)
            return Strings.Current switch
            {
                "ru" => "программная камера, без провода",
                "uk" => "програмна камера, без дроту",
                "de" => "Software-Kamera, ohne Leitung",
                "es" => "cámara por software, sin cable",
                _ => "software camera, no wire"
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
    /// The card is there, the picture is not. Three causes, three cards: the source has
    /// gone quiet (it comes back by itself), another application holds the card (the user
    /// has to close it), or the card itself went away. The first is the common one and
    /// the card says so calmly; nothing here is an alarm.
    /// </summary>
    public static void NoSignal(AppModel model)
    {
        var f = Widgets.F;
        Stage(model);

        bool busy = model.DeviceBusy;
        bool lost = !busy && model.Pump.State == CaptureState.Lost && !model.ForceNoSignal;
        string name = model.Pump.Device.FriendlyName;

        var (icon, title, body) = busy ? (Icon.Lock, Strings.T("busyTitle"), string.Format(Strings.T("busyBody"), name))
                                : lost ? (Icon.MonitorOff, Strings.T("lostTitle"), string.Format(Strings.T("lostBody"), name))
                                : (Icon.MonitorOff, Strings.T("nsTitle"), Strings.T("nsBody"));

        if (BeginCentred(model, "##nosignal", Layout.S(440), new Vector2(Layout.S(24)), Theme.Card))
        {
            float glyph = Layout.S(24);
            Icons.Draw(ImGui.GetWindowDrawList(), icon, ImGui.GetCursorScreenPos(), glyph, Theme.TextSecondary);
            ImGui.Dummy(new Vector2(glyph, glyph + Layout.S(8)));

            Widgets.Text(f.Subtitle, Theme.TextPrimary, title);
            ImGui.Dummy(new Vector2(0, Layout.S(2)));
            Widgets.Wrapped(f.Body, Theme.TextSecondary, body);

            // A lost device is retried every three seconds by the frame loop; a quiet one
            // needs nothing and recovers when frames resume. The ring says something is
            // still being waited for, which is the reassuring part.
            if (!busy)
            {
                ImGui.Dummy(new Vector2(0, Layout.S(8)));
                var pos = ImGui.GetCursorScreenPos();
                float ring = Layout.S(16);
                ProgressRing(ImGui.GetWindowDrawList(), pos + new Vector2(ring / 2f), ring / 2f - 1f);
                var waitText = lost ? Strings.Sentence(Strings.T("retryIn")) : Strings.T("nsWait");
                ImGui.SetCursorScreenPos(pos + new Vector2(ring + Layout.S(8), 0));
                ImGui.PushTextWrapPos(0);
                Widgets.Text(f.Caption, Theme.TextTertiary, waitText);
                ImGui.PopTextWrapPos();
            }

            // The log's own sentence, for the case nobody predicted.
            if (lost && model.Pump.StateDetail is { } detail)
                Widgets.Wrapped(f.Caption, Theme.TextTertiary, detail);

            ImGui.Dummy(new Vector2(0, Layout.S(16)));
            float gap = Layout.S(8);
            float half = (ImGui.GetContentRegionAvail().X - gap) / 2f;
            var size = new Vector2(half, Layout.S(32));

            if (Widgets.Button(Strings.T(busy ? "retryNow" : "retry"), size, accent: true)
                && model.Active is { } active)
                model.SwitchDevice(active.Device);
            ImGui.SameLine(0, gap);
            if (Widgets.Button(Strings.T("otherDevice"), size))
                model.Screen = Screen.Devices;
        }
        EndCentred();
    }

    /// <summary>Fluent's indeterminate progress ring: a quarter arc going round once a second.</summary>
    private static void ProgressRing(ImDrawListPtr draw, Vector2 centre, float radius)
    {
        float t = (float)(ImGui.GetTime() % 1.0) * MathF.PI * 2f;
        float thickness = Math.Max(1.5f, Layout.S(2));
        draw.AddCircle(centre, radius, Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.12f)), 0, thickness);
        draw.PathArcTo(centre, radius, t, t + MathF.PI * 0.6f);
        draw.PathStroke(Theme.U32(Theme.Accent), ImDrawFlags.None, thickness);
    }
}
