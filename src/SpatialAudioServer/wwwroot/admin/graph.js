"use strict";
// Pipeline view for one room (graph.html?room=ID). Polls the admin graph snapshot, groups the room's top-level
// elements into summary blocks (one per participant stage, the room input, each shared mix), lays the result out with
// ELK (vendor/elkjs, in a worker) and draws it as SVG. Blocks and bins open in place; a new layout is made only when
// the structure changes, otherwise just the live values are redrawn. No build step.

const $ = id => document.getElementById(id);
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const short = id => id ? String(id).slice(0, 8) : "";
const SVG = "http://www.w3.org/2000/svg";

const roomId = new URLSearchParams(location.search).get("room");
const elk = new ELK({ workerUrl: "vendor/elkjs/elk-worker.min.js" });

const KIND_TEXT = {
  receive: "Receive (WebRTC)",
  send: "Send (WebRTC)",
  decode: "Decode",
  room: "Room input",
  cluster: "Shared mix",
  personal: "Own mix",
  listener: "Listener's mix",
  other: "Other",
};

/** What each kind of block does, for someone running the server. */
const KIND_HELP = {
  receive: n => `Audio arriving from ${n}: ICE (the network path), DTLS/SRTP decryption and the RTP jitter buffer, all inside webrtcbin.`,
  send: n => `Audio going to ${n}: RTP, SRTP encryption and the network, inside the same webrtcbin as the receive side.`,
  decode: n => `${n}'s Opus audio decoded to raw 48 kHz mono, ready to be mixed.`,
  room: () => "Every participant's decoded audio side by side as one multichannel stream (plus a silent channel that keeps it running). Every mix reads from here.",
  cluster: () => "A mix of the current talkers shared by every listener who hears them the same way, so it is encoded only once.",
  personal: n => `${n} is talking, so they get their own mix: everyone else talking, without themselves (mix-minus).`,
  listener: n => `What ${n} hears: their mix (or the shared mix they are switched to), encoded to Opus and packetised for sending.`,
  other: () => "Elements the view does not recognise.",
};

const state = {
  graph: null,          // GET /api/admin/rooms/{id}/graph
  room: null,           // GET /api/rooms/{id}
  live: null,           // GET /api/admin/rooms/{id}/live
  model: null,          // what is drawn (see buildModel)
  layout: null,         // ELK result for model.signature
  signature: "",
  openBlocks: new Set(),  // block keys shown with their elements
  openBins: new Set(),    // element paths shown with their children
  selected: null,       // node id
  focus: "",            // participant id
  view: { x: 20, y: 20, k: 1 },
  fitted: false,
  busy: false,
  properties: new Map(), // element path → GET …/graph/element result (for the panel)
};

class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

async function get(path) {
  const response = await fetch(path, { credentials: "same-origin" });
  if (!response.ok) {
    let text = await response.text();
    try { const problem = JSON.parse(text); text = problem.detail ?? problem.title ?? text; } catch { /* plain text */ }
    throw new HttpError(response.status, text || `${path}: ${response.status}`);
  }
  return response.json();
}

// ---------------------------------------------------------------------------------------------- polling

async function refresh() {
  if (state.busy || !roomId) return;
  state.busy = true;
  try {
    const query = [...state.openBins].map(p => `expand=${encodeURIComponent(p)}`).join("&");
    const [room, graph, live] = await Promise.all([
      get(`/api/rooms/${roomId}`),
      get(`/api/admin/rooms/${roomId}/graph${query ? `?${query}` : ""}`).catch(e => e.status === 404 ? null : Promise.reject(e)),
      get(`/api/admin/rooms/${roomId}/live`).catch(() => null),
    ]);
    state.room = room;
    state.live = live;
    state.graph = graph;
    $("title").textContent = `${room.name} · pipeline`;
    document.title = `${room.name} pipeline`;
    if (!graph) {
      showEmpty("The room has no pipeline: media runs only while someone is in it.");
      return;
    }
    $("empty").hidden = true;
    const paths = new Set(graph.elements.map(e => e.path));
    for (const path of [...state.openBins]) if (!paths.has(path)) state.openBins.delete(path);
    updateFocusList();

    const model = buildModel();
    if (model.signature !== state.signature) {
      state.layout = await layOut(model);
      state.signature = model.signature;
    }
    state.model = model;
    render();
    if (!state.fitted) { fit(); state.fitted = true; }
    const blocks = model.blockCount, elements = graph.elements.filter(e => !e.parent).length;
    $("statusLine").textContent = `${graph.engine} · ${room.participantCount} participants · ${blocks} blocks, ` +
      `${elements} top-level elements · updated ${new Date().toLocaleTimeString()}`;
  } catch (e) {
    if (e.status === 401) {
      showEmpty("Not signed in.", true);
    } else if (e.status === 404) {
      showEmpty("This room no longer exists.");
    } else {
      $("statusLine").textContent = `Update failed: ${e.message}`;
    }
  } finally {
    state.busy = false;
  }
}

