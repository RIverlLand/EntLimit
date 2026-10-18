const state = document.getElementById('state');
async function refresh() {
  const result = await chrome.runtime.sendMessage({type: 'sync'});
  if (!result.ok) { state.textContent = result.error; return; }
  const phase = result.snapshot.session.phase;
  state.textContent = ['Ready', 'Active'].includes(phase)
    ? '已解锁，重新打开原来的网站即可。计时从首次使用开始。'
    : phase === 'Draining' ? '当前娱乐正在收尾。请在桌面应用中结束本次时段后开始新的学习。' : '准备好后，完成一项练习即可开始。';
}
document.getElementById('learn').onclick = async () => {
  const result = await chrome.runtime.sendMessage({type: 'open_learning'});
  if (!result.ok) state.textContent = result.error;
};
document.getElementById('refresh').onclick = refresh;
refresh().catch(() => { state.textContent = '无法连接，请打开 EntLimit 桌面应用。'; });
