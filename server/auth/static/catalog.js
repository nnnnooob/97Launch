'use strict';
(() => {
  const names = {online:'可连接',starting:'启动中',stopping:'停止中',stopped:'未运行',unknown:'状态未知',unreachable:'游戏端口未就绪',maintenance:'维护中'};
  let servers = [], packs = [], pending = [], trafficDirty = false, connectionDirty = false;
  let uploadTask = null, pauseUpload = false, uploadLoop = false, resumeId = null, loggedOut = false;
  const limitsMap = {'download-total':'download_total_mib','download-user':'download_user_mib','upload-total':'upload_total_mib','upload-task':'upload_task_mib','download-slots':'download_slots','upload-slots':'upload_slots','file-limit':'max_file_gib','storage-limit':'storage_gib'};
  const bytes = n => n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GiB' : n >= 1048576 ? (n / 1048576).toFixed(1) + ' MiB' : n >= 1024 ? (n / 1024).toFixed(0) + ' KiB' : n + ' B';
  const stateBadge = state => `<span class="badge ${escape(state)}">${escape(names[state] || '状态未知')}</span>`;
  const catalogError = (id, message) => { $(id).textContent = message || ''; $(id).hidden = !message; };
  const ids = value => [...new Set(value.split(/[\s,，;；]+/).filter(Boolean))];

  async function loadServers() {
    const data = await api('/api/admin/servers');
    servers = data.items;
    $('servers-published').textContent = servers.filter(s => s.published).length;
    $('servers-found').textContent = servers.length;
    $('mcsm-summary').textContent = data.sync_error ? 'MCSM 连接需检查' : 'MCSM 已连接';
    $('mcsm-sync-at').textContent = data.sync_at ? '最近同步 ' + date(data.sync_at) : '等待首次同步';
    catalogError('server-sync-error', data.sync_error);
    $('servers-empty').hidden = servers.length > 0;
    $('server-list').innerHTML = servers.map((s, index) => `<article class="admin-server"><img src="${escape(s.icon_url)}" alt="服务器图标" width="68" height="68"><div class="admin-server-body"><h3>${escape(s.name)}</h3><div class="server-tags">${stateBadge(s.state)}<span class="badge ${s.published ? 'published' : 'draft'}">${s.published ? '已发布' : '未发布'}</span>${s.players != null ? `<span class="muted">${s.players} / ${s.max_players} 人</span>` : ''}</div><div class="server-meta"><span>${s.managed ? escape(s.address) : '待配置公网连接地址'}</span><span>${escape('节点：' + (s.node || '未命名'))}</span><span class="linked-pack">${s.pack ? escape(s.pack.name + ' / ' + s.pack.version) : '未关联客户端整合包'}</span></div></div><div class="admin-server-actions"><button class="secondary" data-edit-server="${index}">${s.managed ? '编辑配置' : '配置服务器'}</button><small>${!s.published ? '发布后可见' : s.config.group_ids.length ? '指定 QQ 群可见' : '已授权设备可见'}</small></div></article>`).join('');
  }

  function showStorage(storage) {
    const used = storage.stored_bytes + storage.reserved_bytes;
    $('storage-text').textContent = `${bytes(storage.stored_bytes)} / ${bytes(storage.quota_bytes)}`;
    $('storage-progress').value = used; $('storage-progress').max = storage.quota_bytes;
    $('storage-detail').textContent = `上传预留 ${bytes(storage.reserved_bytes)}　磁盘剩余 ${bytes(storage.free_disk_bytes)}`;
  }

  async function loadPacks() {
    const data = await api('/api/admin/packs');
    packs = data.items; pending = data.uploads;
    showStorage(data.storage);
    $('packs-empty').hidden = packs.some(p => p.versions.length);
    $('pending-uploads').hidden = !pending.length;
    $('pending-upload-list').innerHTML = pending.map(u => `<div class="pending-row"><div><strong>${escape(u.filename)}</strong><small>${escape(u.version)}　${bytes(u.offset)} / ${bytes(u.size)}${u.error ? '　' + escape(u.error) : '　24 小时未续传后过期'}</small></div><div>${u.status === 'uploading' ? `<button class="secondary" data-resume-upload="${u.id}">续传</button>` : ''}<button class="quiet" data-cancel-upload="${u.id}">取消</button></div></div>`).join('');
    $('pack-list').innerHTML = packs.filter(p => p.versions.length).map(p => `<article class="panel pack-group"><div class="pack-heading"><div><h2>${escape(p.name)}</h2><p>${p.versions.filter(v => !v.archived_at).length} 个可用版本</p></div><button class="quiet" data-add-version="${p.id}">上传新版本</button></div><div class="table-scroll"><table><thead><tr><th>版本 / 游戏</th><th>文件</th><th>服务器关联</th><th>操作</th></tr></thead><tbody>${p.versions.map(v => `<tr class="${v.archived_at ? 'version-archived' : ''}"><td><span class="primary-text">${escape(v.version)} ${v.archived_at ? '<span class="badge">已归档</span>' : ''}</span>${secondary(v.minecraft + ' / ' + v.loader)}${secondary(date(v.created_at))}</td><td>${primary(v.filename)}${secondary(bytes(v.size) + ' / ' + (v.format === 'modrinth' ? 'Modrinth' : 'CurseForge'))}<span class="secondary-text" title="${escape(v.sha256)}">SHA-256 ${escape(v.sha256.slice(0,16))}… <button class="hash-copy" data-copy-hash="${v.sha256}" aria-label="复制完整 SHA-256">复制</button></span></td><td>${escape(v.servers.join('、') || '未关联')}</td><td><div class="version-actions"><a href="/api/admin/packs/versions/${v.id}/download">下载</a><button data-pack-action="${v.archived_at ? 'restore' : 'archive'}" data-version="${v.id}">${v.archived_at ? '恢复' : '归档'}</button></div></td></tr>`).join('')}</tbody></table></div></article>`).join('');
  }

  async function loadTraffic() {
    const [data, summary] = await Promise.all([api('/api/admin/traffic'), api('/api/admin/catalog')]);
    if (!trafficDirty) for (const [id, field] of Object.entries(limitsMap)) $(id).value = data.limits[field];
    if (!connectionDirty) {
      $('mcsm-url').value = data.connection.base_url;
      $('mcsm-key').placeholder = data.connection.has_api_key ? '已保存，留空保持当前密钥' : '填写 MCSM API Key';
    }
    const connected = !summary.mcsm.error;
    $('mcsm-connection-badge').textContent = connected ? '已连接' : data.connection.has_api_key ? '连接异常' : '未配置';
    $('mcsm-connection-badge').className = 'badge ' + (connected ? 'active' : 'pending');
    catalogError('mcsm-connection-error', summary.mcsm.error);
    $('active-downloads').textContent = data.active.filter(a => a.direction === 'download').length;
    $('active-uploads').textContent = data.active.filter(a => a.direction === 'upload').length;
    $('active-transfers').innerHTML = data.active.length ? data.active.map(a => `<div class="active-transfer"><strong>${escape(a.name)}</strong><progress max="${a.size}" value="${a.bytes}" aria-label="文件传输进度"></progress><small><span>${a.direction === 'upload' ? '上传' : '下载'} / ${escape(a.owner)}</span><span>${bytes(a.bytes)} / ${bytes(a.size)}</span></small></div>`).join('') : '<p class="muted">当前没有文件传输。</p>';
    $('traffic-totals').textContent = `服务启动以来：上传 ${bytes(data.bytes_uploaded)}，下载 ${bytes(data.bytes_downloaded)}。统计起始于 ${date(data.since)}。`;
  }

  window.CatalogUI = {
    load(name) { loggedOut = false; return ({servers:loadServers,packs:loadPacks,traffic:loadTraffic})[name](); },
    logout() { loggedOut = true; pauseUpload = true; $('mcsm-key').value = ''; document.querySelectorAll('.catalog-dialog').forEach(d => d.close()); }
  };
  document.querySelectorAll('[data-go-traffic]').forEach(b => b.addEventListener('click', () => switchView('traffic')));
  document.querySelectorAll('[data-close-dialog]').forEach(b => b.addEventListener('click', () => $(b.dataset.closeDialog).close()));
  $('mcsm-sync').addEventListener('click', async event => {
    const b = event.currentTarget; b.disabled = true; b.textContent = '正在同步…';
    try { const r = await api('/api/admin/mcsm/sync', {method:'POST',timeout:60000}); toast(r.connected ? `已同步 ${r.instances} 个 Minecraft 实例` : r.error); await loadServers(); }
    catch(e) { toast(e.message); } finally { b.disabled = false; b.textContent = '同步 MCSM'; }
  });

  async function editServer(index) {
    const s = servers[index];
    if (!s) return;
    try {
      packs = (await api('/api/admin/packs')).items;
      const cfg = s.managed ? s.config : {daemon_id:s.daemon_id,instance_id:s.instance_id,name:s.name,description:'',host:'',port:s.probe_port,probe_host:s.probe_host,probe_port:s.probe_port,minecraft:'',loader:'',version_id:null,order:0,published:false,maintenance:false,group_ids:[]};
      const fields = {'server-daemon':'daemon_id','server-instance':'instance_id','server-name':'name','server-description':'description','server-host':'host','server-port':'port','server-probe-host':'probe_host','server-probe-port':'probe_port','server-mc':'minecraft','server-loader':'loader','server-order':'order'};
      $('server-form').reset();
      for (const [id, field] of Object.entries(fields)) $(id).value = cfg[field];
      $('server-edit-id').value = s.id || '';
      $('server-source').textContent = `${s.node || 'MCSM'} / ${cfg.instance_id}`;
      $('server-published').checked = cfg.published; $('server-maintenance').checked = cfg.maintenance;
      $('server-groups').value = cfg.group_ids.join('\n');
      $('server-pack').replaceChildren(new Option('暂无相关实例（不关联）', ''));
      for (const p of packs) for (const v of p.versions) if (!v.archived_at) $('server-pack').add(new Option(`${p.name} / ${v.version} / ${v.minecraft}`, v.id));
      $('server-pack').value = cfg.version_id || '';
      catalogError('server-form-error', '');
      $('server-dialog').showModal();
    } catch(e) { toast(e.message); }
  }
  $('server-list').addEventListener('click', e => { const b = e.target.closest('[data-edit-server]'); if (b) editServer(Number(b.dataset.editServer)); });
  $('server-form').addEventListener('submit', async e => {
    e.preventDefault(); const b = $('server-save'); b.disabled = true;
    const body = {daemon_id:$('server-daemon').value,instance_id:$('server-instance').value,name:$('server-name').value.trim(),description:$('server-description').value.trim(),host:$('server-host').value.trim(),port:Number($('server-port').value),probe_host:$('server-probe-host').value.trim(),probe_port:Number($('server-probe-port').value),minecraft:$('server-mc').value.trim(),loader:$('server-loader').value.trim(),version_id:$('server-pack').value || null,order:Number($('server-order').value),published:$('server-published').checked,maintenance:$('server-maintenance').checked,group_ids:ids($('server-groups').value)};
    try {
      if (body.group_ids.some(v => !/^\d{5,20}$/.test(v))) throw new Error('可见 QQ 群应填写 5–20 位数字');
      const id = $('server-edit-id').value;
      await api('/api/admin/servers' + (id ? '/' + id : ''), {method:id ? 'PUT' : 'POST',body:JSON.stringify(body)});
      $('server-dialog').close(); toast(body.published ? '服务器已保存并发布' : '服务器配置已保存'); await loadServers();
    } catch(error) { catalogError('server-form-error', error.message); } finally { b.disabled = false; }
  });
  $('server-pack').addEventListener('change', () => {
    const version = packs.flatMap(p => p.versions).find(v => v.id === $('server-pack').value);
    if (!version) return;
    if (!$('server-mc').value) $('server-mc').value = version.minecraft;
    if (!$('server-loader').value) $('server-loader').value = version.loader;
  });
  $('traffic-form').addEventListener('input', () => { trafficDirty = true; $('traffic-dirty').hidden = false; });
  $('traffic-form').addEventListener('submit', async e => {
    e.preventDefault(); const b = e.submitter; b.disabled = true;
    try {
      const body = Object.fromEntries(Object.entries(limitsMap).map(([id, field]) => [field, Number($(id).value)]));
      await api('/api/admin/traffic', {method:'PUT',body:JSON.stringify(body)});
      trafficDirty = false; $('traffic-dirty').hidden = true; toast('限速已保存，正在传输的任务也会应用新上限'); await loadTraffic();
    } catch(error) { toast(error.message); } finally { b.disabled = false; }
  });
  $('mcsm-form').addEventListener('input', () => { connectionDirty = true; });
  $('mcsm-form').addEventListener('submit', async e => {
    e.preventDefault(); const b = e.submitter; b.disabled = true; b.textContent = '正在连接…';
    try {
      const r = await api('/api/admin/mcsm', {method:'PUT',timeout:60000,body:JSON.stringify({base_url:$('mcsm-url').value.trim(),api_key:$('mcsm-key').value.trim()})});
      $('mcsm-key').value = ''; connectionDirty = false;
      toast(r.connected ? `已连接，发现 ${r.instances} 个 Minecraft 实例` : r.error); await loadTraffic();
    } catch(error) { catalogError('mcsm-connection-error', error.message); } finally { b.disabled = false; b.textContent = '保存并连接'; }
  });

  async function fingerprint(file) {
    const first = await file.slice(0,65536).arrayBuffer(), last = await file.slice(Math.max(0,file.size-65536)).arrayBuffer();
    const size = new TextEncoder().encode(String(file.size));
    const data = new Uint8Array(first.byteLength + last.byteLength + size.byteLength);
    data.set(new Uint8Array(first)); data.set(new Uint8Array(last), first.byteLength); data.set(size, first.byteLength + last.byteLength);
    return [...new Uint8Array(await crypto.subtle.digest('SHA-256', data))].map(v => v.toString(16).padStart(2,'0')).join('');
  }

  function uploadProgress(state, tone = '') {
    if (!uploadTask) return;
    $('upload-form').hidden = true; $('upload-running').hidden = false;
    $('upload-current-name').textContent = uploadTask.file.name;
    $('upload-progress').max = uploadTask.file.size; $('upload-progress').value = uploadTask.offset;
    $('upload-percent').textContent = Math.min(100, uploadTask.offset / uploadTask.file.size * 100).toFixed(1) + '%';
    $('upload-counter').textContent = `${bytes(uploadTask.offset)} / ${bytes(uploadTask.file.size)}`;
    $('upload-state').textContent = state; $('upload-state').className = tone;
  }

  async function runUpload() {
    if (!uploadTask || uploadLoop || loggedOut) return;
    const task = uploadTask;
    uploadLoop = true; pauseUpload = false;
    $('upload-pause').hidden = false; $('upload-pause').disabled = false; $('upload-pause').textContent = '暂停';
    $('upload-continue').hidden = true;
    try {
      const current = await api('/api/admin/uploads/' + task.id);
      if (current.status === 'complete') { task.done = true; task.offset = task.file.size; uploadProgress('已入库', 'success'); return; }
      task.offset = current.offset;
      while (task.offset < task.file.size && !pauseUpload && !loggedOut) {
        uploadProgress('正在上传');
        const part = task.file.slice(task.offset, Math.min(task.offset + 1048576, task.file.size));
        try {
          const r = await api(`/api/admin/uploads/${task.id}/chunk?offset=${task.offset}`, {method:'PUT',body:part,headers:{'Content-Type':'application/octet-stream'},timeout:90000});
          task.offset = r.offset;
        } catch(error) {
          // The server may have accepted a chunk before a connection was lost; resume from its offset.
          try { task.offset = (await api('/api/admin/uploads/' + task.id)).offset; } catch { /* Keep the last known offset. */ }
          throw error;
        }
      }
      if (pauseUpload || loggedOut) { uploadProgress('已暂停，可稍后续传'); return; }
      $('upload-pause').hidden = true;
      uploadProgress('正在校验整合包并计算 SHA-256…');
      const result = await api('/api/admin/uploads/' + task.id + '/complete', {method:'POST',timeout:180000});
      task.done = true;
      uploadProgress('已入库，可前往服务器配置关联', 'success');
      $('upload-counter').textContent = `SHA-256 ${result.sha256}`;
      $('upload-continue').hidden = true;
      toast('整合包已入库');
      if (view === 'packs') await loadPacks();
    } catch(error) {
      try { const row = await api('/api/admin/uploads/' + task.id); task.done = ['failed','cancelled','expired'].includes(row.status); } catch { /* Retain resumable state if offline. */ }
      uploadProgress(error.message + (task.done ? '。请关闭后重新上传。' : '。可暂停后续传。'), 'error');
    }
    finally {
      uploadLoop = false;
      $('upload-pause').hidden = true;
      $('upload-continue').hidden = !!task.done || loggedOut;
    }
  }

  async function openUpload(packId = '') {
    if (uploadTask && !uploadTask.done) { $('upload-dialog').showModal(); return; }
    uploadTask = null;
    try {
      packs = (await api('/api/admin/packs')).items;
      $('upload-form').reset(); $('upload-form').hidden = false; $('upload-running').hidden = true;
      $('upload-pack').replaceChildren(new Option('新建整合包', ''));
      for (const p of packs) $('upload-pack').add(new Option(p.name, p.id));
      $('upload-pack').value = packId;
      changeUploadPack();
      $('upload-file-name').textContent = '支持断点续传，不覆盖已有版本';
      catalogError('upload-error', '');
      $('upload-dialog').showModal();
    } catch(error) { toast(error.message); }
  }
  function changeUploadPack() {
    const p = packs.find(p => p.id === $('upload-pack').value);
    $('upload-name').disabled = !!p;
    if (p) $('upload-name').value = p.name;
    else $('upload-name').value = '';
  }
  $('upload-pack').addEventListener('change', changeUploadPack);
  $('upload-file').addEventListener('change', () => {
    const f = $('upload-file').files[0]; if (!f) return;
    $('upload-file-name').textContent = f.name + ' / ' + bytes(f.size);
    if (!$('upload-name').value) $('upload-name').value = f.name.replace(/\.(zip|mrpack)$/i,'').slice(0,80);
  });
  $('open-upload').addEventListener('click', () => openUpload());
  document.querySelectorAll('[data-open-upload]').forEach(b => b.addEventListener('click', () => openUpload()));
  $('upload-form').addEventListener('submit', async e => {
    e.preventDefault(); const b = $('upload-submit'), file = $('upload-file').files[0];
    if (!file || uploadLoop) return;
    b.disabled = true; catalogError('upload-error', '');
    try {
      if (!/\.(zip|mrpack)$/i.test(file.name)) throw new Error('请选择 ZIP 或 MRPACK 整合包');
      const r = await api('/api/admin/uploads', {method:'POST',body:JSON.stringify({pack_id:$('upload-pack').value || null,name:$('upload-name').value.trim(),version:$('upload-version').value.trim(),filename:file.name,size:file.size,fingerprint:await fingerprint(file)})});
      uploadTask = {id:r.id,file,offset:r.offset,done:false};
      await runUpload();
    } catch(error) { catalogError('upload-error', error.message); } finally { b.disabled = false; }
  });
  $('upload-pause').addEventListener('click', () => { pauseUpload = true; $('upload-pause').disabled = true; $('upload-pause').textContent = '正在暂停…'; });
  $('upload-continue').addEventListener('click', runUpload);
  $('upload-dismiss').addEventListener('click', () => $('upload-dialog').close());
  $('upload-dialog').addEventListener('close', () => { if (uploadTask?.done) uploadTask = null; });
  $('resume-file').addEventListener('change', async () => {
    const file = $('resume-file').files[0], id = resumeId; $('resume-file').value = '';
    if (!file || !id) return;
    try {
      if (uploadLoop) throw new Error('已有上传任务正在进行，请先暂停');
      const row = await api('/api/admin/uploads/' + id);
      if (row.size !== file.size || row.fingerprint !== await fingerprint(file)) throw new Error('请选择原始文件，文件大小或指纹不一致');
      uploadTask = {id,file,offset:row.offset,done:false};
      $('upload-dialog').showModal(); await runUpload();
    } catch(error) { toast(error.message); }
  });
  $('pending-upload-list').addEventListener('click', async e => {
    const resume = e.target.closest('[data-resume-upload]'), cancel = e.target.closest('[data-cancel-upload]');
    if (resume) {
      if (uploadLoop) { toast('请先暂停当前上传任务'); return; }
      resumeId = resume.dataset.resumeUpload; $('resume-file').click();
    }
    if (cancel) {
      cancel.disabled = true;
      try {
        await api('/api/admin/uploads/' + cancel.dataset.cancelUpload, {method:'DELETE'});
        if (uploadTask?.id === cancel.dataset.cancelUpload) uploadTask = null;
        toast('上传已取消'); await loadPacks();
      } catch(error) { toast(error.message); cancel.disabled = false; }
    }
  });
  $('pack-list').addEventListener('click', async e => {
    const add = e.target.closest('[data-add-version]'), action = e.target.closest('[data-pack-action]'), copy = e.target.closest('[data-copy-hash]');
    if (add) openUpload(add.dataset.addVersion);
    if (copy) { try { await navigator.clipboard.writeText(copy.dataset.copyHash); toast('完整 SHA-256 已复制'); } catch { toast('复制失败，请通过文件接口查看校验值'); } }
    if (action) {
      action.disabled = true;
      try {
        await api(`/api/admin/packs/versions/${action.dataset.version}/${action.dataset.packAction}`, {method:'POST'});
        toast(action.dataset.packAction === 'archive' ? '版本已归档，可随时恢复' : '版本已恢复'); await loadPacks();
      } catch(error) { toast(error.message); action.disabled = false; }
    }
  });
})();