function showEmpty(text, signIn) {
  $("viewport").replaceChildren();
  state.signature = "";
  state.model = null;
  $("empty").hidden = false;
  $("empty").innerHTML = esc(text) + (signIn ? ` <a href="./">Sign in to the admin page</a>, then reload.` : "");
  $("statusLine").textContent = "";
}

// ---------------------------------------------------------------------------------------------- model

/**
 * Turns the snapshot into what is drawn: nodes (blocks, elements, containers), ports (pads of visible elements) and
 * edges (links between visible things; links inside a closed block are hidden, parallel ones merged). A
 * participant's webrtcbin is drawn twice, its receive side (src pads) on the left and its send side (sink pads) on
 * the right, so audio reads left to right; opened as a bin it is drawn once.
 */
function buildModel() {
  const g = state.graph;
  const names = new Map(state.room.participants.map(p => [p.id, p.displayName]));
  const byPath = new Map(g.elements.map(e => [e.path, e]));
  const pads = new Map();
  for (const e of g.elements) for (const p of e.pads) pads.set(`${e.path}:${p.name}`, { element: e, pad: p });
  const childrenOf = new Map();
  for (const e of g.elements) if (e.parent) (childrenOf.get(e.parent) ?? childrenOf.set(e.parent, []).get(e.parent)).push(e);
  const topOf = e => { while (e.parent) e = byPath.get(e.parent); return e; };
  const isSplit = top => top.block?.kind === "webrtc" && !state.openBins.has(top.path);

  // Blocks: a webrtcbin's block splits into receive and send.
  const blocks = new Map();
  const addTo = (key, kind, top) => {
    const b = top.block;
    if (!blocks.has(key)) blocks.set(key, { key, kind, participant: b.participant, name: b.name, elements: [] });
    blocks.get(key).elements.push(top);
  };
  for (const top of g.elements.filter(e => !e.parent)) {
    const b = top.block ?? { key: "other", kind: "other" };
    if (b.kind === "webrtc") {
      addTo(`receive:${b.participant}`, "receive", top);
      if (isSplit(top)) addTo(`send:${b.participant}`, "send", top);
    } else {
      addTo(b.key, b.kind, top);
    }
  }
  const blockKeyOf = (top, direction) => top.block?.kind === "webrtc"
    ? (isSplit(top) && direction === "sink" ? `send:${top.block.participant}` : `receive:${top.block.participant}`)
    : (top.block?.key ?? "other");

  const nodes = new Map();
  const roots = [];
  const nodeOfPort = new Map();
  const addElement = (e, parent, half) => {
    const id = `e:${e.path}${half ? `#${half}` : ""}`;
    const open = e.isBin && state.openBins.has(e.path);
    const node = {
      id, type: "element", element: e, half, parent, open, kind: parent?.kind ?? "other",
      ports: e.pads.filter(p => !half || (half === "rx") === (p.direction === "src"))
        .map(p => ({ id: `p:${e.path}:${p.name}`, pad: p, element: e })),
      children: [],
    };
    for (const port of node.ports) nodeOfPort.set(port.id, node);
    nodes.set(id, node);
    parent.children.push(node);
    if (open) for (const child of childrenOf.get(e.path) ?? []) addElement(child, node, null);
  };
  for (const block of blocks.values()) {
    const node = { id: `b:${block.key}`, type: "block", block, kind: block.kind, open: state.openBlocks.has(block.key), ports: [], children: [] };
    nodes.set(node.id, node);
    roots.push(node);
    if (node.open) {
      for (const top of block.elements) {
        addElement(top, node, top.block?.kind === "webrtc" && isSplit(top) ? (block.kind === "receive" ? "rx" : "tx") : null);
      }
    }
  }

  // Edges between whatever shows each end.
  const endpoint = padPath => {
    const found = pads.get(padPath);
    if (!found) return null;
    const top = topOf(found.element);
    const key = blockKeyOf(top, found.element === top ? found.pad.direction : "src");
    if (!state.openBlocks.has(key)) return { node: `b:${key}` };
    const half = found.element === top && isSplit(top) ? (found.pad.direction === "src" ? "rx" : "tx") : null;
    const node = `e:${found.element.path}${half ? `#${half}` : ""}`;
    return nodes.has(node) ? { node, port: `p:${padPath}` } : { node: `b:${key}` };
  };
  const edges = new Map();
  for (const link of g.links) {
    const from = endpoint(link.from), to = endpoint(link.to);
    if (!from || !to || (from.node === to.node && !from.port && !to.port)) continue;
    const id = `${from.port ?? from.node}>${to.port ?? to.node}`;
    const caps = pads.get(link.from)?.pad.caps ?? pads.get(link.to)?.pad.caps ?? null;
    const edge = edges.get(id) ?? edges.set(id, { id, from, to, links: [], pending: 0 }).get(id);
    edge.links.push({ ...link, caps });
    if (!caps) edge.pending++;
  }

  // Sizes depend only on names, so live values never force a new layout.
  for (const node of nodes.values()) {
    const text = labelsOf(node, names);
    node.title = text.title;
    node.sub = text.sub;
    const chars = Math.max(node.title.length, node.sub.length * .92, 16);
    node.width = Math.ceil(Math.min(chars * 7.1 + 26, 300));
    node.height = node.type === "block" ? 64 : 52;
  }

  const signature = JSON.stringify([
    [...nodes.values()].map(n => [n.id, n.open, n.ports.map(p => p.id), n.width, n.title]),
    [...edges.keys()],
  ]);
  return { nodes, roots, edges, names, pads, byPath, nodeOfPort, blockCount: blocks.size, signature };
}

