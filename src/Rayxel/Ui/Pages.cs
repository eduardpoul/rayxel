using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;
using Rayxel.Link.Control;

namespace Rayxel.Ui;

/// <summary>
/// The app when it is not showing a picture: a menu down the left - Watch, Stream, Settings -
/// and the page it opens. Watch lists where a picture can come from and opens one; Stream
/// makes this computer the source for Rayxel on another; Settings has everything else.
/// <para>
/// One program for both directions. The sender used to be a second app with a window of its
/// own, in another toolkit that could not look like this one; its window is this page now,
/// and the program that actually sends, rayxel-send, runs without one.
/// </para>
/// </summary>
internal static class Pages
{
    private const ImGuiWindowFlags HostFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoCollapse;

    public static void Draw(AppModel model)
    {
        var display = new Vector2(model.Renderer.Width, model.Renderer.Height);
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(display, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Layer);

        if (ImGui.Begin("##pages", HostFlags))
        {
            float nav = Math.Min(Layout.S(260), display.X * 0.3f);
            Nav(model, nav, display.Y);

            // The page on its own surface, a step darker, its top-left corner rounded where
            // it meets the menu - Fluent's navigation view.
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(new Vector2(nav, 0), display, Theme.U32(Theme.Stage), Layout.S(Theme.RadiusOverlay),
                               ImDrawFlags.RoundCornersTopLeft);
            draw.AddRect(new Vector2(nav, 0), display + new Vector2(1), Theme.U32(Theme.CardStroke), Layout.S(Theme.RadiusOverlay),
                         ImDrawFlags.RoundCornersTopLeft);

            ImGui.SetCursorPos(new Vector2(nav, 0));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(40), Layout.S(32)));
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, Layout.S(10));
            if (ImGui.BeginChild("##page", new Vector2(display.X - nav, display.Y), ImGuiChildFlags.AlwaysUseWindowPadding))
            {
                // One column, no wider than reads well, as Windows' own Settings keeps it.
                float column = Math.Min(ImGui.GetContentRegionAvail().X, Layout.S(920));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                if (ImGui.BeginChild("##column", new Vector2(column, 0), ImGuiChildFlags.AutoResizeY))
                {
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(4)));
                    switch (model.Screen)
                    {
                        case Screen.Transmit: Transmit(model); break;
                        case Screen.Settings: SettingsPage(model); break;
                        default: Sources(model); break;
                    }
                    ImGui.PopStyleVar();
                }
                ImGui.EndChild();
                ImGui.PopStyleVar();
            }
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(4);
    }

    // ── the menu ─────────────────────────────────────────────────────────────────

    private static void Nav(AppModel model, float width, float height)
    {
        var f = Widgets.F;
        var draw = ImGui.GetWindowDrawList();
        float pad = Layout.S(8);

        // The mark and the name.
        float mark = Layout.S(24);
        Brand.Mark(draw, new Vector2(pad + Layout.S(12), Layout.S(20)), mark);
        var name = Widgets.Measure(f.BodyStrong, "Rayxel");
        Widgets.DrawText(draw, f.BodyStrong, new Vector2(pad + Layout.S(12) + mark + Layout.S(12), Layout.S(20) + (mark - name.Y) / 2f),
                         Theme.TextPrimary, "Rayxel");

        float y = Layout.S(68);
        Item(model, Icon.Camera, Strings.T("navWatch"), Screen.Sources, "", default, width, ref y);

        // Whether this computer is sending, beside the page that does it.
        string note = "";
        Vector4 noteColour = Theme.TextTertiary;
        if (model.Sender.Status is { } sender)
        {
            note = Strings.T(sender.Enabled ? "navOn" : "navOff");
            noteColour = sender.State == SenderStates.Streaming ? Theme.Success : Theme.TextTertiary;
        }
        Item(model, Icon.Broadcast, Strings.T("navTransmit"), Screen.Transmit, note, noteColour, width, ref y);

        float bottom = height - Layout.S(16) - Layout.S(40);
        Item(model, Icon.Gear, Strings.T("navSettings"), Screen.Settings, "", default, width, ref bottom);
    }

    /// <summary>A menu row: Fluent's navigation item, the accent pill at its left edge while it is the page shown.</summary>
    private static void Item(AppModel model, Icon icon, string label, Screen screen, string note, Vector4 noteColour,
                             float width, ref float y)
    {
        var f = Widgets.F;
        float pad = Layout.S(8), height = Layout.S(40);
        bool current = model.Screen == screen || (screen == Screen.Sources && model.Screen == Screen.Sources);
        ImGui.SetCursorPos(new Vector2(pad, y));
        var size = new Vector2(width - pad * 2, height);
        if (Widgets.SubtleButton("##nav" + screen, size, current, out var min)) model.Screen = screen;

        var draw = ImGui.GetWindowDrawList();
        if (current)
        {
            float pill = Layout.S(16), w = Layout.S(3);
            draw.AddRectFilled(new Vector2(min.X, min.Y + (height - pill) / 2f), new Vector2(min.X + w, min.Y + (height + pill) / 2f),
                               Theme.U32(Theme.Accent), w / 2f);
        }
        float glyph = Layout.S(16), x = min.X + Layout.S(12);
        Icons.Draw(draw, icon, new Vector2(x, min.Y + (height - glyph) / 2f), glyph, Theme.TextPrimary);
        var text = Widgets.Measure(f.Body, label);
        Widgets.DrawText(draw, f.Body, new Vector2(x + glyph + Layout.S(12), min.Y + (height - text.Y) / 2f), Theme.TextPrimary, label);
        if (note.Length > 0)
        {
            var n = Widgets.Measure(f.Caption, note);
            Widgets.DrawText(draw, f.Caption, new Vector2(min.X + size.X - Layout.S(12) - n.X, min.Y + (height - n.Y) / 2f), noteColour, note);
        }
        y += height + Layout.S(4);
    }

    // ── shared pieces ────────────────────────────────────────────────────────────

    private static void Title(string title, string subtitle)
    {
        var f = Widgets.F;
        Widgets.Text(f.Title, Theme.TextPrimary, title);
        ImGui.Dummy(new Vector2(0, Layout.S(2)));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        Widgets.Wrapped(f.Body, Theme.TextSecondary, subtitle);
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, Layout.S(20)));
    }

    private static void SectionTitle(string title, string note = "")
    {
        var f = Widgets.F;
        var pos = ImGui.GetCursorScreenPos();
        Widgets.Text(f.BodyStrong, Theme.TextPrimary, title);
        if (note.Length > 0)
        {
            var n = Widgets.Measure(f.Caption, note);
            float right = pos.X + ImGui.GetContentRegionAvail().X;
            if (right - n.X > pos.X + Widgets.Measure(f.BodyStrong, title).X + Layout.S(24))
                Widgets.DrawText(ImGui.GetWindowDrawList(), f.Caption, new Vector2(right - n.X, pos.Y + Layout.S(2)), Theme.TextTertiary, note);
        }
        ImGui.Dummy(new Vector2(0, Layout.S(6)));
    }

    /// <summary>A card: rows on one rounded surface, as long as they need.</summary>
    private static bool BeginCard(string id)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Card);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.CardStroke);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Layout.S(Theme.RadiusOverlay));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(4)));
        bool open = ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding | ImGuiChildFlags.Borders);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), 0));
        return open;
    }

    private static void EndCard()
    {
        ImGui.PopStyleVar();
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        ImGui.Dummy(new Vector2(0, Layout.S(24)));
    }

    private static Vector2 ButtonSize(string label) =>
        new(Math.Max(Layout.S(96), Widgets.Measure(Widgets.F.Body, label).X + Layout.S(32)), Layout.S(32));

    // ── Watch ────────────────────────────────────────────────────────────────────

    private static void Sources(AppModel model)
    {
        var f = Widgets.F;
        Title(Strings.T("watchTitle"), Strings.T("watchSub"));

        // Every source as a small live picture, the one on the screen first; a click opens it.
        if (model.Previews is not null && model.Devices.Count > 0)
            Tiles(model);
        // Without them, what is on the screen right now, and the way back to it.
        else if (!model.NoDevice && model.Pump.State is CaptureState.Streaming or CaptureState.NoSignal or CaptureState.Starting)
        {
            if (BeginCard("##now"))
            {
                float row = Layout.S(56), inset = Layout.S(12);
                var pos = ImGui.GetCursorScreenPos();
                float avail = ImGui.GetContentRegionAvail().X;
                string text = string.Format(Strings.T("nowShowing"), NetDevice.DisplayName(model.Pump.Device, model.Devices));
                var size = Widgets.Measure(f.Body, text);
                Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body, new Vector2(pos.X + inset, pos.Y + (row - size.Y) / 2f), Theme.TextPrimary, text);
                string back = Strings.T("backToPicture");
                var button = ButtonSize(back);
                ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - inset - button.X, pos.Y + (row - button.Y) / 2f));
                if (Widgets.Button(back, button, accent: true)) model.Screen = Screen.App;
                ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + row));
                ImGui.Dummy(Vector2.Zero);
            }
            EndCard();
        }

        var cards = model.Devices.Where(d => d.Bus != DeviceBus.Network).ToList();
        var network = model.Devices.Where(d => d.Bus == DeviceBus.Network).ToList();

        SectionTitle(Strings.T("groupCards"));
        if (BeginCard("##cards"))
        {
            if (cards.Count == 0) EmptyRow(Strings.T("noneCards"));
            for (int i = 0; i < cards.Count; i++)
            {
                if (i > 0) RowDivider();
                SourceRow(model, cards[i]);
            }
        }
        EndCard();

        SectionTitle(Strings.T("groupNetwork"), Strings.T("networkHint"));
        if (BeginCard("##network"))
        {
            if (network.Count == 0) EmptyRow(Strings.T("noneNetwork"));
            for (int i = 0; i < network.Count; i++)
            {
                if (i > 0) RowDivider();
                SourceRow(model, network[i]);
            }
        }
        // A card's spacing after it would come before the button that belongs to it.
        ImGui.PopStyleVar();
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        ImGui.Dummy(new Vector2(0, Layout.S(10)));

        string pair = Strings.T("pairDevice");
        if (Widgets.Button(pair, ButtonSize(pair))) model.Pairing ??= new PairFlow();
        ImGui.Dummy(new Vector2(0, Layout.S(24)));
    }

    /// <summary>
    /// The sources as tiles, a live picture in each: as many to a row as fit at a readable
    /// size, none wider than a thumbnail should be.
    /// </summary>
    private static void Tiles(AppModel model)
    {
        var watching = Watching(model);
        var devices = model.Devices
            .OrderBy(d => watching is not null && d.Is(watching) ? 0 : 1)
            .ThenBy(d => d.Bus == DeviceBus.Network ? 1 : 0)
            .ToList();

        float avail = ImGui.GetContentRegionAvail().X;
        float gap = Layout.S(12);
        int perRow = Math.Clamp((int)((avail + gap) / (Layout.S(220) + gap)), 1, devices.Count);
        float width = MathF.Floor(Math.Min(Layout.S(300), (avail - gap * (perRow - 1)) / perRow));
        float pictureHeight = MathF.Round(width * 9f / 16f);
        var size = new Vector2(width, pictureHeight + Layout.S(52));

        var origin = ImGui.GetCursorScreenPos();
        int rows = (devices.Count + perRow - 1) / perRow;
        for (int i = 0; i < devices.Count; i++)
        {
            var at = origin + new Vector2(i % perRow * (size.X + gap), i / perRow * (size.Y + gap));
            Tile(model, devices[i], watching, at, size, pictureHeight);
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(0, rows * (size.Y + gap) - gap));
        ImGui.Dummy(new Vector2(avail, Layout.S(24)));
    }

    // The source on the screen, while there is one.
    private static CaptureDeviceInfo? Watching(AppModel model) =>
        !model.NoDevice && model.Pump.State is CaptureState.Streaming or CaptureState.NoSignal or CaptureState.Starting
            ? model.Pump.Device : null;

    /// <summary>
    /// One source's tile: its picture filling the top, its name and what it is doing below.
    /// The one on the screen is outlined in the accent colour and marked so.
    /// </summary>
    private static void Tile(AppModel model, CaptureDeviceInfo device, CaptureDeviceInfo? watching, Vector2 min, Vector2 size,
                             float pictureHeight)
    {
        var f = Widgets.F;
        var draw = ImGui.GetWindowDrawList();
        bool current = watching is not null && device.Is(watching);
        bool forgot = NetDevice.Forgot(device);
        var feed = current ? null : model.Previews?.For(device);
        var surface = current ? model.Renderer.ActivePreview : feed?.Surface;
        var state = feed?.State ?? PreviewState.Starting;

        bool network = device.Bus == DeviceBus.Network;
        var (status, colour) = current ? (CurrentMode(model), Theme.TextSecondary)
            : forgot ? (Strings.T("tileForgot"), Theme.Caution)
            : state switch
            {
                // What it will show when opened, as the one on the screen says what it shows.
                PreviewState.Live => (feed?.Opens is { } opens ? ModeText(opens.Width, opens.Height, opens.MaxFps) : Strings.T("dAvail"),
                                      Theme.TextSecondary),
                PreviewState.NoSignal => (Strings.T("tileNoSignal"), Theme.Caution),
                PreviewState.Busy => (Strings.T(network ? "tileBusyNet" : "tileBusyCard"), Theme.Caution),
                PreviewState.Off => (Strings.T("tileOff"), Theme.Caution),
                PreviewState.NotFound => (Strings.T("tileNotFound"), Theme.TextTertiary),
                PreviewState.Forgotten => (Strings.T("tileForgot"), Theme.Caution),
                PreviewState.Failed => (Strings.T("tileFailed"), Theme.TextTertiary),
                _ => (Strings.T("tileStarting"), Theme.TextTertiary)
            };
        // A picture that has stopped stays up, as the last thing the source showed.
        bool picture = surface is { TextureId: not 0 } && (current || state is PreviewState.Live or PreviewState.NoSignal);

        ImGui.SetCursorScreenPos(min);
        ImGui.PushID(device.SymbolicLink);
        bool clicked = ImGui.InvisibleButton("##tile", size);
        bool hovered = ImGui.IsItemHovered();
        ImGui.PopID();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var max = min + size;
        var pictureMax = new Vector2(max.X, min.Y + pictureHeight);
        float radius = Layout.S(Theme.RadiusOverlay);
        draw.AddRectFilled(min, max, Theme.U32(hovered ? new Vector4(0.2f, 0.2f, 0.2f, 1f) : Theme.Card), radius);
        draw.AddRectFilled(min, pictureMax, Theme.U32(Theme.Stage), radius, ImDrawFlags.RoundCornersTop);

        if (picture)
        {
            // Filling the frame: what does not fit its proportions is cut from the edges.
            float aspect = surface!.Width / (float)surface.Height, frame = size.X / pictureHeight;
            Vector2 uv0 = Vector2.Zero, uv1 = Vector2.One;
            if (aspect > frame) { uv0.X = (1f - frame / aspect) / 2f; uv1.X = 1f - uv0.X; }
            else { uv0.Y = (1f - aspect / frame) / 2f; uv1.Y = 1f - uv0.Y; }
            // A step dimmer until pointed at, so the one under the pointer is the bright one.
            uint tint = hovered || current ? 0xFFFFFFFFu : Theme.U32(new Vector4(0.88f, 0.88f, 0.88f, 1f));
            draw.AddImageRounded(surface.TextureId, min, pictureMax, uv0, uv1, tint, radius, ImDrawFlags.RoundCornersTop);
        }
        else
        {
            float glyph = Layout.S(28);
            var centre = (min + pictureMax) / 2f;
            Icons.Draw(draw, GlyphOf(device), centre - new Vector2(glyph / 2f), glyph, Theme.TextDisabled);
        }

        if (current)
        {
            string now = Strings.T("tileNow");
            var text = Widgets.Measure(f.Caption, now);
            var pillMin = min + new Vector2(Layout.S(8));
            var pillMax = pillMin + text + new Vector2(Layout.S(16), Layout.S(6));
            draw.AddRectFilled(pillMin, pillMax, Theme.U32(Theme.Accent), Layout.S(Theme.RadiusControl));
            Widgets.DrawText(draw, f.Caption, pillMin + new Vector2(Layout.S(8), Layout.S(3)), Theme.TextOnAccent, now);
        }

        var stroke = current ? Theme.Accent : hovered ? Theme.SurfaceStroke : Theme.ControlStroke;
        draw.AddRect(min, max, Theme.U32(stroke), radius, ImDrawFlags.None, current ? Layout.S(2) : 1f);

        // The name and its state, cut short at the tile's edge rather than running past it.
        float inset = Layout.S(12);
        string title = NetDevice.DisplayName(device, model.Devices);
        var name = Widgets.Measure(f.Body, title);
        var line = Widgets.Measure(f.Caption, status);
        float top = pictureMax.Y + (max.Y - pictureMax.Y - name.Y - line.Y - Layout.S(1)) / 2f;
        draw.PushClipRect(new Vector2(min.X + inset, pictureMax.Y), new Vector2(max.X - inset, max.Y), true);
        Widgets.DrawText(draw, f.Body, new Vector2(min.X + inset, top), Theme.TextPrimary, title);
        Widgets.DrawText(draw, f.Caption, new Vector2(min.X + inset, top + name.Y + Layout.S(1)), colour, status);
        draw.PopClipRect();

        if (clicked) Open(model, device, forgot, current);
    }

    // What the source on the screen is sending: its size and rate.
    private static string CurrentMode(AppModel model) =>
        model.Pump.CurrentMode is { } mode ? ModeText(mode.Width, mode.Height, model.Pump.CurrentFps) : Strings.T("tileStarting");

    private static string ModeText(int width, int height, double fps) => $"{width}×{height} · {fps:0} fps";

    private static Icon GlyphOf(CaptureDeviceInfo device)
    {
        var peer = NetDevice.PeerOf(device);
        return device.IsVirtual ? Icon.MonitorOff
            : peer is not null ? (peer.Model == "Steam Deck" ? Icon.Handheld : Icon.Monitor)
            : Icon.Camera;
    }

    private static void EmptyRow(string text)
    {
        var f = Widgets.F;
        float row = Layout.S(48);
        var pos = ImGui.GetCursorScreenPos();
        var size = Widgets.Measure(f.Body, text);
        Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body, new Vector2(pos.X + Layout.S(12), pos.Y + (row - size.Y) / 2f), Theme.TextTertiary, text);
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, row));
    }

    private static void RowDivider()
    {
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(pos + new Vector2(Layout.S(12), 0), pos + new Vector2(w - Layout.S(12), 0), Theme.U32(Theme.Divider));
        ImGui.Dummy(new Vector2(w, 1));
    }

    /// <summary>
    /// One source: what it is, whether it can be opened, and the button that opens it - the
    /// accent one for the source on the screen. A paired device can be forgotten from its
    /// row; one that has forgotten this computer opens pairing instead.
    /// </summary>
    private static void SourceRow(AppModel model, CaptureDeviceInfo device)
    {
        var f = Widgets.F;
        model.Probed.TryGetValue(device.SymbolicLink, out var probe);
        var peer = NetDevice.PeerOf(device);
        bool forgot = NetDevice.Forgot(device);

        // A device never opened is shown as exactly that; busy cannot be known without trying.
        var (status, colour, detail, selectable) = probe switch
        {
            { Availability: DeviceAvailability.Ready } p =>
                (Strings.T("dAvail"), Theme.Success, Details(p), true),
            { Availability: DeviceAvailability.Busy } =>
                (Strings.T("dBusyNow"), Theme.Caution, "", false),
            { Availability: DeviceAvailability.Unusable } p =>
                (Strings.T("dOffline"), Theme.Caution, Strings.Modes(p.Modes.Count), false),
            { } p => (p.Problem ?? Strings.T("dOffline"), Theme.Caution, "", forgot),
            _ => (NotChecked(device), Theme.TextTertiary, "", true)
        };
        // Unless its tile has tried: a card whose picture is in its tile is free.
        if (probe is null && device.Bus != DeviceBus.Network
            && model.Previews?.For(device)?.State is PreviewState.Live or PreviewState.NoSignal)
            (status, colour) = (Strings.T("dAvail"), Theme.Success);

        bool current = model.Pump.Device.Is(device) && !model.NoDevice
                       && model.Pump.State is CaptureState.Streaming or CaptureState.NoSignal or CaptureState.Starting;
        float row = Layout.S(64);
        if (Widgets.ListRow($"##src{device.Ordinal}", row, false, selectable, out var min, out var max, overlap: true))
            Open(model, device, forgot, current);

        var draw = ImGui.GetWindowDrawList();
        float inset = Layout.S(12), icon = Layout.S(20);
        Icons.Draw(draw, GlyphOf(device), new Vector2(min.X + inset, min.Y + (row - icon) / 2f), icon,
                   selectable ? Theme.TextSecondary : Theme.TextDisabled);

        string title = NetDevice.DisplayName(device, model.Devices);
        float x = min.X + inset + icon + Layout.S(16);
        var name = Widgets.Measure(f.Body, title);
        var line = Widgets.Measure(f.Caption, status);
        float top = min.Y + (row - name.Y - line.Y - Layout.S(1)) / 2f;
        Widgets.DrawText(draw, f.Body, new Vector2(x, top), selectable ? Theme.TextPrimary : Theme.TextDisabled, title);
        Widgets.DrawText(draw, f.Caption, new Vector2(x, top + name.Y + Layout.S(1)), colour, status);
        if (detail.Length > 0)
            Widgets.DrawText(draw, f.Caption, new Vector2(x + line.X, top + name.Y + Layout.S(1)), Theme.TextTertiary, " · " + detail);

        // The buttons at the right end: forget (a paired device), then watch.
        float right = max.X - Layout.S(12);
        var after = ImGui.GetCursorScreenPos();
        ImGui.PushID(device.SymbolicLink);
        if (selectable)
        {
            string watch = Strings.T(forgot ? "pairAgain" : "watchBtn");
            var size = ButtonSize(watch);
            right -= size.X;
            ImGui.SetCursorScreenPos(new Vector2(right, min.Y + (row - size.Y) / 2f));
            if (Widgets.Button(watch, size, accent: current || forgot)) Open(model, device, forgot, current);
            right -= Layout.S(8);
        }
        if (peer is not null)
        {
            string forget = Strings.T("forgetShort");
            var size = new Vector2(Widgets.Measure(f.Body, forget).X + Layout.S(24), Layout.S(32));
            right -= size.X;
            ImGui.SetCursorScreenPos(new Vector2(right, min.Y + (row - size.Y) / 2f));
            if (Widgets.SubtleButton("##forget", size, false, out var at)) model.ConfirmForget = peer;
            var text = Widgets.Measure(f.Body, forget);
            Widgets.DrawText(draw, f.Body, at + (size - text) / 2f, Theme.TextSecondary, forget);
        }
        ImGui.PopID();
        ImGui.SetCursorScreenPos(after);
    }

    private static void Open(AppModel model, CaptureDeviceInfo device, bool forgot, bool current)
    {
        if (forgot) { model.Pairing ??= new PairFlow(); return; }
        if (!current) model.SwitchDevice(device);
        model.Screen = Screen.App;
    }

    private static string Details(DeviceProbe p)
    {
        var modes = Strings.Modes(p.Modes.Count);
        return !p.Device.IsVirtual && p.Link.Known ? $"{modes} · {p.Link.LinkClass}" : modes;
    }

    private static string NotChecked(CaptureDeviceInfo d)
    {
        // A paired device is being looked for while this page is up; its answer replaces this.
        if (NetDevice.PeerOf(d) is not null) return Strings.T("deckLooking");
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

    // ── Settings ─────────────────────────────────────────────────────────────────

    private static void SettingsPage(AppModel model)
    {
        Title(Strings.T("settingsTitle"), Strings.T("settingsSub"));
        SettingsUi.Page(model);
    }

    // ── Stream ───────────────────────────────────────────────────────────────────

    private static void Transmit(AppModel model)
    {
        var f = Widgets.F;
        var link = model.Sender;
        Title(Strings.T("txTitle"), Strings.T("txSub"));

        if (!SenderLink.Present)
        {
            Widgets.Wrapped(f.Body, Theme.Caution, string.Format(Strings.T("txNoEngine"), SenderLink.ExecutableName));
            return;
        }

        var status = link.Status;
        if (status is null)
        {
            if (BeginCard("##txoff"))
            {
                CardBody(() =>
                {
                    Widgets.Text(f.Title, Theme.TextSecondary, Strings.T("txNotRunning"));
                    ImGui.Dummy(new Vector2(0, Layout.S(4)));
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, Strings.T("txNotRunningBody"));
                    ImGui.Dummy(new Vector2(0, Layout.S(12)));
                    bool starting = link.Starting;
                    string label = Strings.T(starting ? "txStarting" : "txStart");
                    if (Widgets.Button(label, ButtonSize(label), accent: true, enabled: !starting))
                        _startError = link.StartEngine();
                    if ((_startError ?? link.Error) is { } error)
                    {
                        ImGui.Dummy(new Vector2(0, Layout.S(8)));
                        Widgets.Wrapped(f.Caption, Theme.Caution, error);
                    }
                });
            }
            EndCard();
            return;
        }
        _startError = null;

        // The state, the switch, and while someone is pairing, the code to type there.
        if (BeginCard("##txstate"))
        {
            CardBody(() =>
            {
                var (word, colour) = StateWord(status);
                var pos = ImGui.GetCursorScreenPos();
                float avail = ImGui.GetContentRegionAvail().X;
                Widgets.Text(f.Title, colour, word);

                // The switch at the right, level with the state.
                var sw = Widgets.SwitchSize;
                string on = Strings.T("txOn");
                var onSize = Widgets.Measure(f.Body, on);
                float lineH = Widgets.Measure(f.Title, word).Y;
                Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body,
                                 new Vector2(pos.X + avail - sw.X - Layout.S(12) - onSize.X, pos.Y + (lineH - onSize.Y) / 2f),
                                 Theme.TextSecondary, on);
                var resume = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - sw.X, pos.Y + (lineH - sw.Y) / 2f));
                bool enabled = status.Enabled;
                if (Widgets.ToggleSwitch("##txenabled", ref enabled))
                    link.Send(c => c.SetEnabledAsync(enabled));
                ImGui.SetCursorScreenPos(resume);

                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + avail);
                Widgets.Wrapped(f.Body, Theme.TextSecondary, StateDetail(status));
                if (status.Problem is { Length: > 0 } problem) Widgets.Wrapped(f.Caption, Theme.Caution, problem);
                if (link.Error is { } error) Widgets.Wrapped(f.Caption, Theme.Caution, error);
                ImGui.PopTextWrapPos();

                if (status.Pairing is { } pairing) PairingBlock(link, pairing);

                // Discoverable for a while, and the way to stop being so.
                ImGui.Dummy(new Vector2(0, Layout.S(16)));
                long left = status.DiscoverableUntilMs is { } until ? until - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
                string discover = left > 0 ? string.Format(Strings.T("txDiscoverClose"), Countdown(left)) : Strings.T("txDiscover");
                if (Widgets.Button(discover, ButtonSize(discover), enabled: status.Enabled))
                    link.Send(c => c.SetDiscoverableAsync(left <= 0));
                ImGui.SameLine(0, Layout.S(16));
                var hintAt = ImGui.GetCursorScreenPos();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.SetCursorScreenPos(hintAt + new Vector2(0, Layout.S(7)));
                Widgets.Wrapped(f.Caption, Theme.TextTertiary, Strings.T("txDiscoverHint"));
                ImGui.PopTextWrapPos();
            });
        }
        EndCard();

        // Who may watch.
        SectionTitle(Strings.T("txLinked"));
        if (BeginCard("##txlinked"))
        {
            if (status.Viewers.Count == 0) EmptyRow(Strings.T("txLinkedNone"));
            string? watching = status.State == SenderStates.Streaming ? status.Viewer?.Id : null;
            for (int i = 0; i < status.Viewers.Count; i++)
            {
                if (i > 0) RowDivider();
                var viewer = status.Viewers[i];
                bool now = viewer.Id == watching;
                TextRow(viewer.Name.Length > 0 ? viewer.Name : viewer.Id,
                        now ? Strings.T("txWatchingNow") : LastSeen(viewer.LastSeenMs),
                        now ? Theme.Success : Theme.TextTertiary,
                        Strings.T("forgetShort"), () => link.Send(c => c.ForgetAsync(viewer.Id)), viewer.Id);
            }
        }
        EndCard();

        SectionTitle(Strings.T("txOptions"));
        if (BeginCard("##txoptions"))
        {
            bool input = status.AllowInput;
            if (SwitchLine(Strings.T("txInput"), Strings.T("txInputHint"), "##txinput", ref input))
                link.Send(c => c.SetAllowInputAsync(input));
            RowDivider();
            bool autostart = _autostart ??= SenderLink.Autostart;
            if (SwitchLine(Strings.T("txAutostart"), Strings.T("txAutostartHint"), "##txautostart", ref autostart))
            {
                SenderLink.Autostart = autostart;
                _autostart = SenderLink.Autostart;
            }
            RowDivider();
            TextRow(Strings.T("txEngine"), Strings.T("txQuitHint"), Theme.TextTertiary, Strings.T("txQuit"),
                    () => link.Send(c => c.QuitAsync(force: true)), "quit");
        }
        EndCard();

        var codecs = status.Codecs.Where(c => c.Available).Select(c => c.Name).ToList();
        if (codecs.Count > 0)
            Widgets.Text(f.Caption, Theme.TextTertiary, string.Format(Strings.T("txCodecs"), string.Join(", ", codecs)));
        ImGui.Dummy(new Vector2(0, Layout.S(24)));
    }

    private static string? _startError;
    private static bool? _autostart;

    /// <summary>Padding inside a card for content that is not rows.</summary>
    private static void CardBody(Action body)
    {
        // The same margin on the right as on the left: the body is a region of its own, so
        // what is aligned to its right edge stops short of the card's.
        float pad = Layout.S(20);
        ImGui.Dummy(new Vector2(0, pad - Layout.S(4)));
        ImGui.Indent(pad);
        float width = ImGui.GetContentRegionAvail().X - pad;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("##body", new Vector2(width, 0), ImGuiChildFlags.AutoResizeY))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(4)));
            body();
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        ImGui.Unindent(pad);
        ImGui.Dummy(new Vector2(0, pad - Layout.S(4)));
    }

    private static (string, Vector4) StateWord(SenderStatus status) => status.State switch
    {
        SenderStates.Off => (Strings.T("txStOff"), Theme.TextTertiary),
        SenderStates.Discoverable => (Strings.T("txStDiscoverable"), Theme.AccentText),
        SenderStates.Pairing => (Strings.T("txStPairing"), Theme.Caution),
        SenderStates.Streaming => (Strings.T("txStStreaming"), Theme.Success),
        _ => (Strings.T("txStWaiting"), Theme.TextPrimary)
    };

    private static string StateDetail(SenderStatus status)
    {
        switch (status.State)
        {
            case SenderStates.Off: return Strings.T("txDOff");
            case SenderStates.Discoverable:
            case SenderStates.Pairing: return Strings.T("txDDiscoverable");
            case SenderStates.Streaming: break;
            default: return Strings.T(status.Viewers.Count == 0 ? "txDWaitingNone" : "txDWaiting");
        }

        var parts = new List<string>();
        if (status.Viewer is { } viewer) parts.Add(string.Format(Strings.T("txWatching"), viewer.Name));
        if (status.Stream is { } stream)
        {
            parts.Add(stream.Width > 0 ? $"{stream.Codec.ToUpperInvariant()} {stream.Width}×{stream.Height}" : stream.Codec.ToUpperInvariant());
            if (stream.Fps > 0) parts.Add($"{Strings.Num(stream.Fps, "0")} {Strings.T("hz")}");
            parts.Add($"{Strings.Num(stream.Mbps, "0.0")} {Strings.T("mbit")}");
            if (stream.EncodeMs > 0) parts.Add(Strings.Ms(stream.EncodeMs, "0.0"));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Someone is pairing: who, the code to type there in two threes, how long it lasts, and the way to refuse.</summary>
    private static void PairingBlock(SenderLink link, PairingInfo pairing)
    {
        var f = Widgets.F;
        ImGui.Dummy(new Vector2(0, Layout.S(12)));
        var pos = ImGui.GetCursorScreenPos();
        float avail = ImGui.GetContentRegionAvail().X, height = Layout.S(88);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(avail, height), Theme.U32(Theme.Layer), Layout.S(Theme.RadiusControl));
        draw.AddRect(pos, pos + new Vector2(avail, height), Theme.U32(Theme.ControlStroke), Layout.S(Theme.RadiusControl));

        float pad = Layout.S(16);
        string who = string.Format(Strings.T("txPairWants"), pairing.ViewerName);
        long left = pairing.ExpiresMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string hint = pairing.Pin is null ? Strings.T("txExchanging") : string.Format(Strings.T("txPairPin"), Countdown(left));
        var whoSize = Widgets.Measure(f.BodyStrong, who);
        Widgets.DrawText(draw, f.BodyStrong, pos + new Vector2(pad, pad), Theme.TextPrimary, who);
        Widgets.DrawText(draw, f.Caption, pos + new Vector2(pad, pad + whoSize.Y + Layout.S(2)), Theme.TextSecondary, hint);

        string cancel = Strings.T("cancel");
        var button = ButtonSize(cancel);
        float x = pos.X + avail - pad - button.X;
        if (pairing.Pin is { Length: 6 } pin)
        {
            string code = $"{pin[..3]} {pin[3..]}";
            var codeSize = Widgets.Measure(f.Title, code);
            Widgets.DrawText(draw, f.Title, new Vector2(x - Layout.S(24) - codeSize.X, pos.Y + (height - codeSize.Y) / 2f), Theme.TextPrimary, code);
        }
        ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + (height - button.Y) / 2f));
        if (Widgets.Button(cancel, button)) link.Send(c => c.CancelPairingAsync());
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + height));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>A row with a name and a quieter line under it, and one button at the right end.</summary>
    private static void TextRow(string title, string caption, Vector4 captionColour, string button, Action press, string id)
    {
        var f = Widgets.F;
        float row = Layout.S(60), inset = Layout.S(16);
        var pos = ImGui.GetCursorScreenPos();
        float avail = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        var t = Widgets.Measure(f.Body, title);
        var c = Widgets.Measure(f.Caption, caption);
        float top = pos.Y + (row - t.Y - c.Y) / 2f;
        Widgets.DrawText(draw, f.Body, new Vector2(pos.X + inset, top), Theme.TextPrimary, title);
        Widgets.DrawText(draw, f.Caption, new Vector2(pos.X + inset, top + t.Y), captionColour, caption);

        var size = ButtonSize(button);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - inset - size.X, pos.Y + (row - size.Y) / 2f));
        ImGui.PushID(id);
        if (Widgets.Button(button, size)) press();
        ImGui.PopID();
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + row));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>A row with a name, a hint under it, and a switch at the right end.</summary>
    private static bool SwitchLine(string title, string hint, string id, ref bool value)
    {
        var f = Widgets.F;
        float row = Layout.S(60), inset = Layout.S(16);
        var pos = ImGui.GetCursorScreenPos();
        float avail = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        var sw = Widgets.SwitchSize;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + avail - inset * 2 - sw.X - Layout.S(60));
        var t = Widgets.Measure(f.Body, title);
        var h = Widgets.Measure(f.Caption, hint);
        float top = pos.Y + (row - t.Y - h.Y) / 2f;
        Widgets.DrawText(draw, f.Body, new Vector2(pos.X + inset, top), Theme.TextPrimary, title);
        Widgets.DrawText(draw, f.Caption, new Vector2(pos.X + inset, top + t.Y), Theme.TextTertiary, hint);
        ImGui.PopTextWrapPos();

        string word = Strings.Sentence(Strings.T(value ? "navOn" : "navOff"));
        var w = Widgets.Measure(f.Body, word);
        Widgets.DrawText(draw, f.Body, new Vector2(pos.X + avail - inset - sw.X - Layout.S(12) - w.X, pos.Y + (row - w.Y) / 2f),
                         Theme.TextSecondary, word);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - inset - sw.X, pos.Y + (row - sw.Y) / 2f));
        bool changed = Widgets.ToggleSwitch(id, ref value);
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + row));
        ImGui.Dummy(Vector2.Zero);
        return changed;
    }

    /// <summary>Time left as "4:59".</summary>
    private static string Countdown(long ms)
    {
        long seconds = Math.Max(0, (ms + 999) / 1000);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static string LastSeen(long ms)
    {
        if (ms <= 0) return Strings.T("txNever");
        var when = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        string text = when.Date == DateTime.Today ? when.ToString("HH:mm") : when.ToString("dd.MM HH:mm");
        return string.Format(Strings.T("txLastSeen"), text);
    }
}
