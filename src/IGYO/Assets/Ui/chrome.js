// IGYO — the browser's interface: tabs, toolbar, address bar, bookmarks bar and window buttons.
// The app (MainForm) owns the tabs and does the work; this page shows what it is told and says what was clicked.
// Titles and addresses come from websites, so they only ever go into the page as text, never as markup.
(() => {
  'use strict';

  const IGYO = window.IGYO;
  const icon = IGYO.icon;
  const tr = IGYO.tr;
  IGYO.translatePage();   // the tooltips and labels written in chrome.html itself
  const root = document.documentElement;
  const $ = (id) => document.getElementById(id);

  function el(tag, className, html) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (html) node.innerHTML = html;
    return node;
  }

  // ----- talking to the app -----

  const webview = window.chrome && window.chrome.webview;
  const handlers = new Map();
  const Host = {
    real: !!webview,
    post(message) {
      if (webview) webview.postMessage(message);
      else if (window.IGYODevHost) window.IGYODevHost.receive(message);
    },
    cmd(name, extra) { this.post(Object.assign({ t: 'cmd', name }, extra || {})); },
    on(type, fn) { handlers.set(type, fn); },
    deliver(message) {
      const fn = message && handlers.get(message.t);
      if (fn) fn(message);
    },
  };
  if (webview) webview.addEventListener('message', (event) => Host.deliver(event.data));

  // What the app last said.
  const S = {
    tabs: [], active: 0, nav: {}, bookmarks: { bar: [], other: [], otherId: 0 },
    settings: {}, profile: {}, theme: {}, win: {}, isPrivate: false,
    bridge: {},   // the Claude bridge: on for this profile, what is connected, which tabs are being read
  };

  // ----- where the page goes -----
  // The page shows through #viewport. The app is told where that is, and which floating things (menus,
  // notices) lie over the page, so it can make exactly those shapes belong to the interface.

  const viewportEl = $('viewport');
  let layoutQueued = false;
  let lastLayout = '';
  let lastVars = '';

  function layoutBox(node) {
    // Where an element is laid out, ignoring any animation moving it right now.
    let x = 0, y = 0;
    for (let n = node; n && n !== document.body; n = n.offsetParent) { x += n.offsetLeft; y += n.offsetTop; }
    return { left: x, top: y, width: node.offsetWidth, height: node.offsetHeight };
  }

  function reportLayout() {
    layoutQueued = false;
    const dpr = window.devicePixelRatio || 1;
    const px = (v) => Math.round(v * dpr);
    const view = viewportEl.getBoundingClientRect();
    const vars = `${view.left}|${view.top}|${view.width}|${view.height}`;
    if (vars !== lastVars) {
      lastVars = vars;
      const style = document.body.style;
      style.setProperty('--view-x', view.left + 'px');
      style.setProperty('--view-y', view.top + 'px');
      style.setProperty('--view-w', view.width + 'px');
      style.setProperty('--view-h', view.height + 'px');
    }
    const radius = parseFloat(getComputedStyle($('app')).getPropertyValue('--view-r')) || 0;
    root.toggleAttribute('data-view-round', radius > 0 && !root.hasAttribute('data-fullscreen'));

    const hit = [];
    document.querySelectorAll('[data-hit], :root[data-view-round] .tc-vcorner').forEach((node) => {
      if (!node.offsetWidth || !node.offsetHeight) return;
      // Room around it for its shadow: --hit-pad (a kit sets it to fit the shadow it draws), else the element's own number.
      const own = parseFloat(getComputedStyle(node).getPropertyValue('--hit-pad'));
      const pad = Number.isFinite(own) ? own : Number(node.dataset.hit) || 0;
      const box = layoutBox(node);
      hit.push([px(box.left - pad), px(box.top - pad), px(box.width + pad * 2), px(box.height + pad * 2)]);
    });
    const x0 = px(view.left), y0 = px(view.top);
    // Where the tab strip is, and the whole row it is in: a tab dragged from another window over that row goes in.
    const strip = document.querySelector('.tc-tabs');
    const sb = strip && strip.offsetWidth ? strip.getBoundingClientRect() : null;
    const row = document.getElementById('tabstrip');
    const rb = row && row.offsetWidth ? row.getBoundingClientRect() : null;
    const message = {
      t: 'layout',
      view: [x0, y0, px(view.right) - x0, px(view.bottom) - y0],
      size: [px(window.innerWidth), px(window.innerHeight)],
      hit,
      modal: !!document.querySelector('#overlays .is-modal'),
      strip: sb ? [px(sb.left), px(sb.top), px(sb.width), px(sb.height)] : null,
      row: rb ? [px(rb.left), px(rb.top), px(rb.width), px(rb.height)] : null,
    };
    const key = JSON.stringify(message);
    if (key === lastLayout) return;
    lastLayout = key;
    Host.post(message);
  }

  const Layout = {
    queue() {
      if (layoutQueued) return;
      layoutQueued = true;
      requestAnimationFrame(reportLayout);
    },
    now: reportLayout,
  };
  new ResizeObserver(Layout.queue).observe(viewportEl);
  window.addEventListener('resize', Layout.queue);

  // ----- small facts a kit may show as decoration -----

  function setInfo(name, text) {
    document.querySelectorAll(`.tc-info[data-info="${name}"]`).forEach((node) => { if (node.textContent !== text) node.textContent = text; });
  }
  const two = (n) => String(n).padStart(2, '0');

  function updateClock() {
    const now = new Date();
    setInfo('clock', `${two(now.getHours())}:${two(now.getMinutes())}`);
    setInfo('date', `${now.getFullYear()}.${two(now.getMonth() + 1)}.${two(now.getDate())}`);
  }
  updateClock();
  setInterval(updateClock, 20000);

  // ----- tabs -----

  const tabsEl = $('tabs');
  const tabEls = new Map();   // tab id -> its element
  let dragging = null;
  // The card over a tab the mouse rests on (see "the card over a tab" below).
  const CARD_DELAY = 500;   // ms the mouse rests on a tab before its card comes
  const card = { wrap: null, box: null, id: 0, node: null, timer: 0, leave: 0, open: false };
  let firstTabs = true;

  const liveTabs = () => [...tabsEl.children].filter((n) => n.classList.contains('tc-tab') && !n.classList.contains('is-leaving'));

  function createTab(t) {
    const node = el('div', 'tc-tab' + (firstTabs ? '' : ' is-entering'),
      '<span class="tc-tab-icon"></span><span class="tc-tab-index"></span><span class="tc-tab-title"></span>' +
      `<span class="tc-tab-audio" title="Playing sound">${icon('volume')}</span>` +
      `<button class="tc-tab-close" type="button" tabindex="-1" aria-label="Close tab" title="Close tab (Ctrl+W)">${icon('close')}</button>`);
    node.setAttribute('role', 'tab');
    node.querySelector('.tc-tab-audio').title = tr('Playing sound');
    node.dataset.id = t.id;
    node.addEventListener('animationend', (event) => { if (event.target === node) node.classList.remove('is-entering'); });
    node.addEventListener('pointerdown', onTabPointerDown);
    node.addEventListener('mousedown', (event) => { if (event.button === 1) event.preventDefault(); });
    node.addEventListener('auxclick', (event) => { if (event.button === 1) Host.cmd('closeTab', { id: t.id }); });
    node.addEventListener('contextmenu', (event) => { event.preventDefault(); hideCard(true); tabMenu(t.id, event.clientX, event.clientY); });
    node.addEventListener('pointerenter', () => cardOver(node));
    node.addEventListener('pointerleave', cardOut);
    const close = node.querySelector('.tc-tab-close');
    close.title = tr('Close tab (Ctrl+W)');
    close.setAttribute('aria-label', tr('Close tab'));
    close.addEventListener('pointerdown', (event) => event.stopPropagation());
    close.addEventListener('click', (event) => { event.stopPropagation(); Host.cmd('closeTab', { id: t.id }); });
    return node;
  }

  function setTabIcon(node, t) {
    if (node._icon === t.icon) return;
    node._icon = t.icon;
    const box = node.querySelector('.tc-tab-icon');
    box.textContent = '';
    if (t.icon === 'home' || t.icon === 'settings' || t.icon === 'history') box.innerHTML = icon(t.icon);
    else if (t.icon) {
      const image = new Image();
      image.alt = '';
      image.onerror = () => { image.remove(); box.insertAdjacentHTML('afterbegin', icon('globe')); };
      image.src = t.icon;
      box.append(image);
    } else box.innerHTML = icon('globe');
    box.append(el('i', 'tc-tab-spin'));
  }

  function updateTab(node, t, index) {
    const title = t.title || tr('New tab');
    if (node._title !== title) {
      const titleEl = node.querySelector('.tc-tab-title');
      const animate = node._title !== undefined && t.id === S.active && IGYO.fx && IGYO.fx.retitle;
      node._title = title;
      if (animate) IGYO.fx.retitle(titleEl, title);
      else titleEl.textContent = title;
    }
    setTabIcon(node, t);
    const active = t.id === S.active;
    node.classList.toggle('is-active', active);
    node.classList.toggle('is-loading', !!t.loading);
    node.classList.toggle('is-audio', !!t.audio);
    node.classList.toggle('is-asleep', t.state !== 'live');
    node.classList.toggle('is-claude', (S.bridge.reading || []).includes(t.id));   // Claude is reading this tab right now
    node.setAttribute('aria-selected', String(active));
    const position = two(index + 1);
    if (node.dataset.index !== position) {
      node.dataset.index = position;
      node.querySelector('.tc-tab-index').textContent = position;
    }
    if (node.hasAttribute('title')) node.removeAttribute('title');   // the card over the tab says it all (below)
  }

  function renderTabs() {
    const seen = new Set();
    const added = [];
    let previous = null;
    S.tabs.forEach((t, index) => {
      let node = tabEls.get(t.id);
      if (!node) {
        node = createTab(t);
        tabEls.set(t.id, node);
        tabsEl.append(node);
        if (!firstTabs) added.push(node);
      }
      updateTab(node, t, index);
      seen.add(t.id);
      if (!dragging) {
        // Keep the app's order (elements that are animating away are stepped over).
        let expected = previous ? previous.nextSibling : tabsEl.firstChild;
        while (expected && expected !== node && expected.classList.contains('is-leaving')) expected = expected.nextSibling;
        if (expected !== node) tabsEl.insertBefore(node, expected);
      }
      previous = node;
    });
    for (const [id, node] of tabEls) {
      if (seen.has(id)) continue;
      tabEls.delete(id);
      node.classList.remove('is-entering');
      node.classList.add('is-leaving');
      node.addEventListener('animationend', () => node.remove(), { once: true });
      setTimeout(() => node.remove(), 600);   // also when animations are off
    }
    firstTabs = false;
    const activeNode = tabEls.get(S.active);
    // The tab showing is brought into view when it changes, not on every update, so the strip stays where you scrolled it.
    if (activeNode && !dragging && S.active !== revealedId) {
      revealedId = S.active;
      reveal(activeNode);
    }
    for (const node of added) if (node !== activeNode) watchNew(node);
    updateEdges();
    setTimeout(updateEdges, 450);   // again once new tabs have grown in and closed ones have gone
    const index = S.tabs.findIndex((t) => t.id === S.active);
    root.dataset.tabsN = String(S.tabs.length);
    setInfo('tabpos', `${two(index + 1)}/${two(S.tabs.length)}`);
    setInfo('count', String(S.tabs.length));
    markClaudeTabs();   // (a tab that has just appeared may be one Claude opened)
    if (card.open) {
      const shown = tabEls.get(card.id);
      if (shown && shown === card.node) showCard(shown); else hideCard(true);
    }
  }

  function markActive() {
    for (const [id, node] of tabEls) node.classList.toggle('is-active', id === S.active);
  }

  // Press selects at once; moving while pressed drags the tab to a new place. (A press at the screen's edge beside a
  // tab comes here with that tab: see "the screen's edges".)
  function onTabPointerDown(event, tab) {
    if (event.button !== 0) return;
    const node = tab || event.currentTarget;
    const id = Number(node.dataset.id);
    hideCard(true);
    if (id !== S.active) {
      S.active = id;
      markActive();
      Host.cmd('selectTab', { id });
    }
    const vertical = stripIsVertical();
    const axis = (e) => (vertical ? e.clientY : e.clientX);
    const x0 = event.clientX, y0 = event.clientY;
    const rect = node.getBoundingClientRect();
    const grab = axis(event) - (vertical ? rect.top : rect.left);
    let active = false;

    // Dragged well off the strip (across it, not along it), the tab is taken out, as in Chrome: the app gives it a
    // window of its own at once, under the mouse, and moves that window with the mouse from then on, into a strip
    // too (MainForm.Tear). This page keeps the mouse until the button is let go, so it goes on telling the app where
    // the mouse is, wherever it goes, and where it was let go. Esc puts the tab back.
    const OUT = 28;   // how far past the strip's edge that is
    let torn = false;
    const awayFromStrip = (e) => {
      const box = tabsEl.getBoundingClientRect();
      return vertical ? Math.max(box.left - e.clientX, e.clientX - box.right) : Math.max(box.top - e.clientY, e.clientY - box.bottom);
    };
    const mouse = (e) => {
      const dpr = window.devicePixelRatio || 1;
      return { x: Math.round(e.clientX * dpr), y: Math.round(e.clientY * dpr) };
    };
    const tearOff = (e) => {
      torn = true;
      const dpr = window.devicePixelRatio || 1;
      // Where the first tab of a strip starts: in its new window the tab is the only one, there.
      const box = tabsEl.getBoundingClientRect(), strip = getComputedStyle(tabsEl), own = getComputedStyle(node);
      const sx = vertical ? rect.left : box.left + parseFloat(strip.borderLeftWidth) + parseFloat(strip.paddingLeft) + parseFloat(own.marginLeft);
      const sy = vertical ? box.top + parseFloat(strip.borderTopWidth) + parseFloat(strip.paddingTop) + parseFloat(own.marginTop) : rect.top;
      const gx = vertical ? x0 - rect.left : grab, gy = vertical ? grab : y0 - rect.top;
      node.classList.remove('is-dragging');
      node.style.transform = '';
      dragging = null;
      // The tab leaves this strip; the strip itself keeps the mouse for the rest of the drag.
      try { tabsEl.setPointerCapture(event.pointerId); } catch { /* the pointer is already gone */ }
      const at = mouse(e);
      Host.cmd('tearTab', { id, x: at.x, y: at.y, gx: Math.round(gx * dpr), gy: Math.round(gy * dpr), sx: Math.round(sx * dpr), sy: Math.round(sy * dpr) });
    };
    const escape = (e) => {
      if (e.key !== 'Escape' || !torn) return;
      e.preventDefault();
      e.stopPropagation();
      stop();
      Host.cmd('tearEnd', { cancel: true });
    };

    const stop = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
      window.removeEventListener('keydown', escape, true);
    };
    const move = (e) => {
      if (torn) { Host.cmd('tearMove', mouse(e)); return; }
      if (!active) {
        if (Math.hypot(e.clientX - x0, e.clientY - y0) < 6) return;
        if (S.tabs.length < 2) {
          // The window's only tab: dragging it moves the window, as in Chrome. The app hands the drag to Windows,
          // which moves the window from here (snapping it at the screen's edges too); the moment the mouse reaches
          // another window's tab row, the tab goes into it (MainForm.DragWindowNow). x, y: the mouse in this view;
          // gx, gy: the mouse on the tab.
          stop();
          const dpr = window.devicePixelRatio || 1;
          Host.cmd('dragWindow', { x: Math.round(e.clientX * dpr), y: Math.round(e.clientY * dpr), gx: Math.round((e.clientX - rect.left) * dpr), gy: Math.round((e.clientY - rect.top) * dpr) });
          return;
        }
        active = true;
        dragging = node;
        node.classList.add('is-dragging');
        try { node.setPointerCapture(event.pointerId); } catch { /* the pointer is already gone */ }
      }
      if (awayFromStrip(e) > OUT) { tearOff(e); return; }
      dragAlong(node, axis(e), grab);
    };
    const up = (e) => {
      stop();
      if (torn) {
        // Let go there (a drag the system called off leaves the app to read where the mouse is).
        Host.cmd('tearEnd', e && e.type === 'pointerup' ? mouse(e) : {});
        return;
      }
      if (!active) return;
      const from = node.style.transform;
      node.style.transform = '';
      node.classList.remove('is-dragging');
      node.animate([{ transform: from }, { transform: 'none' }], { duration: 160, easing: 'cubic-bezier(.2,.8,.2,1)' });
      dragging = null;
      commitPlace(node);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
    window.addEventListener('keydown', escape, true);
  }

  // A tab being dragged, drawn where the mouse has it along the strip (pos: the mouse along the strip; grab: where
  // on the tab the mouse holds it; this view's pixels), and moved among the others once it is past half of one.
  function dragAlong(node, pos, grab) {
    const vertical = stripIsVertical();
    const shift = (d) => (vertical ? `translateY(${d}px)` : `translateX(${d}px)`);
    const place = (n) => (vertical ? n.offsetTop : n.offsetLeft);
    const extent = (n) => (vertical ? n.offsetHeight : n.offsetWidth);
    const box = tabsEl.getBoundingClientRect();
    const scroll = vertical ? tabsEl.scrollTop : tabsEl.scrollLeft;
    const room = (vertical ? tabsEl.scrollHeight : tabsEl.scrollWidth) - extent(node);
    const reach = pos - (vertical ? box.top : box.left) - grab + scroll;
    const wanted = Math.max(0, Math.min(room, reach));   // where it is drawn: never past either end of the strip
    node.style.transform = shift(wanted - place(node));
    // Its new place is worked out from where the mouse has taken it, even past an end: held to the strip, a tab
    // could never get past the middle of the last one, so it could not be dragged to the last place.
    const centre = reach + extent(node) / 2;
    const others = liveTabs().filter((n) => n !== node);
    const index = others.filter((n) => place(n) + extent(n) / 2 < centre).length;
    if (index === liveTabs().indexOf(node)) return;
    const before = others.map((n) => [n, n.getBoundingClientRect()]);
    tabsEl.insertBefore(node, others[index] || null);
    for (const [n, was] of before) {
      const is = n.getBoundingClientRect();
      const d = vertical ? was.top - is.top : was.left - is.left;
      if (d) n.animate([{ transform: shift(d) }, { transform: 'none' }], { duration: 170, easing: 'cubic-bezier(.2,.8,.2,1)' });
    }
    node.style.transform = shift(wanted - place(node));
  }

  // Where a dragged tab now stands among the others becomes its place, and the app is told.
  function commitPlace(node) {
    const id = Number(node.dataset.id);
    const index = liveTabs().indexOf(node);
    const fromIndex = S.tabs.findIndex((t) => t.id === id);
    if (index < 0 || fromIndex < 0 || index === fromIndex) return;
    S.tabs.splice(index, 0, S.tabs.splice(fromIndex, 1)[0]);
    Host.cmd('moveTab', { id, index });
  }

  // A tab dragged in from another window (or back into this one) while the mouse still holds it: it follows the
  // mouse along this strip (m.drag true; x, y: the mouse, gx, gy: where on the tab it holds it, this view's pixels),
  // and is put down where it stands (m.drag false). Without m.drag: a window's only tab let go over this strip (the
  // window itself was dragged), put between the tabs either side of where it was let go.
  let held = null;
  function putDown(node) {
    held = null;
    if (dragging === node) dragging = null;
    const from = node.style.transform;
    node.style.transform = '';
    node.classList.remove('is-dragging');
    if (from && from !== 'none') node.animate([{ transform: from }, { transform: 'none' }], { duration: 160, easing: 'cubic-bezier(.2,.8,.2,1)' });
    commitPlace(node);
  }
  Host.on('placeTab', (m) => {
    const node = tabEls.get(m.id);
    const vertical = stripIsVertical();
    const dpr = window.devicePixelRatio || 1;
    if (m.drag === true) {
      if (!node) return;
      if (held !== node) {
        if (held) putDown(held);
        held = node;
        dragging = node;
        node.classList.add('is-dragging');
        hideCard(true);
      }
      dragAlong(node, (vertical ? m.y : m.x) / dpr, (vertical ? m.gy : m.gx) / dpr);
      return;
    }
    if (m.drag === false) {
      if (node && held === node) putDown(node);
      return;
    }
    const from = S.tabs.findIndex((t) => t.id === m.id);
    if (from < 0) return;
    const at = (vertical ? m.y : m.x) / dpr;
    const index = liveTabs().filter((n) => Number(n.dataset.id) !== m.id).filter((n) => {
      const r = n.getBoundingClientRect();
      return (vertical ? r.top + r.height / 2 : r.left + r.width / 2) < at;
    }).length;
    if (index === from) return;
    S.tabs.splice(index, 0, S.tabs.splice(from, 1)[0]);
    Host.cmd('moveTab', { id: m.id, index });
    renderTabs();
  });

  tabsEl.addEventListener('wheel', () => hideCard(true), { passive: true });
  // The mouse coming to the strip: the tab on show has its picture taken now, for its card once another is on show.
  let stripHoverAt = 0;
  tabsEl.addEventListener('pointerenter', () => {
    if (Date.now() - stripHoverAt < 2000) return;
    stripHoverAt = Date.now();
    Host.cmd('stripHover');
  });

  tabsEl.addEventListener('wheel', (event) => {
    const vertical = getComputedStyle(tabsEl).flexDirection.startsWith('column');
    const delta = Math.abs(event.deltaY) > Math.abs(event.deltaX) ? event.deltaY : event.deltaX;
    if (vertical) tabsEl.scrollTop += delta; else tabsEl.scrollLeft += delta;
  }, { passive: true });

  // ----- the card over a tab the mouse rests on, as in Chrome -----
  // The tab's title and site, a picture of its page when it is not the tab on show (taken the last time it was), and
  // the memory its page's process has in use. It comes once the mouse has rested on a tab for a moment and goes to
  // the next tab at once while it is up. The app sends the picture and the memory (tabCard).

  function cardSite(url) {
    try {
      const u = new URL(url);
      if (u.protocol === 'http:' || u.protocol === 'https:') {
        if (/\.igyo$/.test(u.hostname)) return '';   // IGYO's own pages (New Tab, Settings)
        return u.hostname.replace(/^www\./, '');
      }
      if (u.protocol === 'file:') return decodeURIComponent(u.pathname.split('/').pop() || '');
      if (u.protocol === 'edge:') return u.protocol + '//' + u.hostname;
    } catch { /* not an address */ }
    return '';
  }

  function cardOver(node) {
    clearTimeout(card.leave);
    if (dragging || held || document.querySelector('#overlays .is-modal')) return;
    if (card.open) { showCard(node); return; }
    clearTimeout(card.timer);
    card.timer = setTimeout(() => { if (node.isConnected && node.matches(':hover') && !dragging && !held) showCard(node); }, CARD_DELAY);
  }

  function cardOut() {
    clearTimeout(card.timer);
    clearTimeout(card.leave);
    card.leave = setTimeout(() => hideCard(false), 140);   // (gone to the tab beside it, the card stays up)
  }

  function showCard(node) {
    const id = Number(node.dataset.id);
    const t = S.tabs.find((x) => x.id === id);
    if (!t) { hideCard(true); return; }
    if (!card.wrap) {
      card.wrap = el('div', 'tc-cardwrap');
      card.box = el('div', 'tc-card',
        '<div class="tc-card-head"><div class="tc-card-title"></div><div class="tc-card-site"></div></div>' +
        '<div class="tc-card-shot" hidden></div><div class="tc-card-foot" hidden></div>');
      card.box.dataset.hit = '30';
      card.box.setAttribute('role', 'tooltip');
      card.wrap.append(card.box);
      overlaysEl.append(card.wrap);
    }
    const other = card.id !== id;
    card.id = id;
    card.node = node;
    card.open = true;
    const title = card.box.querySelector('.tc-card-title');
    const text = t.title || tr('New tab');
    if (title.textContent !== text) title.textContent = text;
    const site = card.box.querySelector('.tc-card-site');
    const where = cardSite(t.url || '');
    if (site.textContent !== where) site.textContent = where;
    site.hidden = !where;
    if (other) {
      // What the app said about the tab before belongs to that tab.
      card.box.querySelector('.tc-card-shot').hidden = true;
      card.box.querySelector('.tc-card-foot').hidden = true;
      Host.cmd('tabCard', { id });
    }
    placeCard();
    Layout.now();
  }

  // Under its tab, left edges together, kept inside the window.
  function placeCard() {
    if (!card.wrap || !card.node) return;
    const r = card.node.getBoundingClientRect();
    const strip = tabsEl.getBoundingClientRect();
    const width = card.box.offsetWidth || 260;
    const vertical = stripIsVertical();
    const left = Math.max(6, Math.min(vertical ? r.right + 6 : r.left, window.innerWidth - width - 6));
    const top = vertical ? r.top : Math.max(r.bottom, strip.bottom) + 6;
    card.wrap.style.left = `${Math.round(left)}px`;
    card.wrap.style.top = `${Math.round(top)}px`;
  }

  function hideCard(instant) {
    clearTimeout(card.timer);
    clearTimeout(card.leave);
    const wrap = card.wrap;
    card.wrap = null;
    card.box = null;
    card.id = 0;
    card.node = null;
    card.open = false;
    if (!wrap) return;
    const remove = () => { wrap.remove(); Layout.now(); };
    if (instant || root.dataset.anim === 'off') remove();
    else {
      wrap.firstChild.classList.add('is-closing');
      setTimeout(remove, 120);
    }
  }

  Host.on('tabCard', (m) => {
    if (!card.box || m.id !== card.id) return;
    const shot = card.box.querySelector('.tc-card-shot');
    if (m.picture) shot.style.backgroundImage = `url("${m.picture}")`;
    shot.hidden = !m.picture;
    const foot = card.box.querySelector('.tc-card-foot');
    // (in use: what Task Manager shows; reserved: what the process holds, which letting go of memory does not lower)
    const words = m.state === 'unloaded' ? tr('Unloaded: uses no memory until you open it')
      : m.memory == null ? '' : tr('Memory: {0} MB in use, {1} MB reserved', m.memory, m.reserved);
    const site = card.box.querySelector('.tc-card-site');
    if (m.state === 'asleep') { site.textContent = [cardSite((S.tabs.find((x) => x.id === m.id) || {}).url || ''), tr('Sleeping')].filter(Boolean).join(' · '); site.hidden = false; }
    foot.replaceChildren();
    if (words) {
      foot.insertAdjacentHTML('afterbegin', icon('leaf'));
      foot.append(el('span', '', ''));
      foot.lastChild.textContent = words;
    }
    foot.hidden = !words;
    placeCard();
    Layout.now();
  });

  window.addEventListener('blur', () => hideCard(true));

  // ----- tabs beyond the ends of the strip -----
  // With more tabs than fit, the strip scrolls. The tab that becomes the one showing is brought into view (and followed
  // while it grows in: a new tab starts with no width). A tab that opens in the background out of view is counted on a
  // "+N" mark at that end of the strip, which scrolls to it when pressed, and the ends fade while tabs are beyond them.
  let revealedId = null;
  const unseen = new Map();   // tab id -> element: opened out of view and not seen yet
  const more = { start: el('button', 'tc-tabs-more'), end: el('button', 'tc-tabs-more') };
  for (const [side, button] of Object.entries(more)) {
    button.type = 'button';
    button.hidden = true;
    button.dataset.side = side;
    button.title = tr('New tabs out of view');
    button.setAttribute('aria-label', tr('New tabs out of view'));
    // Goes to the farthest of them, which brings in the ones before it too when they opened side by side.
    button.addEventListener('click', () => {
      const nodes = [...unseen.values()].filter((node) => sideOf(node) === side);
      if (!nodes.length) return;
      const far = (node) => (side === 'end' ? along(node).to : -along(node).from);
      reveal(nodes.reduce((a, b) => (far(b) > far(a) ? b : a)), true);
    });
  }
  tabsEl.before(more.start);
  tabsEl.after(more.end);

  const stripIsVertical = () => getComputedStyle(tabsEl).flexDirection.startsWith('column');

  // Where a tab is along the strip, counted from the start of the part in view.
  function along(node) {
    const strip = tabsEl.getBoundingClientRect();
    const box = node.getBoundingClientRect();
    return stripIsVertical()
      ? { from: box.top - strip.top, to: box.bottom - strip.top, room: tabsEl.clientHeight }
      : { from: box.left - strip.left, to: box.right - strip.left, room: tabsEl.clientWidth };
  }

  // Most of it is in view (a tab cut by a few pixels at an end still counts as seen).
  function inView(node) {
    const { from, to, room } = along(node);
    return Math.min(to, room) - Math.max(from, 0) >= Math.max(1, to - from) * 0.6;
  }

  const sideOf = (node) => (along(node).from < 0 ? 'start' : 'end');

  // Scrolls the strip, and nothing around it, until the tab is in view, clear of the faded ends.
  function reveal(node, smooth) {
    const pad = 24;
    const until = performance.now() + 900;
    const step = () => {
      if (!node.isConnected || dragging) return;
      const { from, to, room } = along(node);
      const delta = from < pad ? from - pad : to > room - pad ? Math.min(to - room + pad, from - pad) : 0;
      if (Math.abs(delta) > 0.5) tabsEl.scrollBy({ [stripIsVertical() ? 'top' : 'left']: delta, behavior: smooth ? 'smooth' : 'instant' });
      if (!smooth && node.classList.contains('is-entering') && performance.now() < until) requestAnimationFrame(step);
      else updateEdges();
    };
    step();
  }

  // A tab opened in the background is judged once it has grown in (soon after it appears when tabs don't animate; a
  // tab still without width, as when the window is minimized and animations wait, is judged when its animation ends).
  function watchNew(node) {
    let done = false;
    const check = (grown) => {
      if (done) return;
      const { from, to } = along(node);
      if (!grown && to - from < 2) return;
      done = true;
      const id = Number(node.dataset.id);
      if (!node.isConnected || node.classList.contains('is-leaving') || id === S.active || inView(node)) return;
      unseen.set(id, node);
      const button = more[sideOf(node)];
      button.classList.remove('is-new');
      void button.offsetWidth;   // so the mark pulses again for each new tab
      button.classList.add('is-new');
      updateEdges();
    };
    node.addEventListener('animationend', (event) => { if (event.target === node) check(true); });
    setTimeout(() => check(false), 450);
  }

  function updateEdges() {
    for (const [id, node] of unseen) {
      if (!node.isConnected || node.classList.contains('is-leaving') || id === S.active || inView(node)) unseen.delete(id);
    }
    const count = { start: 0, end: 0 };
    for (const node of unseen.values()) count[sideOf(node)]++;
    for (const side of ['start', 'end']) {
      more[side].hidden = !count[side];
      const text = '+' + count[side];
      if (count[side] && more[side].textContent !== text) more[side].textContent = text;
    }
    const vertical = stripIsVertical();
    const at = vertical ? tabsEl.scrollTop : tabsEl.scrollLeft;
    const end = vertical ? tabsEl.scrollHeight - tabsEl.clientHeight : tabsEl.scrollWidth - tabsEl.clientWidth;
    tabsEl.dataset.axis = vertical ? 'y' : 'x';
    tabsEl.classList.toggle('is-more-start', at > 1);
    tabsEl.classList.toggle('is-more-end', at < end - 1);
  }
  tabsEl.addEventListener('scroll', updateEdges, { passive: true });
  new ResizeObserver(updateEdges).observe(tabsEl);

  function tabMenu(id, x, y) {
    const index = S.tabs.findIndex((t) => t.id === id);
    const t = S.tabs[index];
    if (!t) return;
    IGYO.menu.open({
      at: { x, y },
      items: [
        { label: tr('New tab'), keys: 'Ctrl+T', icon: 'plus', run: () => Host.cmd('newTab') },
        { sep: true },
        { label: tr('Reload'), keys: 'Ctrl+R', icon: 'reload', run: () => Host.cmd('reloadTab', { id }) },
        { label: tr('Duplicate'), icon: 'copy', disabled: !t.url, run: () => Host.cmd('duplicateTab', { id }) },
        { sep: true },
        { label: tr('Close'), keys: 'Ctrl+W', icon: 'close', run: () => Host.cmd('closeTab', { id }) },
        { label: tr('Close other tabs'), disabled: S.tabs.length < 2, run: () => Host.cmd('closeOtherTabs', { id }) },
        { label: tr('Close tabs to the right'), disabled: index >= S.tabs.length - 1, run: () => Host.cmd('closeTabsToRight', { id }) },
        { sep: true },
        { label: tr('Reopen closed tab'), keys: 'Ctrl+Shift+T', icon: 'undo', run: () => Host.cmd('reopenTab') },
      ],
    });
  }

  $('newtab').innerHTML = icon('plus');
  $('newtab').addEventListener('click', () => Host.cmd('newTab'));

  // ----- toolbar -----

  const buttons = {
    back: $('back'), forward: $('forward'), reload: $('reload'), star: $('star'), zoom: $('zoom'),
    tools: $('tools'), downloads: $('downloads'), claude: $('claude'), profile: $('profile'), menu: $('menu'),
  };
  const setIcon = (button, name) => {
    if (button._icon === name) return;
    button._icon = name;
    const old = button.querySelector('svg.i');
    if (old) old.remove();
    button.insertAdjacentHTML('afterbegin', icon(name));
  };
  setIcon(buttons.back, 'back');
  setIcon(buttons.forward, 'forward');
  setIcon(buttons.reload, 'reload');
  setIcon(buttons.star, 'star');
  setIcon(buttons.tools, 'puzzle');
  setIcon(buttons.downloads, 'download');
  setIcon(buttons.claude, 'sparkle');
  setIcon(buttons.menu, 'more');
  $('private-chip').querySelector('.tc-private-icon').innerHTML = icon('private');

  buttons.back.addEventListener('click', () => Host.cmd('back'));
  buttons.forward.addEventListener('click', () => Host.cmd('forward'));
  buttons.reload.addEventListener('click', () => Host.cmd('reload'));
  buttons.star.addEventListener('click', () => Host.cmd('star'));
  buttons.zoom.addEventListener('click', () => IGYO.zoom.toggle());

  // The page's zoom. While it is not 100%, a magnifier in the address bar says so (with + above 100%, with - below)
  // and opens the zoom box (overlays.js, IGYO.zoom), which also comes up by itself when the zoom is changed. While
  // the box is open the button stays, even back at 100%: the box hangs from it.
  function renderZoom() {
    const zoom = S.nav.zoom == null ? 100 : S.nav.zoom;
    const open = !!(IGYO.zoom && IGYO.zoom.isOpen);
    const button = buttons.zoom;
    button.hidden = zoom === 100 && !open;
    setIcon(button, zoom < 100 ? 'zoom-out' : 'zoom');
    const text = tr('Zoom {0}%', zoom);
    if (button.title !== text) { button.title = text; button.setAttribute('aria-label', text); }
    button.classList.toggle('is-open', open);
    button.setAttribute('aria-expanded', String(open));
    if (open) IGYO.zoom.update();
  }
  buttons.downloads.addEventListener('click', () => Host.cmd('downloads'));

  // Menus whose lines the app decides (it knows what is checked or possible right now).
  let menuAnchor = null;
  function requestMenu(name, anchor, more) {
    menuAnchor = anchor;
    Host.post(Object.assign({ t: 'menu', name }, more || {}));
  }
  // The tools group: ad block, dark mode for websites, video enhancement, the color picker, the tab's volume,
  // picture in picture and the screenshot, in one menu. Each of them can be pinned there, and then also has a
  // button of its own on the toolbar (below).
  buttons.tools.addEventListener('click', () => requestMenu('tools', buttons.tools));
  buttons.claude.addEventListener('click', () => requestMenu('claude', buttons.claude));
  buttons.profile.addEventListener('click', () => requestMenu('profile', buttons.profile));
  buttons.menu.addEventListener('click', () => requestMenu('main', buttons.menu));

  function showHostMenu(message) {
    // (A tool that is not pinned has no button to hang its menu on: the tools button stands in, or the main one.)
    const usable = (b) => b && b.isConnected && !b.hidden;
    const anchor = usable(menuAnchor) ? menuAnchor : usable(buttons.tools) ? buttons.tools : buttons.menu;
    menuAnchor = null;
    // (A right click on an extension's line opens that extension's own menu, in this menu's place.)
    const items = (message.items || []).map((item) => (item.ext ? Object.assign(item, { className: 'is-extension', context: () => requestMenu('extension', anchor, { key: item.ext }) }) : item));
    IGYO.menu.open({
      anchor, align: 'right', items,
      onPick: (item) => { if (item.id) Host.post({ t: 'menuItem', id: item.id }); },
    });
  }

  // ----- tools pinned to the toolbar -----
  // The app says which tools are pinned and in which order (settings.tools). Each is a button to the left of the
  // tools button: the two that are switched on and off show their state, ad block shows its count and opens its
  // menu, the volume opens its slider, the translate button turns into "show the original" on a translated page,
  // the others act. A pinned tool is dragged along the toolbar to another place among the pinned ones, and a
  // right click unpins it.

  const pinnedEl = $('pinned');
  const TOOLS = {
    adblock: { id: 'shield', icon: 'shield', menu: 'adblock' },
    dark: { id: 'dark', icon: 'moon', cmd: 'dark' },
    enhance: { id: 'enhance', icon: 'enhance', cmd: 'enhance' },
    picker: { id: 'picker', icon: 'dropper', cmd: 'pickColor' },
    translate: { id: 'translate', icon: 'translate', cmd: 'translatePage' },
    volume: { id: 'volume', icon: 'volume', menu: 'volume' },
    pip: { id: 'pip', icon: 'pip', cmd: 'pip' },
    shot: { id: 'shot', icon: 'camera', cmd: 'shot' },
    extensions: { id: 'extensions', icon: 'plug', menu: 'extensions' },
  };
  const toolEls = new Map();   // tool name -> its button
  let toolDragging = null;
  // (The buttons are also reachable by the names they had before they could be pinned: buttons.shield, .pip, .shot.)
  for (const [name, def] of Object.entries(TOOLS)) Object.defineProperty(buttons, def.id, { enumerable: true, get: () => toolEls.get(name) });

  // An extension is pinned like a tool, as "ext:" and its name in the extensions folder (settings.extensions says
  // what there is to know of it). Its button shows the extension's own picture; a click opens the extension's
  // popup under the button, a right click its menu (switch, options, manage, unpin, remove).
  const EXT = 'ext:';
  const extensionOf = (name) => (name.startsWith(EXT) ? (S.settings.extensions || []).find((x) => x.key === name.slice(EXT.length)) : null);

  // Tells the app that an extension was clicked, and where its popup should hang: under its own button when it
  // has one, under the tools button otherwise (a line in the tools menu was clicked).
  function extensionClick(key, button) {
    const usable = (b) => b && b.isConnected && !b.hidden;
    const own = toolEls.get(EXT + key);
    const at = usable(button) ? button : usable(own) ? own : usable(buttons.tools) ? buttons.tools : buttons.menu;
    const r = at.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
    Host.cmd('extensionClick', { key, rect: [r.left, r.top, r.width, r.height].map((v) => Math.round(v * dpr)), dpr });
  }

  function createTool(name) {
    const ext = name.startsWith(EXT);
    const def = TOOLS[name] || { id: '', icon: 'plug' };
    const button = el('button', 'tc-btn tc-tool' + (ext ? ' tc-ext' : ''));
    button.type = 'button';
    if (def.id) button.id = def.id;
    button.dataset.tool = name;
    if (def.menu) button.setAttribute('aria-haspopup', 'menu');
    if (name === 'adblock') button.append(Object.assign(el('span', 'tc-count'), { hidden: true }));
    if (name === 'volume') button.append(Object.assign(el('span', 'tc-dot'), { hidden: true }));
    if (ext) button.append(Object.assign(el('img', 'tc-ext-icon'), { alt: '', draggable: false, hidden: true }));
    setIcon(button, def.icon);   // (an extension that brings no picture keeps this one)
    button.addEventListener('pointerdown', (event) => onToolPointerDown(event, name, button));
    button.addEventListener('click', () => {
      if (button._dragged) { button._dragged = false; return; }   // the click that ends a drag does nothing
      if (ext) extensionClick(name.slice(EXT.length), button);
      else if (def.menu) requestMenu(def.menu, button);
      else Host.cmd(def.cmd);
    });
    button.addEventListener('contextmenu', (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (ext) { requestMenu('extension', button, { key: name.slice(EXT.length) }); return; }
      IGYO.menu.open({
        at: { x: event.clientX, y: event.clientY },
        items: [{ label: tr('Unpin from the toolbar'), icon: 'pin', run: () => Host.cmd('pinTool', { tool: name, on: false }) }],
      });
    });
    return button;
  }

  function renderTools() {
    const wanted = (S.settings.tools || []).filter((name) => TOOLS[name] || extensionOf(name));
    for (const [name, button] of toolEls) {
      if (wanted.includes(name)) continue;
      toolEls.delete(name);
      button.remove();
    }
    let previous = null;
    for (const name of wanted) {
      let button = toolEls.get(name);
      if (!button) {
        button = createTool(name);
        toolEls.set(name, button);
        pinnedEl.append(button);
      }
      if (!toolDragging) {
        const expected = previous ? previous.nextSibling : pinnedEl.firstChild;
        if (expected !== button) pinnedEl.insertBefore(button, expected);
      }
      previous = button;
    }
    updateTools();
  }

  // What each pinned tool's button says right now.
  function updateTools() {
    const nav = S.nav, s = S.settings;
    const say = (button, text) => { if (button.title !== text) { button.title = text; button.setAttribute('aria-label', text); } };
    for (const [name, button] of toolEls) {
      if (name.startsWith(EXT)) {
        const x = extensionOf(name);
        if (!x) continue;
        const image = button.querySelector('.tc-ext-icon'), drawn = button.querySelector('svg.i');
        const show = (picture) => { image.hidden = !picture; if (drawn) drawn.style.display = picture ? 'none' : ''; };
        if (!x.icon) { image.removeAttribute('src'); show(false); }
        else if (image.getAttribute('src') !== x.icon) {
          image.onload = () => show(true);
          image.onerror = () => show(false);
          image.src = x.icon;
        }
        const open = S.extensionPopup === x.key;
        button.classList.toggle('is-off', !x.on);
        button.classList.toggle('is-open', open);
        button.setAttribute('aria-expanded', String(open));
        say(button, x.on ? x.title || x.name : tr('{0} (off)', x.name));
        continue;
      }
      if (name === 'adblock') {
        const on = s.adBlock !== false;
        const count = on ? nav.blocked || 0 : 0;
        const badge = button.querySelector('.tc-count');
        badge.hidden = count <= 0;
        badge.textContent = count > 99 ? '99+' : String(count);
        button.classList.toggle('is-off', !on);
        say(button, on ? tr('Ad block is on: {0} blocked on this page', nav.blocked || 0) : tr('Ad block is off'));
      } else if (name === 'dark') {
        button.classList.toggle('is-on', !!s.dark);
        button.setAttribute('aria-pressed', String(!!s.dark));
        say(button, s.dark ? tr('Dark mode for websites is on') : tr('Dark mode for websites is off'));
      } else if (name === 'enhance') {
        button.classList.toggle('is-on', !!s.enhance);
        button.setAttribute('aria-pressed', String(!!s.enhance));
        say(button, s.enhance ? tr('Video enhancement is on') : tr('Video enhancement is off'));
      } else if (name === 'volume') {
        const volume = nav.volume == null ? 100 : nav.volume;
        button.querySelector('.tc-dot').hidden = volume === 100;
        say(button, tr('Volume of this tab: {0}%', volume));
      } else if (name === 'translate') {
        // On a page that is shown translated, the same button brings the original back.
        button.classList.toggle('is-on', !!nav.translated);
        button.setAttribute('aria-pressed', String(!!nav.translated));
        say(button, nav.translated ? tr('Show the original page') : tr('Translate this page'));
      } else if (name === 'picker') say(button, tr('Pick a color from the page'));
      else if (name === 'pip') say(button, tr('Picture in picture: pop the video out'));
      else if (name === 'shot') say(button, tr('Full-page screenshot (Ctrl+Shift+S)'));
      else if (name === 'extensions') say(button, tr('Extensions'));
    }
  }

  // Dragging a pinned tool along the toolbar moves it to where it is let go, among the pinned tools. The app
  // keeps the order and sends the toolbar back as it now is, to this window and every other one.
  function onToolPointerDown(event, name, button) {
    if (event.button !== 0) return;
    const startX = event.clientX;
    let active = false;
    const all = () => [...pinnedEl.children];

    const move = (e) => {
      if (!active) {
        if (Math.abs(e.clientX - startX) < 6 || toolEls.size < 2) return;
        active = true;
        toolDragging = button;
        button._dragged = true;
        IGYO.menu.close(true, true);
        button.classList.add('is-dragging');
        try { button.setPointerCapture(event.pointerId); } catch { /* the pointer is already gone */ }
      }
      const others = all().filter((n) => n !== button);
      button.style.transform = '';
      const index = others.filter((n) => { const r = n.getBoundingClientRect(); return r.left + r.width / 2 < e.clientX; }).length;
      if (index !== all().indexOf(button)) {
        const before = others.map((n) => [n, n.getBoundingClientRect().left]);
        pinnedEl.insertBefore(button, others[index] || null);
        for (const [n, was] of before) {
          const d = was - n.getBoundingClientRect().left;
          if (d) n.animate([{ transform: `translateX(${d}px)` }, { transform: 'none' }], { duration: 150, easing: 'cubic-bezier(.2,.8,.2,1)' });
        }
      }
      const r = button.getBoundingClientRect();
      button.style.transform = `translateX(${e.clientX - (r.left + r.width / 2)}px)`;
    };

    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
      if (!active) return;
      const from = button.style.transform;
      button.style.transform = '';
      button.classList.remove('is-dragging');
      button.animate([{ transform: from }, { transform: 'none' }], { duration: 140, easing: 'cubic-bezier(.2,.8,.2,1)' });
      toolDragging = null;
      setTimeout(() => { button._dragged = false; }, 0);   // in case no click follows
      const index = all().indexOf(button);
      const order = (S.settings.tools || []).filter((n) => toolEls.has(n));
      if (index < 0 || order.indexOf(name) === index) return;
      order.splice(index, 0, order.splice(order.indexOf(name), 1)[0]);
      S.settings.tools = order;
      Host.cmd('moveTool', { tool: name, index });
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
  }

  function renderNav() {
    const nav = S.nav;
    buttons.back.disabled = !nav.canBack;
    buttons.forward.disabled = !nav.canForward;
    setIcon(buttons.reload, nav.loading ? 'stop' : 'reload');
    buttons.reload.title = nav.loading ? tr('Stop') : tr('Reload (F5)');
    buttons.reload.setAttribute('aria-label', nav.loading ? tr('Stop') : tr('Reload'));
    root.toggleAttribute('data-loading', !!nav.loading);

    setIcon(buttons.star, nav.bookmarked ? 'star-on' : 'star');
    buttons.star.classList.toggle('is-on', !!nav.bookmarked);
    buttons.star.disabled = !nav.canBookmark;
    buttons.star.title = nav.bookmarked ? tr('Edit bookmark (Ctrl+D)') : tr('Bookmark this page (Ctrl+D)');
    buttons.star.setAttribute('aria-label', nav.bookmarked ? tr('Edit bookmark') : tr('Bookmark this page'));
    renderZoom();

    updateTools();
    // While this tab's volume is not the page's own, a dot says so: on the volume's own button when it is pinned,
    // otherwise on the tools button.
    const volume = nav.volume == null ? 100 : nav.volume;
    const told = volume === 100 || toolEls.has('volume');
    buttons.tools.querySelector('.tc-dot').hidden = told;
    buttons.tools.title = told ? tr('Tools') : tr('Tools (volume of this tab: {0}%)', volume);
    buttons.tools.setAttribute('aria-label', buttons.tools.title);

    showAddress(false);
    let host = '';
    try { host = nav.url ? new URL(nav.url).hostname.replace(/^www\./, '') : ''; } catch { host = ''; }
    setInfo('host', host || (nav.kind === 'settings' ? tr('settings') : !nav.url ? tr('new tab') : nav.url.startsWith('file:') ? tr('local file') : tr('page')));
    setInfo('state', nav.loading ? tr('loading') : tr('ready'));
  }

  // ----- address bar -----
  // A plain text box. While you type, the only list that can appear is Google's predictions, if that is turned on.

  const omnibox = $('omnibox');
  const address = $('address');
  const display = $('display');
  const siteInfo = $('siteinfo');
  let edited = false;   // you have typed something that isn't the page's address
  let typed = '';

  function showAddress(force) {
    const nav = S.nav;
    const url = nav.url || '';
    const focused = document.activeElement === address;
    if (force || !focused || !edited) {
      if (address.value !== url) address.value = url;
      edited = false;
    }
    const kind = nav.kind || 'blank';
    omnibox.dataset.kind = kind;
    siteInfo.innerHTML = icon(kind === 'secure' ? 'lock' : kind === 'open' ? 'info' : kind === 'settings' ? 'settings' : 'search');
    siteInfo.title = kind === 'secure' ? tr('Connection is secure (HTTPS)') : kind === 'open' ? tr('This page is not using a secure connection') : '';

    // Resting, the address reads as its site and, more quietly, the rest.
    display.textContent = '';
    let parsed = null;
    try { parsed = url ? new URL(url) : null; } catch { parsed = null; }
    const web = !!parsed && (parsed.protocol === 'https:' || parsed.protocol === 'http:');
    omnibox.classList.toggle('has-display', web);
    if (web) {
      const scheme = el('span', 'd-scheme');
      scheme.textContent = parsed.protocol + '//';
      const hostSpan = el('span', 'd-host');
      hostSpan.textContent = parsed.host.replace(/^www\./, '');
      const rest = el('span', 'd-rest');
      const tail = parsed.pathname + parsed.search + parsed.hash;
      rest.textContent = tail === '/' ? '' : tail;
      display.append(scheme, hostSpan, rest);
    }
  }

  address.addEventListener('focus', () => {
    omnibox.classList.add('is-focused');
    Host.cmd('addressFocus', { on: true });
    address.select();
  });
  // A click that gives the focus keeps everything selected (the mouse-up would otherwise place the caret).
  address.addEventListener('mousedown', () => {
    if (document.activeElement === address) return;
    address.addEventListener('mouseup', (event) => event.preventDefault(), { once: true });
  });
  address.addEventListener('blur', () => {
    omnibox.classList.remove('is-focused');
    Host.cmd('addressFocus', { on: false });
    IGYO.suggest.hide();
    if (!edited) showAddress(false);
  });
  address.addEventListener('input', () => {
    edited = true;
    typed = address.value;
    Host.post({ t: 'suggest', text: typed });
  });
  address.addEventListener('keydown', (event) => {
    if (IGYO.suggest.visible && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      event.preventDefault();
      const text = IGYO.suggest.step(event.key === 'ArrowDown' ? 1 : -1);
      if (text != null) { address.value = text; address.setSelectionRange(text.length, text.length); }
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      go(address.value, event.altKey);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      if (IGYO.suggest.visible) {
        address.value = typed;        // the first Escape closes the list, the next one restores the address
        IGYO.suggest.hide();
      } else if (edited) {
        showAddress(true);
        address.select();
      } else Host.cmd('focusPage');
    }
  });
  omnibox.addEventListener('mousedown', (event) => {
    if (event.target === address || event.target.closest('button')) return;
    event.preventDefault();
    address.focus();
  });
  address.addEventListener('contextmenu', (event) => {
    event.preventDefault();
    const from = address.selectionStart, to = address.selectionEnd;
    const restore = () => { address.focus(); address.setSelectionRange(from, to); };
    IGYO.menu.open({
      at: { x: event.clientX, y: event.clientY },
      keepFocus: true,
      items: [
        { label: tr('Cut'), keys: 'Ctrl+X', disabled: from === to, run: () => { restore(); document.execCommand('cut'); edited = true; } },
        { label: tr('Copy'), keys: 'Ctrl+C', disabled: from === to, run: () => { restore(); document.execCommand('copy'); } },
        { label: tr('Paste'), keys: 'Ctrl+V', run: () => { restore(); Host.cmd('paste'); } },
        { label: tr('Paste and go'), run: () => Host.cmd('paste', { go: true }) },
        { sep: true },
        { label: tr('Select all'), keys: 'Ctrl+A', run: () => { address.focus(); address.select(); } },
      ],
    });
  });

  function go(text, newTab) {
    if (!text.trim()) return;
    edited = false;
    IGYO.suggest.hide();
    Host.cmd('go', { text, newTab: !!newTab });
  }

  function pasteText(text) {
    if (!text) return;
    address.focus();
    address.setRangeText(text.replace(/[\r\n]+/g, ' '), address.selectionStart, address.selectionEnd, 'end');
    edited = true;
    typed = address.value;
    Host.post({ t: 'suggest', text: typed });
  }

  function focusAddress() {
    address.focus();
    address.select();
  }

  // ----- bookmarks bar -----

  const bmList = $('bm-list');
  const bmMore = $('bm-more');
  const bmOther = $('bm-other');
  let overflow = [];
  bmMore.innerHTML = icon('chevrons-right');

  function bookmarkPicture(b) {
    if (b.children) return icon('folder');
    return b.icon ? '' : icon('globe');
  }

  function bookmarkButton(b) {
    const button = el('button', 'tc-bm', bookmarkPicture(b));
    button.type = 'button';
    if (!b.children && b.icon) {
      const image = new Image();
      image.alt = '';
      image.onerror = () => { image.remove(); button.insertAdjacentHTML('afterbegin', icon('globe')); };
      image.src = b.icon;
      button.append(image);
    }
    const label = el('span');
    label.textContent = b.title || (b.children ? tr('Folder') : b.url);
    button.append(label);
    button.title = b.children ? b.title : `${b.title}\n${b.url}`;
    if (b.children) button.setAttribute('aria-haspopup', 'menu');
    button._bookmark = b;
    button.addEventListener('pointerdown', (event) => startBookmarkDrag(event, b, button));
    button.addEventListener('click', (event) => {
      if (button._dragged) { button._dragged = false; return; }   // the click that ends a drag opens nothing
      if (b.children) folderMenu(button, b);
      else Host.cmd('openBookmark', { id: b.id, newTab: event.ctrlKey, behind: event.ctrlKey && !event.shiftKey });
    });
    button.addEventListener('mousedown', (event) => { if (event.button === 1) event.preventDefault(); });
    // (opened with the middle button, or with Ctrl held: behind the tab on show, as a link opened that way is)
    button.addEventListener('auxclick', (event) => { if (event.button === 1 && !b.children) Host.cmd('openBookmark', { id: b.id, newTab: true, behind: !event.shiftKey }); });
    button.addEventListener('contextmenu', (event) => { event.preventDefault(); bookmarkContext(b, event.clientX, event.clientY); });
    return button;
  }

  // ----- dragging bookmarks -----
  // A bookmark is dragged as a see-through copy of itself that follows the mouse wherever it goes, as in other
  // browsers. The bar keeps still and shows where the bookmark would go: a line between two bookmarks, or the
  // folder it would go into, lit up. A folder held under the mouse for a moment opens, and the bookmark can then be
  // let go at a place inside it (a line between two of its lines) or on a folder in it. A bookmark in a folder's
  // menu is dragged the same way: onto the bar, into another folder, or to another place in its own folder. Let go
  // anywhere else, or Esc, and nothing moves. The app does the moving and sends the bookmarks back as they now are.

  const BAR = 0;          // how the app names the bar, as a folder
  const SPRING_MS = 550;  // how long a folder is held under the mouse before it opens
  const overlaysEl = $('overlays');
  let bmDragNow = null;   // the drag going on, if one is

  // Whether the folder lies inside the bookmark: a folder cannot go into itself, or into one of its own folders.
  function holds(b, folderId) {
    if (!b.children) return false;
    return b.id === folderId || b.children.some((c) => holds(c, folderId));
  }

  // The bookmark as its button on the bar looks: what is dragged around.
  function bookmarkChip(b) {
    const chip = el('div', 'tc-bm', bookmarkPicture(b));
    if (!b.children && b.icon) {
      const image = new Image();
      image.alt = '';
      image.onerror = () => { image.remove(); chip.insertAdjacentHTML('afterbegin', icon('globe')); };
      image.src = b.icon;
      chip.append(image);
    }
    const label = el('span');
    label.textContent = b.title || (b.children ? tr('Folder') : b.url);
    chip.append(label);
    return chip;
  }

  // A press on a bookmark: its button on the bar, or its line in a folder's menu (source). where: { parent, index },
  // the folder it is in (BAR for the bar) and its place there. Moving the mouse a little starts the drag.
  function startBookmarkDrag(event, b, source, where) {
    if (event.button !== 0 || bmDragNow) return;
    const from = where || { parent: BAR, index: (S.bookmarks.bar || []).findIndex((x) => x.id === b.id) };
    const x0 = event.clientX, y0 = event.clientY;
    const pointer = event.pointerId;
    let d = null;
    const stop = () => {
      window.removeEventListener('pointermove', move, true);
      window.removeEventListener('pointerup', up, true);
      window.removeEventListener('pointercancel', cancel, true);
      window.removeEventListener('keydown', key, true);
    };
    const move = (e) => {
      if (e.pointerId !== pointer) return;
      if (!d) {
        if (Math.hypot(e.clientX - x0, e.clientY - y0) < 6) return;
        d = beginBookmarkDrag(b, from, source, x0, y0, pointer);
      }
      trackBookmarkDrag(d, e.clientX, e.clientY);
    };
    const up = (e) => { if (e.pointerId !== pointer) return; stop(); if (d) endBookmarkDrag(d, true); };
    const cancel = () => { stop(); if (d) endBookmarkDrag(d, false); };
    const key = (e) => { if (e.key === 'Escape' && d) { e.preventDefault(); e.stopPropagation(); cancel(); } };
    window.addEventListener('pointermove', move, true);
    window.addEventListener('pointerup', up, true);
    window.addEventListener('pointercancel', cancel, true);
    window.addEventListener('keydown', key, true);
  }

  function beginBookmarkDrag(b, from, source, x0, y0, pointer) {
    source._dragged = true;   // the click that ends the drag opens nothing
    const r = source.getBoundingClientRect();
    // While a bookmark is dragged the whole window belongs to the interface ("is-modal"): the copy is seen over the
    // page too, and the mouse is followed wherever it goes. The layer takes the mouse; what lies under it is found
    // by where things are, not by what the mouse is over.
    const layer = el('div', 'tc-bm-draglayer is-modal');
    const line = el('div', 'tc-drop-line');
    line.hidden = true;
    const ghost = el('div', 'tc-bm-ghost');
    ghost.append(bookmarkChip(b));
    layer.append(line, ghost);
    overlaysEl.append(layer);
    try { layer.setPointerCapture(pointer); } catch { /* the pointer is already gone */ }
    source.classList.add('is-drag-source');
    Layout.now();
    // The copy hangs below and to the right of the mouse, so that what is under the mouse (a folder lit up, the
    // line where it would go) is never covered by it, however long its name.
    const grab = { x: -14, y: -16 };
    const d = { b, from, source, layer, line, ghost, grab, target: null, lit: null, springKey: null, springTimer: 0 };
    bmDragNow = d;
    return d;
  }

  function trackBookmarkDrag(d, x, y) {
    d.ghost.style.transform = `translate(${Math.round(x - d.grab.x)}px, ${Math.round(y - d.grab.y)}px)`;
    const t = bookmarkDropAt(d, x, y);
    d.target = t && !t.none ? t : null;
    const lit = (t && t.lit) || null;   // the folder it would go into
    if (lit !== d.lit) {
      if (d.lit) d.lit.classList.remove('is-drop');
      d.lit = lit;
      if (lit) lit.classList.add('is-drop');
    }
    const line = t && !t.none && t.line;   // or the place it would go to
    d.line.hidden = !line;
    if (line) {
      d.line.style.transform = `translate(${Math.round(line.x)}px, ${Math.round(line.y)}px)`;
      d.line.style.width = Math.max(2, Math.round(line.w)) + 'px';
      d.line.style.height = Math.max(2, Math.round(line.h)) + 'px';
    }
    // Held under the mouse for a moment: a folder opens, or the menus opened deeper than this one close.
    const key = (t && t.springKey) || null;
    if (key !== d.springKey) {
      clearTimeout(d.springTimer);
      d.springKey = key;
      if (key) {
        const run = t.spring;
        d.springTimer = setTimeout(() => { if (bmDragNow === d) run(); }, SPRING_MS);
      }
    }
  }

  // Where the bookmark would go if it were let go at x, y: { folder, final } for a place in a folder (final: its
  // place there once it has been taken out of where it is), { folder, index: -1 } for the end of a folder it is
  // let go on, { none: true } for nowhere; with what to light up, the line to draw and what to open meanwhile.
  function bookmarkDropAt(d, x, y) {
    const panels = IGYO.menu.panels;
    for (let i = panels.length - 1; i >= 0; i--) {
      const r = panels[i].getBoundingClientRect();
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return menuDropAt(d, panels, i, x, y);
    }
    const bar = $('bookmarks').getBoundingClientRect();
    if (bar.height > 0 && y >= bar.top - 6 && y <= bar.bottom + 6 && x >= bar.left && x <= bar.right) return barDropAt(d, x);
    return null;
  }

  // A place in a folder: the line is drawn there; what the app is told is counted without the bookmark itself.
  function dropPlace(d, folder, at, line) {
    const final = d.from.parent === folder && d.from.index >= 0 && d.from.index < at ? at - 1 : at;
    return { folder, index: at, final, line };
  }

  function barDropAt(d, x) {
    const over = (n) => { const r = n.getBoundingClientRect(); return x >= r.left && x <= r.right; };
    if (!bmOther.hidden && over(bmOther)) {
      return { folder: S.bookmarks.otherId, index: -1, lit: bmOther, springKey: bmOther, spring: openOtherMenu };
    }
    if (!bmMore.hidden && over(bmMore)) return { none: true, springKey: bmMore, spring: openOverflowMenu };
    const nodes = [...bmList.querySelectorAll('.tc-bm')].filter((n) => !n.classList.contains('is-hidden'));
    const lineAt = (px, n) => { const r = n.getBoundingClientRect(); return { x: px - 1, y: r.top + 1, w: 2, h: r.height - 2 }; };
    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i];
      const r = n.getBoundingClientRect();
      if (x > r.right + 1) continue;
      const nb = n._bookmark;
      // The middle half of a folder means "into it"; its outer quarters, "next to it".
      if (nb.children && x >= r.left + r.width * 0.25 && x <= r.right - r.width * 0.25) {
        if (holds(d.b, nb.id)) return { none: true };
        return { folder: nb.id, index: -1, lit: n, springKey: n, spring: () => folderMenu(n, nb) };
      }
      const before = x < r.left + r.width / 2;
      return dropPlace(d, BAR, before ? i : i + 1, lineAt(before ? r.left - 1 : r.right + 1, n));
    }
    if (nodes.length) {
      const last = nodes[nodes.length - 1];
      return dropPlace(d, BAR, nodes.length, lineAt(last.getBoundingClientRect().right + 1, last));
    }
    const r = bmList.getBoundingClientRect();   // an empty bar: it becomes the first
    return dropPlace(d, BAR, 0, { x: r.left + 2, y: r.top + 4, w: 2, h: r.height - 8 });
  }

  function menuDropAt(d, panels, depth, x, y) {
    const panel = panels[depth];
    const rows = [...panel.querySelectorAll(':scope > .t-menu-item')].filter((row) => row._item && (row._item.bm || row._item.emptyOf !== undefined));
    if (!rows.length) return { none: true };   // a menu of something else
    // Held over a line that did not open the menu shown beside this one, that menu closes (as a menu does when
    // the mouse goes to another of its lines).
    const deeper = panels[depth + 1];
    const closing = (row) => (deeper && deeper._parentRow !== row ? { springKey: 'close:' + depth, spring: () => IGYO.menu.closeDeeper(depth) } : {});
    const lineAt = (py) => { const r = panel.getBoundingClientRect(); return { x: r.left + 6, y: py - 1, w: r.width - 12, h: 2 }; };
    const placeBy = (row, before) => {
      const it = row._item, r = row.getBoundingClientRect();
      if (holds(d.b, it.parent)) return { none: true };
      return Object.assign(dropPlace(d, it.parent, before ? it.index : it.index + 1, lineAt(before ? r.top : r.bottom)), closing(row));
    };
    for (const row of rows) {
      const r = row.getBoundingClientRect();
      if (y < r.top || y > r.bottom) continue;
      const it = row._item;
      if (it.emptyOf !== undefined) {   // the line of an empty folder: into it
        if (holds(d.b, it.emptyOf)) return { none: true };
        return Object.assign({ folder: it.emptyOf, index: 0, final: 0, lit: row }, closing(row));
      }
      const nb = it.bm;
      if (nb.children && y >= r.top + r.height * 0.25 && y <= r.bottom - r.height * 0.25) {
        if (holds(d.b, nb.id)) return { none: true };
        return { folder: nb.id, index: -1, lit: row, springKey: row, spring: () => IGYO.menu.openSub(row) };
      }
      return placeBy(row, y < r.top + r.height / 2);
    }
    // In the menu's own margin: before its first line or after its last.
    const first = rows[0], last = rows[rows.length - 1];
    if (y < first.getBoundingClientRect().top && first._item.bm) return placeBy(first, true);
    if (y > last.getBoundingClientRect().bottom && last._item.bm) return placeBy(last, false);
    return { none: true };
  }

  function endBookmarkDrag(d, commit) {
    clearTimeout(d.springTimer);
    bmDragNow = null;
    const t = commit ? d.target : null;
    if (d.lit) d.lit.classList.remove('is-drop');
    d.source.classList.remove('is-drag-source');
    d.layer.remove();
    Layout.now();
    setTimeout(() => { d.source._dragged = false; }, 0);   // in case no click follows
    if (!t) return;   // let go nowhere: nothing moves, and the menus stay as they are
    IGYO.menu.close(true, true);
    if (t.folder === d.from.parent && t.final === d.from.index) return;   // let go where it already was
    Host.cmd('moveBookmark', { id: d.b.id, folder: t.folder, index: t.final === undefined ? -1 : t.final });
  }

  // Menu lines for the bookmarks of a folder (parent: the folder's id, BAR for the bar; offset: where the list
  // starts in that folder, for the bar's » menu). A folder's own lines are only built when it is opened. Every
  // line can be dragged somewhere else (see above). Opening all of a folder's bookmarks is in its right-click menu.
  function bookmarkItems(list, parent, offset = 0) {
    const items = list.map((b, i) => {
      const where = { parent, index: offset + i };
      const drag = (event, row) => startBookmarkDrag(event, b, row, where);
      return b.children
        ? { label: b.title || tr('Folder'), icon: 'folder', bm: b, parent, index: offset + i, drag, children: () => bookmarkItems(b.children, b.id), context: (x, y) => bookmarkContext(b, x, y) }
        : {
          label: b.title || b.url, image: b.icon, icon: 'globe', className: 'is-bookmark', tip: b.url,
          bm: b, parent, index: offset + i, drag,
          run: (event) => {
            const other = !!(event && (event.ctrlKey || event.button === 1));
            Host.cmd('openBookmark', { id: b.id, newTab: other, behind: other && !event.shiftKey });
          },
          middle: true,
          context: (x, y) => bookmarkContext(b, x, y),
        };
    });
    if (!items.length) items.push({ label: tr('(empty)'), disabled: true, emptyOf: parent });
    return items;
  }

  function folderMenu(anchor, folder) {
    IGYO.menu.open({ anchor, align: 'left', items: bookmarkItems(folder.children, folder.id) });
  }

  function openOtherMenu() {
    IGYO.menu.open({ anchor: bmOther, align: 'right', items: bookmarkItems(S.bookmarks.other || [], S.bookmarks.otherId) });
  }

  function openOverflowMenu() {
    const bar = S.bookmarks.bar || [];
    IGYO.menu.open({ anchor: bmMore, align: 'right', items: bookmarkItems(overflow, BAR, bar.length - overflow.length) });
  }

  // "Open all" counts the bookmarks right in the folder (not those in its folders), as it opens them.
  function openAllItem(folderId, children) {
    const links = (children || []).filter((c) => !c.children).length;
    return { label: links > 1 ? tr('Open all {0} in new tabs', links) : links ? tr('Open in new tab') : tr('Open all in new tabs'), icon: 'external', disabled: !links, run: () => Host.cmd('openAllBookmarks', { id: folderId }) };
  }

  function bookmarkContext(b, x, y) {
    const items = [];
    if (!b.children) items.push({ label: tr('Open in new tab'), icon: 'external', run: () => Host.cmd('openBookmark', { id: b.id, newTab: true }) });
    else items.push(openAllItem(b.id, b.children));
    items.push({ label: tr('Edit…'), icon: 'edit', run: () => Host.cmd('editBookmark', { id: b.id }) });
    items.push({ label: tr('Delete'), icon: 'trash', run: () => Host.cmd('deleteBookmark', { id: b.id }) });
    items.push({ sep: true });
    // On a folder the new one goes inside it; on a bookmark, beside it.
    items.push({ label: b.children ? tr('New folder inside…') : tr('New folder…'), icon: 'folder', run: () => Host.cmd('newBookmarkFolder', { id: b.id }) });
    items.push({ label: tr('Manage bookmarks…'), keys: 'Ctrl+Shift+O', run: () => Host.cmd('settings', { section: 'bookmarks' }) });
    IGYO.menu.open({ at: { x, y }, items });
  }

  // Bookmarks that don't fit go behind the » button.
  function fitBookmarks() {
    const all = [...bmList.querySelectorAll('.tc-bm')];
    all.forEach((b) => b.classList.remove('is-hidden'));
    bmMore.hidden = true;
    const outside = () => all.filter((b) => b.offsetLeft + b.offsetWidth > bmList.clientWidth + bmList.offsetLeft);
    let hidden = outside();
    if (hidden.length) {
      bmMore.hidden = false;
      hidden = outside();
    }
    hidden.forEach((b) => b.classList.add('is-hidden'));
    const bar = S.bookmarks.bar || [];
    overflow = bar.slice(bar.length - hidden.length);
  }

  function renderBookmarks() {
    const bar = S.bookmarks.bar || [];
    bmList.textContent = '';
    if (!bar.length) {
      const hint = el('span', 'tc-bm-empty');
      hint.textContent = tr('Bookmark a page with the star (Ctrl+D) to see it here.');
      bmList.append(hint);
    }
    for (const b of bar) bmList.append(bookmarkButton(b));
    const other = S.bookmarks.other || [];
    bmOther.hidden = !other.length;
    bmOther.innerHTML = icon('folder') + '<span></span>';
    bmOther.querySelector('span').textContent = tr('Other bookmarks');
    fitBookmarks();
  }

  bmMore.addEventListener('click', openOverflowMenu);
  bmOther.addEventListener('click', openOtherMenu);
  bmOther.addEventListener('contextmenu', (event) => {
    event.preventDefault();
    event.stopPropagation();
    IGYO.menu.open({
      at: { x: event.clientX, y: event.clientY },
      items: [
        openAllItem(S.bookmarks.otherId, S.bookmarks.other),
        { sep: true },
        { label: tr('Manage bookmarks…'), keys: 'Ctrl+Shift+O', run: () => Host.cmd('settings', { section: 'bookmarks' }) },
      ],
    });
  });
  new ResizeObserver(fitBookmarks).observe(bmList);
  $('bookmarks').addEventListener('contextmenu', (event) => {
    if (event.target.closest('.tc-bm')) return;
    event.preventDefault();
    IGYO.menu.open({
      at: { x: event.clientX, y: event.clientY },
      items: [
        { label: tr('New folder…'), icon: 'folder', run: () => Host.cmd('newBookmarkFolder', { id: 0 }) },
        { label: tr('Bookmark this page'), keys: 'Ctrl+D', icon: 'star', disabled: !S.nav.canBookmark || !!S.nav.bookmarked, run: () => Host.cmd('star') },
        { sep: true },
        { label: tr('Manage bookmarks…'), keys: 'Ctrl+Shift+O', run: () => Host.cmd('settings', { section: 'bookmarks' }) },
        { label: tr('Hide the bookmarks bar'), keys: 'Ctrl+Shift+B', run: () => Host.cmd('toggleBookmarksBar') },
      ],
    });
  });

  // ----- window buttons -----

  const winButtons = [...document.querySelectorAll('#wincontrols button')];
  for (const button of winButtons) {
    button.innerHTML = icon(button.dataset.win === 'close' ? 'close' : button.dataset.win);
    button.addEventListener('click', () => Host.cmd('win', { action: button.dataset.win }));
  }

  let maxDrawn = null;   // what the maximize button shows (drawn again only when that changes)
  function applyWindow() {
    const w = S.win;
    root.toggleAttribute('data-active', w.active !== false);
    root.toggleAttribute('data-max', !!w.maximized);
    root.toggleAttribute('data-fullscreen', !!w.fullscreen);
    if (maxDrawn !== !!w.maximized) {
      maxDrawn = !!w.maximized;
      const max = winButtons.find((b) => b.dataset.win === 'max');
      max.innerHTML = icon(w.maximized ? 'restore' : 'max');
      max.title = w.maximized ? tr('Restore') : tr('Maximize');
      max.setAttribute('aria-label', max.title);
    }
    Layout.queue();
  }

  // ----- the screen's edges -----
  // Maximized, the window's edges are the screen's, and the mouse thrown against one should still hit what stands
  // there, as in other browsers. A press in the tab list's own room beside a tab (above it at the top of the screen,
  // or beside it in a list down the side) acts on that tab: it selects and drags it, a middle click closes it, a right
  // click opens its menu. A click at the far left of the toolbar presses its first button (Back), one at the far right
  // its last (the menu). Only while maximized: in a smaller window that room is not at the screen's edge, and the
  // window's own edge there resizes it.

  const toolbarEl = $('toolbar');
  const atScreenEdges = () => root.hasAttribute('data-max') && !root.hasAttribute('data-fullscreen');

  function tabBeside(event) {
    if (!atScreenEdges() || event.target !== tabsEl) return null;
    const vertical = getComputedStyle(tabsEl).flexDirection.startsWith('column');
    const at = vertical ? event.clientY : event.clientX;
    return liveTabs().find((node) => {
      const r = node.getBoundingClientRect();
      return vertical ? at >= r.top && at < r.bottom : at >= r.left && at < r.right;
    }) || null;
  }
  tabsEl.addEventListener('pointerdown', (event) => {
    const node = tabBeside(event);
    if (node) onTabPointerDown(event, node);
  });
  tabsEl.addEventListener('mousedown', (event) => { if (event.button === 1 && tabBeside(event)) event.preventDefault(); });
  tabsEl.addEventListener('auxclick', (event) => {
    const node = event.button === 1 ? tabBeside(event) : null;
    if (node) Host.cmd('closeTab', { id: Number(node.dataset.id) });
  });
  tabsEl.addEventListener('contextmenu', (event) => {
    const node = tabBeside(event);
    if (node) tabMenu(Number(node.dataset.id), event.clientX, event.clientY);
  });

  // The toolbar's first and last buttons, when the toolbar runs to the screen's edge on that side.
  function buttonAtEdge(event) {
    if (!atScreenEdges() || event.target !== toolbarEl) return null;
    const box = (b) => b.getBoundingClientRect();
    const shown = [...toolbarEl.querySelectorAll('button')].filter((b) => b.getClientRects().length > 0 && b.offsetWidth > 0);
    if (!shown.length) return null;
    const first = shown.reduce((a, b) => (box(b).left < box(a).left ? b : a));
    const last = shown.reduce((a, b) => (box(b).right > box(a).right ? b : a));
    const bar = box(toolbarEl);
    if (bar.left <= 1 && event.clientX < box(first).left) return first;
    if (bar.right >= window.innerWidth - 1 && event.clientX >= box(last).right) return last;
    return null;
  }
  toolbarEl.addEventListener('click', (event) => {
    const button = buttonAtEdge(event);
    if (button) button.click();   // (a disabled one, like Back with nothing to go back to, does nothing)
  });

  // And under the pointer there, those two light up as they do anywhere else: each reaches out to the screen's edge
  // over the toolbar's own room with a part that is not drawn (chrome.css, .is-edge-first and .is-edge-last), so the
  // pointer at the edge is on the button itself, and every kit's own look under the pointer applies. How far each
  // reaches is measured here, whenever the toolbar changes size or the window is maximized or put back.
  function measureEdgeReach() {
    const box = (b) => b.getBoundingClientRect();
    const shown = atScreenEdges() ? [...toolbarEl.querySelectorAll('button')].filter((b) => b.getClientRects().length > 0 && b.offsetWidth > 0) : [];
    const bar = box(toolbarEl);
    const first = shown.length && bar.left <= 1 ? shown.reduce((a, b) => (box(b).left < box(a).left ? b : a)) : null;
    const last = shown.length && bar.right >= window.innerWidth - 1 ? shown.reduce((a, b) => (box(b).right > box(a).right ? b : a)) : null;
    for (const [mark, button, x] of [['is-edge-first', first, first && box(first).left - bar.left], ['is-edge-last', last, last && bar.right - box(last).right]]) {
      for (const was of toolbarEl.querySelectorAll('.' + mark)) if (was !== button) was.classList.remove(mark);
      if (!button) continue;
      const b = box(button);
      button.style.setProperty('--reach-x', Math.ceil(x) + 'px');
      button.style.setProperty('--reach-up', Math.ceil(b.top - bar.top) + 'px');
      button.style.setProperty('--reach-down', Math.ceil(bar.bottom - b.bottom) + 'px');
      button.classList.add(mark);
    }
  }
  new ResizeObserver(measureEdgeReach).observe(toolbarEl);
  new MutationObserver(measureEdgeReach).observe(root, { attributes: true, attributeFilter: ['data-max', 'data-fullscreen'] });

  // ----- settings, profile, theme -----

  function applySettings() {
    const s = S.settings;
    root.toggleAttribute('data-compact', !!s.compact);
    root.dataset.anim = s.animations === false ? 'off' : 'on';
    root.dataset.bm = s.bookmarksBar === false ? 'off' : 'on';
    buttons.tools.hidden = s.toolsButton === false;
    renderTools();   // the pinned tools, in their order
    buttons.downloads.hidden = s.downloadsButton === false;
    address.placeholder = tr('Search {0} or type an address', s.engine || tr('the web'));
    root.toggleAttribute('data-private', !!S.isPrivate);
    $('private-chip').hidden = !S.isPrivate;
    const p = S.profile || {};
    const image = buttons.profile.querySelector('img');
    if (p.avatar && image.getAttribute('src') !== p.avatar) image.src = p.avatar;
    buttons.profile.title = S.isPrivate ? tr('Private window · {0}', p.name || '') : tr('Profile: {0}', p.name || '');
    Layout.queue();
  }

  // The Claude mark is only there for a profile that has the bridge switched on (never in a private window).
  // A dot on it means a Claude app is connected; the dot beats, and the tab gets a ring, while a page is being read.
  function applyBridge() {
    const b = S.bridge || {};
    const button = buttons.claude;
    const names = b.connected || [];
    const reading = b.reading || [];
    const was = button.hidden;
    button.hidden = !b.on;
    button.classList.toggle('is-off', !!b.paused);
    button.classList.toggle('is-connected', names.length > 0 && !b.paused);
    button.classList.toggle('is-reading', reading.length > 0);
    button.title = b.paused ? tr('Claude: paused, nothing can connect')
      : names.length ? (reading.length ? tr('Claude: connected ({0}), reading a page', names.join(', ')) : tr('Claude: connected ({0})', names.join(', ')))
      : tr('Claude: nothing is connected');
    button.setAttribute('aria-label', button.title);
    for (const [id, node] of tabEls) node.classList.toggle('is-claude', reading.includes(id));
    markClaudeTabs();
    showClaudeStrip(b.acting || '');
    if (was !== button.hidden) Layout.queue();
  }

  // The tabs Claude opened for itself are marked, as long as they are open.
  function markClaudeTabs() {
    const own = (S.bridge && S.bridge.own) || [];
    for (const [id, node] of tabEls) {
      const mine = own.includes(id);
      if (node.classList.contains('is-claude-own') === mine) continue;
      node.classList.toggle('is-claude-own', mine);
      if (mine) node.dataset.claudeTip = tr('Opened by Claude');
      else delete node.dataset.claudeTip;
    }
  }

  // While Claude acts in this window, a strip over the bottom of the page says so, with a button that stops it at
  // once (everything Claude was doing in IGYO is cut off, as with Pause in the Claude menu).
  let claudeStrip = null;
  function showClaudeStrip(program) {
    if (!program) {
      if (claudeStrip) { claudeStrip.parentElement.remove(); claudeStrip = null; Layout.queue(); }
      return;
    }
    if (!claudeStrip) {
      const wrap = el('div', 'tc-claudewrap');
      claudeStrip = el('div', 'tc-claudestrip');
      claudeStrip.dataset.hit = '10';
      claudeStrip.setAttribute('role', 'status');
      const dot = el('span', 'tc-claudestrip-dot');
      const text = el('span', 'tc-claudestrip-text');
      const stop = el('button', 't-btn is-s tc-claudestrip-stop');
      stop.type = 'button';
      stop.textContent = tr('Stop Claude');
      stop.addEventListener('click', () => Host.cmd('claudeStop'));
      claudeStrip.append(dot, text, stop);
      wrap.append(claudeStrip);
      $('overlays').append(wrap);
      Layout.queue();
    }
    claudeStrip.querySelector('.tc-claudestrip-text').textContent = tr('Claude is working in this window ({0}). Leave it be meanwhile.', program);
  }

  // A theme arrives as its kit, options and effects; its colors and shapes are in /theme.css.
  function applyTheme(theme, first) {
    S.theme = theme || {};
    const t = S.theme;
    for (const attribute of [...root.attributes]) {
      if (attribute.name.startsWith('data-o-') || attribute.name.startsWith('data-fx-')) root.removeAttribute(attribute.name);
    }
    root.dataset.kit = t.kit || 'igyo';
    root.dataset.scheme = t.scheme || 'dark';
    root.dataset.theme = t.id || '';
    for (const [key, value] of Object.entries(t.options || {})) root.setAttribute('data-o-' + key.toLowerCase(), value);
    for (const [key, value] of Object.entries(t.fx || {})) root.setAttribute('data-fx-' + key.toLowerCase(), value);
    window.IGYO_THEME = t;
    if (IGYO.fx && IGYO.fx.themeChanged) IGYO.fx.themeChanged(t);
    if (first) { Layout.queue(); return; }
    // The new stylesheet replaces the old one only once it has loaded, so nothing flashes unstyled. Updates can come
    // faster than sheets load: a sheet still loading for an earlier one is dropped, and the one that loads last-asked
    // takes the place of every other, so no earlier theme's sheet is ever left behind to win.
    for (const pending of document.querySelectorAll('link[data-theme-loading]')) pending.remove();
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.dataset.themeLoading = '';
    link.href = '/theme.css?v=' + encodeURIComponent(t.v || Date.now()) + '&n=' + (++themeSheets);
    const done = () => {
      if (!link.isConnected) return;   // a newer update dropped it meanwhile
      for (const sheet of document.querySelectorAll('link[rel="stylesheet"][href^="/theme.css"]')) if (sheet !== link) sheet.remove();
      delete link.dataset.themeLoading;
      link.id = 'theme-css';
      fitBookmarks();
      Layout.queue();
    };
    link.addEventListener('load', done);
    link.addEventListener('error', done);
    ($('theme-css') || document.head.lastChild).after(link);
  }
  let themeSheets = 0;

  // ----- the page itself -----

  document.addEventListener('contextmenu', (event) => event.preventDefault());
  document.addEventListener('dragover', (event) => event.preventDefault());
  document.addEventListener('drop', (event) => event.preventDefault());
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => { fitBookmarks(); Layout.queue(); });

  Object.assign(IGYO, {
    Host, S, Layout, el, setInfo,
    ui: { renderTabs, renderNav, renderBookmarks, applySettings, applyTheme, applyWindow, applyBridge, showHostMenu, requestMenu, focusAddress, pasteText, go, buttons, viewport: viewportEl, extensionClick, updateTools, renderZoom },
  });
})();
