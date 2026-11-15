"use strict";

// Spatial Audio test client. Two ways in:
//  - join link: /client/?participant=<id>&token=<joinToken>[&room=<name>] (what a real integration hands a user);
//  - test mode: no link; uses a control-API key (kept in localStorage) to pick or create a room and join it.
// Signalling (see PLAN.md): GET /signal/{id}/config for ICE servers, POST /signal/{id}/offer with the join token
// (then the session token it returns), trickle ICE both ways via /signal/{id}/ice, DELETE /signal/{id} to leave.

const $ = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const KEY_STORE = "sas.apiKey";
const ROOM_STORE = "sas.room";
const NAME_STORE = "sas.name";
const MAX_RECONNECTS = 6;

class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

function log(message) {
  const line = `${new Date().toISOString().slice(11, 23)} ${message}\n`;
  const el = $("log");
  el.textContent += line;
  el.scrollTop = el.scrollHeight;
}

function store(key, value) {
  try {
    if (value === undefined) return localStorage.getItem(key) ?? "";
    localStorage.setItem(key, value);
  } catch { /* storage blocked: the page still works, it just forgets */ }
  return value ?? "";
}

async function request(method, path, { body, apiKey, bearer, keepalive } = {}) {
  const headers = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (apiKey) headers["X-Api-Key"] = apiKey;
  if (bearer) headers["Authorization"] = `Bearer ${bearer}`;
  const response = await fetch(path, { method, headers, keepalive, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) {
    const text = await response.text();
    throw new HttpError(response.status, `${method} ${path}: ${response.status} ${text}`);
  }
  return response.status === 204 ? null : response.json();
}

// ---------------------------------------------------------------- test mode (control API)

const apiKey = () => $("apiKey").value.trim();

async function loadRooms() {
  if (!apiKey()) {
    $("room").innerHTML = `<option value="">Enter the API key first</option>`;
    return;
  }
  try {
    const [status, rooms] = await Promise.all([
      request("GET", "/api/status", { apiKey: apiKey() }),
      request("GET", "/api/rooms", { apiKey: apiKey() }),
    ]);
    store(KEY_STORE, apiKey());
    serverDefaults.plain = status.defaultEngine;
    serverDefaults.spatial = status.defaultSpatialEngine ?? status.defaultEngine;
    $("newRoomEngine").innerHTML = status.engines
      .map(e => `<option value="${e.name}" title="${escapeHtml(e.description)}"${e.name === status.defaultEngine ? " selected" : ""}>${e.name}</option>`)
      .join("");
    const remembered = store(ROOM_STORE);
    $("room").innerHTML = rooms.length === 0
      ? `<option value="">No rooms yet: create one</option>`
      : rooms.map(r => `<option value="${r.id}"${r.id === remembered ? " selected" : ""}>` +
          `${escapeHtml(r.name)} (${r.participantCount} in, ${r.engine}${r.spatial ? ", spatial" : ""})</option>`).join("");
    knownRooms = new Map(rooms.map(r => [r.id, r]));
    await loadPlaces();
    log(`${rooms.length} room(s); server ${status.version}, ${status.gstreamer.version ?? "GStreamer off"}`);
  } catch (e) {
    log(`error: ${e.message}`);
    $("room").innerHTML = `<option value="">${e.status === 401 ? "API key rejected" : "Could not load rooms"}</option>`;
  }
}

async function createRoom() {
  const name = $("newRoomName").value.trim();
  if (!name) { $("newRoomName").focus(); return; }
  try {
    const room = await request("POST", "/api/rooms", {
      apiKey: apiKey(), body: { name, engine: $("newRoomEngine").value, spatial: $("newRoomSpatial").checked },
    });
    store(ROOM_STORE, room.id);
    $("newRoomName").value = "";
    log(`created room ${room.name} (${room.engine}${room.spatial ? ", spatial" : ""})`);
    await loadRooms();
  } catch (e) { log(`error: ${e.message}`); }
}

// Spatial rooms: where to stand. The centre, or next to someone already there (e.g. an OpenSim avatar, which uses
// global coordinates, so the centre is far from everyone): 2 m west of them, facing east towards them.
const STAND_OFF = 2;
let places = new Map();

async function loadPlaces() {
  const roomId = $("room").value;
  const spatial = knownRooms.get(roomId)?.spatial === true;
  $("placeField").hidden = !spatial;
  // The region module shows web users in-world (an NPC) when the room says so: a choice when optional.
  $("npcField").hidden = knownRooms.get(roomId)?.inWorldAvatars !== "optional";
  places = new Map();
  let options = `<option value="">at the centre (0, 0, 0)</option>`;
  if (spatial) {
    try {
      const list = await request("GET", `/api/rooms/${roomId}/participants`, { apiKey: apiKey() });
      for (const p of list.filter(p => p.position)) {
        places.set(p.id, p.position);
        const { x, y, z } = p.position;
        options += `<option value="${p.id}">next to ${escapeHtml(p.displayName)} ` +
          `(${x.toFixed(1)}, ${y.toFixed(1)}, ${z.toFixed(1)})</option>`;
      }
    } catch (e) { log(`error: ${e.message}`); }
  }
  const previous = $("place").value;
  $("place").innerHTML = options;
  if (places.has(previous)) $("place").value = previous;
}

function chosenPosition() {
  const p = places.get($("place").value);
  return p ? { x: p.x - STAND_OFF, y: p.y, z: p.z } : { x: 0, y: 0, z: 0 };
}

function positionText({ x, y, z }) {
  return `(${x.toFixed(1)}, ${y.toFixed(1)}, ${z.toFixed(1)})`;
}

// Rooms from the last listing, by id (test mode), and the server's default engines.
let knownRooms = new Map();
const serverDefaults = { plain: null, spatial: null };

// Spatial rooms: you stand where Stand says (the centre by default). SL axes: x east, y north, z up; facing is the turn
// left from east, as a rotation about z.
function facingQuaternion(degrees) {
  const half = degrees * Math.PI / 360;
  return { x: 0, y: 0, z: Math.sin(half), w: Math.cos(half) };
}

function facingName(degrees) {
  const names = { 0: "east", 45: "north-east", 90: "north", 135: "north-west", 180: "west", "-180": "west",
    "-45": "south-east", "-90": "south", "-135": "south-west" };
  return names[degrees] ?? `${degrees}°`;
}

async function addParticipant(displayName, placed = false, clientNamed = false) {
  const roomId = $("room").value;
  if (!roomId) throw new Error("Pick or create a room first.");
  store(ROOM_STORE, roomId);
  const spatial = knownRooms.get(roomId)?.spatial === true;
  const body = spatial && placed
    ? { displayName, position: chosenPosition(), orientation: facingQuaternion(0) } // see loadPlaces; facing east
    : { displayName };
  if (clientNamed) body.clientNamed = true;
  const join = await request("POST", `/api/rooms/${roomId}/participants`, { apiKey: apiKey(), body });
  return { ...join, roomId, spatial, position: body.position, roomName: $("room").selectedOptions[0]?.textContent.replace(/ \(.*\)$/, "") ?? "" };
}

// Turning: the participant's own listener orientation, sent with its session token (so invited guests can turn too)
// where the room allows it (clientPose "orientation"). Debounced; the server accepts at most 20 updates a second.
let facingTimer = null;
function onFacing() {
  const degrees = Number($("facing").value);
  $("facingLabel").textContent = facingName(degrees);
  clearTimeout(facingTimer);
  facingTimer = setTimeout(async () => {
    if (!call || call.closed) return;
    if (!call.sessionToken) { log("not connected yet: turn again once connected"); return; }
    try {
      await request("PATCH", `/signal/${call.participantId}/pose`,
        { bearer: call.sessionToken, body: { listenerOrientation: facingQuaternion(degrees) } });
      log(`now facing ${facingName(degrees)}`);
    } catch (e) { log(`error turning: ${e.message}`); }
  }, 150);
}

async function joinFromSetup() {
  const name = $("displayName").value.trim() || "Tester";
  store(NAME_STORE, name);
  try {
    const join = await addParticipant(name, true);
    $("setup").hidden = true;
    if (join.spatial) log(`spatial room: you are at ${positionText(join.position)}`);
    const npc = !$("npcField").hidden && $("npc").checked;
    await startCall(join.participantId, join.joinToken, join.roomName, name, join.roomId, undefined, npc);
  } catch (e) { log(`error: ${e.message}`); }
}

async function invite() {
  try {
    const join = await addParticipant("Guest", true, true); // spatial rooms: placed where Stand says; chooses its own name
    const url = `${location.origin}/client/?participant=${join.participantId}&token=${encodeURIComponent(join.joinToken)}` +
      `&room=${encodeURIComponent(join.roomName)}`;
    $("inviteLink").hidden = false;
    $("inviteLink").textContent = url;
    log(`invite link created (valid until ${new Date(join.expiresAt).toLocaleTimeString()}, one use)`);
    await navigator.clipboard?.writeText(url).then(() => log("invite link copied"), () => {});
  } catch (e) { log(`error: ${e.message}`); }
}

// ---------------------------------------------------------------- the call

let call = null;

async function startCall(participantId, joinToken, roomName, displayName, roomId, chosenName, inWorldAvatar = false) {
  $("call").hidden = false;
  $("start").hidden = true;
  $("roomTitle").textContent = roomName ? `Room: ${roomName}` : "In the room";
  $("whoami").textContent = `${displayName ?? "Participant"} · ${participantId}` + (roomId ? ` · room ${roomId}` : "");
  if (roomId) log(`room ID ${roomId} (the load tool's --room)`);
  call = {
    participantId, roomId, joinToken, chosenName: chosenName ?? null, inWorldAvatar, sessionToken: null, pc: null, stream: null, iceServers: [],
    epoch: 0, reconnects: 0, closed: false, audio: null, statsTimer: null, lastStats: null,
  };
  try {
    const config = await request("GET", `/signal/${participantId}/config`, { bearer: joinToken });
    call.iceServers = config.iceServers;
    if (config.inWorldAvatars === "always") log("this region shows web users in-world: you will appear as an NPC");
    else if (inWorldAvatar) log("you asked to appear in-world as an NPC");
    // Show Facing when this participant may turn itself (spatial rooms by default). Starts facing east.
    const mayTurn = config.clientPose === "orientation" || config.clientPose === "tethered";
    $("facingControl").hidden = !mayTurn;
    if (mayTurn) {
      $("facing").value = 0;
      $("facingLabel").textContent = facingName(0);
      log("you can turn with Facing (you face east to start)");
    }
    call.stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: $("aec").checked, noiseSuppression: $("ns").checked, autoGainControl: $("agc").checked },
    });
    call.audio = new AudioContext();
    meter(call.audio.createMediaStreamSource(call.stream), $("micLevel"));
    call.statsTimer = setInterval(showStats, 1000);
    await connect();
  } catch (e) {
    log(`error: ${e.message}`);
    await endCall(e instanceof HttpError && e.status === 401 ? "the join link has expired or was already used" : "could not join");
  }
}

