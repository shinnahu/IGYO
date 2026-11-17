// IGYO — Settings: what every section is built from (talking to the app, rows and cards, dialogs,
// menus, notices, and which section the address asks for).
// The page asks the app for things with requests to /api/* (see SettingsPage.cs). Names of profiles,
// bookmarks and themes come from files and websites, so they only ever go into the page as text.
(() => {
  'use strict';

  const IGYO = window.IGYO || {};
  const tr = IGYO.tr || ((text) => text);

  // The sections, in the order of the rail. Each one's contents are added with ST.section(id, …).
  const SECTIONS = [
    { id: 'appearance', name: tr('Appearance'), icon: 'palette' },
    { id: 'toolbar', name: tr('Toolbar and tools'), icon: 'window' },
    { id: 'extensions', name: tr('Extensions'), icon: 'plug' },
    { id: 'search', name: tr('Search and new tabs'), icon: 'search' },
    { id: 'default', name: tr('Default browser'), icon: 'globe' },
    { id: 'downloads', name: tr('Downloads and screenshots'), icon: 'download' },
    { id: 'adblock', name: tr('Ad block'), icon: 'shield' },
    { id: 'memory', name: tr('Memory saver'), icon: 'leaf' },
    { id: 'profiles', name: tr('Profiles'), icon: 'people' },
    { id: 'privacy', name: tr('Privacy'), icon: 'lock' },
    { id: 'bookmarks', name: tr('Bookmarks'), icon: 'bookmark' },
    { id: 'about', name: tr('About'), icon: 'info' },
  ];
  // Other names a section is asked for by (the menu says "Themes"; the old dialog had longer names).
  const ALIASES = {
    themes: 'appearance', 'search and new tabs': 'search', 'search and startup': 'search',
    'downloads and screenshots': 'downloads', 'ad block': 'adblock', 'memory saver': 'memory', 'default browser': 'default',
  };

  // Drawings this page needs that the shared set doesn't have (same 24 x 24 grid, same line style).
  const dot = (x, y) => `<circle class="fill" cx="${x}" cy="${y}" r="1.4"/>`;
  const EXTRA = {
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.600 5.600 7 7M17 17l1.400 1.400M5.600 18.400 7 17M17 7l1.400-1.400"/>',
    upload: '<path d="M12 16V5M7 9l5-5 5 5M5 20h14"/>',
    image: '<path d="M4 5h16v14H4z"/><circle cx="9" cy="10" r="1.600"/><path d="M4 17l5-4 4 3 3-2 4 3"/>',
    grip: dot(9, 7) + dot(15, 7) + dot(9, 12) + dot(15, 12) + dot(9, 17) + dot(15, 17),
    auto: '<circle cx="12" cy="12" r="8"/><path class="fill" d="M12 4a8 8 0 0 1 0 16z"/>',
    'folder-plus': '<path d="M3 6h6l2 2.500h10V19H3z"/><path d="M12 11.500v5M9.500 14h5"/>',
  };

  function icon(name) {
    if (EXTRA[name]) return `<svg class="i" viewBox="0 0 24 24" aria-hidden="true" focusable="false">${EXTRA[name]}</svg>`;
    return IGYO.icon ? IGYO.icon(name) : '';
  }

  // Makes an element. "text" is always plain text; "html" is only ever this page's own drawings.
  function h(tag, props, ...kids) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props || {})) {
      if (value == null || value === false) continue;
      if (key === 'class') node.className = value;
      else if (key === 'text') node.textContent = value;
      else if (key === 'html') node.innerHTML = value;
      else if (key === 'vars') for (const [name, v] of Object.entries(value)) node.style.setProperty(name, v);
      else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
      else if (key in node && key !== 'list') node[key] = value;
      else node.setAttribute(key, value === true ? '' : value);
    }
    for (const kid of kids.flat(Infinity)) {
      if (kid == null || kid === false) continue;
      node.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
    }
    return node;
  }

  const kebab = (key) => key.replace(/([a-z])([A-Z0-9])/g, '$1-$2').toLowerCase();

  const ST = { state: null, kits: null, logo: '/logo.png' };   // the app serves its logo there
  const renderers = new Map();
  let view = { syncers: [], leavers: [] };   // belongs to the section being shown
  let current = { id: '', key: '' };
  let main = null, rail = null, layers = null;

  // ----- talking to the app -----

  async function get(path) {
    const response = await fetch(path, { cache: 'no-store' });
    if (!response.ok) throw new Error(tr('IGYO answered {0}.', response.status));
    return response.json();
  }

  // The live preview goes out several times a second; it changes nothing this page shows.
  const QUIET = new Set(['theme/preview', 'theme/preview-end']);

  // Asks the app to do something. Always answers with an object: { ok }, { error } or { cancelled }.
  async function post(command, body, options) {
    let result;
    try {
      const response = await fetch('/api/' + command, Object.assign({ method: 'POST', cache: 'no-store', body: JSON.stringify(body || {}) }, options));
      const text = response.ok ? await response.text() : '';
      result = response.ok ? (text ? JSON.parse(text) : {}) : { error: tr('IGYO refused that ({0}).', response.status) };
    } catch (error) {
      result = { error: tr("IGYO didn't answer.") };
    }
    if (!QUIET.has(command)) refreshSoon();
    return result;
  }

  const api = { get, post, set: (key, value) => post('set', { key, value }) };

  // ----- what is shown follows the app -----
  // A section registers small functions that put the app's state into its controls. They run when the
  // section is drawn and again whenever the state is read anew, so nothing is rebuilt just to change a value.

  function onSync(fn) {
    view.syncers.push(fn);
    if (ST.state) fn(ST.state);
  }
  const onLeave = (fn) => view.leavers.push(fn);

  let refreshTimer = 0;
  function refreshSoon() {
    clearTimeout(refreshTimer);
    refreshTimer = setTimeout(refresh, 60);
  }

  // Also called by the app (window.IGYOSettings.refresh) whenever a setting changes anywhere.
  async function refresh() {
    clearTimeout(refreshTimer);
    let state;
    try { state = await get('/api/state'); } catch { return; }
    ST.state = state;
    for (const fn of [...view.syncers]) {
      try { fn(state); } catch (error) { console.error(error); }
    }
  }

  // ----- notices, dialogs, menus -----

  let noticeTimer = 0;
  function notice(text, kind) {
    for (const old of layers.querySelectorAll('.st-notice')) old.remove();
    const box = h('div', { class: 'st-notice' + (kind === 'error' ? ' is-error' : ''), role: kind === 'error' ? 'alert' : 'status' },
      h('span', { text }),
      h('button', { type: 'button', class: 't-iconbtn st-notice-close', title: tr('Dismiss'), 'aria-label': tr('Dismiss'), html: icon('close'), onclick: () => box.remove() }));
    layers.append(box);
    clearTimeout(noticeTimer);
    noticeTimer = setTimeout(() => box.remove(), kind === 'error' ? 8000 : 3600);
  }

  // What an answer from the app means for the person: an error is shown; true when it went well.
  function settled(result, done) {
    if (!result || result.cancelled) return false;
    if (result.error) { notice(result.error, 'error'); return false; }
    if (done) notice(done);
    return true;
  }

  function trapKeys(layer, box, cancel) {
    layer.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); cancel(); return; }
      if (event.key !== 'Tab') return;
      const stops = [...box.querySelectorAll('input, select, textarea, button')].filter((n) => !n.disabled && n.offsetParent);
      if (!stops.length) return;
      event.preventDefault();
      const at = stops.indexOf(document.activeElement);
      stops[(at + (event.shiftKey ? -1 : 1) + stops.length) % stops.length].focus();
    });
  }

  // A question or a small form. Resolves with the fields' values ({} when there are none), or null when dismissed.
  // spec: { title, message, fields: [{ key, label, value, placeholder, required }], ok, cancel, danger, submit(values) }
  // submit may return (a promise of) a problem to show; the dialog then stays open. body(close): something of the
  // dialog's own under the message, which may close it with a result; noOk: the dialog has only its Cancel button.
  function dialog(spec) {
    closeMenu();
    return new Promise((resolve) => {
      const previous = document.activeElement;
      const inputs = {};
      const problem = h('p', { class: 'st-dialog-problem', role: 'alert', hidden: true });
      // Only the shared kinds of button are used (a kit knows how to draw those in every state).
      const ok = h('button', { type: 'submit', class: 't-btn ' + (spec.danger ? 'is-danger' : 'is-primary'), text: spec.ok || tr('OK') });
      const form = h('form', { class: 't-dialog st-dialog', role: 'dialog', 'aria-modal': 'true', 'aria-label': spec.title, noValidate: true },
        h('h2', { text: spec.title }),
        spec.message ? h('p', { text: spec.message }) : null,
        spec.fields && spec.fields.length ? h('div', { class: 't-dialog-body' }, spec.fields.map((f, i) => {
          const id = 'st-field-' + i + '-' + f.key;
          inputs[f.key] = h('input', { type: 'text', class: 't-input', id, value: f.value || '', placeholder: f.placeholder || '', spellcheck: false, maxLength: f.max || 2048,
            oninput: (event) => event.target.removeAttribute('aria-invalid') });
          // A field can have something before its box, in the same row (lead, given the box), and something under it
          // (after). The label names the box itself either way.
          const box = f.lead ? h('div', { class: 't-field-lead' }, f.lead(inputs[f.key]), inputs[f.key]) : inputs[f.key];
          return [h('label', { class: 't-field', htmlFor: id }, h('span', { text: f.label }), box), f.after ? f.after() : null];
        })) : null,
        spec.body ? h('div', { class: 't-dialog-body' }, spec.body((result) => close(result))) : null,
        problem,
        h('div', { class: 't-dialog-actions' }, h('button', { type: 'button', class: 't-btn', text: spec.cancel || tr('Cancel'), onclick: () => close(null) }), spec.noOk ? null : ok));
      const layer = h('div', { class: 'st-layer' }, h('div', { class: 'st-scrim', onpointerdown: (event) => { event.preventDefault(); close(null); } }), form);
      let busy = false;

      function close(result) {
        layer.remove();
        if (previous && previous.isConnected) previous.focus({ preventScroll: true });
        resolve(result);
      }
      form.addEventListener('submit', async (event) => {
        event.preventDefault();
        if (busy) return;
        const values = {};
        for (const [key, input] of Object.entries(inputs)) values[key] = input.value.trim();
        const missing = (spec.fields || []).find((f) => f.required && !values[f.key]);
        if (missing) { inputs[missing.key].setAttribute('aria-invalid', 'true'); inputs[missing.key].focus(); return; }   // the box turns red until something is typed
        if (spec.submit) {
          busy = ok.disabled = true;
          let trouble;
          try { trouble = await spec.submit(values); } catch (error) { trouble = error.message; }
          busy = ok.disabled = false;
          if (trouble) { problem.textContent = trouble; problem.hidden = false; return; }
        }
        close(values);
      });
      trapKeys(layer, form, () => close(null));
      layers.append(layer);
      const first = Object.values(inputs)[0];
      (first || (spec.noOk && form.querySelector('.t-dialog-body button, .t-dialog-body input')) || ok).focus({ preventScroll: true });
      if (first) first.select();
    });
  }

  const confirm = (title, message, ok, danger) => dialog({ title, message, ok, danger }).then(Boolean);

  // A small menu at a button. items: { label, icon, run, disabled, danger } or { sep: true } (falsy items are skipped).
  let menuNow = null;

  function closeMenu(refocus) {
    const m = menuNow;
    if (!m) return;
    menuNow = null;
    m.panel.remove();
    m.anchor.removeAttribute('aria-expanded');
    document.removeEventListener('pointerdown', m.outside, true);
    window.removeEventListener('blur', m.away);
    window.removeEventListener('resize', m.away);
    if (refocus && m.anchor.isConnected) m.anchor.focus({ preventScroll: true });
  }

  function menu(anchor, items) {
    if (menuNow && menuNow.anchor === anchor) { closeMenu(true); return; }
    closeMenu();
    const panel = h('div', { class: 't-menu st-menu', role: 'menu', tabIndex: -1 });
    const lead = items.some((item) => item && item.icon);
    for (const item of items) {
      if (!item) continue;
      if (item.sep) { panel.append(h('div', { class: 't-menu-sep' })); continue; }
      panel.append(h('button', {
        type: 'button', class: 't-menu-item' + (item.danger ? ' st-menu-danger' : ''), role: 'menuitem', tabIndex: -1,
        'aria-disabled': item.disabled ? 'true' : null,
        onclick: () => { closeMenu(true); item.run(); },
      }, lead ? h('span', { class: 't-menu-icon', html: item.icon ? icon(item.icon) : '' }) : null, h('span', { class: 't-menu-text', text: item.label })));
    }
    layers.append(panel);
    const r = anchor.getBoundingClientRect();
    const w = panel.offsetWidth, ht = panel.offsetHeight;
    const x = Math.max(8, Math.min(window.innerWidth - w - 8, r.right - w));
    const below = r.bottom + 6 + ht <= window.innerHeight - 8 || r.top - 6 - ht < 8;
    panel.style.left = x + 'px';
    panel.style.top = (below ? r.bottom + 6 : r.top - 6 - ht) + 'px';
    panel.style.setProperty('--from-y', below ? '0%' : '100%');

    const rows = () => [...panel.querySelectorAll('.t-menu-item')].filter((n) => n.getAttribute('aria-disabled') !== 'true');
    // The line under the mouse or the keyboard is marked "is-active", the way the shared menu style (and every kit) expects.
    const mark = (row) => { for (const n of panel.querySelectorAll('.t-menu-item.is-active')) if (n !== row) n.classList.remove('is-active'); if (row) row.classList.add('is-active'); };
    panel.addEventListener('pointerover', (event) => { const row = event.target.closest('.t-menu-item'); if (row && row.getAttribute('aria-disabled') !== 'true') mark(row); });
    panel.addEventListener('pointerleave', () => mark(null));
    panel.addEventListener('focusin', (event) => mark(event.target.closest('.t-menu-item')));
    panel.addEventListener('keydown', (event) => {
      const list = rows();
      const at = list.indexOf(document.activeElement);
      if (event.key === 'ArrowDown') { event.preventDefault(); (list[(at + 1) % list.length] || panel).focus(); }
      else if (event.key === 'ArrowUp') { event.preventDefault(); (list[(at - 1 + list.length) % list.length] || panel).focus(); }
      else if (event.key === 'Home') { event.preventDefault(); if (list[0]) list[0].focus(); }
      else if (event.key === 'End') { event.preventDefault(); if (list.length) list[list.length - 1].focus(); }
      else if (event.key === 'Escape' || event.key === 'Tab') { event.preventDefault(); event.stopPropagation(); closeMenu(true); }
    });
    const m = {
      panel, anchor,
      outside: (event) => { if (!panel.contains(event.target) && !anchor.contains(event.target)) closeMenu(); },
      away: () => closeMenu(),
    };
    document.addEventListener('pointerdown', m.outside, true);
    window.addEventListener('blur', m.away);
    window.addEventListener('resize', m.away);
    anchor.setAttribute('aria-expanded', 'true');
    menuNow = m;
    (rows()[0] || panel).focus({ preventScroll: true });
  }

  // ----- rows and cards -----

  function header(page, title, subtitle) {
    const head = h('header', { class: 'st-head' }, h('h1', { class: 't-title st-h1', text: title }), subtitle ? h('p', { class: 'st-sub', text: subtitle }) : null);
    page.append(head);
    return head;
  }

  // A card of rows, with an optional label above it.
  function group(page, label) {
    if (label) page.append(h('h2', { class: 'st-group-label', text: label }));
    const card = h('section', { class: 't-card st-card' });
    page.append(card);
    return card;
  }

  // One row: a title and a short explanation on the left, its controls on the right, optionally something wider below.
  // desc may be a function of the app's state.
  function row(card, spec) {
    const title = h('div', { class: 'st-row-title', text: spec.title || '' });
    const desc = h('div', { class: 'st-row-desc' });
    const end = spec.end && spec.end.length ? h('div', { class: 'st-row-end' }, spec.end) : null;
    const node = h(spec.tag || 'div', { class: 'st-row' + (spec.strong ? ' is-strong' : '') },
      h('div', { class: 'st-row-line' }, spec.lead || null, h('div', { class: 'st-row-text' }, title, desc), end),
      spec.below ? h('div', { class: 'st-row-below' }, spec.below) : null);
    card.append(node);
    const setDesc = (value) => { desc.textContent = value || ''; desc.hidden = !value; };
    if (typeof spec.desc === 'function') onSync((state) => setDesc(spec.desc(state)));
    else setDesc(spec.desc);
    return { node, title, end, setDesc };
  }

  function button(text, run, options) {
    const o = options || {};
    const node = h('button', {
      type: 'button', title: o.title,
      class: 't-btn' + (o.primary ? ' is-primary' : '') + (o.danger ? ' is-danger' : '') + (o.ghost ? ' is-ghost' : '') + (o.small ? ' is-s' : ''),
      html: o.icon ? icon(o.icon) : null,
    }, h('span', { text }));
    if (run) node.addEventListener('click', () => run(node));
    return node;
  }

  // A switch. With "key" it is one of the app's settings; without, it only holds its own value. The whole row flips it.
  function toggle(card, spec) {
    const input = h('input', { type: 'checkbox', checked: !!spec.value });
    const r = row(card, { title: spec.title, desc: spec.desc, tag: 'label', end: [h('span', { class: 't-switch' }, input, h('i'))] });
    r.node.classList.add('is-toggle');
    input.addEventListener('change', async () => {
      const value = input.checked;
      if (!spec.key) return;
      const result = await api.set(spec.key, value);
      if (result.error) { input.checked = !value; notice(result.error, 'error'); }
    });
    if (spec.key) onSync((state) => { input.checked = !!state.settings[spec.key]; });
    return { input, row: r };
  }

  // A choice from a list. options: [{ label, value }] or a function of the state; get(state) is the current value; set(value) changes it.
  function choice(card, spec) {
    const select = h('select', { class: 't-select st-select', 'aria-label': spec.title });
    const r = row(card, { title: spec.title, desc: spec.desc, end: [select] });
    select.addEventListener('change', async () => {
      const result = await spec.set(JSON.parse(select.value));
      if (result && result.error) notice(result.error, 'error');
    });
    onSync((state) => {
      const value = spec.get(state);
      const options = [...(typeof spec.options === 'function' ? spec.options(state) : spec.options)];
      if (!options.some((o) => o.value === value)) options.push({ label: String(value), value });   // a value set some other way
      const signature = JSON.stringify(options);
      if (select._signature !== signature) {
        select._signature = signature;
        select.replaceChildren(...options.map((o) => h('option', { value: JSON.stringify(o.value), text: o.label })));
      }
      select.value = JSON.stringify(value);
    });
    return { select, row: r };
  }

  // While something slow runs, the button says so and can't be pressed twice.
  async function busy(node, label, work) {
    const text = node.querySelector('span') || node;
    const was = text.textContent;
    node.disabled = true;
    text.textContent = label;
    try { return await work(); }
    finally {
      if (node.isConnected) { node.disabled = false; text.textContent = was; }
    }
  }

  // ----- sections and the address -----

  function parseHash() {
    let raw = location.hash.replace(/^#/, '');
    try { raw = decodeURIComponent(raw); } catch { /* shown as typed */ }
    const parts = raw.toLowerCase().split('/').filter(Boolean);
    const id = ALIASES[parts[0]] || parts[0] || 'appearance';
    if (!SECTIONS.some((s) => s.id === id)) return { id: 'appearance', sub: [] };   // an unknown name shows Appearance
    return { id, sub: parts.slice(1) };
  }

  function leaveView() {
    closeMenu();
    for (const fn of view.leavers) {
      try { fn(); } catch (error) { console.error(error); }
    }
    view = { syncers: [], leavers: [] };
  }

  function show() {
    const { id, sub } = parseHash();
    const key = id + '/' + sub.join('/');
    if (key === current.key) return;
    leaveView();
    current = { id, key };
    for (const link of rail.querySelectorAll('.st-nav-item')) {
      if (link.dataset.section === id) link.setAttribute('aria-current', 'page');
      else link.removeAttribute('aria-current');
    }
    const def = renderers.get(id) || {};
    const page = h('div', { class: 'st-page' + (def.fill ? ' is-fill' : '') });
    main.replaceChildren(page);
    main.scrollTop = 0;
    try {
      if (def.render) def.render(page, sub);
    } catch (error) {
      console.error(error);
      page.append(h('p', { class: 't-muted', text: tr("This section couldn't be shown.") }));
    }
  }

  // Changes the address without drawing the section again (the editor keeps going under its theme's new name).
  function setRoute(hash) {
    history.replaceState(null, '', hash);
    const { id, sub } = parseHash();
    current = { id, key: id + '/' + sub.join('/') };
  }

  function loadScript(src) {
    return new Promise((resolve) => {
      const script = h('script', { src, onload: resolve, onerror: resolve });
      document.head.append(script);
    });
  }

  function buildShell() {
    const app = document.getElementById('st-app');
    app.className = 'st-app';
    rail = h('nav', { class: 'st-rail', 'aria-label': tr('Settings sections') },
      h('div', { class: 'st-rail-head' }, h('i', { class: 'st-mark', 'aria-hidden': 'true' }), h('span', { text: tr('Settings') })),   // the mark by itself, in the text color
      SECTIONS.map((s) => h('a', { class: 'st-nav-item', href: '#' + s.id, title: s.name, 'data-section': s.id, html: icon(s.icon) }, h('span', { text: s.name }))));
    // Up and Down move between sections, as in a list.
    rail.addEventListener('keydown', (event) => {
      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
      const links = [...rail.querySelectorAll('.st-nav-item')];
      const at = links.indexOf(document.activeElement);
      if (at < 0) return;
      event.preventDefault();
      const next = links[Math.max(0, Math.min(links.length - 1, at + (event.key === 'ArrowDown' ? 1 : -1)))];
      next.focus();
      next.click();
    });
    main = h('main', { class: 'st-main', tabIndex: -1 });
    layers = h('div', { class: 'st-layers' });
    app.replaceChildren(rail, main, layers);
  }

  let listening = false;
  async function load() {
    try {
      [ST.state, ST.kits] = await Promise.all([get('/api/state'), get('/api/kits')]);
    } catch (error) {
      main.replaceChildren(h('div', { class: 'st-page' },
        h('h1', { class: 't-title st-h1', text: tr('Settings') }),
        h('p', { class: 'st-sub', text: tr("IGYO didn't answer, so the settings can't be shown. ") + (error.message || '') }),
        button(tr('Try again'), load)));
      return;
    }
    if (!listening) window.addEventListener('hashchange', show);
    listening = true;
    current = { id: '', key: '' };
    show();
  }

  async function start() {
    document.title = tr('Settings');
    // Opened from the dev server while working on the page: a stand-in answers for the app.
    if (location.hostname === 'localhost') await loadScript('dev-api.js');
    buildShell();
    await load();
  }

  Object.assign(ST, {
    h, icon, kebab, api, onSync, onLeave, refresh, refreshSoon, notice, settled, dialog, confirm, menu, closeMenu,
    header, group, row, button, toggle, choice, busy, setRoute, start,
    section: (id, def) => renderers.set(id, def),
    get main() { return main; },
  });
  window.ST = ST;
  window.IGYOSettings = { refresh };
})();
