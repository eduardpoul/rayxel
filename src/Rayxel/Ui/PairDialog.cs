using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;
using Rayxel.Link.Discovery;

namespace Rayxel.Ui;

/// <summary>
/// Pairing with a Steam Deck, as a Fluent content dialog over everything else: the senders
/// that answered, as rows like the device picker's; then the PIN the chosen one shows, in six
/// cells of one digit each, sent as soon as the sixth is in; then done. What it shows is the
/// state of <see cref="PairFlow"/>, read every frame.
/// </summary>
internal static class PairDialog
{
    private const string Id = "##pair";
    private const int PinLength = 6;

    public static void Draw(AppModel model)
    {
        if (model.Pairing is not { } flow) return;
        if (!ImGui.IsPopupOpen(Id)) ImGui.OpenPopup(Id);

        var f = Widgets.F;
        float width = Layout.S(460), pad = Layout.S(24);
        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width, model.Renderer.Height) * 0.5f, ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(8)));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.Card);

        bool open = true;
        if (ImGui.BeginPopupModal(Id, ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                                               | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushTextWrapPos(width - pad);
            Widgets.Text(f.Subtitle, Theme.TextPrimary, Strings.T("pairTitle"));
            bool close = false;

            switch (flow.Stage)
            {
                case PairFlow.Stages.Scanning:
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, Strings.T("pairHint"));
                    ImGui.Dummy(new Vector2(0, Layout.S(2)));

                    // A sender already linked with this computer is not offered: it is in the
                    // device list and streams from there, and pairing it again would only
                    // replace keys that work. It is named under the list instead, so a Deck
                    // that does not appear is not a mystery.
                    var all = flow.Found;
                    var offered = all.Where(s => !Linked(s)).ToList();
                    var linked = all.Where(Linked).Select(s => s.Name).Distinct().ToList();
                    if (offered.Count == 0)
                        Widgets.Text(f.Caption, Theme.TextTertiary, Strings.T("pairLooking"));
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, Layout.S(2)));
                    foreach (var sender in offered)
                        if (SenderRow(sender, offered)) flow.Choose(sender);
                    ImGui.PopStyleVar();
                    if (linked.Count > 0)
                    {
                        ImGui.Dummy(new Vector2(0, Layout.S(2)));
                        Widgets.Wrapped(f.Caption, Theme.TextTertiary, string.Format(Strings.T("pairAlready"), string.Join(", ", linked)));
                    }
                    close = Footer(Strings.T("cancel"));
                    break;

                case PairFlow.Stages.Connecting:
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, string.Format(Strings.T("pairConnecting"), flow.SenderName));
                    close = Footer(Strings.T("cancel"));
                    break;

                case PairFlow.Stages.EnterPin:
                case PairFlow.Stages.Confirming:
                    bool checking = flow.Stage == PairFlow.Stages.Confirming;
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, string.Format(Strings.T("pairEnterPin"), flow.SenderName));
                    ImGui.Dummy(new Vector2(0, Layout.S(4)));
                    PinCells(flow, checking);
                    ImGui.Dummy(new Vector2(0, Layout.S(2)));
                    if (flow.WrongPin && !checking)
                        Widgets.Wrapped(f.Caption, Theme.Caution, Strings.T("pairWrongPin"));
                    else
                        Widgets.Wrapped(f.Caption, Theme.TextTertiary, checking ? Strings.T("pairChecking") : Strings.T("pairPinHint"));
                    close = Footer(Strings.T("cancel"));
                    break;

                case PairFlow.Stages.Done:
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, string.Format(Strings.T("pairDone"), flow.Result?.Name ?? flow.SenderName));
                    // Once: the device appears in the list and is switched to.
                    if (!flow.Delivered && flow.Result is { } peer)
                    {
                        flow.Delivered = true;
                        model.Paired(peer);
                    }
                    close = Footer(Strings.T("pairClose"), accent: true);
                    break;

                case PairFlow.Stages.Failed:
                    Widgets.Wrapped(f.Body, Theme.Caution, flow.Error ?? Strings.T("pairFailed"));
                    if (Footer(Strings.T("pairRetry"), accent: true, second: Strings.T("pairClose"), out bool closed)) flow.Retry();
                    close = closed;
                    break;
            }

            ImGui.PopTextWrapPos();
            if (close || flow.CloseRequested || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                flow.Dispose();
                model.Pairing = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Linked with this computer on both sides: its key is kept here, and it says it knows this one.</summary>
    private static bool Linked(FoundSender sender) => sender.KnowsYou && NetLinks.Store.Find(sender.Id) is not null;

    /// <summary>
    /// One sender, as a row of the device picker: the name, and under it whether it can be
    /// paired with now - and its model, when the name does not already say it. A row that can
    /// be paired with says so at its right and lights up under the pointer.
    /// <para>
    /// One that has forgotten this computer while its key is still kept here says that, and
    /// offers pairing again: its stream is refused until then. Two with the same name - two
    /// Decks both called "Steam Deck" - are told apart by their addresses.
    /// </para>
    /// </summary>
    private static bool SenderRow(FoundSender sender, IReadOnlyList<FoundSender> offered)
    {
        var f = Widgets.F;
        bool open = sender.Discoverable;
        bool forgot = NetLinks.Store.Find(sender.Id) is not null;
        bool clicked = Widgets.ListRow($"##sender{sender.Id}", Layout.S(60), false, open, out var min, out var max);

        var draw = ImGui.GetWindowDrawList();
        float inset = Layout.S(12), icon = Layout.S(20), height = max.Y - min.Y;
        Icons.Draw(draw, Icon.Camera, new Vector2(min.X + inset, min.Y + (height - icon) / 2f), icon,
                   open ? Theme.TextSecondary : Theme.TextDisabled);

        var (status, colour) = forgot ? (Strings.T("pairLost"), Theme.Caution)
            : open ? (Strings.T("pairOpen"), Theme.Success)
            : (Strings.T("pairClosed"), Theme.Caution);
        if (forgot && !open) status = $"{status} · {Strings.T("pairClosed")}";
        if (sender.Model.Length > 0 && !string.Equals(sender.Model, sender.Name, StringComparison.OrdinalIgnoreCase))
            status = $"{sender.Model} · {status}";

        string title = offered.Count(s => string.Equals(s.Name, sender.Name, StringComparison.CurrentCultureIgnoreCase)) > 1
            ? $"{sender.Name} · {sender.EndPoint.Address}"
            : sender.Name;
        float x = min.X + inset + icon + Layout.S(12);
        var name = Widgets.Measure(f.Body, title);
        var line = Widgets.Measure(f.Caption, status);
        float top = min.Y + (height - name.Y - line.Y - Layout.S(1)) / 2f;
        Widgets.DrawText(draw, f.Body, new Vector2(x, top), open ? Theme.TextPrimary : Theme.TextDisabled, title);
        Widgets.DrawText(draw, f.Caption, new Vector2(x, top + name.Y + Layout.S(1)), colour, status);

        if (open)
        {
            string action = Strings.T(forgot ? "pairAgain" : "pairPair");
            var size = Widgets.Measure(f.Body, action);
            Widgets.DrawText(draw, f.Body, new Vector2(max.X - inset - size.X, min.Y + (height - size.Y) / 2f), Theme.AccentText, action);
        }
        return clicked;
    }

    /// <summary>
    /// The PIN in six cells. Underneath is one ordinary input, invisible, which has the
    /// keyboard - so typing, pasting a whole code and Backspace all behave as they do anywhere,
    /// and the app's own keys stay quiet while it is in use. The cells only draw what it holds.
    /// The sixth digit sends it.
    /// </summary>
    private static unsafe void PinCells(PairFlow flow, bool checking)
    {
        var f = Widgets.F;
        float cell = Layout.S(52), gap = Layout.S(10);
        float total = cell * PinLength + gap * (PinLength - 1);
        float left = ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - total) / 2f);

        ImGui.SetCursorPosX(left);
        var origin = ImGui.GetCursorScreenPos();

        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, (cell - ImGui.GetFontSize()) / 2f));
        ImGui.SetNextItemWidth(total);
        if (!checking && !ImGui.IsAnyItemActive()) ImGui.SetKeyboardFocusHere();
        string before = flow.Pin;
        ImGui.InputText($"##pin{flow.Attempt}", ref flow.Pin, PinLength,
                        ImGuiInputTextFlags.CallbackCharFilter | (checking ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None),
                        DigitsOnly);
        bool active = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(6);

        var draw = ImGui.GetWindowDrawList();
        bool caretOn = (ImGui.GetTime() % 1.0) < 0.6;
        for (int i = 0; i < PinLength; i++)
        {
            var min = origin + new Vector2(i * (cell + gap), 0);
            var max = min + new Vector2(cell, cell);
            bool current = active && !checking && i == Math.Min(flow.Pin.Length, PinLength - 1) && flow.Pin.Length < PinLength;
            var border = flow.WrongPin && flow.Pin.Length == 0 && !checking ? Theme.Caution
                : current ? Theme.Accent
                : Theme.Divider;
            draw.AddRectFilled(min, max, Theme.U32(Theme.Layer), Layout.S(Theme.RadiusControl));
            draw.AddRect(min, max, Theme.U32(border), Layout.S(Theme.RadiusControl), ImDrawFlags.None, current ? Layout.S(2) : Layout.S(1));

            if (i < flow.Pin.Length)
            {
                string digit = flow.Pin[i].ToString();
                var size = Widgets.Measure(f.Subtitle, digit);
                Widgets.DrawText(draw, f.Subtitle, min + (new Vector2(cell, cell) - size) / 2f,
                                 checking ? Theme.TextSecondary : Theme.TextPrimary, digit);
            }
            else if (current && caretOn)
            {
                float h = Layout.S(22), x = min.X + cell / 2f;
                draw.AddLine(new Vector2(x, min.Y + (cell - h) / 2f), new Vector2(x, min.Y + (cell + h) / 2f), Theme.U32(Theme.TextPrimary), Layout.S(1.5f));
            }
        }

        // The sixth digit sends it: there is nothing else to press.
        if (!checking && flow.Pin.Length == PinLength && before.Length < PinLength) flow.Submit();
    }

    /// <summary>
    /// Asks before a paired sender is forgotten: its key goes, and watching it again takes a
    /// new pairing. Forgetting is this computer's half; the Deck keeps this one in its own
    /// list until it is removed there.
    /// </summary>
    public static void DrawForget(AppModel model)
    {
        const string forgetId = "##forget";
        if (model.ConfirmForget is not { } peer) return;
        if (!ImGui.IsPopupOpen(forgetId)) ImGui.OpenPopup(forgetId);

        var f = Widgets.F;
        float width = Layout.S(420), pad = Layout.S(24);
        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width, model.Renderer.Height) * 0.5f, ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(8)));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.Card);

        bool open = true;
        if (ImGui.BeginPopupModal(forgetId, ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                                                     | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushTextWrapPos(width - pad);
            Widgets.Text(f.Subtitle, Theme.TextPrimary, string.Format(Strings.T("forgetTitle"), peer.Name));
            Widgets.Wrapped(f.Body, Theme.TextSecondary, Strings.T("forgetBody"));
            ImGui.PopTextWrapPos();

            bool forget = Footer(Strings.T("forgetConfirm"), accent: true, second: Strings.T("cancel"), out bool cancelled);
            if (forget) model.Forget(peer);
            if (forget || cancelled || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                model.ConfirmForget = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Lets digits through and nothing else, whether typed or pasted.</summary>
    private static unsafe int DigitsOnly(ImGuiInputTextCallbackData* data) =>
        data->EventChar is >= '0' and <= '9' ? 0 : 1;

    /// <summary>The dialog's buttons, on a line of their own at its right: one, or an accented one and a plain one.</summary>
    private static bool Footer(string label, bool accent = false) => Footer(label, accent, null, out _);

    private static bool Footer(string label, bool accent, string? second, out bool secondPressed)
    {
        var f = Widgets.F;
        float gap = Layout.S(8);
        var size = new Vector2(Math.Max(Layout.S(110), Widgets.Measure(f.Body, label).X + Layout.S(32)), Layout.S(32));
        var size2 = second is null ? Vector2.Zero
            : new Vector2(Math.Max(Layout.S(110), Widgets.Measure(f.Body, second).X + Layout.S(32)), Layout.S(32));

        // A line of its own: the right-alignment joins whatever came last on the line, and
        // without this that was the hint above, which the buttons then sat on top of.
        ImGui.Dummy(new Vector2(0, Layout.S(8)));
        Widgets.RightAlign(size.X + (second is null ? 0 : gap + size2.X));
        bool pressed = Widgets.Button(label, size, accent);
        secondPressed = false;
        if (second is not null)
        {
            ImGui.SameLine(0, gap);
            secondPressed = Widgets.Button(second, size2);
        }
        return pressed;
    }
}