function labelsOf(node, names) {
  if (node.type === "element") {
    const e = node.element;
    const side = node.half === "rx" ? " (receive)" : node.half === "tx" ? " (send)" : "";
    return { title: e.name + side, sub: e.isBin && !node.open ? `${e.factory} · ${e.children} inside` : e.factory };
  }
  const b = node.block;
  const who = names.get(b.participant) ?? short(b.participant);
  switch (b.kind) {
    case "room": return { title: "Room input", sub: "all sources, interleaved" };
    case "cluster": return { title: `Shared mix ${b.name?.split("-")[0].slice(1) ?? ""}`, sub: `${b.elements.length} elements` };
    case "other": return { title: "Other", sub: `${b.elements.length} elements` };
    default: return { title: who, sub: KIND_TEXT[b.kind] };
  }
}

// ---------------------------------------------------------------------------------------------- layout

async function layOut(model) {
  const toElk = node => {
    const elkNode = {
      id: node.id,
      ports: node.ports.map(p => ({ id: p.id, width: 8, height: 8, layoutOptions: { "elk.port.side": p.pad.direction === "sink" ? "WEST" : "EAST" } })),
      layoutOptions: { "elk.portConstraints": "FIXED_SIDE" },
    };
    if (node.open && node.children.length > 0) {
      elkNode.children = node.children.map(toElk);
      elkNode.layoutOptions["elk.padding"] = "[top=34,left=14,bottom=14,right=14]";
    } else {
      elkNode.width = node.width;
      elkNode.height = node.height;
    }
    return elkNode;
  };
  const graph = {
    id: "root",
    layoutOptions: {
      "elk.algorithm": "layered",
      "elk.direction": "RIGHT",
      "elk.hierarchyHandling": "INCLUDE_CHILDREN",
      "elk.edgeRouting": "ORTHOGONAL",
      "elk.layered.spacing.nodeNodeBetweenLayers": "56",
      "elk.layered.spacing.edgeNodeBetweenLayers": "18",
      "elk.spacing.nodeNode": "18",
      "elk.spacing.edgeEdge": "8",
      "elk.layered.nodePlacement.strategy": "NETWORK_SIMPLEX",
      "elk.json.shapeCoords": "ROOT",
      "elk.json.edgeCoords": "ROOT",
    },
    children: model.roots.map(toElk),
    edges: [...model.edges.values()].map(e => ({ id: e.id, sources: [e.from.port ?? e.from.node], targets: [e.to.port ?? e.to.node] })),
  };
  const result = await elk.layout(graph);
  const shapes = new Map(), wires = new Map();
  const walk = n => {
    shapes.set(n.id, n);
    for (const p of n.ports ?? []) shapes.set(p.id, p);
    for (const c of n.children ?? []) walk(c);
  };
  walk(result);
  for (const e of result.edges ?? []) wires.set(e.id, e.sections ?? []);
  return { shapes, wires, width: result.width, height: result.height };
}

