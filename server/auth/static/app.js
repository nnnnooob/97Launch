'use strict';
const $ = id => document.getElementById(id);
const labels = {
  servers: ['服务器', '服务器', '从 MCSManager 同步运行状态，管理玩家可见的服务器与关联整合包。'],
  packs: ['整合包', '文件', '集中管理客户端整合包与版本，上传后即可关联服务器。'],
  traffic: ['带宽与连接', '连接', '设置文件传输上限，为 Minecraft 服务器保留带宽。'],
  devices: ['已授权设备', '设备', '查看已配对的启动器。撤销后，设备下次联网校验将无法通过。'],
  codes: ['配对记录', '配对', '查看谁申请了配对 Token，以及是否已被设备兑换。'],
  audit: ['操作记录', '记录', '配对、权限修改与授权撤销，都记录在这里。'],
  settings: ['接入设置', '设置', '配置群配对权限，将 NoneBot 与启动器连接起来。']
};
const events = {code_issued:'申请配对 Token', admin_code_issued:'管理员签发 Token', device_paired:'设备完成配对', admin_login:'进入管理页',
  policy_updated:'更新配对权限', device_revoked:'撤销设备授权', code_revoked:'作废配对 Token',
  code_delivery_failed:'私聊发送失败，作废 Token', device_logged_out:'设备主动退出'};
Object.assign(events, {traffic_updated:'更新传输限速', mcsm_updated:'更新 MCSM 连接', server_updated:'更新服务器',
  pack_upload_started:'开始上传整合包', pack_upload_cancelled:'取消上传', pack_uploaded:'整合包已入库',
  pack_archived:'归档整合包版本', pack_restored:'恢复整合包版本', pack_renamed:'重命名整合包', pack_download_requested:'获取相关实例'});
const statusNames = {active:'已授权', revoked:'已撤销', pending:'待兑换', used:'已兑换', expired:'已过期'};
let view = 'devices', overview, toastTimer, refreshTimer, loading = false, policyDirty = false;
let issueTimer, issueBusy = false, issueGeneration = 0;
const pages = {devices:0, codes:0, audit:0};
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const date = value => value ? new Date(value * 1000).toLocaleString('zh-CN', {hour12:false}) : '—';
const badge = status => `<span class="badge ${status}">${statusNames[status]}</span>`;
const secondary = value => `<span class="secondary-text">${escape(value)}</span>`;
const primary = value => `<span class="primary-text">${escape(value)}</span>`;

function toast(message) {
  $('toast').textContent = message;
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 3200);
}

function loginView() {
  issueGeneration++;
  $('issue-dialog').close();
  clearIssuedToken();
  overview = null;
  $('app').hidden = true; $('login').hidden = false;
  clearInterval(refreshTimer); refreshTimer = null;
  window.CatalogUI?.logout();
}

async function api(path, options = {}) {
  let response;
  const {timeout = 15000, ...init} = options;
  try {
    response = await fetch(path, {credentials:'same-origin', cache:'no-store', ...init,
      headers:{'Content-Type':'application/json', ...init.headers}, signal:init.signal || AbortSignal.timeout(timeout)});
  } catch { throw new Error(location.hostname === '127.0.0.1' && location.port === '19797'
    ? 'SSH 管理通道已断开或连接超时，请重新建立通道后刷新'
    : '无法连接授权服务，请检查网络后刷新'); }
  let result;
  try { result = await response.json(); } catch { throw new Error('服务暂时不可用，请稍后重试'); }
  if (!response.ok) {
    if (response.status === 401) loginView();
    throw new Error(result.error?.message || '请求失败，请稍后重试');
  }
  return result;
}

function showOverview(data) {
  overview = data;
  $('metric-devices').textContent = data.counts.active_devices;
  $('metric-users').textContent = data.counts.paired_users;
  $('metric-pending').textContent = data.counts.pending_codes;
  $('metric-today').textContent = data.counts.today_pairings;
  $('bot-endpoint').textContent = data.bot_endpoint;
  $('public-access-note').hidden = data.public_ready;
  $('public-access-ready').hidden = !data.public_ready;
  if (!policyDirty) {
    $('groups').value = data.policy.allowed_group_ids.join('\n');
    $('users').value = data.policy.allowed_qq_ids.join('\n');
    $('max-devices').value = data.policy.max_devices_per_user;
  }
  $('updated-at').textContent = '更新于 ' + new Date().toLocaleTimeString('zh-CN', {hour12:false});
}

