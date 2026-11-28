// IGYO — what floats over the page: menus, dialogs, notices and the suggestion list.
// Menus and dialogs take the whole window while they are open (a click anywhere else closes them);
// notices and suggestions only take their own box, so the page around them stays usable.
(() => {
  'use strict';

  const IGYO = window.IGYO;
  const { Host, Layout, el, icon } = IGYO;
  const tr = IGYO.tr;
  const overlays = document.getElementById('overlays');
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  function openLayer(options) {
    const layer = el('div', 'tc-layer is-modal is-waiting' + (options.dim ? ' is-dim' : ''));
    const scrim = el('div', 'tc-scrim');
    scrim.addEventListener('pointerdown', (event) => { event.preventDefault(); options.onDismiss(); });
    scrim.addEventListener('contextmenu', (event) => { event.preventDefault(); options.onDismiss(); });
    layer.append(scrim);
    overlays.append(layer);
    Layout.now();
    // Shown a moment later: by then the app has given the interface the whole window. Something hidden can't take
    // the keyboard, so what a menu or dialog wants focused is focused at that moment (layer.whenShown).
    let shown = false, onShown = null;
    layer.whenShown = (fn) => { if (shown) fn(); else onShown = fn; };
    const show = () => {
      if (shown) return;
      shown = true;
      layer.classList.remove('is-waiting');
      if (onShown && layer.isConnected && !layer._closing) onShown();
    };
    requestAnimationFrame(() => setTimeout(show, 20));
    setTimeout(show, 120);   // a window that isn't being drawn (minimized, covered) gets no animation frame
    return layer;
  }

  function closeLayer(layer, instant) {
    if (!layer || layer._closing) return;
    layer._closing = true;
    const remove = () => { layer.remove(); Layout.now(); };
    if (instant || document.documentElement.dataset.anim === 'off') { remove(); return; }
    layer.classList.add('is-closing');
    setTimeout(remove, 130);
  }

  // After a menu or dialog, the keyboard goes back to where it was: the address bar, or the page.
  function giveFocusBack(previous) {
    const address = document.getElementById('address');
    if (previous === address) address.focus();
    else Host.cmd('focusPage');
  }

  // ----- menus -----
  // An item: { label, keys, icon, image, checked, disabled, sep, head, className, tip, children (a list or a function),
  //            run(event), middle (also acts on a middle click), context(x, y) (its own right-click menu), id }

  let menu = null;

  function closeMenu(instant, keepFocus) {
    const m = menu;
    if (!m) return;
    menu = null;
    clearTimeout(m.timer);
    if (m.options.anchor) m.options.anchor.classList.remove('is-open');
    closeLayer(m.layer, instant);
    if (!keepFocus && !m.options.keepFocus) giveFocusBack(m.previous);
  }

  function rowsOf(panel) {
    return [...panel.querySelectorAll(':scope > .t-menu-item')].filter((row) => row.getAttribute('aria-disabled') !== 'true');
  }

  function setActive(panel, row) {
    panel.querySelectorAll(':scope > .t-menu-item.is-active').forEach((r) => { if (r !== row) r.classList.remove('is-active'); });
    if (row) {
      row.classList.add('is-active');
      row.scrollIntoView({ block: 'nearest' });
    }
    panel._active = row || null;
  }

  function closeDeeper(depth) {
    while (menu && menu.panels.length > depth + 1) {
      const gone = menu.panels.pop();
      if (gone._parentRow) gone._parentRow.classList.remove('is-open');
      gone.remove();
    }
  }

  // A line that is a slider (a tab's volume): { label, icon, disabled, slider: { cmd, value, min, max, step, normal, unit } }.
  // Moving it tells the app at once and the menu stays open. Left and Right move it while its line is the marked
  // one; a double click puts it back to its normal value.
  function sliderRow(item, lead, panel, depth) {
    const s = item.slider;
    const row = el('div', 't-menu-item tc-menu-slider');
    row.setAttribute('role', 'menuitem');
    row._item = item;
    if (item.disabled) row.setAttribute('aria-disabled', 'true');
    if (lead) {
      const mark = el('span', 't-menu-icon');
      if (item.icon) mark.innerHTML = icon(item.icon);
      row.append(mark);
    }
    const text = el('span', 't-menu-text');
    text.textContent = item.label || '';
    // The slider stands on one of the given values ("stops"); they need not be evenly apart, so the slider itself
    // counts places and the values are looked up.
    const stops = s.stops && s.stops.length ? s.stops : [0, 100];
    const place = (v) => stops.reduce((best, stop, i) => (Math.abs(stop - v) < Math.abs(stops[best] - v) ? i : best), 0);
    const range = document.createElement('input');
    range.type = 'range';
    range.className = 't-range';
    range.min = 0;
    range.max = stops.length - 1;
    range.step = 1;
    range.value = place(s.value);
    range.tabIndex = -1;
    range.disabled = !!item.disabled;
    range.setAttribute('aria-label', item.label || '');
    const value = el('span', 'tc-menu-value');
    const now = () => stops[clamp(Math.round(Number(range.value)), 0, stops.length - 1)];
    const show = () => { value.textContent = now() + (s.unit || ''); range.setAttribute('aria-valuetext', value.textContent); };
    let sent = now(), timer = 0;
    const send = () => {
      clearTimeout(timer);
      timer = 0;
      if (sent === now()) return;
      sent = now();
      Host.cmd(s.cmd, { value: sent });
    };
    range.addEventListener('input', () => { show(); if (!timer) timer = setTimeout(send, 60); });
    range.addEventListener('change', send);
    range.addEventListener('dblclick', () => { range.value = place(s.normal); show(); send(); });
    // With the keyboard in the slider itself, the arrow keys are its own.
    range.addEventListener('keydown', (event) => { if (/^(Arrow|Home|End|Page)/.test(event.key)) event.stopPropagation(); });
    row._step = (direction) => { range.value = Number(range.value) + direction; show(); send(); };
    row._value = now;
    if (item.tip) row.title = item.tip;
    show();
    row.append(text, range, value);
    if (item.pin) row.append(pinButton(item));
    row.addEventListener('pointerenter', () => {
      if (!menu) return;
      setActive(panel, row);
      clearTimeout(menu.timer);
      menu.timer = setTimeout(() => { if (menu) closeDeeper(depth); }, 150);
    });
    return row;
  }

  // The pin at the end of a tool's line: pinned, the tool also has a button of its own on the toolbar. Pressing it
  // tells the app (through the menu's onPick, with the pin's own id) and leaves the menu open.
  function pinButton(item) {
    const pin = el('button', 'tc-menu-pin');
    pin.type = 'button';
    pin.tabIndex = -1;
    const show = () => {
      pin.innerHTML = icon(item.pin.on ? 'pin-on' : 'pin');
      pin.classList.toggle('is-on', !!item.pin.on);
      pin.title = item.pin.on ? tr('Unpin from the toolbar') : tr('Pin to the toolbar');
      pin.setAttribute('aria-label', pin.title);
      pin.setAttribute('aria-pressed', String(!!item.pin.on));
    };
    show();
    pin.addEventListener('pointerdown', (event) => event.stopPropagation());
    pin.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (!menu) return;
      item.pin.on = !item.pin.on;
      show();
      if (menu.options.onPick) menu.options.onPick({ id: item.pin.id });
    });
    return pin;
  }

  // A line that switches something on or off shows it with a small switch at its end (and keeps its icon).
  function setSwitch(row, on) {
    row.setAttribute('aria-checked', String(!!on));
    const mark = row.querySelector(':scope > .tc-menu-switch');
    if (mark) mark.classList.toggle('is-on', !!on);
  }

  function buildPanel(items, depth) {
    const panel = el('div', 't-menu tc-pop');
    panel.setAttribute('role', 'menu');
    panel.tabIndex = -1;
    panel._depth = depth;
    const lead = items.some((item) => item.icon || item.image || item.checked);
    if (items.some((item) => item.pin)) panel.classList.add('has-pins');   // lines with a switch and a pin need more room
    for (const item of items) {
      if (item.sep) { panel.append(el('div', 't-menu-sep')); continue; }
      if (item.head) {
        const head = el('div', 't-menu-head');
        head.textContent = item.label || '';
        panel.append(head);
        continue;
      }
      if (item.slider) { panel.append(sliderRow(item, lead, panel, depth)); continue; }
      const toggles = typeof item.switch === 'boolean';
      // (A line that ends in a pin holds a button, so it cannot be one itself.)
      const row = el(item.pin ? 'div' : 'button', 't-menu-item' + (item.className ? ' ' + item.className : ''));
      if (!item.pin) row.type = 'button';
      row.tabIndex = -1;
      row.setAttribute('role', toggles ? 'menuitemcheckbox' : 'menuitem');
      row._item = item;
      if (item.disabled) row.setAttribute('aria-disabled', 'true');
      if (item.tip) row.title = item.tip;
      // What a line starts with: its icon or picture. A check mark stands there only when the line has neither;
      // a checked line that has one keeps it, and the check mark goes to the end of the line.
      const pictured = !!(item.icon || item.image);
      if (lead) {
        const mark = el('span', item.checked && !pictured ? 'tc-menu-check' : 't-menu-icon');
        if (item.checked && !pictured) mark.innerHTML = icon('check');
        else if (item.image) {
          const image = new Image();
          image.alt = '';
          image.onerror = () => { image.remove(); mark.innerHTML = icon(item.icon || 'globe'); };
          image.src = item.image;
          mark.append(image);
        } else if (item.icon) mark.innerHTML = icon(item.icon);
        row.append(mark);
      }
      if (item.checked) row.setAttribute('aria-checked', 'true');
      const text = el('span', 't-menu-text');
      text.textContent = item.label || '';
      row.append(text);
      if (item.keys) {
        const keys = el('span', 't-kbd');
        keys.textContent = item.keys;
        row.append(keys);
      }
      if (item.checked && pictured) row.append(el('span', 'tc-menu-check is-end', icon('check')));
      if (item.children) {
        row.setAttribute('aria-haspopup', 'menu');
        row.append(el('span', 'tc-menu-arrow', icon('chevron-right')));
      }
      if (toggles) {
        row.append(el('span', 'tc-menu-switch'));
        setSwitch(row, item.switch);
      }
      if (item.pin) row.append(pinButton(item));
      row.addEventListener('pointerenter', () => {
        if (!menu) return;
        setActive(panel, row);
        clearTimeout(menu.timer);
        menu.timer = setTimeout(() => {
          if (!menu) return;
          if (item.children && !item.disabled) openSub(row, false);
          else closeDeeper(depth);
        }, 150);
      });
      row.addEventListener('click', (event) => activate(row, event));
      // A line that can be dragged somewhere else (a bookmark in a folder's menu): the item says what a press starts.
      if (item.drag) row.addEventListener('pointerdown', (event) => item.drag(event, row));
      row.addEventListener('mousedown', (event) => { if (event.button === 1) event.preventDefault(); });
      row.addEventListener('auxclick', (event) => { if (event.button === 1 && item.middle) activate(row, event); });
      row.addEventListener('contextmenu', (event) => {
        event.preventDefault();
        event.stopPropagation();
        if (!item.context || item.disabled) return;
        if (menu) { clearTimeout(menu.timer); setActive(panel, row); }   // (the line stays marked under its own menu)
        item.context(event.clientX, event.clientY);
      });
      panel.append(row);
    }
    return panel;
  }

  function openSub(row, focusFirst) {
    if (!menu) return;
    const item = row._item;
    const parent = row.parentElement;
    const depth = parent._depth;
    if (menu.panels[depth + 1] && menu.panels[depth + 1]._parentRow === row) {
      if (focusFirst) setActive(menu.panels[depth + 1], rowsOf(menu.panels[depth + 1])[0]);
      return;
    }
    closeDeeper(depth);
    const items = typeof item.children === 'function' ? item.children() : item.children;
    const panel = buildPanel(items || [], depth + 1);
    panel._parentRow = row;
    row.classList.add('is-open');
    menu.layer.append(panel);
    menu.panels.push(panel);
    const r = row.getBoundingClientRect();
    const host = parent.getBoundingClientRect();
    const w = panel.offsetWidth, h = panel.offsetHeight;
    let x = host.right - 4;
    let from = '0%';
    if (x + w > window.innerWidth - 8) { x = host.left - w + 4; from = '100%'; }
    panel.style.left = clamp(x, 8, Math.max(8, window.innerWidth - w - 8)) + 'px';
    panel.style.top = clamp(r.top - 6, 8, Math.max(8, window.innerHeight - h - 8)) + 'px';
    panel.style.setProperty('--from-x', from);
    panel.style.setProperty('--from-y', '12px');
    if (focusFirst) setActive(panel, rowsOf(panel)[0]);
  }

  function activate(row, event) {
    const item = row._item;
    if (row._dragged) { row._dragged = false; return; }   // the click that ends a drag chooses nothing
    if (!menu || !item || item.disabled) return;
    if (item.children) { if (!row.parentElement.classList.contains('is-context')) openSub(row, true); return; }
    if (item.slider) return;   // a slider is moved, not chosen: the menu stays open
    const options = menu.options;
    if (typeof item.switch === 'boolean') {
      // Switched, not chosen: the menu stays open, so that it can be switched back and forth while watching the page.
      item.switch = !item.switch;
      setSwitch(row, item.switch);
      if (item.run) item.run(event);
      else if (options.onPick) options.onPick(item);
      return;
    }
    closeMenu(false, false);
    if (item.run) item.run(event);
    else if (options.onPick) options.onPick(item);
  }

  function onMenuKey(event) {
    if (!menu) return;
    const context = menu.context;
    const panel = context ? context.panel : menu.panels[menu.panels.length - 1];
    const rows = rowsOf(panel);
    // A line's own right-click menu over the menu: the arrows go through its lines, and Esc or Left closes only it.
    if (context && (event.key === 'Escape' || event.key === 'ArrowLeft')) { event.preventDefault(); event.stopPropagation(); closeContext(); return; }
    if (context && event.key === 'ArrowRight') { event.preventDefault(); event.stopPropagation(); return; }
    const at = rows.indexOf(panel._active);
    const stop = () => { event.preventDefault(); event.stopPropagation(); };
    if (panel._active && panel._active._step && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
      stop();
      panel._active._step(event.key === 'ArrowRight' ? 1 : -1);
      return;
    }
    switch (event.key) {
      case 'ArrowDown': stop(); setActive(panel, rows[(at + 1) % rows.length]); break;
      case 'ArrowUp': stop(); setActive(panel, rows[(at - 1 + rows.length) % rows.length] || rows[rows.length - 1]); break;
      case 'Home': stop(); setActive(panel, rows[0]); break;
      case 'End': stop(); setActive(panel, rows[rows.length - 1]); break;
      case 'ArrowRight': stop(); if (panel._active && panel._active._item.children) openSub(panel._active, true); break;
      case 'ArrowLeft': stop(); if (menu.panels.length > 1) closeDeeper(menu.panels.length - 2); break;
      case 'Enter': case ' ': stop(); if (panel._active) activate(panel._active, event); break;
      case 'Escape': stop(); if (menu.panels.length > 1) closeDeeper(menu.panels.length - 2); else closeMenu(); break;
      case 'Tab': stop(); break;
      default:
        if (event.key.length === 1 && !event.ctrlKey && !event.altKey) {
          // A letter jumps to the next line that starts with it.
          const letter = event.key.toLowerCase();
          const next = [...rows.slice(at + 1), ...rows.slice(0, at + 1)].find((row) => (row._item.label || '').toLowerCase().startsWith(letter));
          if (next) { stop(); setActive(panel, next); }
        }
    }
  }

  function openMenu(options) {
    closeMenu(true, true);
    const previous = document.activeElement;
    const layer = openLayer({ onDismiss: () => closeMenu() });
    layer.addEventListener('keydown', onMenuKey);
    const items = (typeof options.items === 'function' ? options.items() : options.items) || [];
    const panel = buildPanel(items, 0);
    layer.append(panel);
    menu = { layer, options, panels: [panel], previous, timer: 0, context: null };

    if (options.anchor) {
      const w = panel.offsetWidth, h = panel.offsetHeight;
      const W = window.innerWidth, H = window.innerHeight;
      const r = options.anchor.getBoundingClientRect();
      const right = options.align === 'right' || r.left + w > W - 8;
      const x = right ? r.right - w : r.left;
      let y = r.bottom + 6, fromY = '0%';
      if (y + h > H - 8 && r.top - 6 - h >= 8) { y = r.top - 6 - h; fromY = '100%'; }
      panel.style.left = clamp(x, 8, Math.max(8, W - w - 8)) + 'px';
      panel.style.top = clamp(y, 8, Math.max(8, H - h - 8)) + 'px';
      panel.style.setProperty('--from-x', right ? '100%' : '0%');
      panel.style.setProperty('--from-y', fromY);
      options.anchor.classList.add('is-open');
    } else placeAt(panel, options.at);
    layer.whenShown(() => panel.focus({ preventScroll: true }));   // arrows, Enter and Esc then reach the menu
    if (options.keyboard) setActive(panel, rowsOf(panel)[0]);
  }

  // A menu that opens where the mouse was pressed: down and to the right of it, or the other way where it would not fit.
  function placeAt(panel, at) {
    const w = panel.offsetWidth, h = panel.offsetHeight;
    const W = window.innerWidth, H = window.innerHeight;
    let x = at.x, y = at.y, fromX = '0%', fromY = '0%';
    if (x + w > W - 8) { x = at.x - w; fromX = '100%'; }
    if (y + h > H - 8) { y = Math.max(8, at.y - h); fromY = '100%'; }
    panel.style.left = clamp(x, 8, Math.max(8, W - w - 8)) + 'px';
    panel.style.top = clamp(y, 8, Math.max(8, H - h - 8)) + 'px';
    panel.style.setProperty('--from-x', fromX);
    panel.style.setProperty('--from-y', fromY);
  }

  // A line's own right-click menu (a bookmark's, in a folder's menu) opens over the menu that line is in, and that
  // menu stays open behind it, as in other browsers. Esc, or a click on the menu behind, closes only the right-click
  // menu; a right click on another line there opens that line's own instead; a click anywhere else closes both, and
  // so does a choice made in it. With no menu open it is an ordinary menu.
  function openContext(options) {
    if (!menu) { openMenu(options); return; }
    clearTimeout(menu.timer);
    closeContext(true);
    const items = (typeof options.items === 'function' ? options.items() : options.items) || [];
    // Over the menu behind, so that it takes no pointer while this one is open (no line lights up, no folder opens).
    const shield = el('div', 'tc-menu-shield');
    const panel = buildPanel(items, menu.panels.length);
    panel.classList.add('is-context');
    const onMenuBehind = (x, y) => !!menu && menu.panels.some((p) => {
      const r = p.getBoundingClientRect();
      return x >= r.left && x < r.right && y >= r.top && y < r.bottom;
    });
    shield.addEventListener('pointerdown', (event) => {
      if (event.button === 2) return;   // (a right click is handled as one: below)
      event.preventDefault();
      if (onMenuBehind(event.clientX, event.clientY)) closeContext();
      else closeMenu();
    });
    shield.addEventListener('contextmenu', (event) => {
      event.preventDefault();
      const x = event.clientX, y = event.clientY;
      if (!onMenuBehind(x, y)) { closeMenu(); return; }
      closeContext(true);
      const under = document.elementFromPoint(x, y);
      const row = under && under.closest ? under.closest('.t-menu-item') : null;
      if (row && row._item && row._item.context && !row._item.disabled) {
        setActive(row.parentElement, row);
        row._item.context(x, y);
      } else if (menu) {
        const back = menu.panels[menu.panels.length - 1];
        if (back) back.focus({ preventScroll: true });
      }
    });
    menu.layer.append(shield, panel);
    menu.context = { panel, shield };
    placeAt(panel, options.at);
    panel.focus({ preventScroll: true });   // arrows, Enter and Esc reach it
    if (options.keyboard) setActive(panel, rowsOf(panel)[0]);
  }

  function closeContext(quiet) {
    const c = menu && menu.context;
    if (!c) return;
    menu.context = null;
    c.shield.remove();
    c.panel.remove();
    if (quiet) return;
    const back = menu.panels[menu.panels.length - 1];   // the keyboard goes back to the menu behind
    if (back) back.focus({ preventScroll: true });
  }

  // ----- dialogs -----

  let dialog = null;

  function field(labelText, control) {
    const wrap = el('label', 't-field');
    const label = el('span');
    label.textContent = labelText;
    wrap.append(label, control);
    return wrap;
  }

  function button(text, className) {
    const b = el('button', 't-btn' + (className ? ' ' + className : ''));
    b.type = 'button';
    b.textContent = text;
    return b;
  }

  // A new profile's circle as it will look: the first letter of its name on its color. Pressed, it shows the colors a
  // circle can have under the name, and a pick closes them. (Settings > Profiles has the same: Settings\settings.js.)
  function circlePick(colors, color, nameInput) {
    let chosen = String(color || colors[0] || '#2563EB').toUpperCase();
    const circle = el('button', 't-circle');
    circle.type = 'button';
    circle.title = tr('Circle color');
    circle.setAttribute('aria-label', tr('Circle color'));
    circle.setAttribute('aria-expanded', 'false');
    const letter = el('span', 't-circle-letter');
    circle.append(letter, el('span', 't-circle-badge', icon('palette')));
    const list = el('div', 't-circle-colors');
    list.hidden = true;
    list.setAttribute('role', 'radiogroup');
    list.setAttribute('aria-label', tr('Circle color'));
    const swatches = colors.map((hex) => {
      const swatch = el('button', 't-circle-color');
      swatch.type = 'button';
      swatch.title = hex;
      swatch.setAttribute('aria-label', hex);
      swatch.setAttribute('role', 'radio');
      swatch.style.setProperty('--dot', hex);
      swatch.addEventListener('click', () => { chosen = hex.toUpperCase(); show(false); draw(); nameInput.focus(); });
      list.append(swatch);
      return swatch;
    });
    function draw() {
      circle.style.setProperty('--dot', chosen);
      letter.textContent = (Array.from(nameInput.value.trim())[0] || '').toUpperCase();
      swatches.forEach((swatch, i) => {
        const on = colors[i].toUpperCase() === chosen;
        swatch.classList.toggle('is-chosen', on);
        swatch.setAttribute('aria-checked', String(on));
      });
    }
    function show(on) {
      list.hidden = !on;
      circle.setAttribute('aria-expanded', String(on));
    }
    circle.addEventListener('click', () => {
      show(list.hidden);
      if (!list.hidden) (swatches.find((s) => s.classList.contains('is-chosen')) || swatches[0] || circle).focus();
    });
    nameInput.addEventListener('input', draw);
    draw();
    return { circle, colors: list, value: () => chosen };
  }

  // Shows a dialog and resolves with { action, ...values }. action is "cancel" when it was dismissed.
  function showDialog(spec) {
    if (dialog) dialog.finish({ action: 'cancel' });
    closeMenu(true, true);
    return new Promise((resolve) => {
      const previous = document.activeElement;
      const bubble = spec.kind === 'bookmark';
      const layer = openLayer({ dim: !bubble, onDismiss: () => finish({ action: 'cancel' }) });
      const box = el('form', 't-dialog tc-pop tc-dialog' + (bubble ? ' is-bubble' : ''));
      box.setAttribute('role', 'dialog');
      box.setAttribute('aria-modal', 'true');
      box.noValidate = true;
      const title = el('h2');
      title.textContent = spec.title || '';
      box.append(title);
      if (spec.message) {
        const message = el('p');
        message.textContent = spec.message;
        box.append(message);
      }
      const body = el('div', 't-dialog-body');
      const actions = el('div', 't-dialog-actions');
      let collect = () => ({});
      let first = null;

      if (spec.kind === 'prompt') {
        const input = el('input', 't-input');
        input.type = 'text';
        input.value = spec.value || '';
        input.maxLength = 200;
        input.setAttribute('aria-label', spec.title || tr('Value'));
        body.append(input);
        first = input;
        collect = () => ({ value: input.value });
      } else if (spec.kind === 'profile') {
        // A new profile: its name, with its circle before it (pressed, the circle shows the colors it can have).
        const input = el('input', 't-input');
        input.type = 'text';
        input.value = spec.value || '';
        input.maxLength = 40;
        input.setAttribute('aria-label', spec.message || spec.title || tr('Value'));
        const pick = circlePick(spec.colors || [], spec.color, input);
        const row = el('div', 't-field-lead');
        row.append(pick.circle, input);
        body.append(row, pick.colors);
        first = input;
        collect = () => ({ value: input.value, color: pick.value() });
      } else if (spec.kind === 'choice') {
        const list = el('div', 'tc-choice');
        (spec.options || []).forEach((option, i) => {
          const row = el('label');
          const radio = el('input');
          radio.type = 'radio';
          radio.name = 'choice';
          radio.value = String(i);
          radio.checked = i === 0;
          const text = el('span');
          text.textContent = option;
          row.append(radio, text);
          row.addEventListener('dblclick', () => submit());
          list.append(row);
          if (i === 0) first = radio;
        });
        body.append(list);
        collect = () => ({ index: Number((list.querySelector('input:checked') || {}).value || 0) });
      } else if (bubble) {
        const name = el('input', 't-input');
        name.type = 'text';
        name.value = spec.name || '';
        body.append(field(tr('Name'), name));
        const url = el('input', 't-input');
        url.type = 'text';
        url.value = spec.url || '';
        url.spellcheck = false;
        if (!spec.isFolder) body.append(field(tr('Address'), url));
        const folder = el('select', 't-select');
        for (const f of spec.folders || []) {
          const option = el('option');
          option.value = String(f.id);
          option.textContent = '   '.repeat(f.depth || 0) + f.label;
          option.selected = f.id === spec.folder;
          folder.append(option);
        }
        body.append(field(tr('Folder'), folder));
        first = name;
        collect = () => ({ name: name.value, url: url.value, folder: Number(folder.value) });
        const remove = button(tr('Remove'), 'is-ghost is-danger is-left');
        remove.addEventListener('click', () => finish({ action: 'remove' }));
        actions.append(remove);
      }

      const okAction = bubble ? 'done' : 'ok';
      if (spec.kind !== 'info') {
        const cancel = button(spec.cancel || tr('Cancel'));
        cancel.addEventListener('click', () => finish({ action: 'cancel' }));
        actions.append(cancel);
      }
      const ok = button(spec.ok || (bubble ? tr('Done') : tr('OK')), spec.danger ? 'is-primary is-destructive' : 'is-primary');
      ok.type = 'submit';
      actions.append(ok);
      if (body.childNodes.length) box.append(body);
      box.append(actions);
      layer.append(box);

      if (bubble) {
        const star = document.getElementById('star').getBoundingClientRect();
        box.style.setProperty('--bubble-y', Math.round(star.bottom + 10) + 'px');
        box.style.setProperty('--bubble-right', Math.max(8, Math.round(window.innerWidth - star.right - 6)) + 'px');
      }

      function submit() {
        const values = collect();
        if ((spec.kind === 'prompt' || spec.kind === 'profile') && !String(values.value || '').trim()) { first.focus(); return; }
        finish(Object.assign({ action: okAction }, values));
      }
      function finish(result) {
        if (dialog !== handle) return;
        dialog = null;
        closeLayer(layer, false);
        giveFocusBack(previous);
        resolve(result);
      }
      box.addEventListener('submit', (event) => { event.preventDefault(); submit(); });
      layer.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') { event.preventDefault(); finish({ action: 'cancel' }); return; }
        if (event.key !== 'Tab') return;
        // The keyboard stays inside the dialog.
        const stops = [...box.querySelectorAll('input, select, textarea, button')].filter((n) => !n.disabled && n.offsetParent);
        if (!stops.length) return;
        const at = stops.indexOf(document.activeElement);
        event.preventDefault();
        stops[(at + (event.shiftKey ? -1 : 1) + stops.length) % stops.length].focus();
      });
      const handle = { finish };
      dialog = handle;
      layer.whenShown(() => {
        const target = first || ok;
        target.focus({ preventScroll: true });
        if (target.select && target.type === 'text') target.select();
      });
    });
  }

  // ----- notices -----

  let notice = null;

  function hideNotice(instant) {
    const n = notice;
    if (!n) return;
    notice = null;
    clearTimeout(n.timer);
    const remove = () => { n.wrap.remove(); Layout.now(); };
    if (instant || document.documentElement.dataset.anim === 'off') { remove(); return; }
    n.box.classList.add('is-closing');
    setTimeout(remove, 220);
  }

  function showNotice(message) {
    hideNotice(true);
    const wrap = el('div', 'tc-noticewrap');
    const box = el('div', 'tc-notice');
    box.dataset.hit = '30';
    box.setAttribute('role', 'status');
    const text = el('span', 'tc-notice-text');
    text.textContent = message.text || '';
    box.append(text);
    if (message.action) {
      const action = el('button', 'tc-notice-action');
      action.type = 'button';
      action.textContent = message.action;
      action.addEventListener('click', () => { hideNotice(false); Host.post({ t: 'notice', id: message.id }); });
      box.append(action);
    }
    const close = el('button', 't-iconbtn tc-notice-close', icon('close'));
    close.type = 'button';
    close.title = tr('Dismiss');
    close.setAttribute('aria-label', tr('Dismiss'));
    close.addEventListener('click', () => hideNotice(false));
    box.append(close);
    wrap.append(box);
    overlays.append(wrap);
    const life = message.ms > 0 ? message.ms : message.action ? 7000 : 4500;
    const state = { wrap, box, timer: setTimeout(() => hideNotice(false), life) };
    box.addEventListener('pointerenter', () => clearTimeout(state.timer));
    box.addEventListener('pointerleave', () => { state.timer = setTimeout(() => { if (notice === state) hideNotice(false); }, 2500); });
    notice = state;
    if (IGYO.fx && IGYO.fx.notice) IGYO.fx.notice(text, message.text || '');
    Layout.now();
  }

  // (The questions the Claude bridge puts to the person are not drawn here: they stand in the page's bottom left
  // corner, in a window of the app's own that screen recordings leave out. See ClaudeSign.cs.)

  // ----- suggestions (Google's predictions for what is being typed; nothing from your history) -----

  const suggest = {
    visible: false,
    rows: [],
    index: 0,
    panel: null,

    show(data) {
      this.hide();
      const items = data.items || [];
      if (!items.length) return;
      const typed = data.typed || '';
      const box = document.getElementById('omnibox').getBoundingClientRect();
      const panel = el('div', 'tc-suggest');
      panel.dataset.hit = '30';
      panel.setAttribute('role', 'listbox');
      panel.setAttribute('aria-label', tr('Suggestions'));
      panel.style.left = Math.round(box.left) + 'px';
      panel.style.top = Math.round(box.bottom + 6) + 'px';
      panel.style.width = Math.round(box.width) + 'px';
      this.rows = [{ fill: typed, typed: true }, ...items.map((item) => ({ fill: item.text, item }))];
      this.rows.forEach((entry, i) => {
        const row = el('div', 'tc-suggest-row' + (i === 0 ? ' is-active' : ''));
        row.setAttribute('role', 'option');
        const mark = el('span', 'tc-suggest-icon');
        const item = entry.item;
        if (item && item.site && item.icon) {
          const image = new Image();
          image.alt = '';
          image.onerror = () => { image.remove(); mark.innerHTML = icon('globe'); };
          image.src = item.icon;
          mark.append(image);
        } else mark.innerHTML = icon(item && item.site ? 'globe' : 'search');
        const text = el('span', 's-text');
        const note = el('span', 's-note');
        if (entry.typed) {
          text.textContent = typed;
          note.textContent = '—  ' + (data.typedIsAddress ? tr('Open') : tr('Search {0}', data.engine || tr('the web')));
        } else if (item.site) {
          text.classList.add('s-site');
          text.textContent = item.display || item.text;
          if (item.title) note.textContent = '—  ' + item.title;
        } else if (typed && item.text.toLowerCase().startsWith(typed.toLowerCase())) {
          // Like other browsers: what you typed in regular weight, the rest of the prediction in bold.
          text.append(document.createTextNode(item.text.slice(0, typed.length)));
          const rest = el('b');
          rest.textContent = item.text.slice(typed.length);
          text.append(rest);
        } else text.textContent = item.text;
        row.append(mark, text, note);
        row.addEventListener('pointerenter', () => this.mark(i));
        row.addEventListener('mousedown', (event) => {
          event.preventDefault();   // the address bar keeps the keyboard
          if (event.button === 0) IGYO.ui.go(entry.fill, false);
        });
        panel.append(row);
        entry.row = row;
      });
      overlays.append(panel);
      this.panel = panel;
      this.index = 0;
      this.visible = true;
      Layout.now();
    },

    mark(i) {
      this.index = i;
      this.rows.forEach((entry, n) => entry.row.classList.toggle('is-active', n === i));
    },

    // Up and Down in the address bar move through the list and fill the address bar.
    step(direction) {
      if (!this.visible || !this.rows.length) return null;
      this.mark((this.index + direction + this.rows.length) % this.rows.length);
      return this.rows[this.index].fill;
    },

    hide() {
      if (!this.panel) return;
      this.panel.remove();
      this.panel = null;
      this.rows = [];
      this.visible = false;
      Layout.now();
    },
  };

  // ----- the color picker -----
  // The app sends a picture of the page as it is on show. It is laid exactly over the page, so nothing seems to
  // change, and a magnifier follows the mouse: the square in its middle is the pixel a click picks. Only the
  // page can be picked from. Esc, a right click, or a click outside the page leaves without picking.

  let picker = null;

  function closePicker(hex) {
    const p = picker;
    if (!p) return;
    picker = null;
    window.removeEventListener('keydown', p.onKey, true);
    closeLayer(p.layer, true);
    giveFocusBack(null);
    if (hex) Host.cmd('pickedColor', { hex });
  }

  function showPicker(message) {
    closePicker(null);
    closeMenu(true, true);
    const image = new Image();
    image.onload = () => {
      const area = document.getElementById('viewport').getBoundingClientRect();
      if (area.width < 2 || area.height < 2) return;
      const pixels = document.createElement('canvas');
      pixels.width = image.naturalWidth;
      pixels.height = image.naturalHeight;
      const source = pixels.getContext('2d', { willReadFrequently: true });
      source.drawImage(image, 0, 0);

      const layer = openLayer({ onDismiss: () => closePicker(null) });
      layer.classList.add('tc-picker');
      const shot = el('canvas', 'tc-picker-shot');
      shot.width = image.naturalWidth;
      shot.height = image.naturalHeight;
      shot.getContext('2d').drawImage(image, 0, 0);
      shot.style.left = area.left + 'px';
      shot.style.top = area.top + 'px';
      shot.style.width = area.width + 'px';
      shot.style.height = area.height + 'px';
      const lens = el('div', 'tc-picker-lens');
      const zoom = el('canvas', 'tc-picker-zoom');
      const CELLS = 11, CELL = 10;   // an 11 x 11 pixel neighbourhood, each pixel drawn 10 times larger
      zoom.width = zoom.height = CELLS * CELL;
      const label = el('div', 'tc-picker-label');
      const swatch = el('i', 'tc-picker-swatch');
      const code = el('span', 'tc-picker-code');
      label.append(swatch, code);
      lens.append(zoom, label);
      lens.hidden = true;
      layer.append(shot, lens);

      const hexAt = (x, y) => {
        const d = source.getImageData(clamp(x, 0, pixels.width - 1), clamp(y, 0, pixels.height - 1), 1, 1).data;
        return '#' + [d[0], d[1], d[2]].map((v) => v.toString(16).padStart(2, '0')).join('').toUpperCase();
      };
      // Where in the picture the mouse is, or null when it is not over the page.
      const spot = (event) => {
        const r = shot.getBoundingClientRect();
        if (event.clientX < r.left || event.clientX >= r.right || event.clientY < r.top || event.clientY >= r.bottom) return null;
        return { x: Math.floor((event.clientX - r.left) * pixels.width / r.width), y: Math.floor((event.clientY - r.top) * pixels.height / r.height) };
      };
      const move = (event) => {
        const at = spot(event);
        lens.hidden = !at;
        if (!at) return;
        const g = zoom.getContext('2d');
        g.imageSmoothingEnabled = false;
        g.fillStyle = '#000';
        g.fillRect(0, 0, zoom.width, zoom.height);
        const half = (CELLS - 1) / 2;
        g.drawImage(pixels, at.x - half, at.y - half, CELLS, CELLS, 0, 0, zoom.width, zoom.height);
        g.strokeStyle = '#fff';
        g.lineWidth = 2;
        g.strokeRect(half * CELL - 1, half * CELL - 1, CELL + 2, CELL + 2);
        g.strokeStyle = '#000';
        g.lineWidth = 1;
        g.strokeRect(half * CELL - 2.5, half * CELL - 2.5, CELL + 5, CELL + 5);
        const hex = hexAt(at.x, at.y);
        swatch.style.background = hex;
        code.textContent = hex;
        // Beside the mouse, on the side where there is room.
        const w = lens.offsetWidth || 130, h = lens.offsetHeight || 150;
        let x = event.clientX + 18, y = event.clientY + 18;
        if (x + w > window.innerWidth - 6) x = event.clientX - 18 - w;
        if (y + h > window.innerHeight - 6) y = event.clientY - 18 - h;
        lens.style.left = Math.max(6, x) + 'px';
        lens.style.top = Math.max(6, y) + 'px';
      };
      layer.addEventListener('pointermove', move);
      shot.addEventListener('pointerdown', (event) => {
        event.preventDefault();
        event.stopPropagation();
        const at = event.button === 0 ? spot(event) : null;
        closePicker(at ? hexAt(at.x, at.y) : null);
      });
      const onKey = (event) => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); closePicker(null); } };
      window.addEventListener('keydown', onKey, true);
      picker = { layer, onKey };
      layer.tabIndex = -1;
      layer.whenShown(() => layer.focus({ preventScroll: true }));
    };
    image.src = message.image || '';
  }

  // ----- a selection, translated -----
  // The app sends the state of a translation ("working", then "done", "same" or "error") and where on the page the
  // selection was right-clicked. It is shown in a small panel there, over the page; the page around it stays
  // usable, like under a notice. The two languages are named at its top and each can be changed: the languages
  // used before come first, the long list of all of them is one line further. The app does the translating and
  // remembers what was picked.

  let translate = null;
  let translateSeq = 0;
  let languageNames = null;

  // A language's name, in the language the interface is shown in ("ja" -> "Japanese", "Japonés", "日本語"). On a
  // button or a menu line it starts with a capital; inside a sentence it is written as that language writes it
  // ("ya está en japonés").
  function languageName(id, inSentence) {
    if (!id) return '';
    try {
      languageNames = languageNames || new Intl.DisplayNames([IGYO.lang || 'en'], { type: 'language' });
      const name = languageNames.of(id);
      if (!name) return id;
      return inSentence ? name : name.charAt(0).toLocaleUpperCase(IGYO.lang || 'en') + name.slice(1);
    } catch { return id; }
  }

  function hideTranslate(id, instant) {
    const t = translate;
    if (!t || (id != null && t.id !== id)) return;
    translate = null;
    const remove = () => { t.wrap.remove(); Layout.now(); };
    if (instant || document.documentElement.dataset.anim === 'off') { remove(); return; }
    t.box.classList.add('is-closing');
    setTimeout(remove, 160);
  }

  function showTranslate(m) {
    // The same translation moving on (found out the language, got its text) keeps its panel where it is.
    let t = translate && translate.id === m.id ? translate : null;
    if (!t) {
      hideTranslate(null, true);
      for (const old of overlays.querySelectorAll('.tc-translatewrap')) old.remove();
      const wrap = el('div', 'tc-translatewrap');
      const box = el('div', 'tc-translate');
      box.dataset.hit = '30';
      box.tabIndex = -1;
      box.setAttribute('role', 'dialog');
      box.setAttribute('aria-label', tr('Translation'));
      const head = el('div', 'tc-translate-head');
      const from = el('button', 'tc-translate-lang');
      const to = el('button', 'tc-translate-lang');
      for (const b of [from, to]) { b.type = 'button'; b.setAttribute('aria-haspopup', 'menu'); }
      const arrow = el('span', 'tc-translate-arrow', icon('forward'));
      const close = el('button', 't-iconbtn tc-translate-close', icon('close'));
      close.type = 'button';
      close.title = tr('Close');
      close.setAttribute('aria-label', tr('Close'));
      head.append(from, arrow, to, close);
      const body = el('div', 'tc-translate-body');
      const note = el('div', 'tc-translate-note');
      const foot = el('div', 'tc-translate-foot');
      const service = el('span', 'tc-translate-service');
      // (the shared kinds of button: every kit knows how to draw those on a panel of this color)
      const open = el('button', 't-btn is-s tc-translate-open');
      open.type = 'button';
      open.textContent = tr('Open in Google Translate');
      const copy = el('button', 't-btn is-s is-primary tc-translate-copy');
      copy.type = 'button';
      copy.textContent = tr('Copy');
      // (the two buttons stay together: where a kit draws them too wide to stand beside the service's name, they go under it)
      const actions = el('div', 'tc-translate-actions');
      actions.append(open, copy);
      foot.append(service, actions);
      box.append(head, body, note, foot);
      wrap.append(box);
      overlays.append(wrap);
      t = translate = { id: m.id, wrap, box, from, to, body, note, service, open, copy, m };
      close.addEventListener('click', () => { hideTranslate(t.id, false); Host.cmd('translateClose'); Host.cmd('focusPage'); });
      copy.addEventListener('click', () => Host.cmd('translateCopy'));
      open.addEventListener('click', () => Host.cmd('translateOpen'));
      box.addEventListener('keydown', (event) => {
        if (event.key !== 'Escape' || menu) return;
        event.preventDefault();
        close.click();
      });
      // Picking a language: the ones used before first, every other one a line further.
      const pick = (side) => {
        const now = t.m;
        const chosen = side === 'from' ? (now.auto ? '' : now.from) : now.to;
        const send = (id) => Host.cmd('translateAgain', side === 'from' ? { from: id, to: now.to } : { from: now.auto ? '' : now.from, to: id });
        const line = (id) => ({ label: languageName(id), checked: id === chosen, run: () => send(id) });
        const other = side === 'from' ? now.to : now.from;   // never the same language on both sides
        const mine = (now.mine || []).filter((id) => id !== other);
        const items = [];
        if (side === 'from') items.push({ label: tr('Detect the language'), checked: chosen === '', run: () => send('') }, { sep: true });
        items.push(...mine.map(line));
        if (chosen && !mine.includes(chosen)) items.push(line(chosen));
        items.push({ sep: true }, {
          label: tr('All languages'),
          children: () => (now.all || []).filter((id) => id !== other).map((id) => [id, languageName(id)])
            .sort((a, b) => a[1].localeCompare(b[1], IGYO.lang || 'en')).map(([id]) => line(id)),
        });
        openMenu({ anchor: side === 'from' ? from : to, align: 'left', items });
      };
      from.addEventListener('click', () => pick('from'));
      to.addEventListener('click', () => pick('to'));
    }
    t.m = m;
    t.box.dataset.seq = String((translateSeq += 1));   // counts every state shown (checks wait on it)
    const name = languageName(m.from);
    t.from.textContent = m.auto ? (m.from ? tr('{0} (detected)', name) : m.state === 'working' ? tr('Detecting the language…') : tr('Detect the language')) : name;
    t.to.textContent = languageName(m.to);
    t.from.title = tr('Translate from another language');
    t.to.title = tr('Translate into another language');
    t.box.dataset.state = m.state;
    t.body.textContent = m.state === 'working' ? tr('Translating…')
      : m.state === 'same' ? tr('This text is already in {0}. Choose another language above.', languageName(m.to, true))
      : m.state === 'error' ? (m.error || tr('The translation service did not give a translation.'))
      : (m.text || '');
    t.note.textContent = m.note || '';
    t.note.hidden = !m.note;
    t.service.textContent = m.state === 'done' ? tr('Translated by {0}', m.service || '') : '';
    t.copy.hidden = m.state !== 'done';
    t.open.hidden = m.state === 'working';

    // Under the place that was right-clicked (above it when there is no room below), inside the page's area.
    const view = document.getElementById('viewport').getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    const width = Math.min(440, Math.max(220, view.width - 16));
    t.box.style.width = width + 'px';
    t.box.style.setProperty('--translate-max', Math.max(80, Math.round(view.height * 0.5)) + 'px');
    const x = view.left + (m.x || 0) / dpr, y = view.top + (m.y || 0) / dpr;
    const height = t.box.offsetHeight;
    let top = y + 16;
    if (top + height > view.bottom - 8) top = y - 16 - height;
    t.wrap.style.left = clamp(x - 24, view.left + 8, Math.max(view.left + 8, view.right - width - 8)) + 'px';
    t.wrap.style.top = clamp(top, view.top + 8, Math.max(view.top + 8, view.bottom - height - 8)) + 'px';
    Layout.now();
  }

  // ----- the page's zoom -----
  // A small box under the zoom button in the address bar: the page's zoom, a step out, a step in, and the way back
  // to 100%. Opened with the button, it stays until a press somewhere else in the interface, Esc, another tab, or
  // the keyboard going to the page or to another window. It also comes up by itself when the zoom is changed (Ctrl
  // and the wheel, the zoom keys, the menu) and goes again a moment after the last change, unless the mouse is on
  // it (a click in it keeps it, like one opened with the button). Like a notice it leaves the page around it usable,
  // so Ctrl and the wheel keep reaching the page, and the figure follows. (Timers, not animation frames: those never
  // come in a window that is not being drawn.)

  let zoom = null;
  const zoomButton = () => document.getElementById('zoom');
  const onScreen = (node) => !!node && node.isConnected && !node.hidden && node.offsetWidth > 0 && node.offsetHeight > 0;

  function placeZoom() {
    const z = zoom;
    if (!z) return;
    const w = z.box.offsetWidth, h = z.box.offsetHeight;
    const W = window.innerWidth, H = window.innerHeight;
    const button = zoomButton();
    let x, y;
    if (onScreen(button)) {
      // Under the button, its right edge in line with the button's.
      const r = button.getBoundingClientRect();
      x = r.right - w;
      y = r.bottom + 8;
      if (y + h > H - 8 && r.top - 8 - h >= 8) y = r.top - 8 - h;
    } else {
      // No button to hang from (full screen, or a kit that shows none): the top right of the page.
      const view = document.getElementById('viewport').getBoundingClientRect();
      x = view.right - 16 - w;
      y = view.top + 16;
    }
    z.wrap.style.left = clamp(x, 8, Math.max(8, W - w - 8)) + 'px';
    z.wrap.style.top = clamp(y, 8, Math.max(8, H - h - 8)) + 'px';
  }

  // What the box shows: the zoom the app last told (S.nav.zoom) of the tab it was opened over.
  function updateZoom() {
    const z = zoom;
    if (!z) return;
    const nav = IGYO.S.nav || {};
    if (nav.tab !== z.tab) { hideZoom(true); return; }   // another tab is on show: its zoom is its own
    const figure = nav.zoom == null ? 100 : nav.zoom;
    const text = figure + '%';
    if (z.value.textContent !== text) z.value.textContent = text;
    z.box.setAttribute('aria-label', tr('Zoom {0}%', figure));
    placeZoom();
    Layout.now();
  }

  // Goes a moment after the last change, unless the mouse is on it or it was clicked.
  function lingerZoom(ms) {
    const z = zoom;
    if (!z) return;
    clearTimeout(z.timer);
    if (z.kept || z.hover) return;
    z.timer = setTimeout(() => { if (zoom === z && !z.kept && !z.hover) hideZoom(false); }, ms);
  }

  // how: 'auto' (the zoom was just changed) or 'click' (the button).
  function showZoom(how) {
    let z = zoom;
    if (!z) {
      const wrap = el('div', 'tc-zoomwrap');
      const box = el('div', 'tc-zoom');
      box.dataset.hit = '30';
      box.tabIndex = -1;
      box.setAttribute('role', 'dialog');
      const value = el('output', 'tc-zoom-value');
      const out = el('button', 't-iconbtn tc-zoom-step', icon('minus'));
      const into = el('button', 't-iconbtn tc-zoom-step', icon('plus'));
      const reset = el('button', 't-btn is-s tc-zoom-reset');
      for (const b of [out, into, reset]) b.type = 'button';
      out.title = tr('Zoom out');
      into.title = tr('Zoom in');
      out.setAttribute('aria-label', out.title);
      into.setAttribute('aria-label', into.title);
      reset.textContent = tr('Reset');
      reset.title = tr('Reset to 100%');
      box.append(value, out, into, reset);
      wrap.append(box);
      overlays.append(wrap);
      z = zoom = { wrap, box, value, tab: (IGYO.S.nav || {}).tab, kept: false, hover: false, timer: 0 };
      const keep = () => { z.kept = true; clearTimeout(z.timer); };
      const step = (dir) => { keep(); Host.cmd('zoom', { dir, from: 'popup' }); };
      out.addEventListener('click', () => step(-1));
      into.addEventListener('click', () => step(1));
      reset.addEventListener('click', () => step(0));
      box.addEventListener('pointerdown', keep);
      box.addEventListener('pointerenter', () => { z.hover = true; clearTimeout(z.timer); });
      box.addEventListener('pointerleave', () => { z.hover = false; lingerZoom(1500); });
      box.addEventListener('keydown', (event) => {
        if (event.key !== 'Escape') return;
        event.preventDefault();
        event.stopPropagation();
        hideZoom(false);
        Host.cmd('focusPage');
      });
    }
    if (how === 'click') { z.kept = true; clearTimeout(z.timer); }
    else lingerZoom(2000);
    IGYO.ui.renderZoom();   // the button stays while the box hangs from it
    updateZoom();
    if (how === 'click') z.box.focus({ preventScroll: true });   // Esc reaches it
  }

  function hideZoom(instant) {
    const z = zoom;
    if (!z) return;
    zoom = null;
    clearTimeout(z.timer);
    const remove = () => { z.wrap.remove(); Layout.now(); };
    if (instant || document.documentElement.dataset.anim === 'off') remove();
    else {
      z.box.classList.add('is-closing');
      setTimeout(remove, 160);
    }
    IGYO.ui.renderZoom();   // back at 100%, the button goes with the box
  }

  // The button opens the box, and closes one it opened (one that came up by itself is kept instead).
  function toggleZoom() {
    if (zoom && zoom.kept) hideZoom(false);
    else showZoom('click');
  }

  // A press anywhere else in the interface closes it (a press on the button is the button's).
  document.addEventListener('pointerdown', (event) => {
    const z = zoom;
    if (!z || z.box.contains(event.target)) return;
    const button = zoomButton();
    if (button && button.contains(event.target)) return;
    hideZoom(false);
  }, true);

  // The box and a little room around it belong to the interface, so Ctrl and the wheel there would no longer reach
  // the page: they go on zooming it from here, a step for each notch of the wheel.
  let zoomWheel = 0;
  document.addEventListener('wheel', (event) => {
    if (!zoom || !event.ctrlKey || menu || dialog) return;
    const r = zoom.box.getBoundingClientRect(), room = 40;   // (the room is --hit-pad, and a little more)
    if (event.clientX < r.left - room || event.clientX > r.right + room || event.clientY < r.top - room || event.clientY > r.bottom + room) return;
    event.preventDefault();
    const delta = event.deltaMode === 1 ? event.deltaY * 33 : event.deltaY;   // (lines, or pixels)
    if (Math.sign(delta) !== Math.sign(zoomWheel)) zoomWheel = 0;
    zoomWheel += delta;
    if (Math.abs(zoomWheel) < 50) return;
    Host.cmd('zoom', { dir: zoomWheel < 0 ? 1 : -1, from: 'popup' });
    zoomWheel = 0;
    lingerZoom(2000);
  }, { capture: true, passive: false });

  window.addEventListener('resize', () => { if (zoom) updateZoom(); });

  // ----- the tab search, as Chrome's: a box to type in over the open tabs and the tabs closed lately -----
  // The app sends the lists (MainForm.TabSearch.cs): the open tabs of this profile's windows, the one used last first,
  // and this window's closed tabs, the one closed last first. What is typed keeps the lines that have every word of
  // it in their title or address; the arrows move the marked line, Enter takes it, Esc closes. It hangs from the mark
  // at the strip's start when the kit shows one, else from the strip's start itself.

  let search = null;
  const CLOSED_SHOWN = 12;   // closed tabs listed while nothing is typed (with something typed: every one that matches)
  const relative = (() => { try { return new Intl.RelativeTimeFormat(IGYO.lang || undefined, { numeric: 'auto' }); } catch { return null; } })();

  function ago(at) {
    if (!at || !relative) return '';
    const s = Math.max(0, Math.round((Date.now() - at) / 1000));
    if (s < 60) return relative.format(-s, 'second');
    const m = Math.round(s / 60);
    if (m < 60) return relative.format(-m, 'minute');
    const h = Math.round(m / 60);
    if (h < 24) return relative.format(-h, 'hour');
    return relative.format(-Math.round(h / 24), 'day');
  }

  function siteOf(url) {
    try { return url ? new URL(url).host || url : ''; } catch { return url || ''; }
  }

  function showTabSearch(m, anchor) {
    if (m.again) { if (search) { search.data = m; renderSearch(); } return; }   // after a change made in it
    if (search) { search.input.focus({ preventScroll: true }); return; }
    closeMenu(true, true);
    const previous = document.activeElement;
    const layer = openLayer({ onDismiss: () => closeTabSearch() });
    const panel = el('div', 't-menu tc-pop tc-tabsearch');
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', tr('Search tabs'));
    const box = el('label', 'tc-ts-search', icon('search'));
    const input = el('input', 'tc-ts-input');
    input.type = 'text';
    input.placeholder = tr('Search tabs');
    input.spellcheck = false;
    input.autocomplete = 'off';
    const keys = el('span', 't-kbd');
    keys.textContent = 'Ctrl+Shift+A';
    box.append(input, keys);
    const list = el('div', 'tc-ts-list');
    list.setAttribute('role', 'listbox');
    panel.append(box, list);
    layer.append(panel);
    const usable = anchor && anchor.isConnected && anchor.offsetWidth > 0 ? anchor : null;
    search = { layer, panel, input, list, data: m, previous, anchor: usable, rows: [], active: null, foldClosed: false };
    input.addEventListener('input', renderSearch);
    layer.addEventListener('keydown', onSearchKey);
    renderSearch();
    // Under the mark, from its left edge; without one, under the strip's start.
    const strip = document.getElementById('tabstrip').getBoundingClientRect();
    const r = usable ? usable.getBoundingClientRect() : { left: strip.left + 8, bottom: strip.bottom };
    panel.style.left = clamp(r.left, 8, Math.max(8, window.innerWidth - panel.offsetWidth - 8)) + 'px';
    panel.style.top = clamp(r.bottom + 6, 8, Math.max(8, window.innerHeight - panel.offsetHeight - 8)) + 'px';
    panel.style.setProperty('--from-x', '0%');
    panel.style.setProperty('--from-y', '0%');
    if (usable) usable.classList.add('is-open');
    layer.whenShown(() => input.focus({ preventScroll: true }));
  }

  function closeTabSearch(instant, keepFocus) {
    const s = search;
    if (!s) return;
    search = null;
    if (s.anchor) s.anchor.classList.remove('is-open');
    closeLayer(s.layer, instant);
    if (!keepFocus) giveFocusBack(s.previous);
  }

  function renderSearch() {
    const s = search;
    if (!s) return;
    const words = s.input.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    const matches = (t) => words.every((w) => (t.title || '').toLowerCase().includes(w) || (t.url || '').toLowerCase().includes(w));
    const open = (s.data.open || []).filter(matches);
    const closedAll = (s.data.closed || []).filter(matches);
    const closed = words.length ? closedAll : closedAll.slice(0, CLOSED_SHOWN);
    const rows = [];
    const text = (tag, className, words) => { const node = el(tag, className); node.textContent = words; return node; };
    s.list.replaceChildren();
    if (open.length) {
      s.list.append(text('div', 't-menu-head', tr('Open tabs')));
      for (const t of open) { const row = searchRow(t, false); rows.push(row); s.list.append(row); }
    }
    if (closed.length) {
      // Its head folds the list away and back, as in Chrome (while something is typed, it stays open).
      const shown = !s.foldClosed || words.length > 0;
      const head = el('div', 't-menu-head tc-ts-head');
      const fold = el('button', 'tc-ts-fold' + (shown ? '' : ' is-folded'), icon('chevron-down'));
      fold.type = 'button';
      fold.tabIndex = -1;
      fold.setAttribute('aria-label', tr('Recently closed'));
      fold.setAttribute('aria-expanded', String(shown));
      fold.addEventListener('click', () => { s.foldClosed = !s.foldClosed; renderSearch(); s.input.focus({ preventScroll: true }); });
      head.append(text('span', '', tr('Recently closed')), fold);
      s.list.append(head);
      if (shown) for (const t of closed) { const row = searchRow(t, true); rows.push(row); s.list.append(row); }
    }
    if (!open.length && !closed.length) s.list.append(text('div', 'tc-ts-none', tr('No tabs found')));
    s.rows = rows;
    setSearchActive(rows[0] || null, false);
  }

  function searchRow(t, closed) {
    const row = el('div', 't-menu-item tc-ts-row');
    row.setAttribute('role', 'option');
    row._t = t;
    row._closed = closed;
    const mark = el('span', 't-menu-icon');
    if (t.icon && t.icon.startsWith('/')) {
      const image = new Image();
      image.alt = '';
      image.onerror = () => { image.remove(); mark.innerHTML = icon('globe'); };
      image.src = t.icon;
      mark.append(image);
    } else mark.innerHTML = icon(t.icon || 'globe');
    const lines = el('span', 'tc-ts-text');
    const title = el('span', 'tc-ts-title');
    title.textContent = t.title || tr('New tab');
    const sub = el('span', 'tc-ts-sub');
    sub.textContent = [siteOf(t.url), ago(t.at)].filter(Boolean).join(' • ');
    lines.append(title, sub);
    row.title = t.url || '';
    row.append(mark, lines);
    if (!closed) {
      const x = el('button', 'tc-ts-close', icon('close'));
      x.type = 'button';
      x.tabIndex = -1;
      x.title = tr('Close tab');
      x.setAttribute('aria-label', tr('Close tab'));
      x.addEventListener('click', (event) => {
        event.stopPropagation();
        Host.cmd('searchCloseTab', { id: t.id });   // the app sends the lists again
      });
      row.append(x);
    }
    row.addEventListener('pointermove', () => { if (search && search.active !== row) setSearchActive(row, false); });
    row.addEventListener('click', () => takeSearchRow(row));
    return row;
  }

  function setSearchActive(row, scroll) {
    const s = search;
    if (!s) return;
    if (s.active && s.active !== row) s.active.classList.remove('is-active');
    s.active = row || null;
    if (row) {
      row.classList.add('is-active');
      if (scroll) row.scrollIntoView({ block: 'nearest' });
    }
  }

  function takeSearchRow(row) {
    if (!row || !search) return;
    const t = row._t;
    closeTabSearch(false, true);
    if (row._closed) Host.cmd('searchReopen', { key: t.key });
    else Host.cmd('searchShowTab', { id: t.id });
  }

  function onSearchKey(event) {
    const s = search;
    if (!s) return;
    const rows = s.rows;
    const at = rows.indexOf(s.active);
    const stop = () => { event.preventDefault(); event.stopPropagation(); };
    switch (event.key) {
      case 'ArrowDown': stop(); if (rows.length) setSearchActive(rows[(at + 1) % rows.length], true); break;
      case 'ArrowUp': stop(); if (rows.length) setSearchActive(rows[at <= 0 ? rows.length - 1 : at - 1], true); break;
      case 'Enter': stop(); takeSearchRow(s.active); break;
      case 'Escape': stop(); closeTabSearch(); break;
      case 'Tab': stop(); break;
    }
  }

  // Clicking another window, or the page, closes menus (a dialog stays until it is answered), the tab search, and a
  // zoom box that was opened or kept with a click (one that came up by itself goes by itself).
  window.addEventListener('blur', () => { closeMenu(true, true); closeTabSearch(true, true); suggest.hide(); closePicker(null); if (zoom && zoom.kept) hideZoom(true); });

  IGYO.translate = { show: showTranslate, hide: hideTranslate, get isOpen() { return !!translate; } };
  IGYO.picker = { show: showPicker, close: () => closePicker(null), get isOpen() { return !!picker; } };
  IGYO.menu = {
    open: openMenu, close: closeMenu, get isOpen() { return !!menu; },
    // A line's own right-click menu: over the open menu (which stays), or an ordinary menu when none is open.
    context: openContext, get hasContext() { return !!(menu && menu.context); },
    // For a bookmark being dragged over open menus (chrome.js): the panels on show, the first one first; a folder's
    // line held under the mouse opens its own menu, and a line elsewhere closes the ones opened deeper.
    get panels() { return menu ? menu.panels.slice() : []; },
    openSub: (row) => { if (menu && row && row.isConnected) openSub(row, false); },
    closeDeeper: (depth) => closeDeeper(depth),
  };
  IGYO.dialog = { show: showDialog, get isOpen() { return !!dialog; } };
  IGYO.tabSearch = { show: showTabSearch, close: closeTabSearch, get isOpen() { return !!search; } };
  IGYO.notice = { show: showNotice, hide: hideNotice };
  IGYO.zoom = { show: showZoom, hide: hideZoom, toggle: toggleZoom, update: updateZoom, get isOpen() { return !!zoom; } };
  IGYO.suggest = suggest;
})();
