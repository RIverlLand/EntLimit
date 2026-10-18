const HOST = 'com.entlimit.bridge';
let queue = Promise.resolve();
function native(type) {
  return chrome.runtime.sendNativeMessage(HOST, {v: 1, type});
}
function normalizeDomain(value) {
  const domain = String(value).trim().toLowerCase().replace(/\.$/, '');
  if (!/^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(domain)) return null;
  if (domain === 'localhost' || domain.endsWith('.localhost') || /^(?:\d+\.){3}\d+$/.test(domain)) return null;
  return domain;
}
function patterns(domain) { return [`http://*.${domain}/*`, `https://*.${domain}/*`]; }
async function sync() {
  try {
    const {snapshot: previousSnapshot, finishingDeadline} = await chrome.storage.local.get(['snapshot', 'finishingDeadline']);
    const response = await native('snapshot');
    if (!response.ok) throw new Error(response.error || 'Monitor unavailable.');
    const domains = [...new Set((response.config.domains || []).map(normalizeDomain).filter(Boolean))];
    const granted = [];
    for (const domain of domains) {
      if (await chrome.permissions.contains({origins: patterns(domain)})) granted.push(domain);
    }
    const locked = ['Locked', 'Draining'].includes(response.session.phase);
    const old = await chrome.declarativeNetRequest.getDynamicRules();
    await chrome.declarativeNetRequest.updateDynamicRules({
      removeRuleIds: old.map(rule => rule.id),
      addRules: locked ? granted.map((domain, index) => ({
        id: index + 1, priority: 1,
        action: {type: 'redirect', redirect: {extensionPath: '/gate.html'}},
        condition: {requestDomains: [domain], resourceTypes: ['main_frame']}
      })) : []
    });
    const scripts = await chrome.scripting.getRegisteredContentScripts();
    const matches = granted.flatMap(patterns).sort();
    const sameScripts = scripts.length === (matches.length ? 1 : 0) && (!matches.length ||
      JSON.stringify([...scripts[0].matches].sort()) === JSON.stringify(matches));
    if (!sameScripts) {
      if (scripts.length) await chrome.scripting.unregisterContentScripts({ids: scripts.map(s => s.id)});
      if (matches.length) await chrome.scripting.registerContentScripts([{
        id: 'entlimit-managed', matches, js: ['content.js'], runAt: 'document_idle', persistAcrossSessions: true
      }]);
    }
    await chrome.storage.local.set({snapshot: response, domains, granted, lastSync: Date.now(), error: null});
    // Website-only sessions can expire straight to Locked when no game is alive.
    const previousSession = previousSnapshot?.session;
    // The monitor owns time. Local wall-clock rollback must not suppress its finish event.
    const websiteExpired = response.session.phase === 'Locked' && previousSession?.phase === 'Active';
    const deadline = response.session.deadlineUtc || previousSession?.deadlineUtc;
    const finishing = response.session.phase === 'Draining' || websiteExpired;
    // Remember the expired grant so one-minute sync never repeatedly nags.
    if (finishing && granted.length && deadline && deadline !== finishingDeadline) {
      const tabs = await chrome.tabs.query({url: granted.flatMap(patterns)});
      await Promise.allSettled(tabs.map(tab => chrome.tabs.sendMessage(tab.id, {type: 'finishing', deadlineUtc: deadline})));
      await chrome.storage.local.set({finishingDeadline: deadline});
    }
    return {ok: true, snapshot: response, domains, granted};
  } catch (error) {
    const message = error.message || 'Connection unavailable.';
    await chrome.storage.local.set({error: message});
    return {ok: false, error: message};
  }
}
function queuedSync() {
  queue = queue.then(sync, sync);
  return queue;
}
chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create('entlimit-sync', {periodInMinutes: 1});
  queuedSync();
});
chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create('entlimit-sync', {periodInMinutes: 1});
  queuedSync();
});
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'entlimit-sync') queuedSync(); });
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const internalPage = sender.url?.startsWith(chrome.runtime.getURL(''));
  if (message.type === 'sync' && internalPage) {
    queuedSync().then(sendResponse); return true;
  }
  if (message.type === 'open_learning' && internalPage) {
    native('open_learning').then(sendResponse, () => sendResponse({ok: false, error: 'Open the desktop app first.'})); return true;
  }
  if (message.type === 'use' && sender.tab && sender.url) {
    (async () => {
      const host = new URL(sender.url).hostname.toLowerCase();
      const {granted = []} = await chrome.storage.local.get('granted');
      if (!granted.some(domain => host === domain || host.endsWith('.' + domain))) return {ok: false};
      const result = await native('use');
      await queuedSync();
      return result;
    })().then(sendResponse, () => sendResponse({ok: false}));
    return true;
  }
});
