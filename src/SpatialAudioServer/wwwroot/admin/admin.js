// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

"use strict";
// Admin UI: signs in with the admin password (session cookie), then polls the control API (/api/rooms…) and the
// admin-only endpoints (/api/admin/…). No build step, no libraries.

const $ = id => document.getElementById(id);
const escapeHtml = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const time = iso => iso ? new Date(iso).toLocaleTimeString() : "";
const dateTime = iso => iso ? new Date(iso).toLocaleString() : "";
const short = id => id ? String(id).slice(0, 8) : "";

class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

async function request(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: "same-origin",
  });
  if (response.status === 401 && !path.startsWith("/api/admin/login")) {
    showLogin("Your session has ended; sign in again.");
    throw new HttpError(401, "Not signed in.");
  }
  if (!response.ok) {
    let text = await response.text();
    try { const problem = JSON.parse(text); text = problem.detail ?? problem.title ?? text; } catch { /* plain text */ }
    throw new HttpError(response.status, text || `${method} ${path}: ${response.status}`);
  }
  return response.status === 204 ? null : response.json();
}

const state = {
  rooms: [],
  roomNames: new Map(),
  selected: null,       // room id
  details: null,        // GET /api/rooms/{id}
  live: null,           // GET /api/admin/rooms/{id}/live
  rates: new Map(),     // participant id → { at, bytesIn, bytesOut, kbpsIn, kbpsOut }
  status: null,
  cpu: null,            // { at, seconds } for the CPU percentage
  oldestEvent: null,    // id of the oldest event shown (paging)
  paged: false,         // "Older" pressed: stop refreshing the newest page
  editing: false,       // focus in the participants table (a gain being typed)
  acoustics: null,      // GET /api/rooms/{id}/acoustics
  acousticsFor: null,   // the room the panel was built for
  timers: [],
};

// ---------------------------------------------------------------------------------------------- sign-in

function showLogin(hint) {
  state.timers.forEach(clearInterval);
  state.timers = [];
  $("app").hidden = true;
  $("logout").hidden = true;
  $("login").hidden = false;
  $("loginHint").textContent = hint ?? "";
  $("serverLine").textContent = "Not signed in";
  $("password").focus();
}

async function signIn(event) {
  event.preventDefault();
  try {
    await request("POST", "/api/admin/login", { password: $("password").value });
    $("password").value = "";
    start();
  } catch (e) {
    $("loginHint").textContent = e.message;
  }
}

async function signOut() {
  await request("POST", "/api/admin/logout").catch(() => {});
  showLogin("Signed out.");
}

function start() {
  $("login").hidden = true;
  $("app").hidden = false;
  $("logout").hidden = false;
  const every = (ms, fn) => { fn(); state.timers.push(setInterval(fn, ms)); };
  every(5000, loadStatus);
  every(3000, loadRooms);
  every(1000, refreshRoom);
  every(5000, () => loadEvents(false));
}

// ---------------------------------------------------------------------------------------------- status

async function loadStatus() {
  try {
    const s = await request("GET", "/api/status");
    const first = state.status === null;
    state.status = s;
    let cpu = "";
    const now = performance.now();
    if (state.cpu) {
      const share = (s.process.cpuSeconds - state.cpu.seconds) / ((now - state.cpu.at) / 1000);
      cpu = ` · CPU ${(share * 100).toFixed(0)}% of a core (${(share * 100 / s.process.processorCount).toFixed(0)}% of ${s.process.processorCount})`;
    }
    state.cpu = { at: now, seconds: s.process.cpuSeconds };
    $("serverLine").textContent =
      `Server ${s.version.split("+")[0]} · ${s.gstreamer.version ?? "GStreamer off"} · up ${formatUptime(s.uptimeSeconds)} · ` +
      `${s.rooms} rooms, ${s.participants} participants · ${(s.process.workingSetBytes / 1048576).toFixed(0)} MB${cpu}`;
    if (first) {
      $("newEngine").innerHTML = s.engines.map(e =>
        `<option value="${escapeHtml(e.name)}" title="${escapeHtml(e.description)}"${e.name === s.defaultEngine ? " selected" : ""}>${escapeHtml(e.name)}</option>`).join("");
      $("roomAcoustics").innerHTML = (s.acoustics ?? ["outdoor"]).map(a =>
        `<option value="${escapeHtml(a)}">${escapeHtml(a)}</option>`).join("");
    }
  } catch { /* shown by the next successful poll */ }
}