// ---------------------------------------------------------------------------------------------- drawing

function render() {
  const { model, layout } = state;
  if (!model || !layout) return;
  const lit = litNodes(model);
  const root = document.createElementNS(SVG, "g");
  const groups = document.createElementNS(SVG, "g"), wires = document.createElementNS(SVG, "g"), leaves = document.createElementNS(SVG, "g");
  root.append(groups, wires, leaves);

  const draw = node => {
    const shape = layout.shapes.get(node.id);
    if (!shape) return;
    const container = node.open && node.children.length > 0;
    const g = el("g", { class: `node kind-${node.kind}${container ? (node.type === "block" ? " group" : " bin") : ""}${node.id === state.selected ? " selected" : ""}${lit && !lit.has(node.id) ? " dim" : ""}`, "data-node": node.id });
    g.append(el("rect", { class: "box", x: shape.x, y: shape.y, width: shape.width, height: shape.height, rx: 7 }));
    if (!container) g.append(el("rect", { class: "band", x: shape.x, y: shape.y, width: 5, height: shape.height, rx: 2 }));
    g.append(text(shape.x + (container ? 10 : 13), shape.y + 17, node.title));
    if (!container) {
      g.append(text(shape.x + 13, shape.y + 32, node.sub, "sub"));
      const live = liveLine(node);
      if (live.text) g.append(text(shape.x + 13, shape.y + 46, fitText(live.text, shape.width - 20), live.warn ? "live warn" : "live"));
      if (live.level != null) {
        const w = shape.width - 26, y = shape.y + shape.height - 9;
        g.append(el("rect", { class: "meter-bg", x: shape.x + 13, y, width: w, height: 4, rx: 2 }));
        g.append(el("rect", { class: "meter", x: shape.x + 13, y, width: w * live.level, height: 4, rx: 2 }));
      }
    } else {
      g.append(text(shape.x + 10 + node.title.length * 7.4 + 8, shape.y + 17, node.type === "block" ? KIND_TEXT[node.kind] : node.element.factory, "sub"));
    }
    for (const port of node.ports) {
      const p = layout.shapes.get(port.id);
      if (p) g.append(el("rect", { class: "port", x: p.x, y: p.y, width: p.width, height: p.height, rx: 2, "data-port": port.id }));
    }
    (container ? groups : leaves).append(g);
    if (container) node.children.forEach(draw);
  };
  model.roots.forEach(draw);

  for (const edge of model.edges.values()) {
    const sections = layout.wires.get(edge.id) ?? [];
    if (sections.length === 0) continue;
    const d = sections.map(s => `M${s.startPoint.x},${s.startPoint.y}` + (s.bendPoints ?? []).map(b => ` L${b.x},${b.y}`).join("") + ` L${s.endPoint.x},${s.endPoint.y}`).join(" ");
    const dim = lit && !(lit.has(edge.from.node) && lit.has(edge.to.node));
    const g = el("g", { class: `edge${edge.pending === edge.links.length ? " pending" : ""}${edge.links.length > 1 ? " multi" : ""}${dim ? " dim" : ""}`, "data-edge": edge.id });
    g.append(el("path", { class: "hit", d }), el("path", { class: "wire", d }));
    wires.append(g);
  }

  root.id = "content";
  $("viewport").replaceChildren(root);
  applyView();
  if (state.selected && !model.nodes.has(state.selected)) closePanel();
  else if (state.selected) renderPanel(false);
}

