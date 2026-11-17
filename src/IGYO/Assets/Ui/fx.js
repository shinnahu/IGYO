// IGYO — small effects a theme can turn on with its "fx" settings.
//   click:  none | sparks | squares | pixels | stars | notes | hearts | shards     a burst where you click the interface
//   title:  none | scramble | type                                                 how the active tab's title changes
//   notice: none | type                                                            how a notice's words appear
// Other "fx" values (tabIn, loader…) are plain CSS and live in chrome.css.
// Everything here is brief and stops by itself; with animations off, nothing runs.
(() => {
  'use strict';

  const IGYO = window.IGYO;
  const layer = document.getElementById('fx');
  const root = document.documentElement;
  let fx = {};

  const moving = () => root.dataset.anim !== 'off' && !matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ----- click bursts -----

  const GLYPHS = { stars: ['★', '✦', '✧'], notes: ['♪', '♫', '♬'], hearts: ['♥', '♡'] };
  const live = new Set();

  function burst(x, y) {
    const kind = fx.click;
    if (!kind || kind === 'none' || !moving() || live.size > 40) return;
    const count = kind === 'pixels' ? 10 : kind === 'squares' ? 6 : 7;
    for (let i = 0; i < count; i++) {
      const p = document.createElement('i');
      p.className = 'fx-p is-' + kind;
      const glyphs = GLYPHS[kind];
      if (glyphs) p.textContent = glyphs[i % glyphs.length];
      const angle = (Math.PI * 2 * i) / count + Math.random() * 0.7;
      const far = 16 + Math.random() * 26;
      const dx = Math.cos(angle) * far;
      const dy = Math.sin(angle) * far - (glyphs ? 10 : 0);
      const spin = kind === 'squares' || kind === 'shards' ? (Math.random() * 240 - 120) : 0;
      p.style.left = x + 'px';
      p.style.top = y + 'px';
      layer.append(p);
      live.add(p);
      const steps = kind === 'pixels' ? { easing: 'steps(5)' } : { easing: 'cubic-bezier(.15,.7,.3,1)' };
      p.animate(
        [
          { transform: 'translate(-50%, -50%) scale(.4)', opacity: 1 },
          { transform: `translate(calc(-50% + ${dx}px), calc(-50% + ${dy}px)) rotate(${spin}deg) scale(1)`, opacity: 0 },
        ],
        Object.assign({ duration: 420 + Math.random() * 220, fill: 'forwards' }, steps),
      ).onfinish = () => { p.remove(); live.delete(p); };
    }
  }

  document.addEventListener('pointerdown', (event) => {
    if (event.button !== 0 || event.target.closest('#overlays') || event.target.closest('input')) return;
    burst(event.clientX, event.clientY);
  }, true);

  // ----- text that decodes or types itself -----

  const NOISE = '▚▞░▒▓█#%&/<>*+=';
  const running = new WeakMap();

  function stop(node) {
    const timer = running.get(node);
    if (timer) { clearInterval(timer); running.delete(node); }
  }

  function scramble(node, text) {
    stop(node);
    const total = Math.min(14, Math.max(6, Math.ceil(text.length / 3)));
    let step = 0;
    const timer = setInterval(() => {
      step += 1;
      const shown = Math.floor((text.length * step) / total);
      let tail = '';
      for (let i = shown; i < Math.min(text.length, shown + 6); i++) tail += text[i] === ' ' ? ' ' : NOISE[Math.floor(Math.random() * NOISE.length)];
      node.textContent = text.slice(0, shown) + tail;
      if (step >= total) { node.textContent = text; stop(node); }
    }, 32);
    running.set(node, timer);
  }

  function typeOut(node, text, ms) {
    stop(node);
    let shown = 0;
    const per = Math.max(1, Math.ceil(text.length / (ms / 24)));
    node.textContent = '';
    const timer = setInterval(() => {
      shown = Math.min(text.length, shown + per);
      node.textContent = text.slice(0, shown);
      if (shown >= text.length) stop(node);
    }, 24);
    running.set(node, timer);
  }

  IGYO.fx = {
    themeChanged(theme) { fx = (theme && theme.fx) || {}; },

    // The active tab's title changed.
    retitle(node, text) {
      if (!moving() || !fx.title || fx.title === 'none') { stop(node); node.textContent = text; return; }
      if (fx.title === 'type') typeOut(node, text, 260);
      else scramble(node, text);
    },

    // A notice appeared.
    notice(node, text) {
      if (moving() && fx.notice === 'type') typeOut(node, text, 420);
    },

    burst,
  };
  IGYO.fx.themeChanged(window.IGYO_THEME);
})();