function formatUptime(seconds) {
  const h = Math.floor(seconds / 3600), m = Math.floor(seconds % 3600 / 60);
  return h > 0 ? `${h} h ${m} min` : `${m} min`;
}

// ---------------------------------------------------------------------------------------------- rooms

async function loadRooms() {
  try {
    state.rooms = await request("GET", "/api/rooms");
  } catch { return; }
  state.roomNames = new Map(state.rooms.map(r => [r.id, r.name]));
  $("rooms").tBodies[0].innerHTML = state.rooms.length === 0
    ? `<tr><td colspan="6" class="hint">No rooms yet.</td></tr>`
    : state.rooms.map(r => `
      <tr class="clickable${r.id === state.selected ? " selected" : ""}" data-room="${r.id}">
        <td>${escapeHtml(r.name)}${r.spatial ? `<span class="tag">spatial</span>` : ""}<div class="id">${r.id}</div></td>
        <td>${escapeHtml(r.inUse?.engine ?? r.engine)}${r.inUse && r.inUse.engine !== r.engine
          ? `<div class="hint">→ ${escapeHtml(r.engine)} when empty</div>` : ""}</td>
        <td class="num">${r.participantCount} / ${r.maxParticipants}</td>
        <td class="id">${escapeHtml(r.externalId ?? "")}</td>
        <td>${dateTime(r.createdAt)}</td>
        <td><button class="small danger" type="button" data-delete="${r.id}">Delete</button></td>
      </tr>`).join("");
}

async function createRoom(event) {
  event.preventDefault();
  const body = { name: $("newName").value.trim(), engine: $("newEngine").value, spatial: $("newSpatial").checked };
  if ($("newMax").value) body.maxParticipants = Number($("newMax").value);
  try {
    const room = await request("POST", "/api/rooms", body);
    $("newName").value = "";
    await loadRooms();
    selectRoom(room.id);
  } catch (e) { alert(e.message); }
}

async function deleteRoom(id) {
  const name = state.roomNames.get(id) ?? id;
  if (!confirm(`Delete room "${name}"? Everyone in it is disconnected.`)) return;
  try {
    await request("DELETE", `/api/rooms/${id}`);
    if (state.selected === id) closeRoom();
    await loadRooms();
  } catch (e) { alert(e.message); }
}

$("roomAcoustics").onchange = async () => {
  try {
    await request("PATCH", `/api/rooms/${state.selected}`, { acoustics: $("roomAcoustics").value });
    $("roomAcoustics").blur();
    refreshRoom();
    loadAcoustics();
  } catch (e) { alert(e.message); }
};

$("newSpatial").onchange = () => {
  const s = state.status;
  if (s) $("newEngine").value = $("newSpatial").checked ? (s.defaultSpatialEngine ?? s.defaultEngine) : s.defaultEngine;
};

// ---------------------------------------------------------------------------------------------- one room

function selectRoom(id) {
  state.selected = id;
  state.details = null;
  state.live = null;
  state.rates.clear();
  $("room").hidden = false;
  $("history").hidden = true;
  $("inviteLink").hidden = true;
  $("graphView").href = `graph.html?room=${id}`;
  $("graphDot").href = `/api/admin/rooms/${id}/graph.dot`;
  for (const row of $("rooms").tBodies[0].rows) row.classList.toggle("selected", row.dataset.room === id);
  resetEvents();
  refreshRoom();
  loadAcoustics();
}

function closeRoom() {
  state.selected = null;
  $("room").hidden = true;
  for (const row of $("rooms").tBodies[0].rows) row.classList.remove("selected");
  resetEvents();
}

async function refreshRoom() {
  const id = state.selected;
  if (!id) return;
  try {
    const [details, live] = await Promise.all([
      request("GET", `/api/rooms/${id}`),
      request("GET", `/api/admin/rooms/${id}/live`).catch(e => e.status === 404 ? null : Promise.reject(e)),
    ]);
    if (id !== state.selected) return;
    state.details = details;
    state.live = live;
    renderRoom();
  } catch (e) {
    if (e.status === 404 && id === state.selected) { closeRoom(); loadRooms(); }
  }
}