function pagination(name, data) {
  const pager = document.querySelector(`[data-list="${name}"]`);
  pager.querySelector('span').textContent = data.total ? `${data.offset + 1}–${Math.min(data.offset + 50, data.total)} / ${data.total}` : '0 条记录';
  pager.querySelector('[data-page="-1"]').disabled = data.offset === 0;
  pager.querySelector('[data-page="1"]').disabled = data.offset + 50 >= data.total;
}

async function loadList() {
  if (['servers','packs','traffic'].includes(view)) return window.CatalogUI.load(view);
  if (view === 'settings') return;
  const name = view;
  const path = {devices:'devices', codes:'pairing-codes', audit:'audit'}[name];
  const q = name === 'devices' ? '&q=' + encodeURIComponent($('search').value.trim()) : '';
  const data = await api(`/api/admin/${path}?offset=${pages[name]}${q}`);
  if (name === 'devices') {
    $('device-total').textContent = `${data.total} 台`;
    $('devices-body').innerHTML = data.items.map(d => `<tr><td><div class="device-name"><span class="device-mark"><svg><use href="#device-icon"/></svg></span><div>${primary(d.name)}${secondary(`${d.platform} / ${d.app_version}`)}${secondary('配对于 ' + date(d.created_at))}</div></div></td><td>${primary(d.nickname || d.qq_id)}${secondary(d.nickname ? `QQ ${d.qq_id}` : '')}${secondary('群 ' + d.group_id)}</td><td>${primary(date(d.last_seen_at))}${secondary(d.last_ip)}</td><td>${badge(d.revoked_at ? 'revoked' : 'active')}</td><td>${d.revoked_at ? '' : `<button class="revoke" data-revoke="device" data-id="${d.id}" data-name="${escape(d.name)}">撤销授权</button>`}</td></tr>`).join('');
  } else if (name === 'codes') {
    $('codes-body').innerHTML = data.items.map(c => `<tr><td>${primary(c.code_hint)}${secondary(`${c.nickname || 'QQ'} · ${c.qq_id}`)}</td><td>${escape(c.group_id)}</td><td>${primary(date(c.created_at))}${secondary(c.used_at ? '兑换于 ' + date(c.used_at) : '到期于 ' + date(c.expires_at))}</td><td>${badge(c.status)}</td><td>${c.status === 'pending' ? `<button class="revoke" data-revoke="code" data-id="${c.id}" data-name="${escape(c.code_hint)}">作废</button>` : ''}</td></tr>`).join('');
  } else {
    $('audit-body').innerHTML = data.items.map(a => `<tr><td>${primary(events[a.event] || a.event)}</td><td>${primary(a.subject || '管理员')}${secondary(a.detail)}</td><td>${escape(a.ip)}</td><td>${escape(date(a.created_at))}</td></tr>`).join('');
  }
  $(`${name}-empty`).hidden = data.items.length > 0;
  if (name === 'devices' && $('search').value.trim() && !data.items.length) {
    $('devices-empty').querySelector('h3').textContent = '没有找到匹配的设备';
    $('devices-empty').querySelector('p').textContent = '试试其他 QQ、昵称或设备名称。';
    $('devices-empty').querySelector('button').hidden = true;
  } else if (name === 'devices') {
    $('devices-empty').querySelector('h3').textContent = '还没有配对设备';
    $('devices-empty').querySelector('p').textContent = '先在接入设置中允许群配对，机器人接入后，设备会出现在这里。';
    $('devices-empty').querySelector('button').hidden = false;
  }
  pagination(name, data);
}

async function refresh() {
  if (loading) return;
  loading = true; $('refresh').disabled = true;
  try {
    showOverview(await api('/api/admin/overview'));
    await loadList(); $('load-error').hidden = true;
  } catch (e) { $('load-error').textContent = e.message; $('load-error').hidden = false; }
  finally { loading = false; $('refresh').disabled = false; }
}

