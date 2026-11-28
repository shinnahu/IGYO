// IGYO — wires the app's messages to the interface, then tells the app the interface is ready.
(() => {
  'use strict';

  const { Host, S, Layout, ui } = window.IGYO;
  const T = window.IGYO;

  Host.on('init', (m) => {
    S.isPrivate = !!m.isPrivate;
    S.profile = m.profile || {};
    S.settings = m.settings || {};
    S.win = m.window || {};
    S.bridge = m.bridge || {};
    ui.applyTheme(m.theme, !S.theme.id || S.theme.id === (m.theme || {}).id);   // its stylesheet is already loaded with the page
    ui.applySettings();
    ui.applyWindow();
    ui.applyBridge();
    Layout.queue();
  });
  // The Claude bridge: its mark on the toolbar. (Its questions, and that Claude is working in the window, stand in
  // the page's corner in a window of the app's own: ClaudeSign.cs.)
  Host.on('bridge', (m) => {
    S.bridge = m;
    ui.applyBridge();
  });
  // In full screen, the pointer at the very top of the screen: the ✕ that leaves full screen comes down (or goes).
  Host.on('fsExit', (m) => ui.showExitButton(!!m.show));
  Host.on('tabs', (m) => {
    S.tabs = m.list || [];
    S.active = m.active || 0;
    ui.renderTabs();
  });
  Host.on('nav', (m) => {
    S.nav = m;
    ui.renderNav();
  });
  Host.on('bookmarks', (m) => {
    S.bookmarks = m;
    ui.renderBookmarks();
  });
  Host.on('settings', (m) => {
    S.settings = m.settings || S.settings;
    S.profile = m.profile || S.profile;
    ui.applySettings();
    ui.renderNav();
  });
  Host.on('theme', (m) => ui.applyTheme(m.theme, false));
  Host.on('window', (m) => {
    S.win = m;
    ui.applyWindow();
  });
  Host.on('menu', (m) => ui.showHostMenu(m));
  Host.on('openMenu', (m) => ui.requestMenu(m.name || 'main', ui.buttons[m.name === 'profile' ? 'profile' : m.name === 'adblock' ? 'shield' : m.name === 'volume' ? 'volume' : m.name === 'extensions' ? 'extensions' : m.name === 'tools' ? 'tools' : 'menu']));
  // Extensions: the app asks where a popup should hang (a line of the tools menu was clicked, which has no place
  // of its own), and says which extension's popup is open. A press anywhere else in the interface closes it.
  Host.on('extensionAnchor', (m) => ui.extensionClick(m.key));
  Host.on('extensionPopup', (m) => { S.extensionPopup = m.key || ''; ui.updateTools(); });
  document.addEventListener('pointerdown', (event) => {
    if (S.extensionPopup && !(event.target.closest && event.target.closest('.tc-ext'))) Host.cmd('popupDismiss');
  }, true);
  Host.on('pickColor', (m) => T.picker.show(m));   // a picture of the page to pick a color from
  // A selection being translated, and its translation: a small panel over the page, where it was asked for.
  Host.on('translate', (m) => T.translate.show(m));
  Host.on('translateClose', (m) => T.translate.hide(m.id));
  // The zoom was just changed (Ctrl and the wheel, a zoom key, the menu): its box comes up for a moment.
  Host.on('zoomShow', () => T.zoom.show('auto'));
  // The tab search (MainForm.TabSearch.cs): asked for from the mark at the strip's start, or with Ctrl+Shift+A.
  Host.on('tabSearch', (m) => T.tabSearch.show(m, ui.brand));
  Host.on('dialog', (m) => T.dialog.show(m).then((result) => Host.post(Object.assign({ t: 'dialog', id: m.id }, result))));
  Host.on('notice', (m) => T.notice.show(m));
  Host.on('suggestions', (m) => {
    const address = document.getElementById('address');
    if (!m.items || !m.items.length || document.activeElement !== address || address.value !== m.typed) T.suggest.hide();
    else T.suggest.show(m);
  });
  Host.on('focusAddress', () => ui.focusAddress());
  Host.on('paste', (m) => ui.pasteText(m.text || ''));

  // The theme this page was loaded with (from /theme.js) is already applied to <html>; keep its facts.
  if (window.IGYO_THEME) ui.applyTheme(window.IGYO_THEME, true);

  if (Host.real) Host.post({ t: 'ready' });
  else {
    // Opened in an ordinary browser while working on the interface: a stand-in for the app.
    const script = document.createElement('script');
    script.src = 'dev-host.js';
    script.onload = () => Host.post({ t: 'ready' });
    document.head.append(script);
  }
})();
