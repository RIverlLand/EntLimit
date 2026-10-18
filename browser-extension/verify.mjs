import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

let state = {config: {domains: ['bilibili.com', 'localhost', '127.0.0.1', 'bad/path', 'example.com']}, session: {phase: 'Locked'}};
let rules = [], scripts = [], cache = {}, messages = [];
const chrome = {
  runtime: {
    sendNativeMessage: async (_, message) => ({ok: true, ...state}),
    onInstalled: {addListener() {}}, onStartup: {addListener() {}}, onMessage: {addListener() {}},
    getURL: path => 'chrome-extension://test/' + path
  },
  permissions: {contains: async ({origins}) => origins.every(p => p.includes('bilibili.com'))},
  declarativeNetRequest: {
    getDynamicRules: async () => rules,
    updateDynamicRules: async value => { rules = value.addRules; }
  },
  scripting: {
    getRegisteredContentScripts: async () => scripts,
    unregisterContentScripts: async () => { scripts = []; },
    registerContentScripts: async value => { scripts = value; }
  },
  storage: {local: {
    set: async value => { Object.assign(cache, JSON.parse(JSON.stringify(value))); },
    get: async keys => Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, cache[key]]))
  }},
  tabs: {query: async () => [{id: 1}], sendMessage: async (_, message) => { messages.push(message); }},
  alarms: {create() {}, onAlarm: {addListener() {}}}
};
const context = vm.createContext({chrome, URL, console});
vm.runInContext(await readFile(new URL('./background.js', import.meta.url), 'utf8'), context);
await vm.runInContext('sync()', context);
assert.equal(rules.length, 1, 'only valid, granted managed domains get rules');
assert.equal(rules[0].condition.requestDomains[0], 'bilibili.com');
assert.deepEqual(Array.from(rules[0].condition.resourceTypes), ['main_frame']);
assert.equal(rules[0].action.redirect.extensionPath, '/gate.html');
assert.equal(scripts[0].matches.length, 2);
assert.equal(cache.domains.length, 2, 'local hosts, IPs and malformed values are excluded');
state.session.phase = 'Ready';
await vm.runInContext('sync()', context);
assert.equal(rules.length, 0, 'Ready must permit navigation so content first-use can start timer');
state.session.phase = 'Draining';
state.session.deadlineUtc = new Date(Date.now() - 1000).toISOString();
await vm.runInContext('sync()', context);
assert.equal(rules.length, 1, 'new navigation is gated during grace');
assert.equal(messages[0].type, 'finishing');
await vm.runInContext('sync()', context);
assert.equal(messages.length, 1, 'repeat grace sync does not nag');
state.session = {phase: 'Active', deadlineUtc: new Date(Date.now() - 2000).toISOString()};
await vm.runInContext('sync()', context);
state.session = {phase: 'Locked', deadlineUtc: null};
await vm.runInContext('sync()', context);
assert.equal(messages.length, 2, 'browser-only Active to Locked expiry reminds existing pages');
await vm.runInContext('sync()', context);
assert.equal(messages.length, 2, 'browser-only expiry is reminded once');
state.session = {phase: 'Active', deadlineUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString()};
await vm.runInContext('sync()', context);
state.session = {phase: 'Locked', deadlineUtc: null};
await vm.runInContext('sync()', context);
assert.equal(messages.length, 3, 'monitor finish is authoritative even when local clock appears before deadline');
chrome.runtime.sendNativeMessage = async () => { throw new Error('Disconnected'); };
const previous = rules;
const result = await vm.runInContext('sync()', context);
assert.equal(result.ok, false);
assert.equal(rules, previous, 'disconnect retains cached cooperative gates');
assert.equal(cache.error, 'Disconnected');
console.log('Browser rules: permission scoping, domain validation, Ready navigation, grace and disconnect checks passed.');