async function enter() {
  showOverview(await api('/api/admin/overview'));
  $('login').hidden = true; $('app').hidden = false;
  const requested = location.hash.slice(1);
  if (labels[requested] && requested !== view) await switchView(requested);
  else await loadList();
  if (!refreshTimer) refreshTimer = setInterval(() => { if (!document.hidden && !document.querySelector('dialog[open]')) refresh(); }, 30000);
}

async function switchView(name) {
  if (name === view) return;
  view = name;
  history.replaceState(null, '', '#' + name);
  document.querySelector('.metrics').hidden = ['servers','packs','traffic'].includes(name);
  document.querySelectorAll('.view').forEach(el => { el.hidden = el.id !== name; el.classList.remove('switching'); });
  $(name).classList.add('switching');
  document.querySelectorAll('.nav').forEach(el => { el.classList.toggle('active', el.dataset.view === name); el.setAttribute('aria-current', el.dataset.view === name ? 'page' : 'false'); });
  [$('page-title').textContent, $('breadcrumb').textContent, $('page-description').textContent] = labels[name];
  try { await loadList(); $('load-error').hidden = true; }
  catch (e) { $('load-error').textContent = e.message; $('load-error').hidden = false; }
}

$('login-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = event.submitter; button.disabled = true; $('login-error').textContent = '';
  try { await api('/api/admin/login', {method:'POST', body:JSON.stringify({key:$('admin-key').value.trim()})}); $('admin-key').value = ''; await enter(); }
  catch(e) { $('login-error').textContent = e.message; }
  finally { button.disabled = false; }
});
$('logout').addEventListener('click', async () => { try { await api('/api/admin/logout', {method:'POST'}); loginView(); } catch(e) { toast(e.message); } });
document.querySelectorAll('.nav').forEach(button => button.addEventListener('click', () => switchView(button.dataset.view)));
document.querySelectorAll('[data-go-settings]').forEach(button => button.addEventListener('click', () => switchView('settings')));
$('refresh').addEventListener('click', refresh);
$('search-form').addEventListener('submit', async event => { event.preventDefault(); pages.devices = 0; await refresh(); });
document.querySelectorAll('.pagination').forEach(pager => pager.addEventListener('click', async event => {
  const button = event.target.closest('[data-page]'); if (!button || loading) return;
  pages[pager.dataset.list] = Math.max(0, pages[pager.dataset.list] + Number(button.dataset.page) * 50);
  await refresh();
}));
$('policy-form').addEventListener('input', () => { policyDirty = true; });
$('policy-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = event.submitter; button.disabled = true;
  const ids = id => [...new Set($(id).value.split(/[\s,，;；]+/).filter(Boolean))];
  try {
    const groups = ids('groups'), users = ids('users');
    if ([...groups,...users].some(id => !/^\d{5,20}$/.test(id))) throw new Error('群号和 QQ 号应为 5–20 位数字');
    if (groups.length > 100 || users.length > 500) throw new Error('群最多 100 个，QQ 最多 500 个');
    await api('/api/admin/policy', {method:'PUT',body:JSON.stringify({allowed_group_ids:groups,allowed_qq_ids:users,max_devices_per_user:Number($('max-devices').value)})});
    policyDirty = false; toast('配对权限已保存'); await refresh();
  } catch(e) { toast(e.message); } finally { button.disabled = false; }
});
$('copy-endpoint').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText(overview.bot_endpoint); toast('接口地址已复制'); }
  catch { toast('无法访问剪贴板，请手动复制接口地址'); }
});

function clearIssuedToken() {
  clearTimeout(issueTimer);
  $('issued-token').value = '';
  $('issue-result').hidden = true;
  $('issued-expiry').textContent = '';
}

function lockIssueForm(locked) {
  issueBusy = locked;
  for (const field of $('issue-form').elements) field.disabled = locked;
  if (!locked && !overview?.policy.allowed_group_ids.length) {
    $('issue-group').disabled = true;
    $('issue-submit').disabled = true;
  }
  $('close-issue').disabled = locked;
  $('issue-submit').textContent = locked ? '正在生成…' : '生成 Token';
}

