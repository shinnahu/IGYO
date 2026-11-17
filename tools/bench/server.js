// Serves the same made-up pages to every browser measured (bench.ps1), from this PC only, and notes when
// each page reports that it has finished loading.
//   /p/<n>?run=<id>   page number n (an article of text, drawings, a canvas, and some data kept in memory by script)
//   /done?run=&n=     called by the page from its load event; written to done.log with the time it arrived
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = 18811;
const LOG = path.join(__dirname, 'done.log');
const WORDS = 'tide marsh reed heron estuary copper light water morning quiet path mud print otter geese bank flood slow walk notebook wind salt grass boat rope harbour gull stone bridge lantern evening river field cloud rain window door garden'.split(' ');

// The same page every time for the same number: a small generator seeded by it.
function rng(seed) { let s = (seed * 2654435761) >>> 0; return () => { s ^= s << 13; s >>>= 0; s ^= s >> 17; s ^= s << 5; s >>>= 0; return s / 4294967296; }; }

function page(n, run) {
  const r = rng(n + 1);
  const word = () => WORDS[Math.floor(r() * WORDS.length)];
  const sentence = () => { const k = 8 + Math.floor(r() * 14); let s = ''; for (let i = 0; i < k; i++) s += (i ? ' ' : '') + word(); return s.charAt(0).toUpperCase() + s.slice(1) + '.'; };
  const hue = Math.floor(r() * 360);
  let body = '';
  for (let section = 0; section < 12; section++) {
    body += `<h2>${sentence().slice(0, 40)}</h2>`;
    body += `<svg viewBox="0 0 600 120" width="600" height="120" aria-hidden="true"><rect width="600" height="120" rx="14" fill="hsl(${(hue + section * 24) % 360} 45% 22%)"/>` +
      Array.from({ length: 14 }, (_, i) => `<circle cx="${30 + i * 42}" cy="${40 + Math.floor(r() * 50)}" r="${6 + Math.floor(r() * 14)}" fill="hsl(${(hue + i * 17) % 360} 70% 62%)" opacity=".8"/>`).join('') + '</svg>';
    for (let p = 0; p < 9; p++) body += `<p>${Array.from({ length: 4 + Math.floor(r() * 4) }, sentence).join(' ')}</p>`;
    body += '<ul>' + Array.from({ length: 6 }, () => `<li><a href="#">${sentence().slice(0, 50)}</a> <span class="tag">${word()}</span></li>`).join('') + '</ul>';
  }
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Bench page ${n}</title><link rel="icon" href="data:,">
<style>
  body { margin: 0; background: #12141b; color: #e7eaf3; font: 16px/1.6 "Segoe UI", sans-serif; }
  header { position: sticky; top: 0; padding: 14px 40px; background: #171a23; border-bottom: 1px solid #262b3a; font-weight: 600; display: flex; gap: 20px; }
  main { max-width: 860px; margin: 0 auto; padding: 24px 40px 80px; }
  h1 { font-size: 40px; margin: 20px 0; } h2 { margin: 34px 0 12px; }
  p { margin: 0 0 14px; color: #c9cfdd; } svg { display: block; margin: 12px 0 18px; max-width: 100%; }
  li { margin: 4px 0; } a { color: hsl(${hue} 80% 72%); } .tag { padding: 1px 8px; border-radius: 99px; background: #232838; font-size: 12px; }
  canvas { display: block; width: 100%; border-radius: 14px; margin: 16px 0; }
</style></head><body>
<header><span>Bench page ${n}</span><span id="ticks">0</span></header>
<main><h1>${sentence()}</h1><canvas id="c" width="1200" height="500"></canvas>${body}</main>
<script>
  // A drawing, and data a page would keep around: ${n} decides only its values.
  const g = document.getElementById('c').getContext('2d');
  const grad = g.createLinearGradient(0, 0, 1200, 500); grad.addColorStop(0, 'hsl(${hue} 60% 30%)'); grad.addColorStop(1, 'hsl(${(hue + 80) % 360} 60% 16%)');
  g.fillStyle = grad; g.fillRect(0, 0, 1200, 500);
  for (let i = 0; i < 400; i++) { g.fillStyle = 'hsla(' + ((${hue} + i * 3) % 360) + ', 80%, 70%, .5)'; g.beginPath(); g.arc((i * 97) % 1200, (i * 53) % 500, 4 + (i % 9), 0, 7); g.fill(); }
  window.kept = Array.from({ length: 120000 }, (_, i) => ({ id: i, name: 'item ' + i + ' of page ${n}', at: i * 1.5, tags: [i % 7, i % 11] }));
  let ticks = 0; setInterval(() => { document.getElementById('ticks').textContent = ++ticks; }, 1000);
  addEventListener('load', () => { fetch('/done?run=${encodeURIComponent(run)}&n=${n}&ms=' + Math.round(performance.now())).catch(() => {}); });
</script></body></html>`;
}

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/done') {
    fs.appendFileSync(LOG, JSON.stringify({ at: Date.now(), run: url.searchParams.get('run'), n: Number(url.searchParams.get('n')), ms: Number(url.searchParams.get('ms')) }) + '\n');
    res.writeHead(204); res.end(); return;
  }
  const m = /^\/p\/(\d+)$/.exec(url.pathname);
  if (!m) { res.writeHead(404); res.end('not found'); return; }
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(page(Number(m[1]), url.searchParams.get('run') || ''));
}).listen(PORT, '127.0.0.1', () => console.log('bench pages on http://127.0.0.1:' + PORT));