const el = (name, attrs) => {
  const node = document.createElementNS(SVG, name);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  return node;
};

const text = (x, y, content, cls) => {
  const t = el("text", { x, y, ...(cls ? { class: cls } : {}) });
  t.textContent = content;
  return t;
};

const fitText = (s, width) => s.length * 6.3 > width ? s.slice(0, Math.max(3, Math.floor(width / 6.3) - 1)) + "…" : s;

/** The small third line of a box (state, fill, what a listener hears) and an optional level 0–1. */
function liveLine(node) {
  const participants = new Map((state.live?.participants ?? []).map(p => [p.id, p]));
  const people = new Map((state.room?.participants ?? []).map(p => [p.id, p]));
  if (node.type === "element") {
    const e = node.element, live = e.live;
    if (e.state !== "playing") return { text: e.state, warn: true };
    if (e.factory === "queue") return { text: `${live["current-level-buffers"] ?? "?"} buf · ${ms(live["current-level-time"])}` };
    if (e.factory === "webrtcbin") return { text: enumText(live["connection-state"]), warn: enumText(live["connection-state"]) !== "connected" };
    if (e.factory === "input-selector") return { text: `input ${live["active-pad"] ?? "–"}` };
    if (e.factory === "opusenc") return { text: `${Math.round(Number(live.bitrate) / 1000)} kbit/s · c${live.complexity}` };
    if (e.factory === "tee") return { text: `${live["num-src-pads"]} outputs` };
    if (e.factory === "audiomixmatrix") return { text: `${live["in-channels"]} → ${live["out-channels"]} channels` };
    return { text: "" };
  }
  const b = node.block;
  const person = people.get(b.participant), stats = participants.get(b.participant);
  const stopped = b.elements.find(e => e.state !== "playing");
  if (stopped) return { text: `${stopped.name}: ${stopped.state}`, warn: true };
  switch (b.kind) {
    case "receive": {
      const rtc = b.elements[0];
      const conn = enumText(rtc.live["connection-state"]);
      if (person?.muted) return { text: `${conn} · muted`, warn: true };
      return { text: conn, warn: conn !== "connected", level: levelOf(stats) };
    }
    case "decode": return {
      text: stats?.levelDb == null ? "" : stats.levelDb < -90 ? "silent" : `${stats.levelDb.toFixed(0)} dB`,
      level: levelOf(stats),
    };
    case "listener": {
      const heard = hearingOf(b);
      const queue = Math.max(0, ...b.elements.filter(e => e.factory === "queue").map(e => Number(e.live["current-level-time"]) || 0));
      return { text: [heard, queue > 0 ? `queued ${ms(queue)}` : ""].filter(Boolean).join(" · ") };
    }
    case "cluster": {
      const tee = b.elements.find(e => e.factory === "tee");
      const listeners = state.graph.links.filter(l => tee && l.from.startsWith(`${tee.path}:`)).length;
      return { text: `${listeners} listener${listeners === 1 ? "" : "s"}` };
    }
    case "room": {
      const c = state.live?.media?.counters ?? {};
      return { text: c.slots != null ? `${c.slotsInUse} of ${c.slots} slots in use` : "" };
    }
    default: return { text: "" };
  }
}

/** matrix-shared: which branch a listener's input-selector is on (a shared mix, or the talker's own). */
function hearingOf(block) {
  const selector = block.elements.find(e => e.factory === "input-selector");
  const active = selector?.live["active-pad"];
  if (!active) return "";
  const link = state.graph.links.find(l => l.to === `${selector.path}:${active}`);
  const source = link && state.model?.byPath.get(link.from.slice(0, link.from.lastIndexOf(":")));
  // The selector's inputs are queues fed from a cluster's tee, or the personal encoder.
  const feed = source && state.graph.links.find(l => l.to.startsWith(`${source.path}:`));
  const origin = feed ? state.model.byPath.get(feed.from.slice(0, feed.from.lastIndexOf(":"))) : source;
  const block2 = origin?.block ?? source?.block;
  if (block2?.kind === "cluster") return `hears shared mix ${block2.name.split("-")[0].slice(1)}`;
  if (block2?.kind === "personal") return "hears own mix";
  return `input ${active}`;
}