function renderRoom() {
  if (state.editing) return;
  const d = state.details, live = state.live;
  $("roomName").textContent = d.name;
  // What the room's media is running; a change made while people are in it waits until the room is empty.
  const curve = (p) => p ?? "default curve";
  const running = d.inUse ?? d;
  const sound = (r) => `${r.engine}${d.spatial ? `, ${curve(r.spatialProfile)}` : ""}`;
  const pending = d.inUse ? ` (changes to ${sound(d)} when the room is empty)` : "";
  $("roomMeta").textContent = `${d.id} · ${sound(running)}${d.spatial ? ", spatial" : ""}${pending}` +
    ` · ${d.participantCount} of ${d.maxParticipants}` + (d.externalId ? ` · external id ${d.externalId}` : "");
  if (document.activeElement !== $("roomAcoustics")) $("roomAcoustics").value = d.acoustics ?? "outdoor";
  $("counters").textContent = !live ? "No media statistics (media disabled)."
    : !live.media.running ? "Media idle: the pipeline starts when someone joins."
    : `Mixing ${live.media.mixedPairs} source→listener pairs · ` +
      Object.entries(live.media.counters).map(([k, v]) => `${k} ${v}`).join(", ");

  const stats = new Map((live?.participants ?? []).map(p => [p.id, p]));
  const now = performance.now();
  $("participants").tBodies[0].innerHTML = d.participants.length === 0
    ? `<tr><td colspan="11" class="hint">Nobody here.</td></tr>`
    : d.participants.map(p => participantRow(p, stats.get(p.id), now)).join("");
}

function participantRow(p, s, now) {
  const c = s?.connection;
  const rate = updateRate(p.id, c, now);
  const level = s?.levelDb;
  const width = level == null ? 0 : Math.max(0, Math.min(100, (level + 60) / 60 * 100));
  const lossIn = c?.packetsLost != null && c?.packetsReceived ? (100 * c.packetsLost / (c.packetsReceived + Math.max(0, c.packetsLost))) : null;
  const lossOut = c?.remoteFractionLost != null ? 100 * c.remoteFractionLost : null;
  const stateClass = { connected: "connected", connecting: "connecting", failed: "failed", disconnected: "failed" }[p.state] ?? "";
  const pos = p.position ? `${p.position.x.toFixed(1)}, ${p.position.y.toFixed(1)}, ${p.position.z.toFixed(1)}` : "–";
  // Facing: turn about z from east (SL axes), from the listener orientation if it has one.
  const q = p.listenerOrientation ?? p.orientation;
  const facing = q ? `${Math.round(2 * Math.atan2(q.z, q.w) * 180 / Math.PI)}°` : null;
  return `
    <tr>
      <td>${escapeHtml(p.displayName)}${p.clientNamed ? `<span class="tag" title="Chose its own name (invited guest)">guest</span>` : ""}<div class="id" title="${p.id}">${short(p.id)}${p.externalId ? ` · ${escapeHtml(p.externalId)}` : ""}</div></td>
      <td><span class="badge ${stateClass}">${escapeHtml(p.state)}</span></td>
      <td>${p.muted ? `<span class="warn">muted</span>`
        : `<span class="level"><i style="width:${width.toFixed(0)}%"></i></span><span class="level-db">${level == null ? "–" : `${level.toFixed(0)} dB`}</span>`}</td>
      <td class="num">${rate ? `${rate.kbpsIn.toFixed(0)} / ${rate.kbpsOut.toFixed(0)}` : "–"}</td>
      <td class="num">${lossIn == null ? "–" : `${lossIn.toFixed(1)}%`}${lossOut == null ? "" : ` / ${lossOut.toFixed(1)}%`}</td>
      <td class="num">${c?.jitterSeconds == null ? "–" : `${(c.jitterSeconds * 1000).toFixed(0)} ms`}</td>
      <td class="num">${c?.roundTripSeconds == null ? "–" : `${(c.roundTripSeconds * 1000).toFixed(0)} ms`}</td>
      <td>${escapeHtml(c?.icePath ?? "–")}</td>
      <td class="num">${pos}${facing ? `<div class="id" title="Facing: degrees anticlockwise from east">facing ${facing}</div>` : ""}</td>
      <td><input class="gain" type="number" min="0" max="4" step="0.1" value="${p.gain}" data-gain="${p.id}" title="Gain (1 = unity)"></td>
      <td>
        <button class="small" type="button" data-mute="${p.id}" aria-pressed="${p.muted}">${p.muted ? "Unmute" : "Mute"}</button>
        <button class="small danger" type="button" data-kick="${p.id}">Kick</button>
      </td>
    </tr>`;
}

