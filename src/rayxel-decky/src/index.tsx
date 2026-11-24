import {
  ButtonItem,
  ConfirmModal,
  DialogButton,
  Field,
  PanelSection,
  PanelSectionRow,
  ToggleField,
  showModal,
  staticClasses,
} from "@decky/ui";
import { definePlugin, toaster, useQuickAccessVisible, type ToastNotification } from "@decky/api";
import { useEffect, useState, type CSSProperties } from "react";
import { FaBroadcastTower } from "react-icons/fa";

import {
  cancelPairing,
  forgetViewer,
  getAutostart,
  listen,
  publish,
  setAllowInput,
  setAutostart,
  setDiscoverable,
  setEnabled,
  useOverview,
  watchOverview,
  type Answer,
  type PairedViewer,
  type SenderStatus,
} from "./backend";
import { codecName, countdown, f, lastSeen, num, pinText, t } from "./strings";

// Whether the panel is on screen. A PIN that arrives while it is not is shown in a toast
// instead, so it can be read over a running game.
let panelVisible = false;

const stateColours: Record<string, string> = {
  off: "#9a9a9a",
  waiting: "#f2f2f2",
  discoverable: "#60cdff",
  pairing: "#ffc857",
  streaming: "#6ccb5f",
};

const dim: CSSProperties = { opacity: 0.7, fontSize: "12px", lineHeight: "16px" };
const warn: CSSProperties = { color: "#ffb86c", fontSize: "12px", lineHeight: "16px" };
// As wide as its word and no wider, so it fits beside a computer's name.
const forgetButton: CSSProperties = { minWidth: 0, width: "auto", padding: "0 16px", height: "32px", lineHeight: "32px" };

function useNow(intervalMs: number): number {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
  return now;
}

function stateWord(status: SenderStatus | null): string {
  if (!status) return t("notRunning");
  switch (status.state) {
    case "off":
      return t("stOff");
    case "discoverable":
      return t("stDiscoverable");
    case "pairing":
      return t("stPairing");
    case "streaming":
      return t("stStreaming");
    default:
      return t("stWaiting");
  }
}

/**
 * The line under the state: a sentence, or while streaming its figures as parts, each kept
 * whole - "4 fps" never breaks between its number and its unit.
 */
function StateDetail({ status }: { status: SenderStatus | null }) {
  const parts = stateDetail(status);
  return (
    <>
      {parts.map((part, i) => (
        <span key={i} style={{ whiteSpace: parts.length > 1 ? "nowrap" : "normal" }}>
          {i > 0 ? " · " : ""}
          {part}
        </span>
      ))}
    </>
  );
}

function stateDetail(status: SenderStatus | null): string[] {
  if (!status) return [t("notRunningDetail")];
  switch (status.state) {
    case "off":
      return [t("dOff")];
    case "discoverable":
      return [t("dDiscoverable")];
    case "pairing":
      return [f("dPairing", status.pairing?.viewerName ?? "")];
    case "streaming":
      break;
    default:
      return [status.viewers.length === 0 ? t("dWaitingNone") : t("dWaiting")];
  }

  const parts: string[] = [];
  if (status.viewer) parts.push(f("watching", status.viewer.name));
  const stream = status.stream;
  if (stream) {
    let codec = codecName(stream.codec);
    if (stream.askedCodec && stream.askedCodec.toLowerCase() !== stream.codec.toLowerCase())
      codec = f("instead", codec, codecName(stream.askedCodec));
    parts.push(stream.width > 0 ? `${codec} ${stream.width}×${stream.height}` : codec);
    if (stream.fps > 0) parts.push(`${num(stream.fps, 0)} ${t("fps")}`);
    parts.push(`${num(stream.mbps, 1)} ${t("mbit")}`);
    if (stream.encodeMs > 0) parts.push(f("encodeMs", num(stream.encodeMs, 1)));
    if (stream.inputActive) parts.push(t("inputOn"));
  }
  return parts;
}

function describe(answer: Answer): string {
  switch (answer.code) {
    case "no_answer":
      return t("errNoAnswer");
    case "no_binary":
      return t("errNoBinary");
    case "start_failed":
      return f("errStart", answer.error ?? "?");
    default:
      return f("errRefused", answer.error ?? "?");
  }
}