const levelOf = stats => stats?.levelDb == null ? 0 : Math.max(0, Math.min(1, (stats.levelDb + 60) / 60));
const ms = ns => ns == null || ns === "" ? "–" : `${(Number(ns) / 1e6).toFixed(0)} ms`;
const enumText = s => s ? (/^[A-Z][A-Z0-9_]*$/.test(s) ? s.slice(s.lastIndexOf("STATE_") >= 0 ? s.lastIndexOf("STATE_") + 6 : 0).toLowerCase().replaceAll("_", " ") : s) : "–";

/** With a participant in focus: their nodes, their neighbours, and the containers of those. Null = all lit. */
function litNodes(model) {
  if (!state.focus) return null;
  const owns = node => (node.type === "block" ? node.block.participant : topBlockOf(node)?.block.participant) === state.focus;
  const own = new Set([...model.nodes.values()].filter(owns).map(n => n.id));
  const lit = new Set(own);
  for (const edge of model.edges.values()) {
    if (own.has(edge.from.node) || own.has(edge.to.node)) { lit.add(edge.from.node); lit.add(edge.to.node); }
  }
  for (const id of [...lit]) {
    let node = model.nodes.get(id);
    while (node?.parent) { lit.add(node.parent.id); node = node.parent; }
  }
  return lit;
}

const topBlockOf = node => { while (node && node.type !== "block") node = node.parent; return node; };

// ---------------------------------------------------------------------------------------------- pan and zoom

function applyView() {
  const { x, y, k } = state.view;
  $("viewport").setAttribute("transform", `translate(${x},${y}) scale(${k})`);
}

function fit() {
  if (!state.layout) return;
  const box = $("canvas").getBoundingClientRect();
  const panel = $("panel").hidden ? 0 : $("panel").getBoundingClientRect().width;
  const w = Math.max(100, box.width - panel - 40), h = Math.max(100, box.height - 40);
  const k = Math.min(w / state.layout.width, h / state.layout.height, 1.4);
  state.view = { k, x: 20 + (w - state.layout.width * k) / 2, y: 20 + (h - state.layout.height * k) / 2 };
  applyView();
}

const canvas = $("canvas");
canvas.addEventListener("wheel", e => {
  e.preventDefault();
  const box = canvas.getBoundingClientRect();
  const mx = e.clientX - box.left, my = e.clientY - box.top;
  const { x, y, k } = state.view;
  const k2 = Math.max(0.08, Math.min(4, k * Math.exp(-e.deltaY * 0.0015)));
  state.view = { k: k2, x: mx - (mx - x) * k2 / k, y: my - (my - y) * k2 / k };
  applyView();
}, { passive: false });

let drag = null;
canvas.addEventListener("pointerdown", e => {
  if (e.button !== 0) return;
  drag = { x: e.clientX, y: e.clientY, view: { ...state.view }, moved: false };
  canvas.setPointerCapture(e.pointerId);
});
canvas.addEventListener("pointermove", e => {
  if (drag) {
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (!drag.moved && Math.hypot(dx, dy) < 4) return;
    drag.moved = true;
    canvas.classList.add("dragging");
    state.view = { ...drag.view, x: drag.view.x + dx, y: drag.view.y + dy };
    applyView();
    hideTip();
    return;
  }
  showTip(e);
});
canvas.addEventListener("pointerup", e => {
  const moved = drag?.moved;
  drag = null;
  canvas.classList.remove("dragging");
  if (moved) return;
  const target = document.elementFromPoint(e.clientX, e.clientY);
  const node = target?.closest("[data-node]");
  if (node) select(node.dataset.node);
});
canvas.addEventListener("pointerleave", hideTip);
canvas.addEventListener("dblclick", e => {
  const node = e.target.closest("[data-node]");
  if (node) toggle(state.model.nodes.get(node.dataset.node));
});

// ---------------------------------------------------------------------------------------------- tooltip