async function connect() {
  const epoch = ++call.epoch;
  const { participantId } = call;
  setState(call.reconnects ? "reconnecting" : "connecting");
  const pc = new RTCPeerConnection({ iceServers: call.iceServers, bundlePolicy: "max-bundle" });
  call.pc = pc;
  call.stream.getAudioTracks().forEach(track => pc.addTrack(track, call.stream));

  pc.ontrack = e => {
    const stream = e.streams[0] ?? new MediaStream([e.track]);
    $("remote").srcObject = stream;
    $("remote").play().catch(() => {});
    meter(call.audio.createMediaStreamSource(stream), $("mixLevel"));
    log("receiving the room mix");
  };
  pc.onconnectionstatechange = () => onConnectionState(pc, epoch);

  // Candidates found before the answer wait for the session token.
  const pending = [];
  let answered = false;
  pc.onicecandidate = e => {
    if (epoch !== call?.epoch) return;
    const candidate = e.candidate ? { candidate: e.candidate.candidate, sdpMLineIndex: e.candidate.sdpMLineIndex ?? 0 } : null;
    if (answered) sendCandidates([candidate]); else pending.push(candidate);
  };

  try {
    const offer = await pc.createOffer();
    offer.sdp = wantStereo(offer.sdp);
    await pc.setLocalDescription(offer);
    // An invited guest's chosen name and the in-world NPC request go with the first offer (not with re-offers).
    const body = { sdp: pc.localDescription.sdp };
    if (!call.sessionToken && call.chosenName) body.displayName = call.chosenName;
    if (!call.sessionToken && call.inWorldAvatar) body.inWorldAvatar = true;
    const answer = await request("POST", `/signal/${participantId}/offer`, {
      body,
      bearer: call.sessionToken ?? call.joinToken,
    });
    if (epoch !== call?.epoch) return;
    call.sessionToken = answer.sessionToken;
    await pc.setRemoteDescription({ type: "answer", sdp: answer.sdp });
    answered = true;
    sendCandidates(pending.splice(0));
    pollCandidates(pc, epoch);
  } catch (e) {
    log(`error: ${e.message}`);
    if (e instanceof HttpError && (e.status === 401 || e.status === 404)) {
      await endCall("the server ended this participant");
    } else {
      scheduleReconnect(epoch);
    }
  }
}