function Content() {
  const { running, status } = useOverview();
  const now = useNow(1000);
  const visible = useQuickAccessVisible();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [wantEnabled, setWantEnabled] = useState<boolean | null>(null);
  const [autostart, setAutostartShown] = useState<boolean | null>(null);

  useEffect(() => {
    panelVisible = visible;
  }, [visible]);
  useEffect(
    () => () => {
      panelVisible = false;
    },
    [],
  );
  useEffect(() => {
    getAutostart().then(setAutostartShown, () => setAutostartShown(null));
  }, []);

  // One request at a time; its failure is said under the switch, and a good answer's state
  // is drawn at once rather than on the next push.
  async function run(request: () => Promise<Answer>): Promise<Answer | null> {
    setBusy(true);
    setError(null);
    try {
      const answer = await request();
      if (!answer.ok && answer.code !== "needs_force") setError(describe(answer));
      if (answer.status) publish({ running: true, status: answer.status });
      return answer;
    } catch (e) {
      setError(String(e));
      return null;
    } finally {
      setBusy(false);
    }
  }

  // Off is the sender's own switch: it stops streaming and answering, and the program, and
  // with it gamescope's capture, stays as it is.
  async function switchTo(on: boolean) {
    setWantEnabled(on);
    await run(() => setEnabled(on));
    setWantEnabled(null);
  }

  function confirmForget(viewer: PairedViewer) {
    showModal(
      <ConfirmModal
        strTitle={f("forgetTitle", viewer.name || viewer.id)}
        strDescription={t("forgetBody")}
        strOKButtonText={t("forget")}
        strCancelButtonText={t("cancel")}
        bDestructiveWarning={true}
        onOK={() => void run(() => forgetViewer(viewer.id))}
      />,
    );
  }

  // There is no button that ends the program: switching off stops streaming and answering,
  // and ending it while it holds the game's picture can take gamescope and the game down.
  // Whether it starts with the Deck is the one other thing to decide, below.

  async function changeAutostart(value: boolean) {
    setAutostartShown(value);
    const answer = await run(() => setAutostart(value));
    setAutostartShown(answer?.autostart ?? !value);
  }

  const enabled = wantEnabled ?? (running && !!status?.enabled);
  const discoverableLeft = status?.discoverableUntilMs ? status.discoverableUntilMs - now : 0;
  const pairing = status?.pairing;
  const watchingId = status?.state === "streaming" ? status.viewer?.id : undefined;

  return (
    <>
      <PanelSection>
        <PanelSectionRow>
          <div style={{ padding: "4px 0" }}>
            <div style={{ fontSize: "22px", fontWeight: 700, color: stateColours[status?.state ?? "off"] }}>
              {stateWord(status)}
            </div>
            <div style={{ ...dim, marginTop: "2px" }}>
              <StateDetail status={status} />
            </div>
            {status?.problem && <div style={{ ...warn, marginTop: "4px" }}>{status.problem}</div>}
          </div>
        </PanelSectionRow>
        <PanelSectionRow>
          <ToggleField label={t("senderOn")} checked={enabled} disabled={busy} onChange={(on) => void switchTo(on)} />
        </PanelSectionRow>
        <PanelSectionRow>
          <ButtonItem
            layout="below"
            disabled={busy || !running || !status?.enabled}
            onClick={() => void run(() => setDiscoverable(!(discoverableLeft > 0)))}
          >
            {discoverableLeft > 0 ? f("discoverableFor", countdown(discoverableLeft)) : t("makeDiscoverable")}
          </ButtonItem>
        </PanelSectionRow>
        {error && (
          <PanelSectionRow>
            <div style={warn}>{error}</div>
          </PanelSectionRow>
        )}
      </PanelSection>

      {pairing && (
        // A short title - a section title is set in capitals and a computer's name can be
        // long - and who is asking on a line of its own under it.
        <PanelSection title={t("pairTitle")}>
          <PanelSectionRow>
            {pairing.pin ? (
              <div style={{ textAlign: "center", padding: "4px 0 8px" }}>
                <div style={{ fontSize: "16px", marginBottom: "2px" }}>{f("pairWants", pairing.viewerName)}</div>
                <div style={dim}>{t("pairTypePin")}</div>
                <div
                  style={{
                    fontSize: "46px",
                    fontWeight: 700,
                    letterSpacing: "0.08em",
                    fontVariantNumeric: "tabular-nums",
                    lineHeight: "56px",
                    color: "#ffffff",
                  }}
                >
                  {pinText(pairing.pin)}
                </div>
                <div style={dim}>{f("pairExpires", countdown(pairing.expiresMs - now))}</div>
              </div>
            ) : (
              <div style={{ textAlign: "center", padding: "12px 0" }}>
                <div style={{ fontSize: "16px", marginBottom: "2px" }}>{f("pairWants", pairing.viewerName)}</div>
                <div style={dim}>{t("pairExchanging")}</div>
                <div style={dim}>{f("pairExpires", countdown(pairing.expiresMs - now))}</div>
              </div>
            )}
          </PanelSectionRow>
          <PanelSectionRow>
            <ButtonItem layout="below" disabled={busy} onClick={() => void run(() => cancelPairing())}>
              {t("cancel")}
            </ButtonItem>
          </PanelSectionRow>
        </PanelSection>
      )}

      {running && status && (
        <PanelSection title={t("pairedTitle")}>
          {status.viewers.length === 0 ? (
            <PanelSectionRow>
              <div style={dim}>{t("pairedNone")}</div>
            </PanelSectionRow>
          ) : (
            // The name and when it was last here on the left, a small button on the right that
            // stays inside the panel: Decky's inline button item is as wide as a full-width
            // one and runs past the panel's edge.
            status.viewers.map((viewer) => (
              <PanelSectionRow key={viewer.id}>
                <Field
                  label={viewer.name || viewer.id}
                  description={viewer.id === watchingId ? t("watchingNow") : lastSeen(viewer.lastSeenMs, now)}
                  childrenContainerWidth="min"
                  bottomSeparator="none"
                >
                  <DialogButton
                    style={forgetButton}
                    disabled={busy}
                    onClick={() => confirmForget(viewer)}
                  >
                    {t("forget")}
                  </DialogButton>
                </Field>
              </PanelSectionRow>
            ))
          )}
        </PanelSection>
      )}

      <PanelSection title={t("optionsTitle")}>
        <PanelSectionRow>
          <ToggleField
            label={t("allowInput")}
            description={t("allowInputHint")}
            checked={!!status?.allowInput}
            disabled={busy || !running}
            onChange={(value) => void run(() => setAllowInput(value))}
          />
        </PanelSectionRow>
        <PanelSectionRow>
          <ToggleField
            label={t("autostart")}
            description={t("autostartHint")}
            checked={!!autostart}
            disabled={busy || autostart === null}
            onChange={(value) => void changeAutostart(value)}
          />
        </PanelSectionRow>
      </PanelSection>

    </>
  );
}

export default definePlugin(() => {
  let pairingToast: ToastNotification | null = null;

  const stopListening = listen((event) => {
    if (panelVisible) return;
    const left = Math.max(0, event.expiresMs - Date.now());
    pairingToast?.dismiss();
    pairingToast = toaster.toast({
      title: f("pairWants", event.viewerName),
      body: f("toastBody", pinText(event.pin)),
      // Long enough to read six digits and type them on the PC, but never past the PIN's life.
      duration: Math.min(Math.max(left, 8000), 30000),
      expiration: left > 0 ? left : undefined,
      critical: true,
    });
  });

  // Once the pairing is over, its PIN is of no use; take the toast away with it.
  const stopWatching = watchOverview((overview) => {
    if (!overview.status?.pairing?.pin && pairingToast) {
      pairingToast.dismiss();
      pairingToast = null;
    }
  });

  return {
    name: "Rayxel",
    titleView: <div className={staticClasses.Title}>Rayxel</div>,
    content: <Content />,
    icon: <FaBroadcastTower />,
    onDismount() {
      stopListening();
      stopWatching();
      pairingToast?.dismiss();
    },
  };
});
