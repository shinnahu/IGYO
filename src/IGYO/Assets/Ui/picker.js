// IGYO — which profile to open at start. The app (ProfilePicker.cs) sends the profiles; a click on one opens it.
// Names come from the person, so they only ever go into the page as text.
(() => {
  'use strict';

  const tr = window.IGYO.tr;
  window.IGYO.translatePage();
  const webview = window.chrome && window.chrome.webview;
  const post = (m) => { if (webview) webview.postMessage(m); };
  const list = document.getElementById('list');
  const ask = document.getElementById('ask');
  const hint = document.getElementById('hint');
  let first = true;

  function render(m) {
    const cards = m.list.map((p) => {
      const card = document.createElement('button');
      card.type = 'button';
      card.className = 'pk-card';
      card.setAttribute('role', 'listitem');
      const picture = new Image();
      picture.alt = '';
      picture.src = p.avatar;
      const name = document.createElement('span');
      name.className = 'pk-name';
      name.textContent = p.name;
      card.append(picture, name);
      if (p.isDefault) {
        const badge = document.createElement('span');
        badge.className = 'pk-badge';
        badge.textContent = tr('Default');
        card.append(badge);
      }
      card.title = p.name;
      card.addEventListener('click', () => post({ t: 'open', id: p.id }));
      card._profile = p;
      return card;
    });
    list.replaceChildren(...cards);
    ask.checked = !!m.ask;
    hint.textContent = m.ask ? ''
      : m.hasDefault ? tr('From the next start, the default profile opens without asking.')
        : tr('To open one without asking, make it the default: Settings > Profiles, its ⋯ menu.');
    if (first) {
      // The keyboard starts on the default profile, else on the one used last: Enter opens it.
      first = false;
      const start = cards.find((c) => c._profile.isDefault) || cards.find((c) => c._profile.lastUsed) || cards[0];
      if (start) start.focus({ preventScroll: true });
    }
  }

  // Arrows move between the profiles.
  list.addEventListener('keydown', (event) => {
    const cards = [...list.children];
    const at = cards.indexOf(document.activeElement);
    if (at < 0) return;
    const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    cards[(at + step + cards.length) % cards.length].focus();
  });
  ask.addEventListener('change', () => post({ t: 'ask', on: ask.checked }));
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape') post({ t: 'close' }); });
  if (webview) webview.addEventListener('message', (event) => { if (event.data && event.data.t === 'profiles') render(event.data); });
  post({ t: 'ready' });
})();