/** kbit/s in and out from the change in byte counters since the previous poll (kept for ~a second). */
function updateRate(id, c, now) {
  if (!c || c.bytesReceived == null || c.bytesSent == null) return null;
  const previous = state.rates.get(id);
  if (!previous || now - previous.at < 700) {
    if (!previous) state.rates.set(id, { at: now, bytesIn: c.bytesReceived, bytesOut: c.bytesSent, kbpsIn: 0, kbpsOut: 0 });
    return previous ?? null;
  }
  const seconds = (now - previous.at) / 1000;
  const next = {
    at: now, bytesIn: c.bytesReceived, bytesOut: c.bytesSent,
    kbpsIn: Math.max(0, c.bytesReceived - previous.bytesIn) * 8 / 1000 / seconds,
    kbpsOut: Math.max(0, c.bytesSent - previous.bytesOut) * 8 / 1000 / seconds,
  };
  state.rates.set(id, next);
  return next;
}

async function updateParticipant(id, body) {
  try {
    await request("PATCH", `/api/rooms/${state.selected}/participants/${id}`, body);
    refreshRoom();
  } catch (e) { alert(e.message); }
}

async function kick(id) {
  const p = state.details?.participants.find(x => x.id === id);
  if (!confirm(`Remove ${p?.displayName ?? id} from the room?`)) return;
  try {
    await request("DELETE", `/api/rooms/${state.selected}/participants/${id}`);
    refreshRoom();
    if (!state.paged) loadEvents(false);
  } catch (e) { alert(e.message); }
}

async function invite() {
  try {
    // Spatial rooms: the guest stands at the centre facing east (and can turn in the client); otherwise unplaced.
    // The guest chooses its own name when it joins (clientNamed).
    const body = state.details?.spatial
      ? { displayName: "Guest", clientNamed: true, position: { x: 0, y: 0, z: 0 }, orientation: { x: 0, y: 0, z: 0, w: 1 } }
      : { displayName: "Guest", clientNamed: true };
    const join = await request("POST", `/api/rooms/${state.selected}/participants`, body);
    const url = `${location.origin}/client/?participant=${join.participantId}&token=${encodeURIComponent(join.joinToken)}` +
      `&room=${encodeURIComponent(state.details?.name ?? "")}`;
    $("inviteLink").hidden = false;
    $("inviteLink").textContent = url;
    await navigator.clipboard?.writeText(url).catch(() => {});
  } catch (e) { alert(e.message); }
}

async function showSessions() {
  const box = $("history");
  if (!box.hidden) { box.hidden = true; return; }
  try {
    const sessions = await request("GET", `/api/admin/rooms/${state.selected}/sessions`);
    box.hidden = false;
    $("historyTable").tBodies[0].innerHTML = sessions.length === 0
      ? `<tr><td colspan="5" class="hint">No sessions yet.</td></tr>`
      : sessions.slice().reverse().map(s => `
        <tr>
          <td>${escapeHtml(s.displayName)}<div class="id" title="${s.participantId}">${short(s.participantId)}</div></td>
          <td class="id">${escapeHtml(s.externalId ?? "")}</td>
          <td>${dateTime(s.joinedAt)}</td>
          <td>${s.leftAt ? dateTime(s.leftAt) : `<span class="badge connected">in the room</span>`}</td>
          <td>${escapeHtml(s.leaveReason ?? "")}</td>
        </tr>`).join("");
  } catch (e) { alert(e.message); }
}

// ---------------------------------------------------------------------------------------------- acoustics

// The room's acoustic parameters (GET/PATCH /api/rooms/{id}/acoustics, acousticParameters): a row per parameter, built
// from the server's descriptions. A row shows the value in effect; changing it overrides the preset (sliders while
// dragging, a few times a second), ↺ goes back to the preset's (or the dimensions', or the distance curve's).

const ACOUSTIC_GROUPS = {
  dimensions: ["Room dimensions", "With width, depth and height, these give the reflections and reverb instead of the preset."],
  reflections: ["Early reflections", ""],
  reverb: ["Reverb", ""],
  distance: ["Distance and direction", "Overrides of the room's distance curve."],
};
const acousticTimers = new Map();   // key → pending PATCH timer

async function loadAcoustics() {
  const id = state.selected;
  if (!id) return;
  let a;
  try { a = await request("GET", `/api/rooms/${id}/acoustics`); } catch { return; }
  if (id !== state.selected) return;
  state.acoustics = a;
  if (state.acousticsFor !== id) buildAcoustics(a);
  showAcoustics(a);
}

