const state = document.getElementById('state');
let selectedDomains = [];
async function refresh() {
  const result = await chrome.runtime.sendMessage({type: 'sync'});
  state.textContent = result.ok ? `状态：${result.snapshot.session.phase}` : result.error;
  const data = await chrome.storage.local.get(['domains', 'granted']);
  selectedDomains = data.domains || [];
  document.getElementById('domains').textContent = `已选：${(data.domains || []).join('、') || '无'}；已授权：${(data.granted || []).join('、') || '无'}`;
}
document.getElementById('grant').onclick = async () => {
  // Invoke permission request directly in the click's user gesture, before any await.
  if (!selectedDomains.length) { state.textContent = '请在桌面应用中添加网站，然后同步。'; return; }
  const origins = selectedDomains.flatMap(d => [`http://*.${d}/*`, `https://*.${d}/*`]);
  const accepted = await chrome.permissions.request({origins});
  if (accepted) await refresh(); else state.textContent = '未授权；这些网站暂不受管理。';
};
document.getElementById('refresh').onclick = refresh;
document.getElementById('learn').onclick = async () => {
  const result = await chrome.runtime.sendMessage({type: 'open_learning'});
  if (!result.ok) state.textContent = result.error;
};
refresh().catch(() => { state.textContent = '连接失败，请打开桌面应用。'; });
