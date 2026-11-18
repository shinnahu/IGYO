// IGYO — Settings: the plain sections (toolbar, search, folders, ad block, memory, profiles, privacy, about),
// then the page starts. Appearance and its theme editor are in themes.js, the bookmark manager in bookmarks.js.
(() => {
  'use strict';

  const ST = window.ST;
  const tr = window.IGYO.tr;
  const { h, icon, api, onSync, onLeave, notice, settled, dialog, confirm, menu, header, group, row, button, toggle, choice, busy } = ST;

  // Most sections are one readable column.
  const column = (page) => { const node = h('div', { class: 'st-narrow' }); page.append(node); return node; };

  // ----- toolbar -----

  ST.section('toolbar', {
    render(page) {
      const col = column(page);
      header(col, tr('Toolbar and tools'), tr('Choose which buttons sit next to the address bar. Everything stays available in the menu.'));
      const card = group(col);
      toggle(card, { title: tr('Tools'), desc: tr('One button that opens the tools: ad block, dark mode for websites, video enhancement, the color picker, page translation, the volume, picture in picture and the screenshot.'), key: 'ShowToolsButton' });
      toggle(card, { title: tr('Downloads'), key: 'ShowDownloadsButton' });

      // A pinned tool has a button of its own, to the left of the tools button. (The same pins are in the tools menu.)
      const pinned = group(col, tr('Tools pinned to the toolbar'));
      toggle(pinned, { title: tr('Ad block'), desc: tr('Shows how many ads were blocked on the page, and opens the ad block menu.'), key: 'Pin.adblock' });
      toggle(pinned, { title: tr('Dark mode for websites'), desc: tr('Switches it on and off.'), key: 'Pin.dark' });
      toggle(pinned, { title: tr('Enhance videos'), desc: tr('Switches it on and off, also while a video plays.'), key: 'Pin.enhance' });
      toggle(pinned, { title: tr('Pick a color from the page'), key: 'Pin.picker' });
      toggle(pinned, { title: tr('Translate this page'), desc: tr('On a page that is shown translated, the same button shows the original again.'), key: 'Pin.translate' });
      toggle(pinned, { title: tr('Volume'), desc: tr("Opens this tab's volume."), key: 'Pin.volume' });
      toggle(pinned, { title: tr('Picture in picture'), desc: tr("Pops the page's video out into a small floating window."), key: 'Pin.pip' });
      toggle(pinned, { title: tr('Full-page screenshot'), desc: tr('Saves the whole page, top to bottom, as one picture.'), key: 'Pin.shot' });
      toggle(pinned, { title: tr('Extensions'), desc: tr('Opens the list of extensions.'), key: 'Pin.extensions' });
      row(pinned, { title: tr('Their order'), desc: tr('Drag a pinned tool along the toolbar to put it somewhere else. A right click on it unpins it.') });

      const tools = group(col, tr('Tools'));
      toggle(tools, {
        title: tr('Dark mode for websites'), key: 'ForceDark',
        desc: tr('Websites with a light look are shown dark. Sites that have a dark look of their own use it, and pages that are already dark stay as they are.'),
      });
      toggle(tools, {
        title: tr('Enhance videos'), key: 'EnhanceVideos',
        desc: tr('Videos are sharpened as they play: edges and fine detail get more contrast, flat areas are left alone. Switches at once, also for a video that is playing. The engine draws these videos itself, on any graphics card.'),
      });
      const level = choice(tools, {
        title: tr('How much videos are sharpened'),
        options: [{ label: tr('A little'), value: 'soft' }, { label: tr('Medium'), value: 'medium' }, { label: tr('Strongly'), value: 'strong' }],
        get: (state) => state.settings.EnhanceLevel,
        set: (value) => api.set('EnhanceLevel', value),
      });
      onSync((state) => { level.select.disabled = !state.settings.EnhanceVideos; level.row.node.classList.toggle('is-off', !state.settings.EnhanceVideos); });

      // Translating a selection (the page's right-click menu). Language names come from the engine, in the
      // language IGYO is shown in.
      const translate = group(col, tr('Translate a selection'));
      let names = null;
      const languageName = (id) => {
        try {
          names = names || new Intl.DisplayNames([window.IGYO.lang || 'en'], { type: 'language' });
          const name = names.of(id);
          return name ? name.charAt(0).toLocaleUpperCase(window.IGYO.lang || 'en') + name.slice(1) : id;
        } catch { return id; }
      };
      const languages = (state, first) => [first, ...(state.translateLanguages || []).map((id) => ({ label: languageName(id), value: id }))
        .sort((a, b) => a.label.localeCompare(b.label, window.IGYO.lang || 'en'))];
      row(translate, {
        title: tr('Right-click selected text and choose "Translate selection"'),
        desc: (state) => tr('The translation appears beside the selection. Both languages can be changed there, and the ones you used before are offered first. The selected text is sent to {0} (mymemory.translated.net), which translates it; nothing else is sent with it. {0} allows 5,000 characters a day without an account.', state.translateService || ''),
      });
      row(translate, {
        title: tr('"Translate this page" in the tools'),
        desc: tr("Opens the page through Google Translate's website: the page's address is sent to Google, which fetches the page itself and shows it translated. It works for pages anyone can open, not for pages on this PC or behind a sign-in. The same tool then shows the original page again."),
      });
      choice(translate, {
        title: tr('Translate into'),
        options: (state) => languages(state, { label: tr('The language IGYO is shown in'), value: '' }),
        get: (state) => state.settings.TranslateTo,
        set: (value) => api.set('TranslateTo', value),
      });
      choice(translate, {
        title: tr('Text that is already in that language'), desc: tr('Text written in the language above is translated into this one instead.'),
        options: (state) => languages(state, { label: tr('Is not translated'), value: '' }),
        get: (state) => state.settings.TranslateSecond,
        set: (value) => api.set('TranslateSecond', value),
      });

      // Extensions have a section of their own; each of them is pinned from there, or from the tools menu.
      const extensions = group(col, tr('Extensions'));
      row(extensions, {
        title: tr('Extensions on the toolbar'),
        desc: tr('Every extension can have a button of its own, with its own picture: a click opens its popup under the button, a right click its menu. They are pinned and unpinned like the tools.'),
        end: [button(tr('Manage extensions'), () => { location.hash = '#extensions'; })],
      });
    },
  });

  // ----- extensions -----
  // What stands in the extensions folder, one card each: what it is, what it asks for, and its switches.

  const sizeText = (bytes) => (bytes >= 1048576 ? tr('{0} MB', (bytes / 1048576).toFixed(1)) : tr('{0} KB', Math.max(1, Math.round(bytes / 1024))));

  function extensionCard(list, x) {
    const act = async (what, on) => settled(await api.post('extension', { key: x.key, what, on: !!on }));
    const picture = x.icon ? h('img', { class: 'st-ext-icon', src: x.icon, alt: '' }) : h('span', { class: 'st-ext-icon', html: icon('plug') });
    list.append(h('h2', { class: 'st-group-label st-ext-name' }, picture, h('span', { text: x.name }), x.version ? h('span', { class: 'st-ext-version', text: x.version }) : null));
    const card = h('section', { class: 't-card st-card st-ext', 'data-extension': x.key });
    list.append(card);
    if (x.error) row(card, { title: tr('Not used'), desc: x.error });
    else {
      const on = toggle(card, { title: tr('On'), desc: [x.description, x.loaded ? '' : tr('It is loaded the next time IGYO starts.')].filter(Boolean).join(' '), value: x.on });
      on.input.disabled = !x.loaded;
      on.input.addEventListener('change', () => act('on', on.input.checked));
      const pin = toggle(card, { title: tr('Pin to the toolbar'), value: x.pinned });
      pin.input.addEventListener('change', () => act('pin', pin.input.checked));
    }
    row(card, { title: tr('Site access'), desc: x.sites });
    row(card, { title: tr('Permissions'), desc: (x.permissions || []).length ? x.permissions.join(', ') : tr('None besides its websites.') });
    if (x.wantsRecording && !x.error) {
      const record = toggle(card, {
        title: tr('May record tabs'), value: x.mayRecord,
        desc: tr("This extension asks to record a tab's picture and sound. In a browser a click on its button allows that for the tab on show; the engine has no such click in an app like IGYO, and refuses. Switched on here, this one extension may record any tab at any time, and the engine also lets it past its other lists of extensions that may use a feature. Only one extension can have this.") +
          ' ' + (x.mayRecord ? (x.recordsNow ? tr('In force now.') : tr('In force once IGYO is closed and started again.')) : tr('It takes effect once IGYO is closed and started again.')),
      });
      record.input.addEventListener('change', () => act('record', record.input.checked));
    }
    row(card, {
      title: sizeText(x.size || 0), desc: x.store ? tr('From {0}', x.store) : tr('From the extensions folder'),
      end: [
        x.options ? button(tr('Options'), () => act('options'), { small: true }) : null,
        button(tr('Show in folder'), () => act('folder'), { small: true }),
        x.store && x.storeId ? button(tr('View in the store'), () => act('store'), { small: true }) : null,
        button(tr('Remove'), () => act('remove'), { small: true, danger: true }),
      ].filter(Boolean),
    });
  }

  ST.section('extensions', {
    render(page) {
      const col = column(page);
      header(col, tr('Extensions'), tr('Extensions add things to the pages you open. IGYO has no store of its own and adds none by itself. An extension can read and change the pages you open, so only add what you trust.'));
      const how = group(col);
      row(how, {
        title: tr('The extensions folder'),
        desc: (state) => tr("IGYO uses the extensions that are in this folder when it starts: an unpacked extension's own folder, or a .zip or .crx file, which is unpacked there. On an extension's page in the Chrome Web Store or Microsoft Edge Add-ons, \"Add the extension on this page\" in the tools menu downloads it into this folder.") +
          ' ' + (state.extensionsOn ? tr('Extensions are in use now.') : tr('The folder was empty when IGYO started, so nothing of this is running.')),
        end: [
          button(tr('Open folder'), async () => settled(await api.post('open', { what: 'extensions-folder' }))),
          button(tr('Look for new ones'), (node) => busy(node, tr('Looking…'), async () => settled(await api.post('extensions/look')))),
        ],
      });
      const list = h('div', { class: 'st-extensions' });
      col.append(list);
      let drawn = null;
      onSync((state) => {
        const items = state.extensions || [];
        const signature = JSON.stringify(items);
        if (signature === drawn) return;
        drawn = signature;
        list.replaceChildren();
        if (!items.length) list.append(h('p', { class: 't-muted st-ext-none', text: tr('There is no extension in the folder.') }));
        for (const x of items) extensionCard(list, x);
      });
    },
  });

  // ----- search and new tabs -----

  ST.section('search', {
    render(page) {
      const col = column(page);
      header(col, tr('Search and new tabs'));
      const search = group(col, tr('Search'));
      choice(search, {
        title: tr('Search engine'), desc: tr('Used by the address bar and the New Tab page.'),
        options: (state) => state.searchEngines.map((name) => ({ label: name === 'Custom' ? tr('Custom') : name, value: name })),
        get: (state) => state.settings.SearchEngine,
        set: (value) => api.set('SearchEngine', value),
      });
      const custom = h('input', { type: 'text', class: 't-input', spellcheck: false, maxLength: 2048, placeholder: 'https://example.com/search?q=%s', 'aria-label': tr('Custom search address') });
      const customRow = row(search, { title: tr('Custom search address'), desc: tr('Put %s where the words go, for example https://example.com/search?q=%s'), below: custom });
      // Kept when you leave the box or press Enter (that is when a text box says it has changed).
      custom.addEventListener('change', async () => {
        if (custom.value.trim() !== ST.state.settings.CustomSearchUrl) settled(await api.set('CustomSearchUrl', custom.value.trim()));
      });
      onSync((state) => {
        const on = state.settings.SearchEngine === 'Custom';
        custom.disabled = !on;
        customRow.node.classList.toggle('is-off', !on);
        if (document.activeElement !== custom) custom.value = state.settings.CustomSearchUrl || '';
      });
      toggle(search, {
        title: tr('Google suggestions while typing'), key: 'SearchSuggestions',
        desc: tr("Shows Google's predictions (searches and popular sites) under the address bar. Your bookmarks and history don't show up there. ") +
          tr('What you type is sent to Google as you type, without your cookies. Private windows never send it.'),
      });
      const tabs = group(col, tr('New tabs and startup'));
      toggle(tabs, { title: tr('New Tab page'), desc: tr('A search box, your shortcuts and your own pictures. When off, new tabs open blank.'), key: 'NewTabPage' });
      toggle(tabs, { title: tr('Reopen the tabs from last time'), desc: tr('Reopened tabs load only when you click them.'), key: 'RestoreTabs' });
      toggle(tabs, {
        title: tr('Go to a tab opened with the middle mouse button'), key: 'SwitchToNewTabs',
        desc: tr('When off, a link or a bookmark opened with the middle mouse button or with Ctrl held, and an image opened in a new tab, open behind the tab you are on.'),
      });
    },
  });

  // ----- default browser -----
  // Windows keeps the choice of default browser, and lets no program make itself the default: this shows where
  // things stand and opens IGYO's page in Windows' settings, where the person makes the choice.

  ST.section('default', {
    render(page) {
      const col = column(page);
      header(col, tr('Default browser'), tr('Links you open in other programs (an email, a chat) open in the default browser. Windows keeps that choice, in its own settings.'));
      const card = group(col);
      const info = (state) => state.defaultBrowser || {};
      const open = button(tr('Open Windows settings'), async (node) => settled(await busy(node, tr('Opening…'), () => api.post('default-browser'))));
      const line = row(card, {
        desc: (state) => {
          const d = info(state);
          if (!d.installed) return tr("This copy of IGYO wasn't installed with its setup, so Windows can't offer it as a browser.");
          if (d.isDefault) return tr('Links from other programs open here.');
          return (d.current ? tr('Links from other programs open in {0}.', d.current) + ' ' : '') +
            tr("The button opens IGYO's page in Windows settings, where you can make it the default.");
        },
        end: [open],
      });
      onSync((state) => {
        const d = info(state);
        line.title.textContent = d.isDefault ? tr('IGYO is your default browser') : tr('IGYO is not your default browser');
        open.hidden = !d.installed;
        open.classList.toggle('is-primary', !!d.installed && !d.isDefault);
      });
      // (Back from Windows' settings, the app has this page read its facts again: see MainForm.OnActivated.)
    },
  });

  // ----- downloads and screenshots -----

  ST.section('downloads', {
    render(page) {
      const col = column(page);
      header(col, tr('Downloads and screenshots'));
      const card = group(col);
      const folder = (title, which, own, fallback) => {
        const reset = button(tr('Use default'), async () => settled(await api.post('folder', { which, reset: true })));
        row(card, {
          title, desc: (state) => own(state) || fallback(state),
          end: [button(tr('Change…'), async (node) => settled(await busy(node, tr('Choosing…'), () => api.post('folder', { which })))), reset],
        });
        onSync((state) => { reset.hidden = !own(state); });
      };
      folder(tr('Downloads'), 'downloads', (state) => state.settings.DownloadFolder, () => tr("Windows' Downloads folder"));
      folder(tr('Full-page screenshots'), 'screenshots', (state) => state.settings.ScreenshotFolder, (state) => state.settings.EffectiveScreenshotFolder);
    },
  });

  // ----- ad block -----

  ST.section('adblock', {
    render(page) {
      const col = column(page);
      header(col, tr('Ad block'), tr('Checks every request a page makes against a list of ad servers. The page you open is never blocked.'));
      const card = group(col);
      toggle(card, {
        title: tr('Block ads'), key: 'AdBlock',
        desc: (state) => (state.app.adList ? tr('EasyList {0}: {1} ad servers.', state.app.adList, Number(state.app.adRules).toLocaleString()) : tr('The block list is loading…')),
      });
      const update = button(tr('Update now'), async (node) => {
        const result = await busy(node, tr('Updating…'), () => api.post('adblock/update'));
        if (result.error) notice(tr("Couldn't update the list: ") + result.error, 'error');
        else if (result.ok) notice(tr('Ad block list updated: EasyList {0}, {1} ad servers.', result.version, Number(result.rules).toLocaleString()));
      });
      row(card, { title: tr('Block list'), desc: tr('EasyList, from easylist.to (GPLv3 / CC BY-SA 3.0).'), end: [update] });
    },
  });

  // ----- memory saver -----

  ST.section('memory', {
    render(page) {
      const col = column(page);
      header(col, tr('Memory saver'), tr('Sleeping tabs keep their page but stop running. Unloaded tabs give back all their memory and reload when you open them. Tabs playing sound never sleep.'));
      const card = group(col);
      const minutes = (title, key, options) => choice(card, {
        title, options: (state) => state[options] || [],
        get: (state) => state.settings[key],
        set: (value) => api.set(key, value),
      });
      minutes(tr('Put background tabs to sleep after'), 'SleepAfterMinutes', 'sleepOptions');
      minutes(tr('Unload background tabs after'), 'UnloadAfterMinutes', 'unloadOptions');
      toggle(card, {
        title: tr('Sleep when minimized'), key: 'SleepWhenMinimized',
        desc: (state) => tr('The visible tab sleeps {0} seconds after you minimize the window.', Math.round(state.settings.MinimizedSleepSeconds)),
      });
      toggle(card, {
        title: tr('Give memory back to Windows'), key: 'ReleaseMemory',
        desc: tr('A tab left in the background for 30 seconds, and all of IGYO once you have been in another program for a minute, let go of the memory they are not using, so that Windows can pass it on when another program needs it. It stays reserved for IGYO, nothing is paused, and whatever is needed again comes back by itself.'),
      });

      // Memory in use, read every few seconds while this section is on screen.
      const use = group(col, tr('Memory in use'));
      const total = h('div', { class: 'st-mem-total' });
      const meter = h('i');
      const parts = h('div', { class: 'st-mem-parts' });
      const tabs = h('div', { class: 'st-mem-tabs' });
      use.append(h('div', { class: 'st-mem' }, total, h('div', { class: 't-meter', title: tr('Web pages and engine, of all memory in use') }, meter), parts), tabs);
      const mb = (n) => `${Number(n).toLocaleString()} MB`;
      async function read() {
        if (document.hidden) return;
        let m;
        try { m = await api.get('/api/memory'); } catch { return; }
        if (!total.isConnected) return;
        total.replaceChildren(h('strong', { text: mb(m.total) }), h('span', { text: ' ' + tr('in use by IGYO right now') }));
        meter.style.setProperty('--v', m.total ? String(m.engine / m.total) : '0');
        parts.replaceChildren(
          h('span', { class: 'st-mem-part is-engine', text: tr('Web pages and engine: {0} in {1} processes', mb(m.engine), m.processes) }),
          h('span', { class: 'st-mem-part', text: tr('Windows, tabs and toolbar: {0}', mb(m.own)) }));
        tabs.replaceChildren(...(m.tabs || []).map((tab) => h('div', { class: 'st-mem-tab' },
          h('span', { class: 't-chip st-state is-' + String(tab.state).toLowerCase(), text: ({ Active: tr('Active'), Sleeping: tr('Sleeping'), Unloaded: tr('Unloaded'), Open: tr('Open') })[tab.state] || tab.state }),
          h('span', { class: 'st-mem-title', text: tab.title }))));
      }
      read();
      const timer = setInterval(read, 4000);
      document.addEventListener('visibilitychange', read);
      onLeave(() => { clearInterval(timer); document.removeEventListener('visibilitychange', read); });
    },
  });

  // ----- profiles -----

  ST.section('profiles', {
    render(page) {
      const col = column(page);
      header(col, tr('Profiles'), tr('Each profile has its own sign-ins, cookies, history, passwords, bookmarks, tabs and New Tab page, and opens in its own window.'));
      const card = group(col);
      const list = h('div', { class: 'st-profiles' });
      card.append(list);

      let signature = '';
      // A new picture keeps the profile's other facts the same, so the list is told to draw itself again.
      const redraw = (result) => { if (settled(result)) { signature = ''; ST.refresh(); } };
      const choosePicture = async (p) => redraw(await api.post('profile/picture', { id: p.id }));
      const rename = (p) => dialog({
        title: tr('Rename profile'), ok: tr('Rename'),
        fields: [{ key: 'name', label: tr('New name'), value: p.name, max: 40, required: true }],
        submit: async (values) => (await api.post('profile/rename', { id: p.id, name: values.name })).error,
      });
      async function remove(p) {
        const sure = await confirm(tr('Delete the profile "{0}"?', p.name),
          tr("Its sign-ins, history, passwords, bookmarks and tabs are removed the next time IGYO starts. This can't be undone."), tr('Delete profile'), true);
        if (sure) settled(await api.post('profile/delete', { id: p.id }), tr('"{0}" is deleted.', p.name));
      }
      const more = (anchor, p) => menu(anchor, [
        { label: tr('Rename…'), icon: 'edit', run: () => rename(p) },
        { label: tr('Choose a picture…'), icon: 'image', run: () => choosePicture(p) },
        { label: tr('Remove the picture'), icon: 'close', disabled: !p.hasPicture, run: async () => redraw(await api.post('profile/remove-picture', { id: p.id })) },
        { label: tr('Circle color…'), icon: 'palette', disabled: p.hasPicture, run: () => chooseColor(p) },
        { sep: true },
        p.isStartDefault
          ? { label: tr('No longer the default profile'), icon: 'star', run: async () => settled(await api.post('profile/default', { id: p.id, on: false })) }
          : { label: tr('Make it the default profile'), icon: 'star', run: async () => settled(await api.post('profile/default', { id: p.id, on: true }), tr('"{0}" is the default profile.', p.name)) },
        { sep: true },
        { label: tr('Delete profile…'), icon: 'trash', danger: true, disabled: p.isDefault, run: () => remove(p) },
      ]);

      // A profile without a picture is shown as a circle with its first letter: its color is chosen in the profile's
      // menu (the three dots), from a few or as a color of one's own. A pick is used at once.
      const chooseColor = (p) => dialog({
        title: tr('Circle color'), message: p.name, noOk: true,
        body: (close) => {
          const colors = (ST.state && ST.state.profileColors) || [];
          const chosen = (p.color || '').toUpperCase();
          const pick = async (hex) => { close({}); redraw(await api.post('profile/color', { id: p.id, color: hex })); };
          const own = h('input', { type: 'color', class: 'st-accent-input', 'aria-label': tr('Your own color') });
          if (/^#[0-9A-F]{6}$/.test(chosen)) own.value = chosen.toLowerCase();
          own.addEventListener('change', () => pick(own.value.toUpperCase()));
          const mine = !!chosen && !colors.includes(chosen);
          return h('div', { class: 'st-accents st-profile-colors', role: 'radiogroup', 'aria-label': tr('Circle color') },
            ...colors.map((hex) => h('button', {
              type: 'button', class: 'st-accent' + (hex === chosen ? ' is-chosen' : ''), role: 'radio', 'aria-checked': String(hex === chosen),
              title: hex, 'aria-label': hex, vars: { '--dot': hex }, onclick: () => pick(hex),
            })),
            h('label', { class: 'st-accent is-custom' + (mine ? ' is-chosen' : ''), title: tr('Your own color'), vars: mine ? { '--dot': chosen } : null }, own, mine ? null : h('span', { html: icon('plus') })));
        },
      });

      onSync((state) => {
        const next = JSON.stringify(state.profiles.map((p) => [p.id, p.name, p.here, p.open, p.hasPicture, p.color, p.isStartDefault, p.avatar.split('?')[0]]));
        if (next === signature) return;   // the pictures' addresses change every time; the profiles rarely do
        signature = next;
        const keep = document.createElement('div');
        for (const p of state.profiles) {
          row(keep, {
            title: p.name, strong: true,
            desc: (p.here ? tr('This window') : p.open ? tr('Open in another window') : tr('Not open')) + (p.isStartDefault ? ' · ' + tr('Default profile') : ''),
            lead: h('img', { class: 'st-avatar', src: p.avatar, alt: '' }),
            end: [
              p.here ? button(tr('Choose picture…'), () => choosePicture(p)) : button(tr('Open'), async () => settled(await api.post('profile/open', { id: p.id }))),
              h('button', { type: 'button', class: 't-iconbtn', title: tr('Rename, picture, delete'), 'aria-label': tr('More options for {0}', p.name), 'aria-haspopup': 'menu', html: icon('more'), onclick: (event) => more(event.currentTarget, p) }),
            ],
          });
        }
        list.replaceChildren(...keep.childNodes);
      });

      // A new profile's circle as it will look: the first letter of its name on its color. Pressed, it shows the colors
      // a circle can have under the name, and a pick closes them. (The window's own "Add profile…" has the same:
      // Ui\overlays.js.)
      function circlePick(colors, color, nameInput) {
        let chosen = String(color || colors[0] || '#2563EB').toUpperCase();
        const letter = h('span', { class: 't-circle-letter' });
        const circle = h('button', { type: 'button', class: 't-circle', title: tr('Circle color'), 'aria-label': tr('Circle color'), 'aria-expanded': 'false' },
          letter, h('span', { class: 't-circle-badge', html: icon('palette') }));
        const swatches = colors.map((hex) => h('button', {
          type: 'button', class: 't-circle-color', role: 'radio', title: hex, 'aria-label': hex, vars: { '--dot': hex },
          onclick: () => { chosen = hex.toUpperCase(); show(false); draw(); nameInput.focus(); },
        }));
        const list = h('div', { class: 't-circle-colors', role: 'radiogroup', 'aria-label': tr('Circle color'), hidden: true }, swatches);
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

      row(card, {
        title: tr('Add a profile'), desc: tr('For work, a project, or someone else using this PC.'),
        end: [button(tr('Add profile'), () => {
          let pick = null;
          return dialog({
            title: tr('Add profile'), ok: tr('Add'),
            fields: [{
              key: 'name', label: tr('Name for the new profile'), max: 40, required: true,
              lead: (input) => (pick = circlePick((ST.state && ST.state.profileColors) || [], ST.state && ST.state.profileNextColor, input)).circle,
              after: () => pick.colors,
            }],
            submit: async (values) => (await api.post('profile/add', { name: values.name, color: pick ? pick.value() : '' })).error,
          });
        }, { primary: true, icon: 'plus' })],
      });

      // At start, with more than one profile: a window asks which one to open. Off, the default profile opens
      // straight away; without a default profile, the question stays.
      const start = group(col, tr('At start'));
      toggle(start, {
        title: tr('Ask which profile to open'),
        desc: tr('When there is more than one profile. Off: the default profile opens without asking (make one the default in its ⋯ menu above).'),
        key: 'AskProfileAtStart',
      });
      row(start, {
        title: tr('Default profile'),
        desc: (state) => { const d = state.profiles.find((p) => p.isStartDefault); return d ? d.name : tr('None yet: while there is none, IGYO asks at every start.'); },
      });

      // Follows whether this window's profile is signed in on Google's sites (the app reads that from the
      // profile's own cookies; nothing is asked of Google).
      const google = group(col, tr('Google account'));
      const googleButton = button(tr('Sign in'), async () => settled(await api.post('open', { what: 'google' })));
      const googleRow = row(google, {
        title: tr("Sign in on Google's sites"),
        desc: tr("Gmail, YouTube, Drive and other Google sites keep you signed in, separately in each profile. Chrome's own browser sign-in and sync work only in Google Chrome."),
        end: [googleButton],
      });
      onSync((state) => {
        const signedIn = !!(state.window && state.window.googleSignedIn);
        googleRow.title.textContent = signedIn ? tr("Signed in on Google's sites") : tr("Sign in on Google's sites");
        googleButton.querySelector('span').textContent = signedIn ? tr('Open Google account') : tr('Sign in');
      });
    },
  });

  // ----- privacy -----

  ST.section('privacy', {
    render(page, sub) {
      const col = column(page);
      header(col, tr('Privacy'));
      const card = group(col);
      toggle(card, { title: tr('Offer to save passwords'), desc: tr("Passwords are kept by the browser engine in this profile's folder on this PC. Private windows never save them."), key: 'SavePasswords' });
      toggle(card, {
        title: tr('Tell websites this is Microsoft Edge'), key: 'IdentifyAsEdge',
        desc: tr('Needed for Google sign-in: Google refuses sign-in from the engine\'s own name, "Microsoft Edge WebView2". Applies to tabs opened after the change.'),
      });

      const clear = group(col, tr('Clear browsing data for "{0}"', ST.state.window.profile));
      const kinds = [
        ['history', tr('Browsing history'), null, true],
        ['cookies', tr('Cookies and site data'), tr('Signs you out of most sites. Your New Tab shortcuts and pictures stay.'), false],
        ['cache', tr('Cached images and files'), null, true],
        ['downloads', tr('Download history'), null, true],
        ['passwords', tr('Saved passwords'), null, false],
        ['autofill', tr('Autofill form data'), null, false],
      ].map(([kind, title, desc, value]) => ({ kind, input: toggle(clear, { title, desc, value }).input }));
      const go = button(tr('Clear now'), async () => {
        const chosen = kinds.filter((k) => k.input.checked).map((k) => k.kind);
        if (!chosen.length) { notice(tr('Nothing is selected.'), 'error'); return; }
        const label = go.querySelector('span');
        go.disabled = true;
        label.textContent = tr('Clearing…');
        const result = await api.post('clear', { kinds: chosen });
        if (!go.isConnected) return;
        go.disabled = false;
        label.textContent = result.error ? tr('Clear now') : tr('Cleared');
        if (result.error) notice(result.error, 'error');
      }, { primary: true });
      for (const k of kinds) k.input.addEventListener('change', () => { go.querySelector('span').textContent = tr('Clear now'); });
      row(clear, { title: tr('Clear the selected data'), desc: tr("This can't be undone."), end: [go] });

      // ----- Claude: lets a Claude app on this PC read pages in this profile, and, when allowed, act in it -----
      // Off unless switched on here, for this window's profile. The app decides everything; this page only
      // shows it and passes on what is chosen. Site and program names come from outside, so they go in as text.
      const bridge = (state) => state.bridge || {};
      const profileName = ST.state.window.profile;
      const claude = group(col, tr('Claude'));
      const claudeLabel = claude.previousElementSibling;

      const sw = toggle(claude, {
        title: tr('Let Claude use this profile ("{0}")', profileName),
        desc: tr("Off: no program can reach this profile through IGYO. On: a Claude app on this PC that you let in can list this profile's open tabs and, on the sites you allow, read the pages in them, and add bookmarks. Private windows are never included."),
      });
      sw.input.addEventListener('change', async () => {
        const on = sw.input.checked;
        const result = await api.post('bridge/enable', { on });
        if (result.error) { sw.input.checked = !on; notice(result.error, 'error'); }
      });
      // Acting: a second switch, only with the first one on. Each site is still asked about, apart from reading.
      const act = toggle(claude, {
        title: tr('Let Claude act in this profile'),
        desc: tr('Claude can then open tabs of its own here, and, on the sites you allow for acting, click, type, fill in forms and run scripts, through the browser engine: your mouse and keyboard are not touched, and the window is not brought forward. While it acts, a sign in the corner of the page says so, with a button that stops it; screen recordings leave the sign out. It never types into password, card or code boxes.'),
      });
      act.input.addEventListener('change', async () => {
        const on = act.input.checked;
        const result = await api.post('bridge/act', { on });
        if (result.error) { act.input.checked = !on; notice(result.error, 'error'); }
      });
      onSync((state) => {
        sw.input.checked = !!bridge(state).on;
        act.input.checked = !!bridge(state).act;
        act.input.disabled = !bridge(state).on;
        act.row.node.classList.toggle('is-off', !bridge(state).on);
      });

      row(claude, {
        title: tr('What Claude reads leaves this PC'),
        desc: tr('Text and pictures Claude reads from a page are sent to Anthropic as part of your conversation, like anything else you give Claude. A page can also hold text written to steer an assistant; IGYO marks what it hands over as website content, but keep Claude away from banking, health records and anything with credentials.'),
      });

      // A line to copy: shown in a box that can't be typed in, with a button that copies it.
      const lineBox = (label) => h('input', { type: 'text', class: 't-input t-mono', readOnly: true, spellcheck: false, 'aria-label': label });
      const copyButton = (box) => button(tr('Copy'), () => {
        box.focus();
        box.select();
        let copied = false;
        try { copied = document.execCommand('copy'); } catch { copied = false; }
        if (copied) notice(tr('Copied.')); else notice(tr('Select the line and press Ctrl+C.'), 'error');
      });
      const command = lineBox(tr('The command that connects Claude Code'));
      row(claude, {
        title: tr('Connect Claude Code'),
        desc: tr('Run this once in a terminal, then start a new Claude Code session. The first time it connects, IGYO names the program and asks you.'),
        end: [copyButton(command)], below: command,
      });
      const entry = lineBox(tr('The entry the command writes'));
      row(claude, {
        title: tr('What the command adds'),
        desc: tr('The command writes this entry under "mcpServers" in the file .claude.json in your user folder.'),
        end: [copyButton(entry)], below: entry,
      });
      onSync((state) => {
        const b = bridge(state);
        if (command.value !== (b.command || '')) command.value = b.command || '';
        if (entry.value !== (b.entry || '')) entry.value = b.entry || '';
      });

      const pause = button(tr('Pause'), async () => settled(await api.post('bridge/pause', { on: !bridge(ST.state).paused })));
      const status = row(claude, {
        title: tr('Nothing is connected'),
        desc: (state) => {
          const b = bridge(state);
          if (b.paused) return tr('Every connection was cut, and nothing can connect until you resume.');
          const list = b.connected || [];
          return list.length ? list.map((c) => tr('{0}, since {1}', c.name, c.since)).join('; ') : tr('A Claude app connects the first time it uses IGYO in a session.');
        },
        end: [pause],
      });
      onSync((state) => {
        const b = bridge(state);
        status.title.textContent = b.paused ? tr('Paused') : (b.connected || []).length ? tr('Connected now') : tr('Nothing is connected');
        pause.querySelector('span').textContent = b.paused ? tr('Resume') : tr('Pause');
      });

      // Sites answered with "Always" or "Never". Every other site is asked about each time.
      const sites = group(col, tr('Sites, for "{0}"', profileName));
      const siteList = h('div', { class: 'st-claude-list' });
      sites.append(siteList);
      let siteSignature = null;
      onSync((state) => {
        const list = bridge(state).sites || [];
        const signature = JSON.stringify(list);
        if (signature === siteSignature) return;
        siteSignature = signature;
        const keep = document.createElement('div');
        for (const s of list) {
          const select = h('select', { class: 't-select st-select', 'aria-label': tr('What Claude may do on {0}', s.name) },
            h('option', { value: 'read', text: tr('Always allow reading') }),
            h('option', { value: 'act', text: tr('Always allow reading and acting') }),
            h('option', { value: 'never', text: tr('Never') }),
            h('option', { value: 'ask', text: tr('Ask again (take off this list)') }));
          select.value = s.mode;
          select.addEventListener('change', async () => settled(await api.post('bridge/site', { site: s.site, mode: select.value })));
          row(keep, {
            title: s.name,
            desc: s.mode === 'act' ? tr('Claude reads and acts on pages on this site without asking.') : s.mode === 'read' ? tr('Claude reads pages on this site without asking, and asks before acting.') : tr('Claude is refused on this site.'),
            end: [select],
          });
        }
        if (!list.length) {
          row(keep, {
            title: tr('No sites yet'),
            desc: tr('IGYO asks you the first time Claude wants to read a site. "Allow this time" lasts while that tab stays on the site; sites you answer "Always" or "Never" for are listed here.'),
          });
        }
        siteList.replaceChildren(...keep.childNodes);
      });

      const programs = group(col, tr('Programs that may connect without asking'));
      const programList = h('div', { class: 'st-claude-list' });
      programs.append(programList);
      let programSignature = null;
      onSync((state) => {
        const list = bridge(state).programs || [];
        const signature = JSON.stringify(list);
        if (signature === programSignature) return;
        programSignature = signature;
        const keep = document.createElement('div');
        for (const p of list) {
          row(keep, {
            title: p.name, desc: tr('{0}  ·  added {1}', p.path, p.added),
            end: [button(tr('Remove'), async () => settled(await api.post('bridge/program-remove', { path: p.path })))],
          });
        }
        if (!list.length) row(keep, { title: tr('None'), desc: tr('A program that wants to connect is named and you are asked. One you answer "Always" for is listed here, for all profiles.') });
        programList.replaceChildren(...keep.childNodes);
      });

      // What was asked and done: when, which tool, which site. Never a page's content, title or full address.
      const activity = group(col, tr('Activity'));
      const clearActivity = button(tr('Clear'), async () => settled(await api.post('bridge/activity-clear')));
      row(activity, {
        title: tr('What Claude asked for'),
        desc: (state) => {
          const count = bridge(state).activityCount || 0;
          const shown = (bridge(state).activity || []).length;
          return count ? tr('Showing the last {0} of {1}. Up to 500 are kept, on this PC only: the time, the tool and the site, never what a page said.', shown, count) : tr('Nothing yet. The last 500 requests are kept on this PC: the time, the tool and the site, never what a page said.');
        },
        end: [clearActivity],
      });
      const log = h('div', { class: 'st-claude-log' });
      activity.append(log);
      let logSignature = null;
      onSync((state) => {
        const list = bridge(state).activity || [];
        const signature = JSON.stringify(list);
        if (signature === logSignature) return;
        logSignature = signature;
        clearActivity.disabled = !list.length;
        log.replaceChildren(...list.map((a) => h('div', { class: 'st-claude-entry' + (a.ok ? '' : ' is-refused') },
          h('span', { class: 'st-claude-time', text: String(a.time || '').slice(5, 16) }),
          h('span', { class: 'st-claude-what', text: [a.tool, a.site].filter(Boolean).join(' · ') }),
          h('span', { class: 'st-claude-note', text: (a.note || '') + (a.profile ? `  (${a.profile})` : '') }))));
      });

      // "Sites, programs and activity…" in the toolbar's Claude menu comes straight here.
      // Placed by its own position in the page, twice: the section slides in as it appears, and a window that
      // isn't being drawn gets no animation frames, so a timer does it.
      if (sub && sub[0] === 'claude') {
        const bring = () => {
          const main = claudeLabel.closest('.st-main');   // the part of the page that scrolls
          if (main) main.scrollTop += claudeLabel.getBoundingClientRect().top - main.getBoundingClientRect().top - 14;
        };
        setTimeout(bring, 40);
        setTimeout(bring, 420);
      }
    },
  });

  // ----- about -----

  ST.section('about', {
    render(page) {
      const col = column(page);
      header(col, tr('About'));
      const card = group(col);
      const logo = h('i', { class: 'st-mark st-logo', 'aria-hidden': 'true' });
      const app = (state) => state.app || {};
      row(card, { title: ST.state.app.name, desc: ST.state.app.tagline, strong: true, lead: logo });
      row(card, { title: tr('Version'), desc: (state) => app(state).version });
      row(card, {
        title: tr('Engine'),
        desc: (state) => (app(state).runtime
          ? tr('Microsoft Edge WebView2 {0}, installed with {1} in {2}.', app(state).engine || tr('(starting)'), app(state).name, app(state).runtime)
          : tr("Microsoft Edge WebView2 {0}, Windows' shared copy.", app(state).engine || tr('(starting)'))),
      });
      row(card, { title: tr('Ad block list'), desc: (state) => (app(state).adList ? tr('EasyList {0} from easylist.to (GPLv3 / CC BY-SA 3.0).', app(state).adList) : tr('Loading…')) });
      row(card, {
        title: tr('Fonts'),
        desc: tr('Some themes use Averia Libre, Barlow, Cormorant Garamond, Fredoka, Google Sans Flex, JetBrains Mono, M PLUS 1, Mochiy Pop One and Saira. Each is under the SIL Open Font License 1.1; the files, their license texts and where each came from are in one folder.'),
        end: [button(tr('Open folder'), async () => settled(await api.post('open', { what: 'fonts-folder' })))],
      });
      row(card, {
        title: tr('Your data'), desc: (state) => tr('Settings, profiles, bookmarks and New Tab pictures: ') + app(state).dataDir,
        end: [button(tr('Open folder'), async () => settled(await api.post('open', { what: 'data-folder' })))],
      });
    },
  });

  ST.start();
})();
