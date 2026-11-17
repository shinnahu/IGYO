// IGYO — Settings > Appearance: the theme gallery, the accent color, and the theme editor.
// Kits, their options, the colors a theme has and the effects all come from /api/kits (kits.json):
// nothing about a particular kit is written here.
(() => {
  'use strict';

  const ST = window.ST;
  const tr = window.IGYO.tr;
  const { h, icon, kebab, api, onSync, onLeave, notice, settled, confirm, menu, header, group, row, button, toggle, choice, busy } = ST;
  const root = document.documentElement;
  // The accent colors IGYO offers have names; they are among its own texts.
  const ACCENT_NAMES = { Periwinkle: tr('Periwinkle'), Acid: tr('Acid'), Yellow: tr('Yellow'), 'Hot pink': tr('Hot pink'), Sakura: tr('Sakura'), Beige: tr('Beige'), Blue: tr('Blue'), Violet: tr('Violet'), Green: tr('Green'), Amber: tr('Amber'), Rose: tr('Rose'), Teal: tr('Teal') };

  const UNSAFE = /[;{}<>\\]|url\(|@import|expression\(/i;   // a theme value may never contain these (the app drops such values too)

  // The colors a theme gets when it doesn't name one: the design system's own, light and dark.
  // Only used to draw the small pictures in the gallery.
  const BASE = {
    light: { frame: '#e4e7ee', frameInk: '#20242c', surface: '#ffffff', surface2: '#f2f4f8', page: '#f4f5f9', field: '#eef0f5', ink: '#1b1f27', ink2: '#5c6473', ink3: '#8a91a0', line: '#dfe3ea', accent: '#3d5afe', accentInk: '#ffffff' },
    dark: { frame: '#17181c', frameInk: '#e6e8ee', surface: '#24262c', surface2: '#2c2e35', page: '#1a1b20', field: '#17181c', ink: '#eceef3', ink2: '#a5abb8', ink3: '#767d8b', line: '#35373f', accent: '#8aa4ff', accentInk: '#0b1230' },
  };
  const AUTO = { id: 'auto', name: tr('Automatic'), description: tr('Light or dark, with Windows') };

  const colorOf = (theme, key) => (theme.colors && theme.colors[key]) || BASE[theme.scheme === 'light' ? 'light' : 'dark'][key];

  // ----- the gallery -----

  // A small browser drawn with a theme's own colors: tabs, toolbar with its address pill, a bit of page.
  function mock(theme) {
    const vars = {};
    for (const key of Object.keys(BASE.dark)) vars['--m-' + kebab(key)] = colorOf(theme, key);
    const i = (className) => h('i', { class: className });
    return h('span', { class: 'st-mock', vars, 'aria-hidden': 'true' },
      h('span', { class: 'st-mock-strip' }, i('st-mock-tab'), i('st-mock-tab is-on'), i('st-mock-tab')),
      h('span', { class: 'st-mock-bar' }, i('st-mock-dot'), i('st-mock-pill'), i('st-mock-dot')),
      h('span', { class: 'st-mock-page' }, i('st-mock-line is-long'), i('st-mock-line'), i('st-mock-btn'), i('st-mock-card')));
  }

  const autoMock = () => h('span', { class: 'st-mock-auto', 'aria-hidden': 'true' }, mock({ scheme: 'light' }), mock({ scheme: 'dark' }));

  function card(theme, state) {
    const chosen = state.theme === theme.id;
    const byline = theme.inspiredBy ? tr('Inspired by {0}', theme.inspiredBy)
      : theme.id === 'auto' || theme.builtIn ? tr(theme.description || '')   // a built-in theme's description is one of IGYO's own texts
      : theme.author ? tr('By {0}', theme.author) : theme.description || '';
    const node = h('div', { class: 'st-theme' + (chosen ? ' is-chosen' : '') },
      h('button', { type: 'button', class: 'st-theme-pick', 'aria-pressed': String(chosen), title: (theme.builtIn ? tr(theme.description || '') : theme.description) || theme.name, onclick: () => api.post('theme/apply', { id: theme.id }).then((r) => settled(r)) },
        theme.id === 'auto' ? autoMock() : mock(theme),
        h('span', { class: 'st-theme-meta' },
          h('span', { class: 'st-theme-name' }, h('span', { text: theme.name }), chosen ? h('span', { class: 'st-theme-check', title: tr('In use'), html: icon('check') }) : null),
          h('span', { class: 'st-theme-by', text: byline }))));
    if (theme.id !== 'auto') {
      node.append(h('button', {
        type: 'button', class: 't-iconbtn st-theme-more', title: tr('More'), 'aria-label': tr('More for {0}', theme.name), 'aria-haspopup': 'menu',
        html: icon('more'), onclick: (event) => themeMenu(event.currentTarget, theme),
      }));
    }
    return node;
  }

  function themeMenu(anchor, theme) {
    menu(anchor, [
      { label: theme.builtIn ? tr('Customize a copy') : tr('Customize'), icon: 'edit', run: () => { location.hash = '#themes/edit/' + theme.id; } },
      { label: tr('Duplicate'), icon: 'copy', run: async () => settled(await api.post('theme/duplicate', { id: theme.id }), tr('Made a copy of "{0}".', theme.name)) },
      { label: tr('Export…'), icon: 'upload', run: async () => { const r = await api.post('theme/export', { id: theme.id }); if (settled(r)) notice(tr('Exported to {0}', r.file)); } },
      theme.builtIn ? null : { sep: true },
      theme.builtIn ? null : {
        label: tr('Delete'), icon: 'trash', danger: true,
        run: async () => {
          if (!await confirm(tr('Delete the theme "{0}"?', theme.name), tr("This can't be undone."), tr('Delete'), true)) return;
          settled(await api.post('theme/delete', { id: theme.id }), tr('Deleted "{0}".', theme.name));
        },
      },
    ]);
  }

  async function importThemes(node) {
    const result = await busy(node, tr('Importing…'), () => api.post('theme/import'));
    if (!result || result.cancelled) return;
    const count = (result.ids || []).length;
    if (result.error) notice(count ? tr('Imported {0}. {1}', count, result.error) : result.error, 'error');
    else if (count) notice(count === 1 ? tr('Imported 1 theme.') : tr('Imported {0} themes.', count));
  }

  function accentRow(card) {
    const dots = h('div', { class: 'st-accents', role: 'radiogroup', 'aria-label': tr('Accent color') });
    const custom = h('input', { type: 'color', class: 'st-accent-input', 'aria-label': tr('Your own accent color') });
    const pick = async (hex) => settled(await api.set('Accent', hex));
    custom.addEventListener('change', () => pick(custom.value));
    row(card, { title: tr('Accent color'), desc: tr('Highlights, switches, links and the loading line.'), below: dots });

    const dot = (name, color, chosen, run, own) => h('button', {
      type: 'button', class: 'st-accent' + (chosen ? ' is-chosen' : '') + (own ? ' is-own' : ''), role: 'radio', 'aria-checked': String(chosen),
      title: name, 'aria-label': name, vars: { '--dot': color }, onclick: run,
    }, own ? h('span', { text: 'A', 'aria-hidden': 'true' }) : null);

    let signature = '';
    onSync((state) => {
      const chosen = (state.accent || '').toLowerCase();
      const active = state.themes.find((t) => t.id === state.activeTheme) || { scheme: root.dataset.scheme, colors: {} };
      const presets = state.accents || [];
      const next = JSON.stringify([chosen, colorOf(active, 'accent'), presets]);
      if (next === signature) return;
      signature = next;
      const mine = !!chosen && !presets.some((a) => a.hex.toLowerCase() === chosen);
      if (/^#[0-9a-f]{6}$/.test(chosen)) custom.value = chosen;
      dots.replaceChildren(
        dot(tr("Theme's own"), colorOf(active, 'accent'), !chosen, () => pick(''), true),
        ...presets.map((a) => dot(ACCENT_NAMES[a.name] || a.name, a.hex, a.hex.toLowerCase() === chosen, () => pick(a.hex))),
        h('label', { class: 'st-accent is-custom' + (mine ? ' is-chosen' : ''), title: tr('Your own color'), vars: mine ? { '--dot': chosen } : null }, custom, mine ? null : h('span', { html: icon('plus') })));
    });
  }

  function renderAppearance(page) {
    header(page, tr('Appearance'), tr('Make IGYO look the way you like.'));
    // The language of IGYO's own menus and pages (each language is listed under its own name). Choosing one
    // loads IGYO's pages again, this one included. Websites are not told anything by it.
    const top = h('div', { class: 'st-narrow' });
    page.append(top);
    choice(group(top), {
      title: tr('Language'), desc: tr("For IGYO's own menus and pages. Websites are not affected."),
      options: (state) => (state.languages || []).map((l) => ({ label: l.name, value: l.id })),
      get: (state) => state.language || 'auto',
      set: (value) => api.set('Language', value),
    });
    page.append(h('div', { class: 'st-group-head' },
      h('h2', { class: 'st-group-label', text: tr('Themes') }),
      h('div', { class: 'st-actions' },
        button(tr('Import…'), importThemes, { icon: 'download' }),
        button(tr('New theme'), () => { location.hash = '#themes/edit/new'; }, { icon: 'plus' }),
        button(tr('Open themes folder'), async () => settled(await api.post('open', { what: 'themes-folder' })), { icon: 'folder', ghost: true }))));
    const builtIn = h('div', { class: 'st-gallery' });
    const mine = h('div', { class: 'st-gallery' });
    const none = h('p', { class: 'st-note', text: tr('Themes you make or import appear here.') });
    // The theme and the accent color are this window's profile's: each profile keeps its own.
    const whose = h('p', { class: 'st-note' });
    onSync((state) => { whose.textContent = tr('Theme and accent color for the profile "{0}". Each profile keeps its own.', state.themeProfile || ''); });
    page.append(whose, builtIn,
      h('p', { class: 'st-note', text: tr("Automatic follows Windows' light or dark mode. Pages that support it switch with the theme.") }),
      h('h2', { class: 'st-group-label', text: tr('Your themes') }), mine, none);

    // Built-in themes are shown kit by kit, in the order kits.json lists the kits, a kit's namesake themes first.
    const kitOrder = ((ST.kits || {}).kits || []).map((k) => k.id);
    const rank = (t) => { const at = kitOrder.indexOf(t.kit); return (at < 0 ? kitOrder.length : at) * 2 + (t.id.startsWith(t.kit) ? 0 : 1); };

    let signature = '';
    onSync((state) => {
      const next = JSON.stringify([state.themes, state.theme]);
      if (next === signature) return;
      signature = next;
      const own = state.themes.filter((t) => !t.builtIn);
      const shipped = state.themes.filter((t) => t.builtIn).map((t, i) => [rank(t), i, t]).sort((a, b) => a[0] - b[0] || a[1] - b[1]).map((entry) => entry[2]);
      builtIn.replaceChildren(card(AUTO, state), ...shipped.map((t) => card(t, state)));
      mine.replaceChildren(...own.map((t) => card(t, state)));
      mine.hidden = !own.length;
      none.hidden = own.length > 0;
    });

    const narrow = h('div', { class: 'st-narrow' });
    page.append(narrow);
    accentRow(group(narrow, tr('Accent')));
    const layout = group(narrow, tr('Layout and motion'));
    toggle(layout, { title: tr('Animations'), desc: tr('Tabs, lists, panels and the New Tab page move smoothly. Turn off for an instant, calmer feel.'), key: 'Animations' });
    toggle(layout, { title: tr('Compact mode'), desc: tr('Smaller tabs and toolbar, more room for pages.'), key: 'Compact' });
    toggle(layout, { title: tr('Show the bookmarks bar'), key: 'ShowBookmarksBar' });
  }

  // ----- the editor -----

  const isHex = (v) => /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(v);
  const fullHex = (v) => (v.length === 4 ? '#' + [...v.slice(1)].map((c) => c + c).join('') : v).toLowerCase();
  const title = (v) => v.charAt(0).toUpperCase() + v.slice(1);

  // Any CSS color as #rrggbb (a color box only takes that form): a canvas paints it and is read back.
  const paint = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  paint.canvas.width = paint.canvas.height = 1;
  function toHex(color) {
    paint.clearRect(0, 0, 1, 1);
    paint.fillStyle = '#000000';
    paint.fillStyle = color;
    paint.fillRect(0, 0, 1, 1);
    const [r, g, b] = paint.getImageData(0, 0, 1, 1).data;
    return '#' + [r, g, b].map((n) => n.toString(16).padStart(2, '0')).join('');
  }

  function normalize(t) {
    const text = (v) => (typeof v === 'string' ? v : '');
    const map = (v) => (v && typeof v === 'object' ? Object.assign({}, v) : {});
    return Object.assign({}, t, {
      id: text(t.id), name: text(t.name), author: text(t.author), description: text(t.description), inspiredBy: text(t.inspiredBy),
      kit: text(t.kit) || 'igyo', scheme: t.scheme === 'light' ? 'light' : 'dark',
      colors: map(t.colors), fonts: map(t.fonts), shape: map(t.shape), options: map(t.options), fx: map(t.fx), vars: map(t.vars),
      wallpaper: text(t.wallpaper), css: text(t.css),
    });
  }

  // A few ordinary things in the theme being edited, so a change is seen without looking up at the toolbar.
  function sample() {
    return h('div', { class: 'st-sample', role: 'group', 'aria-label': tr('Sample') },
      h('button', { type: 'button', class: 't-btn', text: tr('Button') }),
      h('button', { type: 'button', class: 't-btn is-primary', text: tr('Primary') }),
      h('input', { type: 'text', class: 't-input st-sample-input', value: tr('Text box'), 'aria-label': tr('Sample text box') }),
      h('span', { class: 't-switch' }, h('input', { type: 'checkbox', checked: true, 'aria-label': tr('Sample switch') }), h('i')),
      h('span', { class: 't-chip', text: tr('Chip') }),
      h('div', { class: 't-card st-sample-card' },
        h('div', { class: 'st-sample-title', text: tr('A card') }),
        h('div', { class: 'st-row-desc', text: tr('Secondary text') }),
        h('div', { class: 't-meter' }, h('i', { vars: { '--v': '.6' } }))));
  }

  async function renderEditor(page, wanted) {
    page.classList.add('st-editor');
    const opening = h('p', { class: 'st-note', text: tr('Opening the theme…') });
    page.append(opening);
    const state = ST.state;
    const kits = ST.kits || {};

    // "New theme" starts from the theme in use.
    let source = null;
    for (const id of wanted === 'new' ? [state.activeTheme, state.theme, 'igyo-dark'] : [wanted]) {
      if (source || !id || id === 'auto') continue;
      try { source = await api.get('/api/theme?id=' + encodeURIComponent(id)); } catch { source = null; }
    }
    if (!page.isConnected) return;   // another section was opened meanwhile
    if (!source) {
      notice(tr('That theme no longer exists.'), 'error');
      location.hash = '#appearance';
      return;
    }
    opening.remove();

    const summary = state.themes.find((t) => t.id === source.id);
    const copy = wanted === 'new' || !summary || summary.builtIn;   // a built-in theme is never changed: a copy is
    const theme = normalize(source);
    const sourceName = theme.name;
    if (copy) {
      theme.id = '';
      theme.name = wanted !== 'new' ? tr('My {0}', sourceName) : summary && !summary.builtIn ? tr('{0} copy', sourceName) : tr('My theme');
      theme.author = '';
    }
    const kitList = [...(kits.kits || [])];
    if (!kitList.some((k) => k.id === theme.kit)) kitList.push({ id: theme.kit, name: theme.kit, options: [] });
    const kitOf = (id) => kitList.find((k) => k.id === id) || { options: [] };

    // ----- live preview: the whole theme goes to the app, at most about five times a second, and never
    // faster than the app answers (a theme with a picture in it is a large message).
    let timer = 0, sending = false, pending = false, lastSent = 0, ended = false;
    function changed() {
      if (ended) return;
      if (sending) { pending = true; return; }
      if (!timer) timer = setTimeout(send, Math.max(0, 200 - (Date.now() - lastSent)));
    }
    async function send() {
      timer = 0;
      sending = true;
      pending = false;
      lastSent = Date.now();
      await api.post('theme/preview', { theme });
      sending = false;
      if (pending) changed();
    }
    function endPreview() {
      if (ended) return Promise.resolve();
      ended = true;
      clearTimeout(timer);
      return api.post('theme/preview-end');
    }
    // Closing or reloading the page also ends it; keepalive lets the request outlive the page.
    const onHide = () => { if (!ended) fetch('/api/theme/preview-end', { method: 'POST', body: '{}', keepalive: true }).catch(() => {}); };

    // What a value falls back to when the theme doesn't set it, read from the page itself: the page wears the theme being edited.
    const probe = h('span', { hidden: true });
    const tokenHex = (key) => { probe.style.color = `var(--c-${kebab(key)})`; return toHex(getComputedStyle(probe).color); };
    const token = (name) => getComputedStyle(root).getPropertyValue(name).trim();
    const shows = [];   // each redraws one control from the theme (and from the page, for what the theme leaves to the kit)
    const showAll = () => shows.forEach((fn) => fn());
    const resetButton = (what, run) => h('button', { type: 'button', class: 't-iconbtn st-reset', title: tr("Use the kit's own"), 'aria-label': tr("Use the kit's own: {0}", what), html: icon('undo'), onclick: run });

    // ----- top: back, title, actions, sample -----
    const saveButton = button(tr('Save'), save, { primary: true });
    const exportButton = button(tr('Export…'), exportIt, { title: tr('Saves the theme, then writes it to a file') });
    const body = h('div', { class: 'st-narrow' });
    page.append(
      h('div', { class: 'st-editor-top' },
        h('div', { class: 'st-editor-bar' },
          button(tr('Themes'), cancel, { icon: 'back', ghost: true, title: tr('Back to the themes without saving') }),
          h('div', { class: 'st-editor-title' },
            h('h1', { class: 't-title st-h1', text: copy ? tr('New theme') : tr('Customize theme') }),
            h('p', { class: 'st-sub', text: (copy ? tr('Starts as a copy of {0}.', sourceName) + ' ' : '') + tr('Changes show in the whole browser as you make them; nothing is kept until you save.') })),
          h('div', { class: 'st-editor-actions' }, exportButton, button(tr('Cancel'), cancel), saveButton)),
        sample()),
      body, probe);

    // ----- about -----
    const box = (value, label, max) => h('input', { type: 'text', class: 't-input', value, maxLength: max, spellcheck: false, 'aria-label': label });
    const field = (label, control, wide) => h('label', { class: 't-field' + (wide ? ' is-wide' : '') }, h('span', { text: label }), control);
    const nameBox = box(theme.name, tr('Name'), 60);
    const authorBox = box(theme.author, tr('Author'), 60);
    const descriptionBox = box(theme.description, tr('Description'), 240);
    nameBox.addEventListener('input', () => { theme.name = nameBox.value; });
    authorBox.addEventListener('input', () => { theme.author = authorBox.value; });
    descriptionBox.addEventListener('input', () => { theme.description = descriptionBox.value; });
    group(body, tr('About this theme')).append(h('div', { class: 'st-form' }, field(tr('Name'), nameBox), field(tr('Author'), authorBox), field(tr('Description'), descriptionBox, true)));

    // ----- kit, light or dark -----
    const kitCard = group(body, tr('Kit'));
    const kitSelect = h('select', { class: 't-select st-select', 'aria-label': tr('Kit') }, kitList.map((k) => h('option', { value: k.id, text: k.name || k.id })));
    kitSelect.value = theme.kit;
    const kitRow = row(kitCard, { title: tr('Kit'), end: [kitSelect] });
    const describeKit = () => kitRow.setDesc(tr(kitOf(theme.kit).description || '') || tr('The shapes, decorations and animations the theme is drawn with.'));
    describeKit();
    kitSelect.addEventListener('change', () => {
      // Options that only the old kit had go; ones the new kit also has stay.
      const had = (kitOf(theme.kit).options || []).map((o) => o.key);
      const has = new Set((kitOf(kitSelect.value).options || []).map((o) => o.key));
      for (const key of had) if (!has.has(key)) delete theme.options[key];
      theme.kit = kitSelect.value;
      describeKit();
      drawOptions();
      changed();
    });
    const schemeButtons = ['light', 'dark'].map((scheme) => h('button', {
      type: 'button', class: 'st-seg-item', html: icon(scheme === 'light' ? 'sun' : 'moon'),
      onclick: () => { theme.scheme = scheme; showScheme(); changed(); },
    }, h('span', { text: scheme === 'light' ? tr('Light') : tr('Dark') })));
    const showScheme = () => schemeButtons.forEach((b, i) => b.setAttribute('aria-pressed', String((i === 0) === (theme.scheme === 'light'))));
    showScheme();
    row(kitCard, { title: tr('Light or dark'), desc: tr('What the kit falls back to for anything the theme leaves out, and what pages are told you prefer.'), end: [h('div', { class: 'st-seg', role: 'group', 'aria-label': tr('Light or dark') }, schemeButtons)] });

    // ----- colors -----
    const colorGrid = h('div', { class: 'st-colors' });
    group(body, tr('Colors')).append(colorGrid);
    for (const def of kits.colors || []) {
      const key = def.key;
      const swatch = h('input', { type: 'color', class: 't-swatch', 'aria-label': tr(def.label) });
      const text = h('input', { type: 'text', class: 't-input t-mono st-hex', spellcheck: false, maxLength: 120, 'aria-label': tr('{0}, as a color value', tr(def.label)) });
      const line = h('div', { class: 'st-color' }, swatch,
        h('div', { class: 'st-color-text' }, h('div', { class: 'st-row-title', text: tr(def.label) }), def.hint ? h('div', { class: 'st-row-desc', text: tr(def.hint) }) : null),
        text, resetButton(tr(def.label), () => { delete theme.colors[key]; show(); changed(); }));
      const show = () => {
        const own = theme.colors[key];
        const hex = !own ? tokenHex(key) : isHex(own) ? fullHex(own) : toHex(own);
        swatch.value = hex;
        text.placeholder = hex;
        if (document.activeElement !== text) { text.value = own || ''; text.removeAttribute('aria-invalid'); }
        line.classList.toggle('is-own', !!own);
      };
      swatch.addEventListener('input', () => { theme.colors[key] = swatch.value; text.value = swatch.value; text.removeAttribute('aria-invalid'); line.classList.add('is-own'); changed(); });
      text.addEventListener('input', () => {
        const value = text.value.trim();
        const fine = !value || (value.length <= 120 && !UNSAFE.test(value) && CSS.supports('color', value));
        if (fine) text.removeAttribute('aria-invalid'); else text.setAttribute('aria-invalid', 'true');
        if (!fine) return;
        if (value) theme.colors[key] = isHex(value) ? fullHex(value) : value; else delete theme.colors[key];
        swatch.value = !value ? tokenHex(key) : isHex(value) ? fullHex(value) : toHex(value);
        line.classList.toggle('is-own', !!value);
        changed();
      });
      text.addEventListener('blur', show);
      shows.push(show);
      colorGrid.append(line);
    }

    // ----- type -----
    const fontCard = group(body, tr('Type'));
    // A choice is a font list, or { value, note } for one that comes with IGYO (the note shows beside it).
    const fontList = h('datalist', { id: 'st-font-choices' }, (kits.fontChoices || []).map((choice) =>
      (typeof choice === 'string' ? h('option', { value: choice }) : h('option', { value: choice.value, label: tr(choice.note || '') }))));
    fontCard.append(fontList);
    for (const def of kits.fonts || []) {
      const key = def.key;
      const text = h('input', { type: 'text', class: 't-input st-font', list: 'st-font-choices', spellcheck: false, maxLength: 300, 'aria-label': tr('{0} font', tr(def.label)) });
      const show = () => {
        text.placeholder = token(`--f-${kebab(key)}`);
        if (document.activeElement !== text) text.value = theme.fonts[key] || '';
      };
      text.addEventListener('input', () => {
        const value = text.value.trim();
        const fine = !UNSAFE.test(value);
        if (fine) text.removeAttribute('aria-invalid'); else text.setAttribute('aria-invalid', 'true');
        if (!fine) return;
        if (value) theme.fonts[key] = value; else delete theme.fonts[key];
        changed();
      });
      shows.push(show);
      row(fontCard, { title: tr(def.label), desc: key === 'ui' ? tr('One of the fonts that come with IGYO, or one installed on this PC, with fallbacks after commas. Empty keeps the kit\'s own.') : null, below: text });
    }

    // ----- shape -----
    const shapeCard = group(body, tr('Shape'));
    const range = (spec) => {
      const input = h('input', { type: 'range', class: 't-range st-range', min: spec.min, max: spec.max, step: spec.step, 'aria-label': spec.title });
      const value = h('output', { class: 't-mono st-range-value' });
      const show = () => {
        const own = Number(theme.shape[spec.key]);
        const now = theme.shape[spec.key] != null && Number.isFinite(own) ? own : parseFloat(token(spec.token)) || 0;
        input.value = String(now);
        value.textContent = String(now) + spec.unit;
      };
      input.addEventListener('input', () => { theme.shape[spec.key] = Number(input.value); value.textContent = input.value + spec.unit; changed(); });
      shows.push(show);
      row(shapeCard, { title: spec.title, desc: spec.desc, end: [input, value, resetButton(spec.title, () => { delete theme.shape[spec.key]; show(); changed(); })] });
    };
    range({ key: 'radius', title: tr('Corner roundness'), desc: tr('From square to very round. Some kits keep their own corners.'), min: 0, max: 20, step: 1, token: '--radius', unit: '' });
    range({ key: 'border', title: tr('Border width'), min: 0, max: 3, step: 0.5, token: '--bw', unit: 'px' });

    // ----- the kit's options, and effects -----
    const pick = (card, def, bag, unset) => {
      const values = (def.values || []).map((v) => (typeof v === 'string' ? { value: v, label: title(v) } : v));
      if (bag[def.key] && !values.some((v) => v.value === bag[def.key])) values.push({ value: bag[def.key], label: bag[def.key] });
      const select = h('select', { class: 't-select st-select', 'aria-label': tr(def.label || def.key) }, h('option', { value: '', text: unset }), values.map((v) => h('option', { value: v.value, text: tr(v.label || v.value) })));
      select.value = bag[def.key] || '';
      select.addEventListener('change', () => { if (select.value) bag[def.key] = select.value; else delete bag[def.key]; changed(); });
      row(card, { title: tr(def.label || def.key), desc: def.hint ? tr(def.hint) : null, end: [select] });
    };
    const optionsCard = group(body, tr('Kit options'));
    function drawOptions() {
      const options = kitOf(theme.kit).options || [];
      optionsCard.replaceChildren();
      if (!options.length) optionsCard.append(h('p', { class: 'st-note st-pad', text: tr('This kit has no options of its own.') }));
      for (const def of options) pick(optionsCard, def, theme.options, tr("Kit's own"));
    }
    drawOptions();
    const fxCard = group(body, tr('Effects'));
    for (const def of kits.fx || []) pick(fxCard, def, theme.fx, tr('Default'));

    // ----- New Tab background -----
    const thumb = h('div', { class: 'st-wall' });
    const removeWall = button(tr('Remove'), () => { theme.wallpaper = ''; showWall(); changed(); });
    const chooseWall = button(tr('Choose picture…'), async (node) => {
      const result = await busy(node, tr('Choosing…'), () => api.post('theme/picture'));
      if (!settled(result) || !result.data) return;
      theme.wallpaper = result.data;
      showWall();
      changed();
    });
    function showWall() {
      thumb.replaceChildren(theme.wallpaper ? h('img', { src: theme.wallpaper, alt: '' }) : h('span', { html: icon('image') }));
      removeWall.hidden = !theme.wallpaper;
    }
    showWall();
    row(group(body, tr('New Tab background')), {
      title: tr('Picture'), desc: tr("A picture for the New Tab page's background. It is kept inside the theme, so it travels with the theme file."),
      lead: thumb, end: [chooseWall, removeWall],
    });

    // ----- extra CSS -----
    const cssBox = h('textarea', { class: 't-textarea st-css', spellcheck: false, value: theme.css, placeholder: '/* for example */\n.tc-tab.is-active { font-weight: 600; }', 'aria-label': tr('Extra CSS') });
    cssBox.addEventListener('input', () => { theme.css = cssBox.value; changed(); });
    row(group(body, tr('Extra CSS')), {
      title: tr('Your own rules'), below: cssBox,
      desc: tr("Added after the kit's own styles, for the browser's interface, the New Tab page and Settings. It can't load anything from the internet: @import and pictures from web addresses are removed."),
    });

    // ----- saving and leaving -----
    async function save() {
      theme.name = nameBox.value.trim() || tr('My theme');
      const result = await busy(saveButton, tr('Saving…'), () => api.post('theme/save', { theme, apply: true }));
      if (!settled(result)) return;
      ended = true;   // saving ends the preview: the theme is now the one in use
      notice(tr('Saved "{0}".', theme.name));
      location.hash = '#appearance';
    }
    async function cancel() {
      await endPreview();
      location.hash = '#appearance';
    }
    // A file is written from a saved theme, so exporting saves first (under the theme's own id from then on).
    async function exportIt() {
      theme.name = nameBox.value.trim() || tr('My theme');
      const saved = await busy(exportButton, tr('Saving…'), () => api.post('theme/save', { theme, apply: false }));
      if (!settled(saved)) return;
      theme.id = saved.id;
      ST.setRoute('#themes/edit/' + saved.id);
      if (!ended) send();   // saving ended the preview in the app; show the theme again
      const result = await api.post('theme/export', { id: saved.id });
      if (settled(result)) notice(tr('Saved, and exported to {0}', result.file));
    }

    const onTheme = () => showAll();   // the preview arrived: values left to the kit may read differently now
    window.addEventListener('igyo-theme', onTheme);
    window.addEventListener('pagehide', onHide);
    onLeave(() => {
      window.removeEventListener('igyo-theme', onTheme);
      window.removeEventListener('pagehide', onHide);
      endPreview();
    });
    showAll();
    send();
  }

  ST.section('appearance', {
    render(page, sub) {
      if (sub[0] === 'edit' && sub[1]) renderEditor(page, sub.slice(1).join('/')).catch((error) => { console.error(error); notice(tr("The editor couldn't open."), 'error'); });
      else renderAppearance(page);
    },
  });
})();
