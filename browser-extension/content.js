// Only injected into user-approved managed domains. Existing pages are never closed.
chrome.runtime.sendMessage({type: 'use'}).catch(() => {});
let finishingShown = null;
chrome.runtime.onMessage.addListener(message => {
  if (message.type !== 'finishing' || finishingShown === message.deadlineUtc) return;
  finishingShown = message.deadlineUtc;
  document.getElementById('entlimit-finish-note')?.remove();
  const note = document.createElement('div');
  note.id = 'entlimit-finish-note';
  Object.assign(note.style, {position: 'fixed', bottom: '20px', right: '20px', maxWidth: '320px', padding: '14px', background: '#173b35', color: 'white', borderRadius: '12px', zIndex: '2147483647', font: '14px system-ui'});
  note.append(document.createTextNode('这段娱乐时间结束了。当前内容可以收尾；下次打开时再学一点。 '));
  const close = document.createElement('button');
  close.textContent = '知道了';
  close.onclick = () => note.remove();
  note.append(close);
  document.documentElement.append(note);
});