function sendCandidates(list) {
  if (list.length === 0 || !call?.sessionToken) return;
  const candidates = list.filter(Boolean);
  request("POST", `/signal/${call.participantId}/ice`, {
    body: { candidates, completed: list.includes(null) },
    bearer: call.sessionToken,
  }).catch(e => log(`error sending candidates: ${e.message}`));
}

async function pollCandidates(pc, epoch) {
  let since = 0;
  while (epoch === call?.epoch && !call.closed) {
    try {
      const r = await request("GET", `/signal/${call.participantId}/ice?since=${since}&waitMs=10000`, { bearer: call.sessionToken });
      if (epoch !== call?.epoch) return;
      for (const c of r.candidates) {
        await pc.addIceCandidate({ candidate: c.candidate, sdpMLineIndex: c.sdpMLineIndex });
      }
      since = r.next;
      if (r.complete) return; // the server has sent all its candidates (polling again would return at once)
    } catch (e) {
      if (e instanceof HttpError && (e.status === 401 || e.status === 404)) {
        await endCall("the server ended this participant");
        return;
      }
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
  }
}

function onConnectionState(pc, epoch) {
  if (epoch !== call?.epoch) return;
  const state = pc.connectionState;
  log(`connection ${state}`);
  if (state === "connected") {
    call.reconnects = 0;
    setState("connected");
  } else if (state === "failed") {
    scheduleReconnect(epoch);
  } else if (state === "disconnected") {
    setState("reconnecting");
    // Often recovers by itself (a brief network blip); rebuild only if it does not.
    setTimeout(() => { if (epoch === call?.epoch && pc.connectionState === "disconnected") scheduleReconnect(epoch); }, 4000);
  }
}

function scheduleReconnect(epoch) {
  if (epoch !== call?.epoch || call.closed) return;
  call.epoch++; // retire the old connection's callbacks
  call.pc?.close();
  if (++call.reconnects > MAX_RECONNECTS) {
    endCall("could not reconnect");
    return;
  }
  const delay = Math.min(1000 * 2 ** (call.reconnects - 1), 15000);
  setState("reconnecting");
  log(`reconnecting in ${delay / 1000} s (attempt ${call.reconnects} of ${MAX_RECONNECTS})`);
  setTimeout(() => { if (call && !call.closed) connect(); }, delay);
}

async function leave() {
  if (!call) return;
  const { participantId, sessionToken } = call;
  await endCall("left");
  if (sessionToken) {
    await request("DELETE", `/signal/${participantId}`, { bearer: sessionToken }).catch(() => {});
  }
}

async function endCall(reason) {
  if (!call || call.closed) return;
  call.closed = true;
  call.epoch++;
  clearInterval(call.statsTimer);
  call.pc?.close();
  call.stream?.getTracks().forEach(t => t.stop());
  await call.audio?.close().catch(() => {});
  $("remote").srcObject = null;
  setState("closed", reason);
  log(`call ended: ${reason}`);
  if (!params.has("participant")) {
    $("setup").hidden = false;
    loadRooms();
  }
}

function setState(state, detail) {
  const badge = $("state");
  badge.textContent = detail ? `${state}: ${detail}` : state;
  badge.className = `badge ${state}`;
  const inCall = state !== "closed";
  $("mute").disabled = !inCall;
  $("leave").disabled = !inCall;
}

// Chrome decodes Opus as stereo only if its own offer says stereo=1 (the server sends a stereo mix).
function wantStereo(sdp) {
  const pt = sdp.match(/a=rtpmap:(\d+) opus\/48000\/2/i)?.[1];
  if (!pt) return sdp;
  const fmtp = new RegExp(`^a=fmtp:${pt} (.*)$`, "m");
  if (fmtp.test(sdp)) {
    return sdp.replace(fmtp, (line, p) => /(^|;)\s*stereo=/.test(p) ? line : `${line.trimEnd()};stereo=1`);
  }
  return sdp.replace(new RegExp(`^(a=rtpmap:${pt} .*)$`, "m"), `$1\r\na=fmtp:${pt} stereo=1`);
}

// ---------------------------------------------------------------- meters and stats

function meter(source, bar) {
  const analyser = call.audio.createAnalyser();
  analyser.fftSize = 1024;
  source.connect(analyser);
  const samples = new Float32Array(analyser.fftSize);
  const ctx = call.audio;
  (function draw() {
    if (!call || call.closed || call.audio !== ctx) { bar.style.width = "0"; return; }
    analyser.getFloatTimeDomainData(samples);
    let sum = 0;
    for (const s of samples) sum += s * s;
    const db = 20 * Math.log10(Math.sqrt(sum / samples.length) || 1e-6);
    bar.style.width = `${Math.max(0, Math.min(100, (db + 60) / 60 * 100)).toFixed(0)}%`; // -60..0 dBFS
    requestAnimationFrame(draw);
  })();
}

async function showStats() {
  const pc = call?.pc;
  if (!pc || pc.connectionState === "closed") return;
  const report = await pc.getStats();
  const now = performance.now();
  let inbound, outbound, remoteInbound, pair, codec;
  const byId = new Map();
  report.forEach(s => byId.set(s.id, s));
  report.forEach(s => {
    if (s.type === "inbound-rtp" && s.kind === "audio") inbound = s;
    else if (s.type === "outbound-rtp" && s.kind === "audio") outbound = s;
    else if (s.type === "remote-inbound-rtp" && s.kind === "audio") remoteInbound = s;
    else if (s.type === "candidate-pair" && (s.nominated || s.selected) && s.state === "succeeded") pair = s;
  });
  if (inbound?.codecId) codec = byId.get(inbound.codecId);
  const last = call.lastStats;
  const kbps = (bytes, lastBytes) => last && bytes != null && lastBytes != null
    ? `${((bytes - lastBytes) * 8 / (now - last.time)).toFixed(0)} kbit/s` : "–";
  const local = pair && byId.get(pair.localCandidateId);
  const remote = pair && byId.get(pair.remoteCandidateId);
  const rows = {
    "Receiving": kbps(inbound?.bytesReceived, last?.inBytes),
    "Sending": kbps(outbound?.bytesSent, last?.outBytes),
    "Codec in": codec ? `${codec.mimeType} ${codec.clockRate / 1000} kHz, ${codec.channels ?? 1} ch` : "–",
    "Lost (in / out)": `${inbound?.packetsLost ?? "–"} / ${remoteInbound?.packetsLost ?? "–"}`,
    "Jitter": inbound?.jitter != null ? `${(inbound.jitter * 1000).toFixed(1)} ms` : "–",
    "Round trip": pair?.currentRoundTripTime != null ? `${(pair.currentRoundTripTime * 1000).toFixed(1)} ms` : "–",
    "Path": local && remote ? `${local.candidateType} ${local.protocol} → ${remote.address ?? remote.ip}:${remote.port} (${remote.candidateType})` : "–",
  };
  call.lastStats = { time: now, inBytes: inbound?.bytesReceived, outBytes: outbound?.bytesSent };
  $("stats").innerHTML = Object.entries(rows).map(([k, v]) => `<dt>${k}</dt><dd>${escapeHtml(String(v))}</dd>`).join("");
}

function escapeHtml(text) {
  return text.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// ---------------------------------------------------------------- wiring

$("mute").onclick = () => {
  const muted = $("mute").getAttribute("aria-pressed") !== "true";
  call?.stream?.getAudioTracks().forEach(t => { t.enabled = !muted; });
  $("mute").setAttribute("aria-pressed", String(muted));
  $("mute").textContent = muted ? "Unmute mic" : "Mute mic";
};
$("volume").oninput = () => { $("remote").volume = Number($("volume").value); };
$("leave").onclick = leave;
$("join").onclick = joinFromSetup;
$("invite").onclick = invite;
$("createRoom").onclick = createRoom;
$("facing").oninput = onFacing;
// Ticking Spatial picks the server's engine for spatial rooms (still changeable before Create).
$("newRoomSpatial").onchange = () => {
  const engine = $("newRoomSpatial").checked ? serverDefaults.spatial : serverDefaults.plain;
  if (engine) $("newRoomEngine").value = engine;
};
$("refreshRooms").onclick = loadRooms;
$("room").onchange = loadPlaces;
$("apiKey").onchange = loadRooms;

// Tell the server on the way out (tab closed or navigated away), so the participant does not linger.
addEventListener("pagehide", () => {
  if (call && !call.closed && call.sessionToken) {
    request("DELETE", `/signal/${call.participantId}`, { bearer: call.sessionToken, keepalive: true }).catch(() => {});
  }
});

if (!window.isSecureContext || !navigator.mediaDevices) {
  log("This page needs HTTPS (or localhost) for microphone access: use https://<server>:5001/client/");
}

if (params.has("participant") && params.has("token")) {
  // Join link: wait for a click (microphone permission and audio playback need a user gesture).
  $("call").hidden = false;
  $("start").hidden = false;
  $("roomTitle").textContent = params.get("room") ? `Room: ${params.get("room")}` : "Ready to join";
  $("whoami").textContent = `Participant ${params.get("participant")}`;
  setState("ready");
  // An invited guest may choose its name (the config says so); others keep the name the server gave them.
  request("GET", `/signal/${params.get("participant")}/config`, { bearer: params.get("token") }).then(config => {
    $("whoami").textContent = `${config.displayName ?? "Participant"} · ${params.get("participant")}`;
    if (config.clientNamed) {
      $("guestNameField").hidden = false;
      $("guestName").value = store(NAME_STORE) || "";
      $("guestName").focus();
    }
    $("guestNpcField").hidden = config.inWorldAvatars !== "optional";
  }).catch(e => log(`error: ${e.message}`));
  $("start").onclick = () => {
    const chosen = $("guestNameField").hidden ? null : $("guestName").value.trim();
    if (chosen === "") { $("guestName").focus(); log("enter your name first"); return; }
    if (chosen) store(NAME_STORE, chosen);
    const npc = !$("guestNpcField").hidden && $("guestNpc").checked;
    $("guestNameField").hidden = true;
    $("guestNpcField").hidden = true;
    startCall(params.get("participant"), params.get("token"), params.get("room"), chosen ?? undefined, undefined, chosen, npc);
  };
} else {
  $("setup").hidden = false;
  $("apiKey").value = store(KEY_STORE);
  $("displayName").value = store(NAME_STORE);
  loadRooms();
}