$('open-issue').addEventListener('click', () => {
  if (!overview) return;
  issueGeneration++;
  clearIssuedToken();
  $('issue-form').reset();
  $('issue-error').hidden = true;
  const groups = overview.policy.allowed_group_ids;
  $('issue-group').replaceChildren(...groups.map(group => new Option(group, group)));
  if (!groups.length) $('issue-group').add(new Option('尚未开放 QQ 群', ''));
  $('issue-no-groups').hidden = groups.length > 0;
  lockIssueForm(false);
  $('issue-dialog').showModal();
  $('issue-qq').focus();
});
$('close-issue').addEventListener('click', () => $('issue-dialog').close());
$('issue-dialog').addEventListener('cancel', event => { if (issueBusy) event.preventDefault(); });
$('issue-dialog').addEventListener('close', () => { issueGeneration++; clearIssuedToken(); $('issue-form').reset(); });
$('issue-settings').addEventListener('click', () => { $('issue-dialog').close(); switchView('settings'); });
$('issue-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (issueBusy || $('issue-submit').disabled) return;
  const generation = ++issueGeneration;
  const body = {qq_id:$('issue-qq').value.trim(), group_id:$('issue-group').value, nickname:$('issue-nickname').value.trim()};
  clearIssuedToken();
  $('issue-error').hidden = true;
  lockIssueForm(true);
  let issued = false;
  try {
    const data = await api('/api/admin/pairing-codes', {method:'POST', body:JSON.stringify(body)});
    if (generation !== issueGeneration || !$('issue-dialog').open) return;
    if (!/^97p_[A-Za-z0-9_-]{43}$/.test(data.code) || data.expires_in !== 300) throw new Error('服务返回的 Token 格式异常，请查看配对记录');
    issued = true;
    $('issued-token').value = data.code;
    $('issued-expiry').textContent = '有效至 ' + date(data.expires_at);
    $('copy-token').disabled = false;
    $('issue-result').hidden = false;
    $('issued-token').focus(); $('issued-token').select();
    issueTimer = setTimeout(() => {
      $('issued-token').value = '';
      $('issued-expiry').textContent = 'Token 已过期，请关闭后重新生成';
      $('copy-token').disabled = true;
    }, data.expires_in * 1000);
    pages.codes = 0;
    await refresh();
  } catch(e) {
    if (generation === issueGeneration && $('issue-dialog').open) {
      $('issue-error').textContent = e.message; $('issue-error').hidden = false;
    }
  } finally {
    if (generation === issueGeneration) {
      lockIssueForm(false);
      if (issued) for (const field of $('issue-form').elements) field.disabled = true;
    }
  }
});
$('copy-token').addEventListener('click', async () => {
  const token = $('issued-token').value;
  if (!token) return;
  try { await navigator.clipboard.writeText(token); toast('配对 Token 已复制'); }
  catch { $('issued-token').focus(); $('issued-token').select(); toast('请手动复制已选中的 Token'); }
});
document.querySelector('main').addEventListener('click', async event => {
  const button = event.target.closest('[data-revoke]'); if (!button) return;
  const device = button.dataset.revoke === 'device';
  $('confirm-title').textContent = device ? '撤销设备授权？' : '作废配对 Token？';
  $('confirm-message').textContent = device ? `「${button.dataset.name}」将在下次联网校验时失去授权。恢复使用需要重新获取配对 Token。` : `配对 Token ${button.dataset.name} 将立即失效。`;
  $('confirm-dialog').returnValue = 'cancel'; $('confirm-dialog').showModal();
  $('confirm-dialog').addEventListener('close', async () => {
    if ($('confirm-dialog').returnValue !== 'confirm') return;
    button.disabled = true;
    try { await api(`/api/admin/${device ? 'devices' : 'pairing-codes'}/${button.dataset.id}/revoke`, {method:'POST'}); toast(device ? '设备授权已撤销' : '配对 Token 已作废'); await refresh(); }
    catch(e) { toast(e.message); button.disabled = false; }
  }, {once:true});
});
document.addEventListener('DOMContentLoaded', () => {
  enter().catch(e => { loginView(); if (e.message !== '请重新输入管理密钥') $('login-error').textContent = e.message; });
}, {once:true});
