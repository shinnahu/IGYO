using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    static class PageTools
    {
        const string PictureInPictureScript = @"(async () => {
  const docs = [document];
  for (const f of document.querySelectorAll('iframe')) { try { if (f.contentDocument) docs.push(f.contentDocument); } catch (e) {} }
  for (const d of docs) { if (d.pictureInPictureElement) { await d.exitPictureInPicture(); return 'exited'; } }
  if (!document.pictureInPictureEnabled) return 'unsupported';
  const videos = docs.flatMap(d => Array.from(d.querySelectorAll('video'))).filter(v => v.readyState > 0);
  if (!videos.length) return 'no-video';
  const area = v => { const r = v.getBoundingClientRect(); return r.width * r.height; };
  const video = videos.find(v => !v.paused) || videos.sort((a, b) => area(b) - area(a))[0];
  video.removeAttribute('disablepictureinpicture');
  video.disablePictureInPicture = false;
  try { await video.requestPictureInPicture(); return 'entered'; } catch (e) { return 'error: ' + e.message; }
})()";

        // Pops the page's video into a floating window (or brings it back). Returns entered, exited, no-video, unsupported or error.
        public static async Task<string> TogglePictureInPictureAsync(CoreWebView2 core)
        {
            var request = Json.Write(new Dictionary<string, object>
            {
                ["expression"] = PictureInPictureScript,
                ["awaitPromise"] = true,
                ["returnByValue"] = true,
                ["userGesture"] = true,   // the toolbar click counts as the user's gesture
            });
            var response = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request)));
            var details = Json.Obj(Json.Get(response, "exceptionDetails"));
            if (details != null) return "error: " + (Json.Str(details, "text") ?? "script failed");
            return Json.Str(Json.Obj(Json.Get(response, "result")), "value") ?? "error";
        }

        // Saves the whole page, top to bottom, as a PNG. Returns the file path.
        public static async Task<string> CaptureFullPageAsync(CoreWebView2 core, string folder, string title)
        {
            var metrics = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}")));
            var size = Json.Obj(Json.Get(metrics, "cssContentSize")) ?? Json.Obj(Json.Get(metrics, "contentSize"));
            double width = Math.Max(1, Math.Ceiling(Convert.ToDouble(Json.Get(size, "width") ?? 1)));
            double height = Math.Max(1, Math.Ceiling(Convert.ToDouble(Json.Get(size, "height") ?? 1)));
            height = Math.Min(height, 30000);   // very long pages are cut at 30,000 pixels
            var request = Json.Write(new Dictionary<string, object>
            {
                ["format"] = "png",
                ["captureBeyondViewport"] = true,
                ["clip"] = new Dictionary<string, object> { ["x"] = 0, ["y"] = 0, ["width"] = width, ["height"] = height, ["scale"] = 1 },
            });
            var shot = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", request)));
            var data = Json.Str(shot, "data");
            if (string.IsNullOrEmpty(data)) throw new InvalidOperationException(L.T("The page could not be captured."));
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"{SafeName(title)} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            File.WriteAllBytes(file, Convert.FromBase64String(data));
            return file;
        }

        // The page as it is on show right now, as PNG data in base64 (the color picker lays it over the page).
        public static async Task<string> CaptureViewAsync(CoreWebView2 core)
        {
            var shot = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}")));
            var data = Json.Str(shot, "data");
            if (string.IsNullOrEmpty(data)) throw new InvalidOperationException(L.T("The page could not be captured."));
            return data;
        }

        // ----- a tab's volume -----
        // The engine has no volume of its own for a page, so the page's sound is turned down or up where it is
        // made: in the page, by a script given to the page's documents (and to the frames inside them).
        //   - Below 100%, every <audio> and <video> plays at the volume the page chose, times the tab's volume. The
        //     page still reads back the volume it set itself, so its own volume slider does not move.
        //   - Above 100% a sound has to be played through an amplifier (Web Audio). That is only done for sound the
        //     page owns: sound fetched from another site without that site's permission would fall silent if it
        //     were routed that way, so it is left at 100%. It is also only done once the page may start sound at all.
        // The script sits behind a name made up anew every time IGYO starts, and does nothing until it is called.