function showTip(e) {
  const tip = $("tip");
  const target = e.target;
  let html = "";
  const port = target.closest?.("[data-port]");
  const edge = target.closest?.("[data-edge]");
  const node = target.closest?.("[data-node]");
  if (port) {
    const found = state.model.pads.get(port.dataset.port.slice(2));
    if (found) html = `<b>${esc(found.element.name)}:${esc(found.pad.name)}</b> (${found.pad.direction}${found.pad.ghost ? ", ghost" : ""})` +
      `<div class="mono">${esc(found.pad.caps ?? "not negotiated")}</div>`;
  } else if (edge) {
    const e2 = state.model.edges.get(edge.dataset.edge);
    if (e2) {
      const first = e2.links[0];
      html = `<b>${esc(first.from)}</b> → <b>${esc(first.to)}</b>` + (e2.links.length > 1 ? ` <i>and ${e2.links.length - 1} more</i>` : "") +
        `<div class="mono">${esc(first.caps ?? "not negotiated yet")}</div>`;
    }
  } else if (node) {
    const n = state.model.nodes.get(node.dataset.node);
    if (n?.type === "block") {
      html = `<b>${esc(n.title)}</b> · ${esc(KIND_TEXT[n.kind])}<br>${esc(KIND_HELP[n.kind](n.title))}<br>` +
        `<span class="hint">${n.block.elements.length} element(s); double-click to ${n.open ? "close" : "open"}</span>`;
    } else if (n) {
      const e2 = n.element;
      html = `<b>${esc(e2.name)}</b> · ${esc(e2.factory)} · ${esc(e2.state)}` +
        Object.entries(e2.live).map(([k, v]) => `<div class="mono">${esc(k)} = ${esc(v)}</div>`).join("") +
        (e2.isBin ? `<span class="hint">${e2.children} elements inside; double-click to ${n.open ? "close" : "open"}</span>` : "");
    }
  }
  if (!html) { hideTip(); return; }
  tip.innerHTML = html;
  tip.hidden = false;
  const x = Math.min(e.clientX + 14, window.innerWidth - tip.offsetWidth - 8);
  const y = Math.min(e.clientY + 14, window.innerHeight - tip.offsetHeight - 8);
  tip.style.left = `${x}px`;
  tip.style.top = `${y}px`;
}

function hideTip() { $("tip").hidden = true; }

// ---------------------------------------------------------------------------------------------- opening and closing

function toggle(node) {
  if (!node) return;
  if (node.type === "block") {
    state.openBlocks.has(node.block.key) ? state.openBlocks.delete(node.block.key) : state.openBlocks.add(node.block.key);
  } else if (node.element.isBin) {
    const path = node.element.path;
    if (state.openBins.has(path)) {
      for (const p of [...state.openBins]) if (p === path || p.startsWith(`${path}/`)) state.openBins.delete(p);
    } else {
      state.openBins.add(path);
    }
  } else {
    return;
  }
  refresh();
}

function setAll(open) {
  state.openBlocks.clear();
  if (open && state.graph) for (const e of state.graph.elements) if (!e.parent) {
    const b = e.block;
    if (b?.kind === "webrtc") { state.openBlocks.add(`receive:${b.participant}`); state.openBlocks.add(`send:${b.participant}`); }
    else state.openBlocks.add(b?.key ?? "other");
  }
  if (!open) state.openBins.clear();
  state.fitted = false;
  refresh();
}

// ---------------------------------------------------------------------------------------------- side panel

function select(id) {
  state.selected = id;
  $("panel").hidden = false;
  render();
  renderPanel(true);
}

function closePanel() {
  state.selected = null;
  $("panel").hidden = true;
  render();
}

