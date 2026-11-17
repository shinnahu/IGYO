// Development only (never shipped): answers for the app when Settings is opened from tools/serve.js.
// It keeps pretend settings, themes, profiles and bookmarks in memory and applies what the page asks for,
// answering in the same shapes as SettingsPage.cs, so every part of the page can be tried without IGYO.
(() => {
  'use strict';

  const realFetch = window.fetch.bind(window);
  const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const clone = (value) => JSON.parse(JSON.stringify(value));
  const root = document.documentElement;
  const OK = { ok: true };
  const nudge = () => setTimeout(() => window.IGYOSettings && window.IGYOSettings.refresh(), 30);   // the app does this after a setting changes

  const avatar = (letter, color) => 'data:image/svg+xml,' + encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96"><circle cx="48" cy="48" r="48" fill="${color}"/>` +
    `<text x="48" y="65" font-family="Segoe UI" font-size="48" font-weight="600" fill="#fff" text-anchor="middle">${letter}</text></svg>`);

  // ----- kits (a copy of kits.json, plus kits with other options so the editor's lists can be seen changing) -----

  const kits = {
    colors: [
      { key: 'frame', label: 'Window frame', hint: "The tab strip and the window's edge" }, { key: 'frameInk', label: 'Text on the frame' },
      { key: 'surface', label: 'Panels', hint: 'Toolbar, active tab, menus, cards' }, { key: 'surface2', label: 'Quiet panels' },
      { key: 'page', label: 'Page background', hint: 'New Tab and Settings' }, { key: 'field', label: 'Text boxes', hint: 'The address bar and inputs' },
      { key: 'ink', label: 'Text' }, { key: 'ink2', label: 'Secondary text' }, { key: 'ink3', label: 'Faint text' }, { key: 'line', label: 'Lines and borders' },
      { key: 'accent', label: 'Accent' }, { key: 'accentInk', label: 'Text on accent' }, { key: 'accent2', label: 'Second accent' },
      { key: 'good', label: 'Good' }, { key: 'warn', label: 'Warning' }, { key: 'bad', label: 'Danger' },
    ],
    fonts: [{ key: 'ui', label: 'Interface' }, { key: 'display', label: 'Headings' }, { key: 'mono', label: 'Monospace' }],
    fontChoices: ['"Segoe UI Variable Text", "Segoe UI", sans-serif', 'Bahnschrift, "Segoe UI", sans-serif', 'Georgia, serif', '"Cascadia Mono", Consolas, monospace'],
    fx: [
      { key: 'tabIn', label: 'New tabs appear', values: ['grow', 'pop', 'drop', 'slide', 'wipe', 'glitch', 'none'] },
      { key: 'loader', label: 'Loading line', values: ['line', 'ticks', 'stripes', 'blocks'] },
      { key: 'click', label: 'Click burst', values: ['none', 'sparks', 'squares', 'pixels', 'stars', 'notes', 'hearts', 'shards'] },
      { key: 'title', label: 'Tab title changes', values: ['none', 'scramble', 'type'] },
      { key: 'notice', label: 'Notices', values: ['none', 'type'] },
    ],
    // The kits as in kits.json when this was written. Only their shape matters here: the page builds everything from them.
    kits: [
      ['igyo', 'IGYO', 'Soft rounded shapes and calm motion. The quiet one.', 'tabs:Tabs=float/Floating,attached/Attached to the toolbar|window:Window corners=round/Rounded,square/Square'],
      ['bunker', 'Bunker', 'Paper and ink: flat rectangles, hairlines and square marks. What you choose fills with ink from the left; a status line runs along the bottom.', 'status:Status line=on/Shown,off/Hidden|caps:Capital letters=on/On,off/Off|icons:Site icons=mono/One tone,color/In color|pattern:Ruled paper=lines/On,none/Off|window:Window corners=square/Square,round/Rounded'],
      ['protocol', 'Protocol', 'A field terminal: numbered tabs, charcoal blocks, one signal color, readouts and survey lines in the frame.', 'brand:Name block=on/Shown,off/Hidden|readouts:Readouts (tab position, clock)=on/Shown,off/Hidden|contours:Survey lines=on/Shown,off/Hidden|window:Window corners=square/Square,round/Rounded'],
      ['victory', 'Victory', 'Everything leans forward: slanted tabs with a moving shine, hazard stripes, bold italic type.', 'shine:Shine on the active tab=on/On,off/Off|stripes:Hazard stripes=on/On,off/Off|counter:Tab counter=on/Shown,off/Hidden|window:Window corners=round/Rounded,square/Square'],
      ['encore', 'Encore', 'Sticker outlines and candy colors: hopping pill tabs, an equalizer for sound, a combo counter and a PERFECT! when a page lands.', 'tilt:Tilted tabs=on/On,off/Off|combo:Combo counter=on/Shown,off/Hidden|perfect:PERFECT! when a page loads=on/On,off/Off|dots:Dotted frame=on/On,off/Off|window:Window corners=round/Rounded,square/Square'],
    ].map(([id, name, description, options]) => ({
      id, name, description,
      options: options.split('|').map((option) => {
        const [head, values] = option.split('=');
        const [key, label] = head.split(':');
        return { key, label, values: values.split(',').map((v) => ({ value: v.split('/')[0], label: v.split('/')[1] })) };
      }),
    })),
  };

  // ----- themes -----
  // The built-in ones are read from the dev server, which builds /theme.css and /theme.js from the real theme
  // files: their colors, fonts and shape are in the stylesheet's :root block, their options and effects in the script.
  // One theme "of your own" is made up here.

  const themes = [{
    format: 'igyo-theme/1', id: 'sakura-night', name: 'Sakura night', author: 'You', description: 'Pink on plum.', inspiredBy: '', kit: 'igyo', scheme: 'dark',
    colors: Object.fromEntries('frame frameInk surface surface2 page field ink ink2 ink3 line accent accentInk accent2 good warn bad'.split(' ').map((key, i) =>
      [key, '#1d1420 #f3e6ee #2a1d2e #34243a #201624 #1a1220 #f6eaf1 #c3a9bb #8f768a #46334c #ff8fb7 #2a0f1c #b79bff #7be0a5 #ffd27a #ff7a8a'.split(' ')[i]])),
    fonts: {}, shape: { radius: 14, border: 1 }, options: { tabs: 'float' }, fx: { tabIn: 'pop', click: 'hearts' }, vars: {}, wallpaper: '', css: '', builtIn: false,
  }];
  const serverThemes = new Set();   // themes the dev server itself can serve (the page can really switch to these)
  const camel = (name) => name.replace(/-([a-z0-9])/g, (all, c) => c.toUpperCase());

  async function readBuiltIn(info) {
    const [css, js] = await Promise.all(['css', 'js'].map((kind) => realFetch(`/theme.${kind}?theme=${info.id}`).then((r) => r.text())));
    const t = { format: 'igyo-theme/1', id: info.id, name: info.name, author: 'IGYO', description: info.description || '', inspiredBy: info.inspiredBy || '', kit: info.kit, scheme: info.scheme, colors: {}, fonts: {}, shape: {}, options: {}, fx: {}, vars: {}, wallpaper: '', css: '', builtIn: true };
    const block = /\n:root \{\n((?:  --[^\n]*\n)*)\}\n@layer user \{/.exec(css);
    for (const line of block ? block[1].split('\n') : []) {
      const found = /^  --([a-z0-9-]+): (.*);$/.exec(line);
      if (!found) continue;
      const [, name, value] = found;
      if (name.startsWith('c-')) t.colors[camel(name.slice(2))] = value;
      else if (name.startsWith('f-')) t.fonts[camel(name.slice(2))] = value;
      else if (name === 'radius') t.shape.radius = Number(value);
      else if (name === 'bw') t.shape.border = parseFloat(value);
      else t.vars['--' + name] = value;
    }
    const meta = /\}\)\((\{.*\})\);\s*$/.exec(js);
    if (meta) { const m = JSON.parse(meta[1]); t.options = m.options || {}; t.fx = m.fx || {}; }
    return t;
  }

  const ready = realFetch('/__dev/themes').then((r) => r.json()).then(async (list) => {
    const order = ['igyo-dark', 'igyo-light'];
    list.sort((a, b) => (order.indexOf(b.id) - order.indexOf(a.id)) || a.id.localeCompare(b.id));
    const builtIn = await Promise.all(list.map(readBuiltIn));
    builtIn.forEach((t) => serverThemes.add(t.id));
    themes.unshift(...builtIn);
  }).catch((error) => console.warn('[dev] could not read the themes from the dev server', error));

  const summary = (t) => ({ id: t.id, name: t.name, author: t.author, description: t.description, inspiredBy: t.inspiredBy, kit: t.kit, scheme: t.scheme, builtIn: t.builtIn, colors: t.colors });
  const findTheme = (id) => themes.find((t) => t.id === id);

  // ----- state -----

  const settings = {
    Compact: false, Animations: true, NewTabPage: true, SearchSuggestions: true, ShowBookmarksBar: true,
    ShowToolsButton: true, ForceDark: false, EnhanceVideos: true, EnhanceLevel: 'medium',
    'Pin.adblock': true, 'Pin.dark': false, 'Pin.enhance': false, 'Pin.picker': false, 'Pin.translate': true, 'Pin.volume': false, 'Pin.pip': true, 'Pin.shot': true, 'Pin.extensions': false,
    TranslateTo: '', TranslateSecond: '',
    ShowDownloadsButton: true, SearchEngine: 'Google', CustomSearchUrl: '',
    RestoreTabs: true, SwitchToNewTabs: false, AskProfileAtStart: true, AdBlock: true, SavePasswords: true, IdentifyAsEdge: true, SleepAfterMinutes: 5, UnloadAfterMinutes: 30,
    SleepWhenMinimized: true, ReleaseMemory: true, MinimizedSleepSeconds: 30, DownloadFolder: '', ScreenshotFolder: '',
  };
  const app = { name: 'IGYO', tagline: 'Web browser', version: '1.0.2', engine: '157.0.4341.57', runtime: 'C:\\Users\\You\\AppData\\Local\\Programs\\IGYO\\runtime\\Microsoft.WebView2.FixedVersionRuntime.157.0.4341.57.x64', dataDir: 'C:\\Users\\You\\AppData\\Local\\IGYO', adList: '202611130459', adRules: 43205 };
  let startWith = '';   // the default profile, opened at start without asking when that is switched off
  let profiles = [
    { id: 'Default', name: 'Personal', color: '#2563eb', hasPicture: false, open: true },
    { id: 'Profile1', name: 'Work', color: '#7c3aed', hasPicture: true, open: true },
    { id: 'Profile2', name: 'Streaming', color: '#16a34a', hasPicture: false, open: false },
  ];
  // The Claude bridge, pretended: a few sites, one program and some activity to look at.
  const defaultBrowser = { isDefault: false };   // Settings > Default browser
  const claude = {
    on: true, paused: false,
    sites: [
      { site: 'https://mail.example.com', name: 'mail.example.com', mode: 'read' },
      { site: 'https://bank.example.com', name: 'bank.example.com', mode: 'never' },
    ],
    programs: [{ path: 'C:\\Users\\You\\.local\\bin\\claude.exe', name: 'claude.exe', added: '2026-11-14' }],
    activity: [
      { time: '2026-11-14 12:06:31', profile: 'Personal', tool: 'page_text', site: 'mail.example.com', note: 'read 12,400 of 84,210 characters', ok: true },
      { time: '2026-11-14 12:06:02', profile: 'Personal', tool: 'page_text', site: 'bank.example.com', note: 'refused: the site is set to Never', ok: false },
      { time: '2026-11-14 12:05:40', profile: '', tool: 'tabs_list', site: '', note: 'listed 5 tabs', ok: true },
      { time: '2026-11-14 12:04:10', profile: '', tool: 'connection', site: '', note: 'claude.exe connected', ok: true },
    ],
  };
  let chosenTheme = new URLSearchParams(location.search).get('theme') || 'igyo-dark';
  let accent = '';
  let previewing = null;

  const state = () => ({
    app,
    settings: Object.assign({}, settings, { EffectiveScreenshotFolder: settings.ScreenshotFolder || 'C:\\Users\\You\\Pictures\\IGYO' }),
    searchEngines: ['Google', 'DuckDuckGo', 'Bing', 'Brave Search', 'Ecosia', 'Startpage', 'Custom'],
    translateLanguages: ['de', 'en', 'es', 'fr', 'it', 'ja', 'ko', 'pt', 'ru', 'zh-Hans', 'zh-Hant'], translateService: 'MyMemory',
    extensionsFolder: 'C:\\Users\\You\\AppData\\Local\\IGYO\\Extensions', extensionsOn: false,
    sleepOptions: [['Never', 0], ['1 minute', 1], ['5 minutes', 5], ['15 minutes', 15], ['1 hour', 60]].map(([label, value]) => ({ label, value })),
    unloadOptions: [['Never', 0], ['15 minutes', 15], ['30 minutes', 30], ['1 hour', 60], ['4 hours', 240]].map(([label, value]) => ({ label, value })),
    theme: chosenTheme,
    themeProfile: 'Default',   // (the profile whose look this is: each has its own)
    activeTheme: previewing ? previewing.id || 'preview' : chosenTheme === 'auto' ? 'igyo-dark' : chosenTheme,
    previewing: !!previewing,
    themes: themes.map(summary),
    accent,
    accents: [['Rose', '#E11D48'], ['Amber', '#D97706'], ['Yellow', '#FFD500'], ['Acid', '#C5F135'], ['Green', '#16A34A'], ['Teal', '#0D9488'], ['Blue', '#2563EB'], ['Periwinkle', '#8AA4FF'], ['Violet', '#7C3AED'], ['Hot pink', '#FE58C9'], ['Sakura', '#FF99C5'], ['Beige', '#D9C6A2']].map(([name, hex]) => ({ name, hex })),
    profiles: profiles.map((p) => ({ id: p.id, name: p.name, avatar: avatar(p.name[0].toUpperCase(), p.color) + '#' + Date.now(), here: p.id === 'Default', open: p.open, hasPicture: p.hasPicture, isDefault: p.id === 'Default', color: p.color.toUpperCase(), isStartDefault: p.id === startWith })),
    profileColors: ['#2563EB', '#7C3AED', '#16A34A', '#D97706', '#E11D48', '#0D9488', '#DB2777', '#475569', '#8AA4FF', '#C5F135', '#FFD500', '#FE58C9', '#FF99C5', '#D9C6A2'],
    profileNextColor: ['#2563EB', '#7C3AED', '#16A34A', '#D97706', '#E11D48', '#0D9488', '#DB2777', '#475569', '#8AA4FF', '#C5F135', '#FFD500', '#FE58C9', '#FF99C5', '#D9C6A2'][profiles.length % 14],
    window: { isPrivate: false, profile: 'Personal' },
    bridge: {
      on: claude.on, paused: claude.paused,
      command: 'claude mcp add --scope user igyo -- "C:\\Users\\You\\AppData\\Local\\Programs\\IGYO\\IGYO.exe" --mcp',
      connected: claude.on && !claude.paused ? [{ name: 'claude.exe', path: 'C:\\Users\\You\\.local\\bin\\claude.exe', since: '12:04' }] : [],
      sites: claude.sites, programs: claude.programs, activity: claude.activity, activityCount: claude.activity.length,
    },
    defaultBrowser: { installed: true, isDefault: defaultBrowser.isDefault, current: defaultBrowser.isDefault ? '' : 'Another Browser' },
  });

  // ----- how the page looks (what the app does by swapping /theme.css) -----

  let worn = [];
  function wear(t) {
    const style = root.style;
    for (const name of worn) style.removeProperty(name);
    worn = [];
    const put = (name, value) => { style.setProperty(name, value); worn.push(name); };
    const kebab = (key) => key.replace(/([a-z])([A-Z0-9])/g, '$1-$2').toLowerCase();
    for (const [key, value] of Object.entries(t.colors || {})) put('--c-' + kebab(key), value);
    for (const [key, value] of Object.entries(t.fonts || {})) put('--f-' + kebab(key), value);
    if (t.shape && t.shape.radius != null) put('--radius', String(t.shape.radius));
    if (t.shape && t.shape.border != null) put('--bw', t.shape.border + 'px');
    root.dataset.scheme = t.scheme === 'light' ? 'light' : 'dark';
    root.dataset.kit = t.kit || 'igyo';
    let extra = document.getElementById('dev-extra-css');
    if (!extra) { extra = document.createElement('style'); extra.id = 'dev-extra-css'; document.head.append(extra); }
    extra.textContent = `@layer user {\n${t.css || ''}\n}`;
    window.dispatchEvent(new CustomEvent('igyo-theme', { detail: {} }));
  }
  function look() {
    if (previewing) { wear(previewing); return; }
    const id = chosenTheme === 'auto' ? 'igyo-dark' : chosenTheme;
    if (serverThemes.has(id)) {
      for (const name of worn) root.style.removeProperty(name);
      worn = [];
      const extra = document.getElementById('dev-extra-css');
      if (extra) extra.textContent = '';
      history.replaceState(null, '', '?theme=' + id + location.hash);   // the dev server reads the theme from the page's address
      window.IGYOTheme.reload();
    } else if (findTheme(id)) wear(findTheme(id));
    if (accent) root.style.setProperty('--c-accent', accent); else if (!worn.includes('--c-accent')) root.style.removeProperty('--c-accent');
  }

  // ----- bookmarks -----

  let nextId = 100;
  const link = (title, url) => ({ id: ++nextId, title, url, icon: '' });
  const folder = (title, children) => ({ id: ++nextId, title, children });
  const roots = [
    folder('Bookmarks bar', [
      link('Mail', 'https://mail.example.com/'), link('Calendar', 'https://calendar.example.com/'), link('Video', 'https://video.example.com/'),
      folder('Games', [
        link('Tier lists', 'https://games.example.com/tiers'), link('Event calendar', 'https://games.example.com/events'),
        folder('Guides', [link('Beginner guide', 'https://games.example.com/start'), link('Boss notes', 'https://games.example.com/boss'), link('Base layouts', 'https://games.example.com/base'), folder('Old patches', [link('Patch 1.2 notes', 'https://games.example.com/p12')])]),
        link('Gacha rates explained', 'https://games.example.com/rates'), link('Soundtrack list', 'https://music.example.com/ost'),
      ]),
      folder('Work', [link('Tracker', 'https://tracker.example.com/board'), link('Docs', 'https://docs.example.com/'), link('Chat', 'https://chat.example.com/'), link('Build status', 'https://ci.example.com/main'), link('Design system', 'https://design.example.com/tokens')]),
      link('Music', 'https://music.example.com/'), link('News', 'https://news.example.org/'), link('Weather', 'https://weather.example.org/forecast'),
      link('A bookmark with a very long title that should be cut off with an ellipsis somewhere in the list', 'https://long.example.com/a/very/long/address/that/also/keeps/going/and/going?with=query&and=more'),
    ]),
    folder('Other bookmarks', [
      link('Read later', 'https://later.example.com/'),
      folder('Recipes', [link('Pasta', 'https://food.example.net/pasta'), link('Empanadas', 'https://food.example.net/empanadas'), link('Chimichurri', 'https://food.example.net/chimichurri'), link('Flan', 'https://food.example.net/flan')]),
      folder('Imported from Google Chrome', [
        folder('Bookmarks bar', [link('Old mail', 'https://oldmail.example.com/'), link('Forum', 'https://forum.example.com/'), link('Wiki', 'https://wiki.example.org/'), link('Maps', 'https://maps.example.com/')]),
        link('Translator', 'https://translate.example.com/'), link('Dictionary', 'https://dictionary.example.com/'), link('Bank', 'https://bank.example.com/'),
        link('Shopping list', 'https://shop.example.com/list'), link('Parcel tracking', 'https://post.example.com/track'), link('Tickets', 'https://tickets.example.com/'),
      ]),
      folder('Empty folder', []),
      link('Art references', 'https://art.example.com/refs'), link('Color tools', 'https://color.example.com/'), link('Fonts', 'https://fonts.example.com/'),
    ]),
  ];
  const flat = (node, out = []) => { out.push(node); (node.children || []).forEach((c) => flat(c, out)); return out; };
  const all = () => roots.flatMap((r) => flat(r));
  const byId = (id) => all().find((n) => n.id === id);
  const parentOf = (node) => all().find((n) => n.children && n.children.includes(node));

  // As BookmarkStore.Move: index is counted with the node still in its old place.
  function move(node, target, index) {
    if (!node || !target || !target.children || flat(node).includes(target) || roots.includes(node)) return false;
    const parent = parentOf(node);
    if (!parent) return false;
    const old = parent.children.indexOf(node);
    parent.children.splice(old, 1);
    if (parent === target && index > old) index--;
    if (index < 0 || index > target.children.length) target.children.push(node); else target.children.splice(index, 0, node);
    return true;
  }

  function picture() {
    const canvas = document.createElement('canvas');
    canvas.width = 640;
    canvas.height = 400;
    const g = canvas.getContext('2d');
    const fill = g.createLinearGradient(0, 0, 640, 400);
    const hue = Math.floor(Math.random() * 360);
    fill.addColorStop(0, `hsl(${hue} 70% 55%)`);
    fill.addColorStop(1, `hsl(${(hue + 80) % 360} 70% 35%)`);
    g.fillStyle = fill;
    g.fillRect(0, 0, 640, 400);
    return canvas.toDataURL('image/jpeg', 0.8);
  }

  // ----- the answers -----

  const BOOLS = ['Compact', 'Animations', 'NewTabPage', 'SearchSuggestions', 'ShowBookmarksBar', 'ShowDownloadsButton', 'RestoreTabs', 'SwitchToNewTabs', 'AskProfileAtStart', 'AdBlock', 'SavePasswords', 'IdentifyAsEdge', 'SleepWhenMinimized', 'ReleaseMemory', 'ShowToolsButton', 'ForceDark', 'EnhanceVideos',
    'Pin.adblock', 'Pin.dark', 'Pin.enhance', 'Pin.picker', 'Pin.translate', 'Pin.volume', 'Pin.pip', 'Pin.shot', 'Pin.extensions'];

  function saveTheme(incoming) {
    const t = Object.assign(clone(incoming), { builtIn: false });
    const existing = findTheme(t.id);
    if (!existing || existing.builtIn) {
      const slug = (t.name || 'theme').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'theme';
      t.id = slug;
      for (let n = 2; findTheme(t.id); n++) t.id = `${slug}-${n}`;
      themes.push(t);
    } else themes[themes.indexOf(existing)] = t;
    return t;
  }

  async function post(command, a) {
    switch (command) {
      case 'set':
        if (BOOLS.includes(a.key) && typeof a.value === 'boolean') settings[a.key] = a.value;
        else if (a.key === 'SearchEngine' || a.key === 'CustomSearchUrl' || a.key === 'EnhanceLevel' || a.key === 'TranslateTo' || a.key === 'TranslateSecond') settings[a.key] = String(a.value);
        else if (a.key === 'SleepAfterMinutes' || a.key === 'UnloadAfterMinutes') settings[a.key] = Number(a.value);
        else if (a.key === 'Accent' && (a.value === '' || /^#[0-9a-f]{3,8}$/i.test(a.value))) { accent = String(a.value).toUpperCase(); look(); }
        else return { error: "That setting can't be changed here." };
        nudge();
        return OK;
      case 'theme/apply':
        if (a.id !== 'auto' && !findTheme(a.id)) return { error: 'That theme no longer exists.' };
        chosenTheme = a.id; previewing = null; look(); nudge();
        return OK;
      case 'theme/preview': previewing = clone(a.theme || {}); look(); return OK;
      case 'theme/preview-end': if (previewing) { previewing = null; look(); } return OK;
      case 'theme/save': {
        if (!a.theme) return { error: "That theme can't be saved." };
        const saved = saveTheme(a.theme);
        if (a.apply) { chosenTheme = saved.id; nudge(); }
        previewing = null; look();
        return { ok: true, id: saved.id };
      }
      case 'theme/duplicate': {
        const source = findTheme(a.id);
        if (!source) return { error: 'That theme no longer exists.' };
        return { ok: true, id: saveTheme(Object.assign(clone(source), { id: '', name: source.name + ' copy', author: '' })).id };
      }
      case 'theme/delete': {
        const t = findTheme(a.id);
        if (!t || t.builtIn) return { error: 'Only your own themes can be deleted.' };
        themes.splice(themes.indexOf(t), 1);
        if (chosenTheme === t.id) { chosenTheme = 'auto'; nudge(); }
        previewing = null; look();
        return OK;
      }
      case 'theme/import': {
        await wait(600);
        const t = saveTheme(Object.assign(clone(findTheme('midnight') || themes[0]), { id: '', name: 'Imported theme', author: 'Someone' }));
        return { ok: true, ids: [t.id] };
      }
      case 'theme/export': await wait(400); return findTheme(a.id) ? { ok: true, file: `C:\\Users\\You\\Documents\\${a.id}.igyo-theme.json` } : { error: 'That theme no longer exists.' };
      case 'theme/picture': await wait(500); return { ok: true, data: picture() };
      case 'folder': {
        const key = a.which === 'downloads' ? 'DownloadFolder' : 'ScreenshotFolder';
        if (a.reset) settings[key] = ''; else { await wait(600); settings[key] = a.which === 'downloads' ? 'D:\\Downloads\\IGYO' : 'D:\\Pictures\\Screenshots'; }
        nudge();
        return OK;
      }
      case 'adblock/update': await wait(1400); app.adList = String(Number(app.adList) + 1); app.adRules += 37; return { ok: true, version: app.adList, rules: app.adRules };
      case 'clear': await wait(1100); return (a.kinds || []).length ? OK : { error: 'Nothing was selected.' };
      case 'open': console.info('[dev] the app would open:', a.what); return OK;
      // (The app opens Windows' page; here the choice is made at once, to see both states.)
      case 'default-browser': await wait(300); defaultBrowser.isDefault = !defaultBrowser.isDefault; return OK;
      case 'bridge/enable': claude.on = !!a.on; nudge(); return OK;
      case 'bridge/pause': claude.paused = !!a.on; nudge(); return OK;
      case 'bridge/site': {
        const s = claude.sites.find((x) => x.site === a.site);
        if (!s) return { error: "That site can't be set." };
        if (a.mode === 'ask') claude.sites.splice(claude.sites.indexOf(s), 1); else s.mode = a.mode;
        nudge();
        return OK;
      }
      case 'bridge/program-remove': claude.programs = claude.programs.filter((p) => p.path !== a.path); nudge(); return OK;
      case 'bridge/activity-clear': claude.activity = []; nudge(); return OK;
      case 'profile/add': {
        const name = String(a.name || '').trim();
        if (!name) return { error: 'Give the profile a name.' };
        const color = /^#[0-9a-f]{6}$/i.test(a.color || '') ? a.color : ['#d97706', '#e11d48', '#0d9488'][profiles.length % 3];   // as picked in its circle
        profiles.push({ id: 'Profile' + (profiles.length + 1), name: name.slice(0, 40), color, hasPicture: false, open: false });
        nudge();
        return OK;
      }
      case 'profile/rename': {
        const p = profiles.find((x) => x.id === a.id);
        const name = String(a.name || '').trim();
        if (!p || !name) return { error: 'Give the profile a name.' };
        p.name = name.slice(0, 40); nudge();
        return OK;
      }
      case 'profile/delete': {
        const p = profiles.find((x) => x.id === a.id);
        if (!p || p.id === 'Default') return { error: "That profile can't be deleted." };
        if (p.open) return { error: `Close the windows of "${p.name}" first.` };
        profiles = profiles.filter((x) => x !== p); nudge();
        return OK;
      }
      case 'profile/open': { const p = profiles.find((x) => x.id === a.id); if (p) p.open = true; nudge(); return OK; }
      case 'profile/picture': { await wait(500); const p = profiles.find((x) => x.id === a.id); if (!p) return { error: 'That profile no longer exists.' }; p.hasPicture = true; p.color = '#db2777'; nudge(); return OK; }
      case 'profile/default': { const p = profiles.find((x) => x.id === a.id); if (!p) return { error: 'That profile no longer exists.' }; if (a.on) startWith = p.id; else if (startWith === p.id) startWith = ''; nudge(); return OK; }
      case 'profile/color': { const p = profiles.find((x) => x.id === a.id); if (!p) return { error: 'That profile no longer exists.' }; if (!/^#[0-9a-f]{6}$/i.test(a.color || '')) return { error: 'That is not a color.' }; p.color = a.color.toUpperCase(); nudge(); return OK; }
      case 'profile/remove-picture': { const p = profiles.find((x) => x.id === a.id); if (p) p.hasPicture = false; nudge(); return OK; }
      case 'bm/open': console.info('[dev] the app would open the bookmark', (byId(a.id) || {}).url); return OK;
      case 'bm/add-folder': {
        const parent = byId(a.parent) && byId(a.parent).children ? byId(a.parent) : roots[0];
        const made = folder(String(a.title || '').trim() || 'New folder', []);
        parent.children.push(made);
        return { ok: true, id: made.id };
      }
      case 'bm/add': {
        const parent = byId(a.parent) && byId(a.parent).children ? byId(a.parent) : roots[0];
        const address = String(a.url || '').trim();
        if (!address || /\s/.test(address) || !address.includes('.')) return { error: 'Type a web address, like example.com' };
        const made = link(String(a.title || '').trim() || address, /^[a-z]+:\/\//i.test(address) ? address : 'https://' + address + '/');
        parent.children.push(made);
        return { ok: true, id: made.id };
      }
      case 'bm/edit': {
        const b = byId(a.id);
        if (!b || roots.includes(b)) return { error: 'That bookmark no longer exists.' };
        b.title = String(a.title || '').trim() || b.url || 'Folder';
        if (!b.children && String(a.url || '').trim()) b.url = String(a.url).trim();
        return OK;
      }
      case 'bm/delete': {
        const b = byId(a.id);
        if (!b || roots.includes(b)) return { error: 'That bookmark no longer exists.' };
        parentOf(b).children.splice(parentOf(b).children.indexOf(b), 1);
        return OK;
      }
      case 'bm/move': return move(byId(a.id), byId(a.folder), a.index == null ? -1 : Number(a.index)) ? OK : { error: "That can't be moved there." };
      case 'bm/import-chrome': case 'bm/import-file':
        setTimeout(() => roots[1].children.push(folder('Imported just now', [link('Imported page', 'https://imported.example.com/'), link('Another one', 'https://another.example.com/')])), 2500);
        return OK;
      case 'bm/export': console.info('[dev] the app would export the bookmarks'); return OK;
    }
    return null;
  }

  function get(url) {
    switch (url.pathname) {
      case '/api/state': return state();
      case '/api/kits': return kits;
      case '/api/bookmarks': return { roots, chrome: true };
      case '/api/memory': {
        const engine = 380 + Math.round(Math.random() * 60);
        return { total: engine + 61, engine, processes: 7, own: 61, tabs: [
          { title: 'Settings', state: 'Active' }, { title: 'Field notes and base planning: a wiki page with a long title', state: 'Open' },
          { title: 'lofi beats to browse to', state: 'Open' }, { title: 'An old tab that went to sleep a while ago and has a rather long title for a tab', state: 'Sleeping' },
          { title: 'Recipes — empanadas', state: 'Unloaded' }, { title: 'Work tracker', state: 'Active' },
        ] };
      }
      case '/api/theme': { const t = findTheme(url.searchParams.get('id')); return t ? Object.assign(clone(t), { builtIn: undefined }) : null; }
    }
    return null;
  }

  const answer = (value) => (value == null
    ? new Response('', { status: 404 })
    : new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } }));

  window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url, location.href);
    if (url.origin !== location.origin || !url.pathname.startsWith('/api/')) return realFetch(input, init);
    await ready;
    await wait(25);
    if (((init && init.method) || 'GET') === 'GET') return answer(get(url));
    let body = {};
    try { body = JSON.parse((init && init.body) || '{}'); } catch { body = {}; }
    return answer(await post(url.pathname.slice('/api/'.length), body));
  };

  // The dev server has no logo to serve; a plain mark stands in for it.
  if (window.ST) {
    window.ST.logo = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="14" fill="#3d5afe"/><path d="M18 20h28M32 20v26" stroke="#fff" stroke-width="7" stroke-linecap="round"/></svg>');
  }

  window.IGYODevApi = { settings, themes, roots, look, get profiles() { return profiles; } };
  console.info('[dev] Settings is using dev-api.js instead of IGYO.');
})();
