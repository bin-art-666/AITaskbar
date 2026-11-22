const host = globalThis.recentSessionsNativeHost ?? "com.aitaskbar.edge_recent";
const reconnect = "reconnect";
let connection;

async function recentTabs() {
  const recent = await chrome.sessions.getRecentlyClosed();
  return recent.flatMap(item => {
    const tab = item.tab;
    if (!tab?.sessionId || tab.incognito || !/^https?:\/\//i.test(tab.url || "")) return [];
    const title = (tab.title || new URL(tab.url).hostname).trim().slice(0, 160);
    return title ? [{sessionId: tab.sessionId, title}] : [];
  });
}

function connect() {
  if (connection) return;
  const port = chrome.runtime.connectNative(host);
  const sessionPrefix = crypto.randomUUID() + ":";
  connection = port;
  chrome.alarms.clear(reconnect);
  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    connection = undefined;
    chrome.alarms.create(reconnect, {delayInMinutes: 0.5});
  });
  port.onMessage.addListener(async request => {
    if (typeof request?.id !== "string") return;
    const response = {id: request.id, ok: false};
    try {
      const items = await recentTabs();
      if (request.action === "list") {
        response.items = items.slice(0, 5).map(item => ({
          ...item, sessionId: sessionPrefix + item.sessionId
        }));
        response.ok = true;
      } else if (request.action === "restore") {
        const selected = items.find(item => sessionPrefix + item.sessionId === request.sessionId);
        if (selected) response.ok = Boolean((await chrome.sessions.restore(selected.sessionId)).tab);
      }
    } catch {
      // A session can disappear between listing and restoring it.
    }
    if (connection === port) port.postMessage(response);
  });
}

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === reconnect) connect(); });
connect();
