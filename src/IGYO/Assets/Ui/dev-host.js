// Development only (never shipped): stands in for the app when the interface is opened in an ordinary browser
// through tools/serve.js. It keeps a pretend set of tabs and bookmarks, answers what the interface asks, and
// draws a pretend page where the real one would be, so menus and notices can be seen over something.
(() => {
  'use strict';
  const T = window.IGYO;
  const send = (m) => setTimeout(() => T.Host.deliver(JSON.parse(JSON.stringify(m))), 0);
  let seq = 10;
  const state = {
    tabs: [
      { id: 1, title: 'New tab', url: '', icon: 'home', loading: false, audio: false, state: 'live' },
      { id: 2, title: 'Field notes and base planning: a wiki page with a long title', url: 'https://wiki.example.org/notes/base', icon: '', loading: false, audio: false, state: 'live' },
      { id: 3, title: 'lofi beats to browse to', url: 'https://video.example.com/watch?v=lofi', icon: '', loading: false, audio: true, state: 'live' },
      { id: 4, title: 'Loading a slow page…', url: 'https://slow.example.net/', icon: '', loading: true, audio: false, state: 'live' },
      { id: 5, title: 'An old tab that went to sleep', url: 'https://old.example.com/', icon: '', loading: false, audio: false, state: 'asleep' },
    ],
    active: 2,
    settings: { compact: false, animations: true, bookmarksBar: true, toolsButton: true, tools: ['adblock', 'enhance', 'pip', 'shot', 'translate'], downloadsButton: true, adBlock: true, dark: false, enhance: true, engine: 'Google' },
    volume: 100, translated: false, zoom: 100,
    isPrivate: false,
    win: { active: true, maximized: false, fullscreen: false },
    bridge: { on: true, paused: false, connected: ['claude.exe'], reading: [] },   // the Claude bridge, pretended
    bookmarks: {
      otherId: 99,
      bar: [
        { id: 20, title: 'Mail', url: 'https://mail.example.com/', icon: '' },
        { id: 21, title: 'Video', url: 'https://video.example.com/', icon: '' },
        { id: 22, title: 'Games', children: [
          { id: 23, title: 'Tier lists', url: 'https://games.example.com/tiers', icon: '' },
          { id: 24, title: 'Guides', children: [{ id: 25, title: 'Beginner guide', url: 'https://games.example.com/start', icon: '' }, { id: 26, title: 'Boss notes', url: 'https://games.example.com/boss', icon: '' }] },
          { id: 27, title: 'Event calendar', url: 'https://games.example.com/events', icon: '' },
        ] },
        { id: 28, title: 'Docs', url: 'https://docs.example.com/', icon: '' },
        { id: 29, title: 'Music', url: 'https://music.example.com/', icon: '' },
      ],
      other: [{ id: 30, title: 'Read later', url: 'https://later.example.com/', icon: '' }],
    },
  };

  const current = () => state.tabs.find((t) => t.id === state.active) || state.tabs[0];
  const pushTabs = () => send({ t: 'tabs', active: state.active, list: state.tabs });
  const pushNav = () => {
    const tab = current();
    send({
      t: 'nav', tab: tab.id, url: tab.url, title: tab.title, kind: !tab.url ? 'blank' : tab.url.startsWith('https://') ? 'secure' : 'open',
      canBack: !!tab.url, canForward: false, loading: tab.loading, bookmarked: tab.id === 2, canBookmark: !!tab.url, blocked: tab.url ? 7 : 0,
      volume: state.volume, website: !!tab.url, translated: state.translated, zoom: state.zoom,
    });
  };
  const pushSettings = () => send({ t: 'settings', settings: state.settings });
  const pinned = (tool) => state.settings.tools.includes(tool);
  const volumeLine = () => ({ label: 'Volume', icon: 'volume', slider: { cmd: 'volume', value: state.volume, normal: 100, unit: '%', stops: [...Array(21).keys()].map((i) => i * 5).concat([...Array(20).keys()].map((i) => 125 + i * 25)) } });
  // What a pressed line of an app-made menu does here (the app would do it): pins, and the two switches.
  const picked = {
    60: () => { state.settings.dark = !state.settings.dark; pushSettings(); },
    61: () => { state.settings.enhance = !state.settings.enhance; pushSettings(); },
    62: () => { state.volume = 100; pushNav(); },
  };
  const TOOLS = ['adblock', 'dark', 'enhance', 'picker', 'translate', 'volume', 'pip', 'shot', 'extensions'];
  TOOLS.forEach((tool, i) => {
    picked[70 + i] = () => { state.settings.tools = pinned(tool) ? state.settings.tools.filter((t) => t !== tool) : [...state.settings.tools, tool]; pushSettings(); };
  });
  picked[63] = () => { state.translated = !state.translated; pushNav(); };
  const pin = (tool) => ({ on: pinned(tool), id: 70 + TOOLS.indexOf(tool) });

  const menus = {
    main: () => [
      { id: 1, label: 'New tab', keys: 'Ctrl+T', icon: 'plus' }, { id: 2, label: 'New window', keys: 'Ctrl+N', icon: 'window' },
      { id: 3, label: 'New private window', keys: 'Ctrl+Shift+N', icon: 'private' }, { id: 4, label: 'Reopen closed tab', keys: 'Ctrl+Shift+T', icon: 'undo', disabled: true },
      { sep: true },
      { label: 'Bookmarks', icon: 'star', children: [{ id: 5, label: 'Bookmark this page', keys: 'Ctrl+D' }, { id: 6, label: 'Show bookmarks bar', keys: 'Ctrl+Shift+B', checked: true }, { id: 7, label: 'Manage bookmarks…', keys: 'Ctrl+Shift+O' }] },
      { id: 8, label: 'Downloads', keys: 'Ctrl+J', icon: 'download' },
      { sep: true },
      { id: 9, label: 'Picture in picture', icon: 'pip' }, { id: 10, label: 'Full-page screenshot', keys: 'Ctrl+Shift+S', icon: 'camera' }, { id: 11, label: 'Print…', keys: 'Ctrl+P', icon: 'print' },
      { label: 'Zoom (100%)', icon: 'zoom', children: [{ id: 12, label: 'Zoom in', keys: 'Ctrl +' }, { id: 13, label: 'Zoom out', keys: 'Ctrl −' }, { id: 14, label: 'Reset to 100%', keys: 'Ctrl+0' }] },
      { id: 15, label: 'Full screen', keys: 'F11', icon: 'fullscreen' }, { id: 16, label: 'Developer tools', keys: 'F12', icon: 'code' },
      { sep: true },
      { label: 'Ad block', icon: 'shield', children: [{ id: 17, label: 'Block ads', checked: true }, { id: 18, label: 'Update the block list now' }, { sep: true }, { label: 'EasyList 202611130459 · 43,205 ad domains', disabled: true }] },
      { label: 'Memory saver', icon: 'leaf', children: [{ label: 'Put background tabs to sleep after', children: [{ id: 19, label: 'Never' }, { id: 20, label: '5 minutes', checked: true }] }, { id: 21, label: 'Memory in use…' }] },
      { id: 22, label: 'Themes', icon: 'palette' }, { id: 23, label: 'Settings', keys: 'Ctrl+,', icon: 'settings' },
      { sep: true },
      { id: 24, label: 'About IGYO', icon: 'info' }, { id: 25, label: 'Exit', icon: 'exit' },
    ],
    profile: () => [
      { head: true, label: 'Personal' }, { id: 30, label: 'Personal', checked: true }, { id: 31, label: 'Work', icon: 'people' },
      { sep: true }, { id: 32, label: 'Add profile…', icon: 'plus' }, { id: 33, label: 'Manage profiles…', icon: 'people' },
      { id: 34, label: 'New private window', keys: 'Ctrl+Shift+N', icon: 'private' }, { sep: true }, { id: 35, label: 'Sign in to Google…', icon: 'key' },
    ],
    tools: () => [
      { label: 'Ad block (7 blocked)', icon: 'shield', children: menus.adblock(), pin: pin('adblock') },
      { id: 60, label: 'Dark mode for websites', icon: 'moon', switch: state.settings.dark, pin: pin('dark') },
      { id: 61, label: 'Enhance videos', icon: 'enhance', switch: state.settings.enhance, pin: pin('enhance') },
      { id: 52, label: 'Pick a color from the page', icon: 'dropper', pin: pin('picker') },
      { id: 63, label: state.translated ? 'Show the original page' : 'Translate this page', icon: 'translate', pin: pin('translate') }, { sep: true },
      Object.assign(volumeLine(), { pin: pin('volume') }),
      { sep: true }, { id: 53, label: 'Picture in picture', icon: 'pip', pin: pin('pip') }, { id: 54, label: 'Full-page screenshot', keys: 'Ctrl+Shift+S', icon: 'camera', pin: pin('shot') },
      { sep: true }, { label: 'Extensions', icon: 'plug', children: menus.extensions(), pin: pin('extensions') },
    ],
    extensions: () => [
      { label: 'Typing sounds', children: [{ id: 64, label: 'On', switch: true }, { id: 65, label: 'Open its popup', icon: 'window' }, { id: 66, label: 'Its options', icon: 'settings' }, { id: 67, label: 'Show in folder', icon: 'folder' }] },
      { label: 'Old helper (not used)', children: [{ id: 68, label: 'Why it is not used…', icon: 'info' }, { id: 67, label: 'Show in folder', icon: 'folder' }] },
      { sep: true }, { id: 69, label: 'Open the extensions folder', icon: 'folder' }, { id: 69, label: 'Look for new extensions', icon: 'reload' },
    ],
    volume: () => [volumeLine(), { id: 62, label: 'Back to 100%', icon: 'undo', disabled: state.volume === 100 }],
    adblock: () => [
      { id: 40, label: 'Block ads', checked: true }, { id: 41, label: 'Update the block list now' }, { sep: true },
      { label: 'EasyList 202611130459 · 43,205 ad domains', disabled: true }, { label: '7 blocked on this page', disabled: true },
    ],
    claude: () => [
      { head: true, label: 'Claude' }, { label: 'Connected: claude.exe', disabled: true }, { sep: true },
      { head: true, label: 'wiki.example.org' }, { id: 50, label: 'Ask before reading', checked: true }, { id: 51, label: 'Always allow reading' }, { id: 52, label: 'Never allow' },
      { sep: true }, { id: 53, label: 'Pause: disconnect now' }, { id: 54, label: 'Sites, programs and activity…', icon: 'settings' },
    ],
  };

  // A made-up translation, to look at the panel: window.IGYODevHost.translate() in the console shows it.
  function showTranslation(from, to) {
    const base = { id: 1, x: 260, y: 180, to: to || 'en', mine: ['en', 'es', 'ja'], all: ['de', 'en', 'es', 'fr', 'it', 'ja', 'ko', 'pt', 'zh-Hans'], service: 'MyMemory' };
    send(Object.assign({ t: 'translate', state: 'working', from: from || '', auto: !from, text: '' }, base));
    setTimeout(() => send(Object.assign({ t: 'translate', state: 'done', from: from || 'ja', auto: !from, text: 'It was a nice day today, so I went for a walk in the park.' }, base)), 700);
  }

  function command(m) {
    const tab = state.tabs.find((t) => t.id === m.id);
    switch (m.name) {
      case 'newTab': state.tabs.push({ id: ++seq, title: 'New tab', url: '', icon: 'home', loading: false, audio: false, state: 'live' }); state.active = seq; pushTabs(); pushNav(); send({ t: 'focusAddress' }); break;
      case 'selectTab': if (tab) { state.active = tab.id; tab.state = 'live'; pushTabs(); pushNav(); } break;
      case 'closeTab': case 'closeOtherTabs': case 'closeTabsToRight': {
        if (!tab) break;
        const index = state.tabs.indexOf(tab);
        if (m.name === 'closeTab') state.tabs.splice(index, 1);
        else if (m.name === 'closeOtherTabs') state.tabs = [tab];
        else state.tabs.splice(index + 1);
        if (!state.tabs.length) state.tabs.push({ id: ++seq, title: 'New tab', url: '', icon: 'home', loading: false, audio: false, state: 'live' });
        if (!state.tabs.some((t) => t.id === state.active)) state.active = state.tabs[Math.min(index, state.tabs.length - 1)].id;
        pushTabs(); pushNav();
        break;
      }
      case 'moveTab': if (tab) { state.tabs.splice(state.tabs.indexOf(tab), 1); state.tabs.splice(m.index, 0, tab); pushTabs(); } break;
      case 'detachTab':   // the app opens a window for it; here it just leaves this one
        if (tab && state.tabs.length > 1) {
          const i = state.tabs.indexOf(tab);
          state.tabs.splice(i, 1);
          if (state.active === tab.id) state.active = state.tabs[Math.min(i, state.tabs.length - 1)].id;
          window.__devDetached = (window.__devDetached || []).concat(tab.id);
          pushTabs(); pushNav();
        }
        break;
      case 'duplicateTab': if (tab) { state.tabs.splice(state.tabs.indexOf(tab) + 1, 0, Object.assign({}, tab, { id: ++seq })); state.active = seq; pushTabs(); pushNav(); } break;
      case 'go': {
        const t = current();
        t.url = /^[a-z]+:\/\//i.test(m.text) ? m.text : m.text.includes('.') && !m.text.includes(' ') ? 'https://' + m.text + '/' : 'https://www.google.com/search?q=' + encodeURIComponent(m.text);
        t.title = m.text; t.icon = ''; t.loading = true; pushTabs(); pushNav();
        setTimeout(() => { t.loading = false; pushTabs(); pushNav(); }, 1600);
        document.getElementById('address').blur();
        break;
      }
      case 'reload': { const t = current(); t.loading = !t.loading; pushTabs(); pushNav(); if (t.loading) setTimeout(() => { t.loading = false; pushTabs(); pushNav(); }, 2400); break; }
      case 'star': send({ t: 'dialog', id: 1, kind: 'bookmark', title: 'Bookmark added', name: current().title, url: current().url, isFolder: false, folder: 1, folders: [{ id: 1, label: 'Bookmarks bar', depth: 0 }, { id: 2, label: 'Games', depth: 1 }, { id: 3, label: 'Other bookmarks', depth: 0 }] }); break;
      case 'shot': send({ t: 'notice', id: 2, text: 'Full-page screenshot saved: lofi beats.png', action: 'Show in folder' }); break;
      case 'pip': send({ t: 'notice', id: 3, text: "There's no playable video on this page." }); break;
      case 'zoom': {
        // The app's steps; the box comes up by itself unless the change was made in it.
        const steps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];
        state.zoom = m.dir === 0 ? 100 : m.dir > 0 ? steps.find((s) => s > state.zoom) || 500 : [...steps].reverse().find((s) => s < state.zoom) || 25;
        pushNav();
        if (m.from !== 'popup') send({ t: 'zoomShow' });
        break;
      }
      case 'moveBookmark': {
        // As the app does it: folder 0 is the bar; index is where it ends up (-1: at the end); never into itself.
        const all = (list, parent) => list.flatMap((b) => [[b, parent], ...(b.children ? all(b.children, b) : [])]);
        const bm = state.bookmarks;
        const roots = { children: bm.bar }, other = { id: bm.otherId, children: bm.other };
        const pairs = [...all(bm.bar, roots), ...all(bm.other, other)];
        const found = pairs.find(([b]) => b.id === m.id);
        const folder = m.folder === 0 ? roots : m.folder === bm.otherId ? other : (pairs.find(([b]) => b.id === m.folder) || [])[0];
        const inside = (b, f) => b === f || (b.children || []).some((c) => inside(c, f));
        if (found && folder && folder.children && !inside(found[0], folder)) {
          const [b, parent] = found;
          parent.children.splice(parent.children.indexOf(b), 1);
          if (m.index < 0 || m.index > folder.children.length) folder.children.push(b); else folder.children.splice(m.index, 0, b);
        }
        send(Object.assign({ t: 'bookmarks' }, state.bookmarks));
        break;
      }
      case 'exitFullScreen': state.win.fullscreen = false; send(Object.assign({ t: 'window' }, state.win)); break;
      case 'toggleBookmarksBar': state.settings.bookmarksBar = !state.settings.bookmarksBar; send({ t: 'settings', settings: state.settings }); break;
      // the tools
      case 'dark': picked[60](); break;
      case 'enhance': picked[61](); break;
      case 'volume': state.volume = m.value; pushNav(); break;
      case 'pickColor': send({ t: 'notice', id: 5, text: '#3B82F6 copied. As RGB: 59, 130, 246.', action: 'Copy as rgb()' }); break;
      case 'translatePage': picked[63](); break;
      case 'pinTool': state.settings.tools = state.settings.tools.filter((t) => t !== m.tool).concat(m.on ? [m.tool] : []); pushSettings(); break;
      case 'moveTool': { const list = state.settings.tools.filter((t) => t !== m.tool); list.splice(m.index, 0, m.tool); state.settings.tools = list; pushSettings(); break; }
      // a selection, translated (a made-up one, to look at the panel)
      case 'translateAgain': showTranslation(m.from, m.to); break;
      case 'translateCopy': send({ t: 'notice', id: 6, text: 'Translation copied.' }); break;
      case 'win': if (m.action === 'max') { state.win.maximized = !state.win.maximized; send(Object.assign({ t: 'window' }, state.win)); } break;
      case 'paste': if (!m.go) send({ t: 'paste', text: 'pasted text' }); break;
      case 'focusPage': if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); break;
    }
  }

  // The pretend page, placed where the interface says the page goes.
  const page = document.createElement('div');
  page.id = 'dev-page';
  page.style.cssText = 'position:fixed;z-index:0;overflow:hidden;background:#fff;color:#1c1f26;font:15px/1.6 "Segoe UI",sans-serif;';
  page.innerHTML = '<div style="max-width:760px;margin:0 auto;padding:48px 32px">' +
    '<h1 style="font:600 30px/1.2 \'Segoe UI\',sans-serif;margin:0 0 8px">A page being browsed</h1>' +
    '<p style="color:#5c6473;margin:0 0 24px">This stands in for a website while the interface is worked on outside IGYO.</p>' +
    '<div style="height:180px;border-radius:14px;background:linear-gradient(120deg,#c7d2fe,#fbcfe8,#fde68a);margin-bottom:24px"></div>' +
    '<p>Menus, notices and dialogs drawn by the interface should float over this area with their shadows intact.</p>' +
    '<p style="color:#5c6473">Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio. Praesent libero. Sed cursus ante dapibus diam. Sed nisi. Nulla quis sem at nibh elementum imperdiet.</p></div>';
  document.body.prepend(page);

  const panel = document.createElement('div');
  panel.style.cssText = 'position:fixed;right:12px;bottom:12px;z-index:50;display:flex;flex-wrap:wrap;gap:6px;max-width:520px;padding:8px;border-radius:10px;background:rgba(20,22,30,.86);color:#fff;font:12px "Segoe UI",sans-serif;';
  const add = (label, fn) => { const b = document.createElement('button'); b.textContent = label; b.style.cssText = 'font:inherit;padding:3px 8px;border-radius:6px;border:1px solid #555;background:#2a2d38;color:#fff;cursor:pointer'; b.addEventListener('click', fn); panel.append(b); return b; };
  const themeSelect = document.createElement('select');
  themeSelect.style.cssText = 'font:inherit;padding:3px;border-radius:6px;background:#2a2d38;color:#fff;border:1px solid #555';
  panel.append(themeSelect);
  async function setTheme(id) {
    await fetch('/__dev/theme?id=' + encodeURIComponent(id));
    const script = document.createElement('script');
    script.src = '/theme.js?t=' + Date.now();
    script.onload = () => { script.remove(); send({ t: 'theme', theme: window.IGYO_THEME }); };
    document.head.append(script);
  }
  window.IGYODev = { setTheme, state, send, pushTabs, pushNav };
  fetch('/__dev/themes').then((r) => r.json()).then((list) => {
    for (const t of list) { const o = document.createElement('option'); o.value = t.id; o.textContent = t.name; themeSelect.append(o); }
    const wanted = new URLSearchParams(location.search).get('theme');
    themeSelect.value = wanted || (window.IGYO_THEME && window.IGYO_THEME.id) || 'igyo-dark';
  });
  themeSelect.addEventListener('change', () => setTheme(themeSelect.value));
  add('compact', () => { state.settings.compact = !state.settings.compact; send({ t: 'settings', settings: state.settings }); });
  // A tab opened in the background (as a middle-clicked link would): after the one showing, not selected.
  add('background tab', () => {
    const at = state.tabs.findIndex((t) => t.id === state.active) + 1;
    state.tabs.splice(at, 0, { id: ++seq, title: 'Opened in the background ' + seq, url: 'https://example.test/' + seq, icon: '', loading: false, audio: false, state: 'live' });
    pushTabs();
  });
  add('background tab at end', () => {
    state.tabs.push({ id: ++seq, title: 'Opened in the background ' + seq, url: 'https://example.test/' + seq, icon: '', loading: false, audio: false, state: 'live' });
    pushTabs();
  });
  add('bookmarks bar', () => command({ name: 'toggleBookmarksBar' }));
  add('private', () => { state.isPrivate = !state.isPrivate; send({ t: 'init', isPrivate: state.isPrivate, profile: { id: 'Default', name: 'Personal', avatar: '' }, settings: state.settings, theme: window.IGYO_THEME, window: state.win }); });
  add('inactive', () => { state.win.active = !state.win.active; send(Object.assign({ t: 'window' }, state.win)); });
  add('notice', () => command({ name: 'shot' }));
  add('ctrl+wheel up', () => command({ name: 'zoom', dir: 1 }));     // as Ctrl and the wheel over the page would
  add('ctrl+wheel down', () => command({ name: 'zoom', dir: -1 }));
  add('confirm', () => send({ t: 'dialog', id: 5, kind: 'confirm', title: 'Import your bookmarks from Google Chrome?', message: 'You can do this later from Menu > Bookmarks.', ok: 'Import', cancel: 'Not now' }));
  add('prompt', () => send({ t: 'dialog', id: 6, kind: 'prompt', title: 'New folder', message: 'Name of the new folder:', value: '', ok: 'Create' }));
  add('add profile', () => send({ t: 'dialog', id: 60, kind: 'profile', title: 'Add profile', message: 'Name for the new profile:', value: '', ok: 'Add',
    colors: ['#2563EB', '#7C3AED', '#16A34A', '#D97706', '#E11D48', '#0D9488', '#DB2777', '#475569', '#8AA4FF', '#C5F135', '#FFD500', '#FE58C9', '#FF99C5', '#D9C6A2'], color: '#16A34A' }));
  add('choice', () => send({ t: 'dialog', id: 7, kind: 'choice', title: 'Import from Google Chrome', message: 'Import bookmarks from this Chrome profile:', options: ['Personal (Default)', 'Work (Profile 1)', 'Old (Profile 2)'], ok: 'Import' }));
  add('fullscreen', () => { state.win.fullscreen = !state.win.fullscreen; send(Object.assign({ t: 'window' }, state.win)); });
  // (In the app, the ✕ comes when the pointer reaches the top of the screen in full screen.)
  add('fullscreen ✕', () => send({ t: 'fsExit', show: true }));
  add('claude reads', () => { state.bridge.reading = state.bridge.reading.length ? [] : [state.active]; send(Object.assign({ t: 'bridge' }, state.bridge)); });
  add('claude mark', () => { state.bridge.on = !state.bridge.on; send(Object.assign({ t: 'bridge' }, state.bridge)); });
  // (Claude's questions and its "working in this window" sign are a window of the app's own: not drawn here.)
  add('claude tab', () => {
    state.bridge.own = state.bridge.own && state.bridge.own.length ? [] : [state.tabs[state.tabs.length - 1].id];
    send(Object.assign({ t: 'bridge' }, state.bridge));
  });
  document.body.append(panel);

  window.IGYODevHost = {
    translate: () => showTranslation('', 'en'),
    receive(m) {
      switch (m.t) {
        case 'ready':
          send({ t: 'init', app: 'IGYO', isPrivate: state.isPrivate, profile: { id: 'Default', name: 'Personal', avatar: '' }, settings: state.settings, theme: window.IGYO_THEME, window: state.win, bridge: state.bridge });
          pushTabs(); pushNav(); send(Object.assign({ t: 'bookmarks' }, state.bookmarks));
          break;
        case 'layout': {
          const d = window.devicePixelRatio || 1;
          page.style.left = m.view[0] / d + 'px'; page.style.top = m.view[1] / d + 'px';
          page.style.width = m.view[2] / d + 'px'; page.style.height = m.view[3] / d + 'px';
          window.IGYODev.lastLayout = m;
          break;
        }
        case 'cmd': command(m); break;
        case 'menu': send({ t: 'menu', name: m.name, items: (menus[m.name] || menus.main)() }); break;
        case 'menuItem':
          if (m.id === 15) { state.win.fullscreen = true; send(Object.assign({ t: 'window' }, state.win)); }
          else if (picked[m.id]) picked[m.id]();
          break;
        case 'suggest': {
          const text = m.text || '';
          send({ t: 'suggestions', typed: text, engine: 'Google', typedIsAddress: text.includes('.'), items: text.trim() ? [
            { text: text + ' wiki', display: text + ' wiki', site: false }, { text: text + ' tier list', display: text + ' tier list', site: false },
            { text: 'https://www.' + text.replace(/\s+/g, '') + '.com/', display: text.replace(/\s+/g, '') + '.com', site: true, title: 'Official site', icon: '' },
            { text: 'how to ' + text, display: 'how to ' + text, site: false },
          ] : [] });
          break;
        }
      }
    },
  };
})();
