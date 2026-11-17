// IGYO — lets one of IGYO's own pages (New Tab, Settings) take a new theme without reloading.
// The app calls IGYOTheme.reload() in each such page when the theme changes; the page keeps its state
// (an open editor, a running game) and only its look changes. Pages can listen for the "igyo-theme" event.
(() => {
  'use strict';
  let loading = false;
  let again = false;

  function clearOld() {
    const root = document.documentElement;
    for (const attribute of [...root.attributes]) if (attribute.name.startsWith('data-o-')) root.removeAttribute(attribute.name);
  }

  function reload() {
    if (loading) { again = true; return; }
    loading = true;
    const stamp = Date.now();
    const script = document.createElement('script');
    script.src = '/theme.js?v=' + stamp;
    const finish = () => {
      script.remove();
      const old = document.getElementById('theme-css');
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = '/theme.css?v=' + stamp;
      const done = () => {
        if (old && old !== link) old.remove();
        link.id = 'theme-css';
        loading = false;
        window.dispatchEvent(new CustomEvent('igyo-theme', { detail: window.IGYO_THEME || {} }));
        if (again) { again = false; reload(); }
      };
      link.addEventListener('load', done);
      link.addEventListener('error', done);
      if (old) old.after(link); else document.head.append(link);
    };
    clearOld();
    script.addEventListener('load', finish);
    script.addEventListener('error', finish);
    document.head.append(script);
  }

  window.IGYOTheme = { reload };
})();
