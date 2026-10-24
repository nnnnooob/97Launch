'use strict';
(() => {
  let items = [], paused = false, uploading = false, resumeId = null, generation = 0;
  const state = {uploading:'待完成上传',ready:'待发布',published:'已发布',withdrawn:'已撤回',failed:'校验失败',cancelled:'已取消',expired:'已过期'};
  const size = n => (n / 1048576).toFixed(1) + ' MiB';
  async function load() {
    const data = await api('/api/admin/releases'); items = data.items;
    $('release-build-hint').textContent = `下一个可用构建编号：${data.next_build}。版本信息读取自签名清单。`;
    $('release-key-hint').textContent = `签名公钥标识：${data.signing_key_id}`;
    $('release-list').innerHTML = items.length ? items.map(r => `<article class="panel release-card"><header><div><h3>${escape(r.version)}</h3><div class="release-meta"><span>构建 ${r.build}</span><span>${r.channel === 'stable' ? '稳定版' : '测试版'}</span><span>${escape(r.architecture)}</span><span>${size(r.size)}</span></div></div><span class="badge ${r.state === 'published' ? 'active' : 'pending'}">${escape(state[r.state] || r.state)}</span></header><pre>${escape(r.notes || '暂无更新说明')}</pre>${r.error ? `<p class="error-text">${escape(r.error)}</p>` : ''}<footer><small>${date(r.published_at || r.created_at)} · 下载请求 ${r.downloads} 次</small><div class="release-actions">${r.state === 'uploading' ? `<button class="secondary" data-release-resume="${r.id}">继续上传</button><button class="quiet" data-release-action="cancel" data-id="${r.id}">取消</button>` : ''}${r.state === 'ready' || r.state === 'withdrawn' ? `<button class="primary" data-release-action="publish" data-id="${r.id}">发布版本</button>` : ''}${r.state === 'published' ? `<button class="quiet" data-release-action="withdraw" data-id="${r.id}">撤回版本</button>` : ''}${r.state === 'failed' ? `<button class="quiet" data-release-action="cancel" data-id="${r.id}">取消上传</button>` : ''}</div></footer></article>`).join('') : '<div class="panel empty"><h3>准备发布第一个版本</h3><p>上传 EXE 与签名清单，通过校验后即可发布。</p></div>';
  }
  async function upload(id, file) {
    if (uploading) throw new Error('已有应用文件正在上传');
    const taskGeneration = ++generation; uploading = true; paused = false;
    $('release-upload').disabled = true; $('release-progress-area').hidden = false; $('release-error').textContent = '';
    try {
      let row = await api('/api/admin/releases/' + id), failures = 0;
      if (row.size !== file.size) throw new Error('文件大小与签名清单不符，请选择原始 EXE');
      while (row.offset < file.size && !paused && taskGeneration === generation) {
        $('release-progress').value = row.offset / file.size * 100;
        $('release-progress-text').textContent = `正在上传 ${size(row.offset)} / ${size(file.size)}`;
        try {
          row = await api(`/api/admin/releases/${id}/chunk?offset=${row.offset}`,{method:'PUT',body:file.slice(row.offset,row.offset + 1048576),headers:{'Content-Type':'application/octet-stream'},timeout:90000}); failures = 0;
        } catch (e) {
          if (++failures > 3) throw e;
          await new Promise(resolve => setTimeout(resolve,3000)); row = await api('/api/admin/releases/' + id);
        }
      }
      if (paused || taskGeneration !== generation) { $('release-progress-text').textContent = '已暂停，可从版本列表重新选择原文件续传。'; return; }
      $('release-progress').value = 100; $('release-progress-text').textContent = '正在校验哈希与程序架构…';
      await api(`/api/admin/releases/${id}/complete`,{method:'POST',timeout:180000});
      $('release-progress-text').textContent = '校验通过，可在右侧发布版本。'; toast('应用上传完成，等待发布');
    } finally { uploading = false; $('release-upload').disabled = false; await load(); }
  }
  window.UpdateUI = {load, logout(){paused = true; generation++;}};
  $('release-refresh').addEventListener('click', () => load().catch(e => toast(e.message)));
  $('release-pause').addEventListener('click', () => {paused = true;});
  $('release-form').addEventListener('submit', async event => {
    event.preventDefault(); $('release-error').textContent = '';
    try {
      const file = $('release-exe').files[0], manifestFile = $('release-manifest').files[0];
      if (!file || !manifestFile || manifestFile.size > 16384) throw new Error('请选择 EXE 和有效的签名清单');
      const manifest = JSON.parse(await manifestFile.text());
      const row = await api('/api/admin/releases',{method:'POST',body:JSON.stringify({manifest,notes:$('release-notes').value.trim()})});
      await load(); await upload(row.id,file);
    } catch(e) { $('release-error').textContent = e.message; }
  });
  $('release-list').addEventListener('click', async event => {
    const resume = event.target.closest('[data-release-resume]');
    if (resume) { resumeId = resume.dataset.releaseResume; $('release-resume-file').click(); return; }
    const b = event.target.closest('[data-release-action]'); if (!b) return;
    b.disabled = true;
    try {
      const action = b.dataset.releaseAction;
      if (!confirm(action === 'publish' ? '确认将此版本提供给对应通道的客户端？' : action === 'withdraw' ? '确认撤回？客户端将不再获得该版本。' : '确认取消此上传？')) return;
      await api(`/api/admin/releases/${b.dataset.id}${action === 'cancel' ? '' : '/' + action}`,{method:action === 'cancel' ? 'DELETE' : 'POST'});
      toast(action === 'publish' ? '版本已发布' : action === 'withdraw' ? '版本已撤回' : '上传已取消'); await load();
    } catch(e) { toast(e.message); } finally {b.disabled = false;}
  });
  $('release-resume-file').addEventListener('change', async () => {
    try { const f = $('release-resume-file').files[0]; if (resumeId && f) await upload(resumeId,f); }
    catch(e) { $('release-error').textContent = e.message; }
    finally { $('release-resume-file').value = ''; }
  });
})();
