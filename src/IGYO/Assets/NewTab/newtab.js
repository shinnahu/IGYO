// IGYO — New Tab page.
// Your shortcuts and pictures are kept by IGYO, in this profile, on this PC (see config.js and /api/*).
// The search box only searches: nothing is ever suggested.
(() => {
  'use strict';

  // Without its settings the page could overwrite your shortcuts, so it tries once more, then shows a read-only page.
  if (!window.IGYO_NEWTAB) {
    try {
      if (!sessionStorage.getItem('igyo-retry')) {
        sessionStorage.setItem('igyo-retry', '1');
        location.reload();
        return;
      }
    } catch { /* storage unavailable */ }
  }
  try { sessionStorage.removeItem('igyo-retry'); } catch { /* storage unavailable */ }

  const T = window.IGYO_NEWTAB || { isPrivate: true, unavailable: true };
  const tr = (window.IGYO && window.IGYO.tr) || ((text) => text);
  if (window.IGYO && window.IGYO.translatePage) window.IGYO.translatePage();   // the texts written in index.html itself
  const language = (window.IGYO && window.IGYO.lang) || undefined;   // the clock and date are written the way that language writes them
  const $ = (id) => document.getElementById(id);
  const root = document.documentElement;
  const body = document.body;
  const MAX_SHORTCUTS = 24;
  const MAX_PICTURES = 24;

  const animate = T.anim !== false && !matchMedia('(prefers-reduced-motion: reduce)').matches;
  if (!animate) { root.classList.add('no-anim'); root.dataset.anim = 'off'; }
  // Colors, shapes and fonts come from the browser's theme (/theme.css); nothing about the look is set here.

  const readOnly = !!T.isPrivate;   // private windows show the page but change nothing
  body.classList.toggle('read-only', readOnly);
  $('private-badge').hidden = !readOnly || !!T.unavailable;

  const state = {
    clock: T.clock !== false,
    clockSize: Number.isFinite(T.clockSize) ? T.clockSize : 100,   // percent of the size the look gives the time
    blur: Number.isFinite(T.blur) && T.blur >= 0 ? Math.min(100, T.blur) : -1,   // how out of focus the picture is, 0 to 100; -1: as the look has it
    showShortcuts: T.showShortcuts !== false,
    shortcuts: Array.isArray(T.shortcuts) ? T.shortcuts.slice(0, MAX_SHORTCUTS) : [],
    pictures: Array.isArray(T.pictures) ? T.pictures : [],
    picture: T.picture || '',
    rotate: !!T.rotate,
  };
  let shown = T.shown || '';

  const ICONS = {
    plus: '<svg class="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>',
    edit: '<svg class="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20h4L19 9l-4-4L4 16v4z"/><path d="M13.5 6.5l4 4"/></svg>',
    close: '<svg class="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>',
    check: '<svg class="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>',
  };

  // ----- talking to IGYO -----

  async function api(path, params) {
    const url = new URL(path, location.origin);
    for (const [key, value] of Object.entries(params || {})) url.searchParams.set(key, value);
    const response = await fetch(url, { method: 'POST', cache: 'no-store' });
    if (!response.ok) throw new Error('HTTP ' + response.status);
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }

  let saveTimer = 0;
  function save() {
    if (readOnly) return;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      const data = {
        clock: state.clock,
        clockSize: state.clockSize,
        blur: state.blur,
        showShortcuts: state.showShortcuts,
        shortcuts: state.shortcuts,
        picture: state.picture,
        rotate: state.rotate,
      };
      api('/api/save', { data: JSON.stringify(data) }).catch(() => toast(tr("Couldn't save that change.")));
    }, 120);
  }

  // ----- toast -----

  let toastTimer = 0;
  function toast(text, actionLabel, action) {
    const box = $('toast');
    const button = $('toast-action');
    $('toast-text').textContent = text;
    button.hidden = !actionLabel;
    button.textContent = actionLabel || '';
    button.onclick = () => { hideToast(); if (action) action(); };
    box.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(hideToast, actionLabel ? 6000 : 2800);
  }
  function hideToast() { $('toast').classList.remove('show'); }

  // ----- search -----

  const query = $('query');
  const engine = typeof T.engine === 'string' && T.engine.includes('%s') ? T.engine : 'https://www.google.com/search?q=%s';
  query.placeholder = tr('Search {0}', T.engineName || 'Google');
  $('search').addEventListener('submit', (event) => {
    event.preventDefault();
    const words = query.value.trim();
    if (!words) { query.focus(); return; }
    location.href = engine.split('%s').join(encodeURIComponent(words));
  });

  // ----- clock -----

  const timeFormat = new Intl.DateTimeFormat(language, { hour: 'numeric', minute: '2-digit' });
  const dateFormat = new Intl.DateTimeFormat(language, { weekday: 'long', day: 'numeric', month: 'long' });
  // A look may have the clock written as a Japanese phone writes it, whatever the language (the option "clock: ja",
  // Subculture's): 18:59 under 11月27日 金曜日.
  const phoneTime = new Intl.DateTimeFormat('ja-JP', { hour: 'numeric', minute: '2-digit', hourCycle: 'h23' });
  const phoneDay = new Intl.DateTimeFormat('ja-JP', { month: 'long', day: 'numeric' });
  const phoneWeekday = new Intl.DateTimeFormat('ja-JP', { weekday: 'long' });
  const asJapanesePhone = () => root.dataset.oClock === 'ja';
  let clockTimer = 0;
  // The time is written in parts so a look can dress one of them: the part of the day (AM, PM), with the space that
  // parts it from the time, gets an element of its own, and #time says in data-half which half of the day it is
  // (Ura hides that element and writes 午前 or 午後 before the time instead).
  function writeTime(now) {
    const time = $('time');
    const parts = (asJapanesePhone() ? phoneTime : timeFormat).formatToParts(now);
    const blank = (part) => !!part && part.type === 'literal' && !part.value.trim();
    let first = parts.findIndex((part) => part.type === 'dayPeriod'), last = first;
    if (first >= 0 && blank(parts[first - 1])) first--;
    else if (first >= 0 && blank(parts[last + 1])) last++;
    time.textContent = '';
    let period = null;
    parts.forEach((part, i) => {
      if (first < 0 || i < first || i > last) { time.append(part.value); return; }
      if (!period) { period = document.createElement('span'); period.className = 'nt-period'; time.append(period); }
      period.append(part.value);
    });
    if (period) time.dataset.half = now.getHours() < 12 ? 'am' : 'pm'; else delete time.dataset.half;
  }
  function tick() {
    const now = new Date();
    writeTime(now);
    $('date').textContent = asJapanesePhone() ? phoneDay.format(now) + ' ' + phoneWeekday.format(now) : dateFormat.format(now);
    clearTimeout(clockTimer);
    clockTimer = setTimeout(tick, 60500 - (now.getSeconds() * 1000 + now.getMilliseconds()));
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
  new MutationObserver(tick).observe(root, { attributes: true, attributeFilter: ['data-o-clock'] });   // the look changed

  function applyVisibility() {
    $('clock').hidden = !state.clock;
    $('shortcuts').hidden = !state.showShortcuts;
    $('clock-toggle').checked = state.clock;
    $('shortcuts-toggle').checked = state.showShortcuts;
    $('clock-size').value = state.clockSize;
    $('clock-size').disabled = readOnly || !state.clock;
    applyClockSize();
  }

  // The time at your size: the look decides how large it is, and this scales that (whatever the look is).
  function applyClockSize() {
    $('time').style.zoom = state.clockSize === 100 ? '' : String(state.clockSize / 100);
  }

  // ----- background picture -----

  function pictureById(id) { return state.pictures.find((p) => p.id === id); }

  // A theme can bring its own picture; it shows when you haven't chosen one of yours.
  const themeHasPicture = () => getComputedStyle(root).getPropertyValue('--wallpaper').trim().startsWith('url(');

  // A look can lay your picture behind glass, out of focus: its kit says so with --nt-picture: soft. IGYO keeps
  // a small soft copy of each picture for that (it also costs far less memory to show than the picture itself).
  const wantsSoft = () => getComputedStyle(root).getPropertyValue('--nt-picture').trim() === 'soft';

  // The address of the theme's own picture, out of its --wallpaper.
  function themePicture() {
    const found = /^url\(\s*(["']?)([\s\S]*)\1\s*\)$/.exec(getComputedStyle(root).getPropertyValue('--wallpaper').trim());
    return found ? found[2] : '';
  }

  // A theme's picture of a few pixels is one color laid out the way pictures are (IGYO Dark and Light bring one):
  // there is nothing in it to blur, so Blur stays off for it.
  const themePictureIsPlain = () => themePicture().length < 2000;

  // A blur drawn for an amount IGYO keeps no copy of is kept on this PC (the last picture and amount only), so New
  // Tab pages opened later show it at once instead of drawing it again. Never from a private window.
  const KEPT_BLUR = 'igyo.blur';
  function keptBlur(key) {
    try { const kept = JSON.parse(localStorage.getItem(KEPT_BLUR) || 'null'); return kept && kept.key === key ? kept.data : ''; } catch { return ''; }
  }
  function keepBlur(key, data) {
    if (!data || readOnly) return;
    try { localStorage.setItem(KEPT_BLUR, JSON.stringify({ key, data })); } catch { /* full or refused: drawn again next time */ }
  }

  // How out of focus the picture is, 0 (sharp) to 100: what you set with Blur in Customize, in every look. Until
  // you set it the look decides: a look that lays your picture behind glass starts it at 50 (IGYO's soft copy),
  // the others show it sharp, and a picture that a theme brings is shown as the theme brings it.
  const SOFT = 50;
  const blurFor = (fromTheme) => (state.blur >= 0 ? state.blur : !fromTheme && wantsSoft() ? SOFT : 0);

  // A picture made out of focus here, for an amount IGYO keeps no copy for (and for WebP or AVIF pictures, which
  // Windows' own picture library can't read, so they have no soft copy). Its edges are mirrored outwards first, so
  // they neither darken nor zoom; then it is blurred by its share of the width (50 = the soft copy's 1.56%, 100 =
  // twice that), less what the source has already (a soft copy has 50). A small JPEG, or '' if the engine can't.
  function blurred(source, amount, already) {
    try {
      const w = source.width;
      const h = source.height;
      const unit = Math.min(w, h * 16 / 9) * 0.0156 / SOFT;
      const reach = Math.sqrt(Math.max(0, (amount * unit) ** 2 - ((already || 0) * unit) ** 2));
      const pad = Math.min(w, h, Math.ceil(reach * 3));
      const framed = document.createElement('canvas');
      framed.width = w + pad * 2;
      framed.height = h + pad * 2;
      const f = framed.getContext('2d');
      for (const dx of [-1, 0, 1]) {
        for (const dy of [-1, 0, 1]) {
          const x = pad + dx * w;
          const y = pad + dy * h;
          f.setTransform(dx ? -1 : 1, 0, 0, dy ? -1 : 1, dx ? x + w : x, dy ? y + h : y);   // mirrored beside the picture
          f.drawImage(source, 0, 0, w, h);
        }
      }
      const out = document.createElement('canvas');
      out.width = w;
      out.height = h;
      const g = out.getContext('2d');
      if (reach > 0.05) g.filter = `blur(${reach.toFixed(2)}px)`;
      g.drawImage(framed, -pad, -pad);
      return out.toDataURL('image/jpeg', 0.9);
    } catch {
      return '';
    }
  }

  // The picture to blur, drawn at most 960 wide. While Customize is open the last two are kept, so moving Blur
  // draws from them instead of loading the picture again; closing Customize lets them go.
  const kept = new Map();
  function withSource(address, then, otherwise) {
    if (kept.has(address)) { then(kept.get(address)); return; }
    const image = new Image();
    image.onload = () => {
      const canvas = document.createElement('canvas');
      try {
        const k = Math.min(1, 960 / Math.max(image.naturalWidth, image.naturalHeight));
        canvas.width = Math.max(1, Math.round(image.naturalWidth * k));
        canvas.height = Math.max(1, Math.round(image.naturalHeight * k));
        const g = canvas.getContext('2d');
        g.imageSmoothingQuality = 'high';
        g.drawImage(image, 0, 0, canvas.width, canvas.height);
      } catch {
        otherwise();
        return;
      }
      if (body.classList.contains('sheet-open')) {
        kept.set(address, canvas);
        if (kept.size > 2) kept.delete(kept.keys().next().value);
      }
      then(canvas);
    };
    image.onerror = otherwise;
    image.src = address;
  }

  let backgroundTurn = 0;   // a picture that arrives after another was chosen is not shown
  function showBackground(id) {
    const backdrop = $('backdrop');
    const picture = pictureById(id);
    const turn = ++backgroundTurn;
    shown = picture ? picture.id : '';
    const fromTheme = !picture && themeHasPicture();
    const amount = blurFor(fromTheme);
    body.classList.toggle('has-wallpaper', fromTheme);
    body.classList.toggle('has-image', !!picture || fromTheme);
    $('blur').value = String(amount);
    $('blur').disabled = readOnly || (!picture && (!fromTheme || themePictureIsPlain()));
    // '' leaves the backdrop to the stylesheet: the look's own ground, or the theme's picture as the theme brings it.
    const show = (address) => {
      if (turn !== backgroundTurn) return;
      backdrop.style.backgroundImage = address ? `url("${address}")` : '';
      backdrop.classList.toggle('photo', !!picture);
      backdrop.classList.toggle('soft', !!address && amount > 0);
      backdrop.classList.add('ready');
    };
    const fail = () => {
      if (turn !== backgroundTurn) return;
      body.classList.remove('has-image');
      backdrop.classList.add('ready');
    };
    const load = (address, then, otherwise) => {
      const image = new Image();
      image.onload = () => then(image);
      image.onerror = otherwise;
      image.src = address;
    };
    const key = picture ? picture.id + '|' + amount : '';
    const drawn = (source, already, fallback) => { const data = blurred(source, amount, already); keepBlur(key, data); show(data || fallback); };
    const made = (address, already) => withSource(address, (source) => drawn(source, already, address), fail);
    if (!picture) {
      if (fromTheme && amount > 0 && !themePictureIsPlain()) withSource(themePicture(), (source) => show(blurred(source, amount, 0)), () => show(''));
      else show('');
      return;
    }
    const kept = amount !== 0 && amount !== SOFT ? keptBlur(key) : '';
    if (kept) show(kept);
    else if (amount === 0) load(picture.full, () => show(picture.full), fail);
    else if (amount === SOFT) load(picture.soft, () => show(picture.soft), () => made(picture.full, 0));
    else if (amount > SOFT) withSource(picture.soft, (source) => drawn(source, SOFT, picture.soft), () => made(picture.full, 0));
    else made(picture.full, 0);
  }

  function switchBackground(id) {
    if (!animate) { showBackground(id); return; }
    $('backdrop').classList.remove('ready');
    setTimeout(() => showBackground(id), 230);
  }

  // ----- shortcuts -----

  const hostOf = (url) => { try { return new URL(url).hostname.toLowerCase(); } catch { return ''; } };
  const hueOf = (text) => [...text].reduce((h, c) => (h * 31 + c.codePointAt(0)) % 360, 7);

  function iconFor(item) {
    const box = document.createElement('span');
    box.className = 'icon';
    const host = hostOf(item.url);
    const letter = ((item.name || host).trim()[0] || '?').toUpperCase();
    const monogram = () => {
      box.textContent = letter;
      box.classList.add('monogram');
      box.style.setProperty('--hue', String(hueOf(host || item.name || '')));
    };
    if (!host) { monogram(); return box; }
    // The icon IGYO saved when you visited the site; otherwise the site's own; otherwise a letter.
    const sources = [`/favicon/${encodeURIComponent(host.replace(/^www\./, ''))}`, `https://${host}/favicon.ico`];
    const image = new Image();
    image.alt = '';
    image.referrerPolicy = 'no-referrer';
    image.decoding = 'async';
    let step = 0;
    const next = () => {
      step += 1;
      if (step < sources.length) image.src = sources[step];
      else { image.remove(); monogram(); }
    };
    image.onload = () => { if (image.naturalWidth < 4) next(); };
    image.onerror = next;
    image.src = sources[0];
    box.append(image);
    return box;
  }

  function toolButton(icon, label, onClick) {
    const button = document.createElement('button');
    button.type = 'button';
    button.innerHTML = icon;
    button.title = label;
    button.setAttribute('aria-label', label);
    button.addEventListener('click', (event) => { event.preventDefault(); event.stopPropagation(); onClick(); });
    return button;
  }

  let dragFrom = -1;
  let freshIndex = -1;

  function renderShortcuts() {
    const list = $('shortcuts');
    list.textContent = '';
    state.shortcuts.forEach((item, i) => {
      const li = document.createElement('li');
      li.className = 'tile' + (i === freshIndex ? ' fresh' : '');
      li.style.animationDelay = list.classList.contains('settled') ? '0ms' : `${110 + i * 35}ms`;
      const link = document.createElement('a');
      link.href = item.url;
      link.title = item.url;
      link.draggable = false;
      const name = document.createElement('span');
      name.className = 'name';
      name.textContent = item.name;
      link.append(iconFor(item), name);
      li.append(link);
      if (!readOnly) {
        const tools = document.createElement('div');
        tools.className = 'tools';
        tools.append(
          toolButton(ICONS.edit, tr('Edit {0}', item.name), () => openEditor(i)),
          toolButton(ICONS.close, tr('Remove {0}', item.name), () => removeShortcut(i, li)),
        );
        li.append(tools);
        li.draggable = true;
        li.addEventListener('dragstart', (event) => {
          dragFrom = i;
          li.classList.add('dragging');
          event.dataTransfer.effectAllowed = 'move';
          event.dataTransfer.setData('text/uri-list', item.url);
        });
        li.addEventListener('dragend', () => {
          dragFrom = -1;
          li.classList.remove('dragging');
          list.querySelectorAll('.drop-target').forEach((el) => el.classList.remove('drop-target'));
        });
        li.addEventListener('dragover', (event) => {
          if (dragFrom < 0 || dragFrom === i) return;
          event.preventDefault();
          event.dataTransfer.dropEffect = 'move';
          li.classList.add('drop-target');
        });
        li.addEventListener('dragleave', () => li.classList.remove('drop-target'));
        li.addEventListener('drop', (event) => {
          event.preventDefault();
          if (dragFrom < 0 || dragFrom === i) return;
          const [moved] = state.shortcuts.splice(dragFrom, 1);
          state.shortcuts.splice(i, 0, moved);
          dragFrom = -1;
          save();
          renderShortcuts();
        });
      }
      list.append(li);
    });
    if (!readOnly && state.shortcuts.length < MAX_SHORTCUTS) {
      const li = document.createElement('li');
      li.className = 'tile';
      li.style.animationDelay = list.classList.contains('settled') ? '0ms' : `${110 + state.shortcuts.length * 35}ms`;
      const add = document.createElement('button');
      add.type = 'button';
      add.className = 'add';
      add.innerHTML = `<span class="icon">${ICONS.plus}</span><span class="name"></span>`;
      add.querySelector('.name').textContent = tr('Add shortcut');
      add.addEventListener('click', () => openEditor(-1));
      li.append(add);
      list.append(li);
    }
    freshIndex = -1;
  }

  function removeShortcut(index, li) {
    const removed = state.shortcuts[index];
    const finish = () => {
      state.shortcuts.splice(index, 1);
      save();
      renderShortcuts();
      toast(tr('Removed {0}.', removed.name), tr('Undo'), () => {
        state.shortcuts.splice(Math.min(index, state.shortcuts.length), 0, removed);
        freshIndex = Math.min(index, state.shortcuts.length - 1);
        save();
        renderShortcuts();
      });
    };
    if (!animate) { finish(); return; }
    li.classList.add('leaving');
    setTimeout(finish, 190);
  }

  // ----- add / edit dialog -----

  let editing = -1;

  function openEditor(index) {
    editing = index;
    const item = state.shortcuts[index] || { name: '', url: '' };
    $('editor-title').textContent = index < 0 ? tr('Add shortcut') : tr('Edit shortcut');
    $('edit-name').value = item.name;
    $('edit-url').value = item.url;
    $('edit-error').textContent = '';
    $('editor').showModal();
    $('edit-name').focus();
  }

  function normalizeUrl(text) {
    let value = text.trim();
    if (!value) return null;
    if (!/^https?:\/\//i.test(value)) value = (/^(localhost|127\.|\[::1\])/i.test(value) ? 'http://' : 'https://') + value;
    try {
      const url = new URL(value);
      if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
      if (!url.hostname || !(url.hostname.includes('.') || url.hostname === 'localhost' || url.hostname.startsWith('['))) return null;
      return url.href;
    } catch {
      return null;
    }
  }

  $('editor-form').addEventListener('submit', (event) => {
    event.preventDefault();
    const url = normalizeUrl($('edit-url').value);
    if (!url) {
      $('edit-error').textContent = tr('Type a web address, like gmail.com');
      $('edit-url').focus();
      return;
    }
    const name = $('edit-name').value.trim() || hostOf(url).replace(/^www\./, '');
    if (editing < 0) {
      state.shortcuts.push({ name, url });
      freshIndex = state.shortcuts.length - 1;
    } else {
      state.shortcuts[editing] = { name, url };
      freshIndex = editing;
    }
    $('editor').close();
    save();
    renderShortcuts();
  });
  $('edit-cancel').addEventListener('click', () => $('editor').close());

  // ----- customize sheet -----

  function openSheet() {
    renderPictures();
    body.classList.add('sheet-open');
    $('sheet').inert = false;
    $('customize').setAttribute('aria-expanded', 'true');
    setTimeout(() => $('close-sheet').focus(), animate ? 120 : 0);
  }

  function closeSheet() {
    if (!body.classList.contains('sheet-open')) return;
    body.classList.remove('sheet-open');
    kept.clear();
    $('sheet').inert = true;
    $('customize').setAttribute('aria-expanded', 'false');
    $('customize').focus();
  }

  function renderPictures() {
    const grid = $('pictures');
    grid.textContent = '';
    grid.append(pictureTile(null, 0));
    state.pictures.forEach((picture, i) => grid.append(pictureTile(picture, i + 1)));
    if (state.pictures.length < MAX_PICTURES) {
      const tile = document.createElement('div');
      tile.className = 'pic add';
      const choose = document.createElement('button');
      choose.type = 'button';
      choose.className = 'choose';
      choose.innerHTML = `${ICONS.plus}<span></span>`;
      choose.querySelector('span').textContent = tr('Add pictures');
      choose.addEventListener('click', addPictures);
      tile.append(choose);
      grid.append(tile);
    }
    $('rotate').checked = state.rotate;
    $('rotate').disabled = state.pictures.length < 2;
  }

  function pictureTile(picture, number) {
    const id = picture ? picture.id : '';
    const tile = document.createElement('div');
    tile.className = 'pic' + (picture ? '' : ' default') + (id === shown ? ' selected' : '');
    const choose = document.createElement('button');
    choose.type = 'button';
    choose.className = 'choose';
    choose.setAttribute('aria-pressed', String(id === shown));
    choose.setAttribute('aria-label', picture ? tr('Picture {0}', number) : tr('IGYO colors'));
    if (picture) choose.style.backgroundImage = `url("${picture.thumb}")`;
    else choose.innerHTML = '<span class="label">IGYO</span>';
    choose.addEventListener('click', () => {
      state.picture = id;
      save();
      if (id !== shown) switchBackground(id);
      shown = id;
      renderPictures();
    });
    const check = document.createElement('span');
    check.className = 'check';
    check.innerHTML = ICONS.check;
    tile.append(choose, check);
    if (picture) {
      const remove = toolButton(ICONS.close, tr('Remove picture {0}', number), () => confirmRemove(tile, picture));
      remove.className = 'remove';
      tile.append(remove);
    }
    return tile;
  }

  // Removing deletes the file, so it takes a second click.
  function confirmRemove(tile, picture) {
    if (tile.querySelector('.confirm')) return;
    const confirm = document.createElement('button');
    confirm.type = 'button';
    confirm.className = 'confirm';
    confirm.textContent = tr('Remove');
    confirm.addEventListener('click', async () => {
      try {
        const result = await api('/api/remove-picture', { id: picture.id });
        state.pictures = result.pictures;
        state.picture = result.picture;
        if (shown === picture.id) switchBackground(state.picture);
        renderPictures();
      } catch {
        toast(tr("Couldn't remove that picture."));
      }
    });
    tile.append(confirm);
    confirm.focus();
    setTimeout(() => confirm.remove(), 4000);
  }

  let adding = false;
  async function addPictures() {
    if (adding) return;
    adding = true;
    try {
      const result = await api('/api/add-pictures');
      if (!result) return;
      const before = new Set(state.pictures.map((p) => p.id));
      state.pictures = result.pictures;
      state.picture = result.picture;
      const added = state.pictures.some((p) => !before.has(p.id));
      if (added && state.picture !== shown) switchBackground(state.picture);
      if (added) shown = state.picture;
      renderPictures();
      if (result.skipped > 0) toast(result.skipped === 1 ? tr("One file couldn't be used as a picture.") : tr("{0} files couldn't be used as pictures.", result.skipped));
    } catch {
      toast(tr("Couldn't add pictures."));
    } finally {
      adding = false;
    }
  }

  $('customize').addEventListener('click', () => (body.classList.contains('sheet-open') ? closeSheet() : openSheet()));
  $('close-sheet').addEventListener('click', closeSheet);
  // Look: the browser's themes, in Settings (IGYO opens it in this window, or goes to the tab that has it).
  $('open-themes').addEventListener('click', () => { closeSheet(); api('/api/open-themes').catch(() => { }); });
  $('scrim').addEventListener('click', closeSheet);
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape' && !$('editor').open) closeSheet(); });
  $('clock-toggle').addEventListener('change', (event) => { state.clock = event.target.checked; applyVisibility(); save(); });
  $('shortcuts-toggle').addEventListener('change', (event) => { state.showShortcuts = event.target.checked; applyVisibility(); save(); });
  $('clock-size').addEventListener('input', (event) => { state.clockSize = Number(event.target.value) || 100; applyClockSize(); save(); });
  // Blur: the picture is drawn again a moment after the slider stops, from the copy kept while Customize is open.
  let blurTimer = 0;
  $('blur').addEventListener('input', (event) => {
    state.blur = Math.max(0, Math.min(100, Math.round(Number(event.target.value) || 0)));
    save();
    clearTimeout(blurTimer);
    blurTimer = setTimeout(() => showBackground(pictureById(shown) ? shown : ''), 80);
  });
  $('rotate').addEventListener('change', (event) => { state.rotate = event.target.checked; save(); });

  // ----- start -----

  tick();
  applyVisibility();
  renderShortcuts();
  showBackground(shown);
  setTimeout(() => $('shortcuts').classList.add('settled'), 1400);
  // A new tab starts in the address bar; once you click into the page, typing goes to the search box.
  window.addEventListener('focus', () => {
    if (!document.activeElement || document.activeElement === body) query.focus();
  });
  // The browser's theme changed while this page was open: the stylesheet is already swapped; pick up its picture,
  // and show yours the way the new look wants it (as it is, or out of focus).
  window.addEventListener('igyo-theme', () => {
    showBackground(pictureById(shown) ? shown : '');
  });
})();