const label = key => key.replace(/([A-Z])/g, " $1").replace(/^./, c => c.toUpperCase());

function buildAcoustics(a) {
  state.acousticsFor = state.selected;
  $("acousticsGroups").innerHTML = Object.entries(ACOUSTIC_GROUPS).map(([group, [title, note]]) => `
    <div class="acoustic-group">
      <h3>${escapeHtml(title)}</h3>${note ? `<p class="hint">${escapeHtml(note)}</p>` : ""}
      ${a.parameters.filter(p => p.group === group).map(acousticRow).join("")}
    </div>`).join("");
}

function acousticRow(p) {
  const unit = p.unit ? ` (${p.unit})` : "";
  const title = `${p.description}${unit}. In-world notecard key: ${p.property}`;
  let control;
  if (p.kind === "number") {
    control = `<input type="range" min="${p.min}" max="${p.sliderMax}" step="${p.step}" data-acoustic="${p.key}">` +
      `<input type="number" min="${p.min}" max="${p.max}" step="${p.step}" data-acoustic="${p.key}">`;
  } else if (p.kind === "list") {
    control = `<input type="text" placeholder="e.g. ${p.maxCount === 2 ? "0.1, 0.6" : "12, 20, 33"}" data-acoustic="${p.key}">`;
  } else {
    control = `<select data-acoustic="${p.key}">${p.choices.map(c => `<option value="${escapeHtml(c)}">${escapeHtml(c)}</option>`).join("")}</select>`;
  }
  return `
    <div class="acoustic-row inherited" data-row="${p.key}" title="${escapeHtml(title)}">
      <label>${escapeHtml(label(p.key))}${escapeHtml(unit)}</label>
      <span class="control">${control}</span>
      <span class="source"></span>
      <button class="small" type="button" data-reset="${p.key}" title="Back to the preset's or default value">↺</button>
    </div>`;
}

const formatList = v => Array.isArray(v) ? v.join(", ") : (v ?? "");

function showAcoustics(a) {
  const values = a.values ?? {};
  const set = a.acousticParameters ?? {};
  const derived = new Set(values.derived ?? []);
  const count = Object.keys(set).length;
  $("acousticsSummary").textContent = `· ${a.acoustics ?? "outdoor"}${count ? `, ${count} changed` : ""}`;
  $("acousticsNote").textContent = !a.values
    ? "Values in effect need the acousticmultimixer plugin on the server; changes are still stored."
    : !a.rendered ? "This room's engine is not acoustic: these are stored, but heard only with the acoustic engine."
    : values.reverbRoomLevel != null ? `The room's own reverb level: ${values.reverbRoomLevel} dB.` : "No reverb (outdoors).";
  for (const p of a.parameters) {
    const row = document.querySelector(`[data-row="${p.key}"]`);
    if (!row) continue;
    const overridden = p.key in set;
    const value = overridden ? set[p.key] : values[p.key];
    row.classList.toggle("overridden", overridden);
    row.classList.toggle("inherited", !overridden);
    // Where an unset value comes from: the preset (reflections, reverb), else the server's default or the room's curve.
    const fromPreset = (p.group === "reflections" || p.group === "reverb") && p.key !== "reverbLevel";
    row.querySelector(".source").textContent = overridden ? "set" : derived.has(p.key) ? "from size"
      : value == null || (Array.isArray(value) && value.length === 0 && fromPreset) ? "none" : fromPreset ? "preset" : "default";
    row.querySelector("[data-reset]").disabled = !overridden;
    for (const input of row.querySelectorAll("[data-acoustic]")) {
      if (input === document.activeElement || acousticTimers.has(p.key)) continue;
      input.value = p.kind === "list" ? formatList(value) : (value ?? "");
    }
  }
}

function sendAcoustic(key, value, delay) {
  clearTimeout(acousticTimers.get(key));
  acousticTimers.set(key, setTimeout(async () => {
    acousticTimers.delete(key);
    try {
      await request("PATCH", `/api/rooms/${state.selected}`, { acousticParameters: { [key]: value } });
    } catch (e) { alert(e.message); }
    loadAcoustics();
  }, delay));
}

function acousticValue(p, input) {
  if (p.kind === "number") return input.value === "" ? null : Number(input.value);
  if (p.kind === "list") return input.value.trim() === "" ? null : input.value;
  return input.value;
}