#if DEBUG
        // Test builds: a fixed name, and a second function that says what was really done, for the checks to read.
        static readonly string VolumeKey = "__igyoVolumeForChecks";
        const string VolumeDebug = "Object.defineProperty(window, KEY + 'State', { value: () => ({ factor, context: audio ? audio.state : 'none', items: refs.map((r) => r.deref()).filter(Boolean).map((el) => ({ id: el.id || '', wanted: wanted.get(el), real: volume.get.call(el), amplified: gains.has(el), gain: gains.has(el) ? gains.get(el).gain.value : null, own: own(el) })) }) });";
#else
        static readonly string VolumeKey = "__t" + Guid.NewGuid().ToString("N").Substring(0, 18);
        const string VolumeDebug = "";
#endif

        const string VolumeScriptText = @"(() => {
  const KEY = '%KEY%', F = %F%;
  if (typeof window[KEY] === 'function') { window[KEY](F); return; }
  const proto = window.HTMLMediaElement && HTMLMediaElement.prototype;
  const volume = proto && Object.getOwnPropertyDescriptor(proto, 'volume');
  if (!volume || !volume.get || !volume.set) return;
  let factor = F, audio = null;
  const wanted = new WeakMap(), gains = new WeakMap(), known = new WeakSet(), refs = [];
  const scale = () => Math.min(factor, 1);
  // Whether this sound may be amplified: only when it is known where it comes from, and that is the page's own.
  const own = (el) => {
    if (el.srcObject) return false;
    const src = el.currentSrc || el.src || '';
    if (!src) return false;
    if (/^(blob|data):/i.test(src)) return true;
    try { return new URL(src, location.href).origin === location.origin || el.crossOrigin !== null; } catch (e) { return false; }
  };
  const boost = (el) => {
    const gain = gains.get(el);
    if (gain) { gain.gain.value = Math.max(factor, 1); return; }
    if (factor <= 1 || !own(el)) return;
    try {
      audio = audio || new AudioContext();
      const route = () => {
        if (gains.has(el) || audio.state !== 'running' || factor <= 1 || !own(el)) return;
        const g = audio.createGain();
        g.gain.value = factor;
        audio.createMediaElementSource(el).connect(g).connect(audio.destination);
        gains.set(el, g);
      };
      if (audio.state === 'running') route(); else audio.resume().then(route, () => {});
    } catch (e) { /* this sound stays at 100% */ }
  };
  const apply = (el) => {
    if (!wanted.has(el)) wanted.set(el, volume.get.call(el));
    try { volume.set.call(el, Math.max(0, Math.min(1, wanted.get(el) * scale()))); } catch (e) { /* not ready */ }
    boost(el);
  };
  const meet = (el) => {
    if (!(el instanceof HTMLMediaElement) || known.has(el)) return;
    known.add(el);
    refs.push(new WeakRef(el));
    apply(el);
  };
  Object.defineProperty(proto, 'volume', {
    configurable: true, enumerable: volume.enumerable,
    get() { return wanted.has(this) ? wanted.get(this) : volume.get.call(this); },
    set(value) {
      const n = Number(value);
      if (!(n >= 0 && n <= 1)) { volume.set.call(this, value); return; }   // refused by the engine, as ever
      if (!known.has(this)) { known.add(this); refs.push(new WeakRef(this)); }
      wanted.set(this, n);
      volume.set.call(this, n * scale());
      boost(this);
    },
  });
  const touch = (el) => { if (!(el instanceof HTMLMediaElement)) return; if (known.has(el)) boost(el); else meet(el); };
  const play = proto.play;
  proto.play = function () { touch(this); return play.apply(this, arguments); };
  for (const type of ['play', 'playing', 'loadedmetadata']) addEventListener(type, (e) => touch(e.target), true);
  // The volume moved without the page setting it (the player's own built-in slider): that is the new wish.
  addEventListener('volumechange', (e) => {
    const el = e.target;
    if (!(el instanceof HTMLMediaElement) || !known.has(el)) return;
    const real = volume.get.call(el), s = scale();
    const expected = Math.max(0, Math.min(1, (wanted.has(el) ? wanted.get(el) : real) * s));
    if (Math.abs(real - expected) < 0.004) return;
    if (s > 0) wanted.set(el, Math.min(1, real / s));
    else try { volume.set.call(el, 0); } catch (err) { /* stays as it is */ }
  }, true);
  const scan = () => { for (const el of document.querySelectorAll('audio, video')) meet(el); };
  if (document.documentElement) scan();
  addEventListener('DOMContentLoaded', scan, { once: true });
  Object.defineProperty(window, KEY, { value: (f) => {
    factor = Number(f) >= 0 ? Number(f) : 1;
    scan();
    for (let i = refs.length - 1; i >= 0; i--) { const el = refs[i].deref(); if (el) apply(el); else refs.splice(i, 1); }
  } });
  %DEBUG%
})();";

        public static string VolumeScript(double factor) =>
            VolumeScriptText.Replace("%KEY%", VolumeKey).Replace("%DEBUG%", VolumeDebug).Replace("%F%", factor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        // ----- video enhancement -----
        // The engine sharpens a video for which it is asked to (see Tools.EnhanceKind), and it is asked through the
        // <video> itself. So a script given to the page's documents (and to the frames inside them) asks for every
        // video there is, and for each one that appears or starts playing later. Called again with another kind
        // it changes them all at once; called with none it puts them back as they were. An engine that does not
        // have the property is left alone, and nothing is given to a page while enhancement is off.
        // (play() is watched through a stand-in that behaves exactly like the engine's own, so that players which
        // are not part of the page's ordinary tree are met too.)
#if DEBUG
        static readonly string EnhanceKey = "__igyoEnhanceForChecks";
        const string EnhanceDebug = "Object.defineProperty(window, KEY + 'State', { value: () => ({ kind, videos: refs.map((r) => r.deref()).filter(Boolean).map((v) => ({ id: v.id || '', now: v.msVideoProcessing })) }) });";
#else
        static readonly string EnhanceKey = "__t" + Guid.NewGuid().ToString("N").Substring(0, 18);
        const string EnhanceDebug = "";
#endif

        const string EnhanceScriptText = @"(() => {
  const KEY = '%KEY%', K = '%K%';
  if (typeof window[KEY] === 'function') { window[KEY](K); return; }
  const proto = window.HTMLVideoElement && HTMLVideoElement.prototype;
  if (!K || !proto || !('msVideoProcessing' in proto)) return;
  let kind = K;
  const known = new WeakSet(), refs = [];
  const reads = () => (kind === 'cas' ? 'cas:0' : kind || 'default');   // how the engine reads the wanted kind back
  // The engine only acts on a video's kind once the video has begun to load: one given earlier reads back as
  // given, but the video is drawn plain. So it is given again (again: true) at each of these moments.
  const moments = ['loadstart', 'loadedmetadata', 'playing'];
  const apply = (v, again) => { try { if ((again && kind) || v.msVideoProcessing !== reads()) v.msVideoProcessing = kind || 'default'; } catch (e) { /* this video stays as it is */ } };
  const meet = (v, again) => {
    if (!(v instanceof HTMLVideoElement)) return;
    if (!known.has(v)) {
      known.add(v);
      refs.push(new WeakRef(v));
      for (const type of moments) v.addEventListener(type, () => apply(v, true));
    }
    apply(v, again);
  };
  const media = HTMLMediaElement.prototype;
  media.play = new Proxy(media.play, { apply(target, self, args) { try { meet(self, false); } catch (e) { /* plays as ever */ } return Reflect.apply(target, self, args); } });
  for (const type of moments) addEventListener(type, (e) => { if (!known.has(e.target)) meet(e.target, true); }, true);
  const scan = () => { for (const v of document.querySelectorAll('video')) meet(v, false); };
  if (document.documentElement) scan();
  addEventListener('DOMContentLoaded', scan, { once: true });
  Object.defineProperty(window, KEY, { value: (k) => {
    kind = String(k || '');
    scan();
    for (let i = refs.length - 1; i >= 0; i--) { const v = refs[i].deref(); if (v) apply(v, true); else refs.splice(i, 1); }
  } });
  %DEBUG%
})();";

        // The script for a kind ("cas:1"…), or for none ("": videos go back to how the engine shows them by itself).
        public static string EnhanceScript(string kind) =>
            EnhanceScriptText.Replace("%KEY%", EnhanceKey).Replace("%DEBUG%", EnhanceDebug)
                .Replace("%K%", System.Text.RegularExpressions.Regex.IsMatch(kind ?? "", "^[a-zA-Z]+(:[0-9.]+)?$") ? kind : "");

        static string SafeName(string title)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string((title ?? "Page").Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
            if (name.Length == 0) name = "Page";
            return name.Length > 80 ? name.Substring(0, 80).Trim() : name;
        }
    }
}