function renderPanel(fetchProperties) {
  const node = state.model?.nodes.get(state.selected);
  if (!node) return;
  const body = [];
  if (node.type === "block") {
    const b = node.block;
    $("panelTitle").textContent = `${node.title} · ${KIND_TEXT[b.kind]}`;
    body.push(`<p>${esc(KIND_HELP[b.kind](node.title))}</p>`);
    const live = liveLine(node);
    if (live.text) body.push(`<p class="${live.warn ? "warn" : ""}">${esc(live.text)}</p>`);
    body.push(`<div class="actions"><button type="button" data-toggle>${node.open ? "Close block" : "Open block"}</button></div>`);
    body.push(`<h3>Elements</h3><ul class="elements">` + b.elements.map(e =>
      `<li><a data-element="${esc(e.path)}">${esc(e.name)}</a> <span class="hint">${esc(e.factory)}${e.state !== "playing" ? ` · ${esc(e.state)}` : ""}</span></li>`).join("") + "</ul>");
  } else {
    const e = node.element;
    $("panelTitle").textContent = e.name;
    body.push(`<p>${esc(e.factory)} · ${esc(e.state)}${e.parent ? ` · inside <a data-element="${esc(e.parent)}">${esc(e.parent.split("/").pop())}</a>` : ""}</p>`);
    if (e.isBin) body.push(`<div class="actions"><button type="button" data-toggle>${node.open ? "Close bin" : `Open bin (${e.children} inside)`}</button></div>`);
    const peers = new Map();
    for (const l of state.graph.links) { peers.set(l.from, l.to); peers.set(l.to, l.from); }
    body.push(`<h3>Pads</h3><table>` + e.pads.map(p => {
      const peer = peers.get(`${e.path}:${p.name}`);
      return `<tr><td>${esc(p.name)}</td><td>${p.direction}${p.ghost ? " · ghost" : ""}${peer ? ` → <span class="mono">${esc(peer)}</span>` : ""}` +
        `<div class="mono">${esc(p.caps ?? "not negotiated")}</div></td></tr>`;
    }).join("") + `</table>`);
    body.push(`<h3>Properties</h3><div id="props"><p class="hint">Loading…</p></div>`);
  }
  $("panelBody").innerHTML = body.join("");
  if (node.type === "element") loadProperties(node.element.path, fetchProperties);
}

async function loadProperties(path, fetch) {
  const show = details => {
    if (state.model?.nodes.get(state.selected)?.element?.path !== path || !$("props")) return;
    $("props").innerHTML = `<table>` + details.properties.map(p => `<tr><td>${esc(p.name)}</td><td class="mono">${esc(p.value)}</td></tr>`).join("") + `</table>`;
  };
  if (state.properties.has(path) && !fetch) { show(state.properties.get(path)); return; }
  try {
    const details = await get(`/api/admin/rooms/${roomId}/graph/element?path=${encodeURIComponent(path)}`);
    state.properties.set(path, details);
    show(details);
  } catch (e) {
    if ($("props")) $("props").innerHTML = `<p class="hint">${esc(e.message)}</p>`;
  }
}

$("panelClose").onclick = closePanel;
$("panelBody").onclick = e => {
  if (e.target.closest("[data-toggle]")) { toggle(state.model.nodes.get(state.selected)); return; }
  const link = e.target.closest("[data-element]");
  if (!link) return;
  const path = link.dataset.element;
  const element = state.model.byPath.get(path);
  if (!element) return;
  // Open the element's block (and bins above it) so it is visible, then select it.
  let top = element; while (top.parent) top = state.model.byPath.get(top.parent);
  const b = top.block;
  state.openBlocks.add(b?.kind === "webrtc" ? `receive:${b.participant}` : b?.key ?? "other");
  const half = b?.kind === "webrtc" && element === top && !state.openBins.has(top.path) ? "#rx" : "";
  state.selected = `e:${path}${half}`;
  refresh();
};

// ---------------------------------------------------------------------------------------------- toolbar

function updateFocusList() {
  const select = $("focus");
  const current = select.value;
  const options = [`<option value="">Everyone</option>`].concat(state.room.participants.map(p =>
    `<option value="${p.id}"${p.id === current ? " selected" : ""}>${esc(p.displayName)}</option>`));
  if (select.options.length !== options.length || [...select.options].some((o, i) => o.value !== (i === 0 ? "" : state.room.participants[i - 1]?.id))) {
    select.innerHTML = options.join("");
  }
  if (current && !state.room.participants.some(p => p.id === current)) { select.value = ""; state.focus = ""; }
}

$("focus").onchange = () => { state.focus = $("focus").value; render(); };
$("expandAll").onclick = () => setAll(true);
$("collapseAll").onclick = () => setAll(false);
$("fit").onclick = fit;
$("dot").href = `/api/admin/rooms/${roomId}/graph.dot`;
window.addEventListener("resize", () => { if (!state.fitted) fit(); });

if (!roomId) {
  showEmpty("No room given: open this page from a room in the admin page.");
} else {
  refresh();
  setInterval(() => { if ($("auto").checked && !drag) refresh(); }, 2000);
}
