using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// Pairing with a Steam Deck, as a Fluent content dialog over everything else: the senders
/// that are open to pairing, then the PIN the chosen one shows, then done. What it shows is
/// the state of <see cref="PairFlow"/>, read every frame.
/// </summary>
internal static class PairDialog
{
    private const string Id = "##pair";

    public static void Draw(AppModel model)
    {
        if (model.Pairing is not { } flow) return;
        if (!ImGui.IsPopupOpen(Id)) ImGui.OpenPopup(Id);

        var f = Widgets.F;
        float width = Layout.S(440), pad = Layout.S(24);
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
            var button = new Vector2(Math.Max(Layout.S(110), Widgets.Measure(f.Body, Strings.T("cancel")).X + Layout.S(32)), Layout.S(32));
            bool close = false;

            switch (flow.Stage)
            {
                case PairFlow.Stages.Scanning:
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, Strings.T("pairHint"));
                    ImGui.Dummy(new Vector2(0, Layout.S(4)));
                    var found = flow.Found;
                    if (found.Count == 0)
                        Widgets.Text(f.Caption, Theme.TextTertiary, Strings.T("pairLooking"));
                    foreach (var sender in found)
                    {
                        string tag = sender.KnowsYou ? Strings.T("pairKnown")
                            : sender.Discoverable ? Strings.T("pairOpen")
                            : Strings.T("pairClosed");
                        string label = $"{sender.Name}   ·   {sender.Model}   ·   {tag}##{sender.Id}";
                        if (Widgets.Button(label, new Vector2(width - pad * 2, Layout.S(40)), accent: sender.Discoverable,
                                           enabled: sender.Discoverable))
                            flow.Choose(sender);
                    }
                    ImGui.Dummy(new Vector2(0, Layout.S(4)));
                    Widgets.RightAlign(button.X);
                    close = Widgets.Button(Strings.T("cancel"), button);
                    break;

                case PairFlow.Stages.Connecting:
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, string.Format(Strings.T("pairConnecting"), flow.SenderName));
                    Widgets.RightAlign(button.X);
                    close = Widgets.Button(Strings.T("cancel"), button);
                    break;

                case PairFlow.Stages.EnterPin:
                case PairFlow.Stages.Confirming:
                    bool checking = flow.Stage == PairFlow.Stages.Confirming;
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, string.Format(Strings.T("pairEnterPin"), flow.SenderName));
                    ImGui.SetNextItemWidth(width - pad * 2);
                    ImGui.PushFont(f.Subtitle);
                    if (ImGui.IsWindowAppearing() || !ImGui.IsAnyItemActive()) ImGui.SetKeyboardFocusHere();
                    bool entered = ImGui.InputText("##pin", ref flow.Pin, 6,
                        ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue |
                        (checking ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None));
                    ImGui.PopFont();
                    if (flow.WrongPin)
                        Widgets.Wrapped(f.Caption, Theme.Caution, Strings.T("pairWrongPin"));
                    else
                        Widgets.Wrapped(f.Caption, Theme.TextTertiary, checking ? Strings.T("pairChecking") : Strings.T("pairPinHint"));

                    float gap = Layout.S(8);
                    Widgets.RightAlign(button.X * 2 + gap);
                    bool ready = flow.Pin.Length == 6 && !checking;
                    if ((Widgets.Button(Strings.T("pairPair"), button, accent: true, enabled: ready) || entered) && ready)
                        flow.Submit();
                    ImGui.SameLine(0, gap);
                    close = Widgets.Button(Strings.T("cancel"), button);
                    break;

                case PairFlow.Stages.Done:
                    Widgets.Wrapped(f.Body, Theme.TextSecondary, string.Format(Strings.T("pairDone"), flow.Result?.Name ?? flow.SenderName));
                    // Once: the device appears in the list and is switched to.
                    if (!flow.Delivered && flow.Result is { } peer)
                    {
                        flow.Delivered = true;
                        model.Paired(peer);
                    }
                    Widgets.RightAlign(button.X);
                    close = Widgets.Button(Strings.T("pairClose"), button, accent: true);
                    break;

                case PairFlow.Stages.Failed:
                    Widgets.Wrapped(f.Body, Theme.Caution, flow.Error ?? Strings.T("pairFailed"));
                    float space = Layout.S(8);
                    Widgets.RightAlign(button.X * 2 + space);
                    if (Widgets.Button(Strings.T("pairRetry"), button, accent: true)) flow.Retry();
                    ImGui.SameLine(0, space);
                    close = Widgets.Button(Strings.T("pairClose"), button);
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
}
