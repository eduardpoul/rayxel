import { addEventListener, callable, removeEventListener } from "@decky/api";
import { useEffect, useState } from "react";

// The sender's state, as its control socket sends it (Rayxel.Link/Control/ControlModel.cs).
// Fields the sender leaves out when empty are optional here.

export interface PairingInfo {
  viewerName: string;
  pin?: string;
  expiresMs: number;
}

export interface ViewerInfo {
  id: string;
  name: string;
  address: string;
  paired: boolean;
}

export interface StreamInfo {
  codec: string;
  askedCodec?: string;
  width: number;
  height: number;
  fps: number;
  mbps: number;
  bitrateKbps: number;
  encodeMs: number;
  encodeMaxMs: number;
  encoder: string;
  inputActive: boolean;
}

export interface PairedViewer {
  id: string;
  name: string;
  pairedMs: number;
  lastSeenMs: number;
}

export interface CodecInfo {
  name: string;
  available: boolean;
  why?: string;
}

export type SenderState = "off" | "waiting" | "discoverable" | "pairing" | "streaming";

export interface SenderStatus {
  version: string;
  id: string;
  name: string;
  platform: string;
  model: string;
  port: number;
  enabled: boolean;
  state: SenderState;
  discoverableUntilMs?: number;
  pairing?: PairingInfo;
  viewer?: ViewerInfo;
  stream?: StreamInfo;
  viewers: PairedViewer[];
  codecs: CodecInfo[];
  allowInput: boolean;
  captureHeld: boolean;
  problem?: string;
}

/** What the backend says about the sender: whether anything answers, and if so its state. */
export interface Overview {
  running: boolean;
  status: SenderStatus | null;
}

/** The sender's answer, or the backend's when it could not get one. */
export interface Answer {
  ok: boolean;
  error?: string;
  /** The backend's own reasons: no_answer, no_binary, start_failed, needs_force. */
  code?: string;
  captureHeld?: boolean | null;
  autostart?: boolean;
  status?: SenderStatus;
}

export interface PairingEvent {
  viewerName: string;
  pin: string;
  expiresMs: number;
}

export const getStatus = callable<[], Overview>("get_status");
export const setEnabled = callable<[value: boolean], Answer>("set_enabled");
export const setDiscoverable = callable<[value: boolean], Answer>("set_discoverable");
export const cancelPairing = callable<[], Answer>("cancel_pairing");
export const forgetViewer = callable<[id: string], Answer>("forget");
export const setAllowInput = callable<[value: boolean], Answer>("set_allow_input");
export const getAutostart = callable<[], boolean>("get_autostart");
export const setAutostart = callable<[value: boolean], Answer>("set_autostart");
export const stopService = callable<[force: boolean], Answer>("stop_service");

// The latest state, kept outside any component: the backend's events arrive whether the
// panel is open or not, and the panel starts from whatever came last.

let latest: Overview = { running: false, status: null };
const watchers = new Set<(overview: Overview) => void>();

export function currentOverview(): Overview {
  return latest;
}

export function publish(overview: Overview): void {
  latest = overview;
  for (const watcher of watchers) watcher(overview);
}

/** Calls back on every state from now on; returns the function that stops it. */
export function watchOverview(watcher: (overview: Overview) => void): () => void {
  watchers.add(watcher);
  return () => {
    watchers.delete(watcher);
  };
}

export function useOverview(): Overview {
  const [overview, setOverview] = useState(latest);
  useEffect(() => {
    setOverview(latest);
    return watchOverview(setOverview);
  }, []);
  return overview;
}

/** Listens to the backend's two events; returns the function that stops listening. */
export function listen(onPairing: (event: PairingEvent) => void): () => void {
  const statusListener = addEventListener<[Overview]>("rayxel_status", (overview) => publish(overview));
  const pairingListener = addEventListener<[PairingEvent]>("rayxel_pairing", onPairing);
  // The backend sends only changes; ask once for where things stand now.
  getStatus().then(publish, () => undefined);
  return () => {
    removeEventListener("rayxel_status", statusListener);
    removeEventListener("rayxel_pairing", pairingListener);
  };
}
