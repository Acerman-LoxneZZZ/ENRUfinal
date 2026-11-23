/* A Native Messaging channel to the desktop process is the only transport. */
const frames = new Set();
const pending = new Map();
let recent = false;
let native = null;

function post(port, message) {
  try { port.postMessage(message); } catch { /* The frame or host has closed. */ }
}

function configure(enabled) {
  recent = enabled;
  for (const frame of frames) post(frame, { op: "config", enabled });
}

function connect() {
  if (native) return;
  const host = chrome.runtime.connectNative("org.raskladka.editor");
  native = host;
  host.onMessage.addListener(message => {
    handle(host, message).catch(() => {
      if (message.id) post(host, { id: message.id, state: "inactive" });
    });
  });
  host.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    native = null;
    for (const entry of pending.values()) clearTimeout(entry.timer);
    pending.clear();
    configure(false);
  });
}

async function handle(host, message) {
  if (message.op === "config") { configure(message.enabled === true); return; }
  if (typeof message.id !== "string") return;
  if (message.op !== "prepare") {
    const item = pending.get(message.id);
    if (!item || !item.owner) {
      if (message.op === "commit") post(host, { id: message.id, state: "inactive" });
      return;
    }
    post(item.owner, message);
    if (message.op === "release") { clearTimeout(item.timer); pending.delete(message.id); }
    return;
  }
  const win = await chrome.windows.getLastFocused();
  if (!win.focused) { post(host, { id: message.id, state: "inactive" }); return; }
  const [tab] = await chrome.tabs.query({ active: true, windowId: win.id });
  const targets = [...frames].filter(frame => frame.sender.tab && frame.sender.tab.id === tab?.id);
  if (!targets.length) { post(host, { id: message.id, state: "inactive" }); return; }
  const item = { remaining: new Set(targets), owner: null, timer: null };
  item.timer = setTimeout(() => { pending.delete(message.id); }, 2200);
  pending.set(message.id, item);
  for (const target of targets) post(target, message);
}

chrome.runtime.onConnect.addListener(frame => {
  if (frame.name !== "field" || !frame.sender.tab) return;
  frames.add(frame);
  connect();
  post(frame, { op: "config", enabled: recent });
  frame.onDisconnect.addListener(() => frames.delete(frame));
  frame.onMessage.addListener(reply => {
    const item = pending.get(reply.id);
    if (!item || !native) return;
    if (item.owner === frame) { post(native, reply); return; }
    if (item.owner || !item.remaining.delete(frame)) return;
    if (reply.state !== "inactive") {
      item.owner = frame;
      post(native, reply);
    } else if (!item.remaining.size) post(native, reply);
  });
});
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