$("acousticsGroups").addEventListener("input", event => {
  const input = event.target.closest("[data-acoustic]");
  const p = input && state.acoustics?.parameters.find(x => x.key === input.dataset.acoustic);
  if (!p || p.kind !== "number") return;
  // Keep the slider and the number box together; send while dragging, at most every 200 ms.
  for (const other of input.closest(".acoustic-row").querySelectorAll("[data-acoustic]")) if (other !== input) other.value = input.value;
  sendAcoustic(p.key, acousticValue(p, input), 200);
});

$("acousticsGroups").addEventListener("change", event => {
  const input = event.target.closest("[data-acoustic]");
  const p = input && state.acoustics?.parameters.find(x => x.key === input.dataset.acoustic);
  if (p) sendAcoustic(p.key, acousticValue(p, input), 0);
});

$("acousticsGroups").addEventListener("click", event => {
  const reset = event.target.closest("[data-reset]");
  if (reset) sendAcoustic(reset.dataset.reset, null, 0);
});

// ---------------------------------------------------------------------------------------------- events

// The newest 100 events (of the selected room, or all), refreshed every few seconds. "Older" appends the next page
// and pauses the refresh until the room selection changes.
async function loadEvents(older) {
  if (!older && state.paged) return;
  const params = new URLSearchParams({ limit: "100" });
  if (state.selected) params.set("room", state.selected);
  if (older) params.set("before", state.oldestEvent);
  let events;
  try {
    events = await request("GET", `/api/admin/events?${params}`);
  } catch { return; }
  $("eventScope").textContent = state.selected ? `· ${state.roomNames.get(state.selected) ?? "this room"}` : "· all rooms";
  const rows = events.map(e => `
    <tr>
      <td>${dateTime(e.at)}</td>
      <td>${escapeHtml(e.kind)}</td>
      <td>${e.roomId ? escapeHtml(state.roomNames.get(e.roomId) ?? short(e.roomId)) : ""}</td>
      <td class="id">${short(e.participantId)}</td>
      <td class="detail">${escapeHtml(eventDetail(e))}</td>
    </tr>`).join("");
  const body = $("events").tBodies[0];
  if (older) {
    body.insertAdjacentHTML("beforeend", rows);
  } else {
    body.innerHTML = rows || `<tr><td colspan="5" class="hint">No events.</td></tr>`;
  }
  if (events.length > 0) state.oldestEvent = events[events.length - 1].id;
  $("olderEvents").disabled = events.length < 100;
}

/** "Simon: left"; just "Simon" when the detail is the name itself (joined), the detail alone without a participant. */
function eventDetail(e) {
  const name = e.participantName;
  if (!name) return e.detail ?? "";
  return e.detail && e.detail !== name ? `${name}: ${e.detail}` : name;
}

async function olderEvents() {
  if (state.oldestEvent === null) return;
  state.paged = true;
  await loadEvents(true);
}

function resetEvents() {
  state.paged = false;
  state.oldestEvent = null;
  loadEvents(false);
}

// ---------------------------------------------------------------------------------------------- wiring

$("loginForm").onsubmit = signIn;
$("logout").onclick = signOut;
$("createForm").onsubmit = createRoom;
$("closeRoom").onclick = closeRoom;
$("invite").onclick = invite;
$("sessions").onclick = showSessions;
$("olderEvents").onclick = olderEvents;

$("rooms").onclick = event => {
  const remove = event.target.closest("[data-delete]");
  if (remove) { deleteRoom(remove.dataset.delete); return; }
  const row = event.target.closest("tr[data-room]");
  if (row) selectRoom(row.dataset.room);
};

$("participants").onclick = event => {
  const mute = event.target.closest("[data-mute]");
  if (mute) updateParticipant(mute.dataset.mute, { muted: mute.getAttribute("aria-pressed") !== "true" });
  const kicked = event.target.closest("[data-kick]");
  if (kicked) kick(kicked.dataset.kick);
};

$("participants").onchange = event => {
  const gain = event.target.closest("[data-gain]");
  if (gain) updateParticipant(gain.dataset.gain, { gain: Number(gain.value) });
};

// Polling re-renders the table every second; don't fight someone typing a gain.
$("participants").addEventListener("focusin", () => { state.editing = true; });
$("participants").addEventListener("focusout", () => { state.editing = false; });

(async () => {
  try {
    const session = await request("GET", "/api/admin/session");
    if (session.loggedIn) start();
    else showLogin(session.passwordSet ? "" : "No admin password is set yet: run 'SpatialAudioServer admin set-password' on the server.");
  } catch (e) {
    showLogin(e.message);
  }
})();
