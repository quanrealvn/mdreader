// sw.js — the web version's offline app shell (ARCHITECTURE §15).
//
// This file is a template: the server substitutes __MDR_CACHE_VERSION__ and __MDR_ASSETS__ when
// it serves /sw.js (src/MdReader.Web/ServiceWorkerScript.cs). The version is a hash of this file
// and of every served file — vendor libraries included, even though they are not precached — so
// the bytes of /sw.js change if and only if something a page can load changed, which is exactly
// when the browser should notice an update, and never otherwise.
//
// The update path is the whole design, because the failure to avoid is a page running new HTML
// with old JavaScript (or the reverse):
//
//   install   cache.addAll(ASSETS) into a cache named after this version. All or nothing: if one
//             asset fails, install fails, this worker never activates, and the previous one keeps
//             serving its own complete set. A cache therefore only ever holds one whole shell.
//   activate  every other cache is deleted and this worker claims its clients.
//   fetch     answered ONLY for same-origin GETs that belong to the shell, and for a navigation
//             only to the reader's own address: this origin also serves the public pages, which
//             are the server's to render and not this app's to hand out. Cross-origin image
//             requests are left to the browser — re-issuing them here would put them under this
//             worker's own CSP (connect-src 'self') and they would be blocked.
//   waiting   there is no unconditional skipWaiting(). A page keeps the SHELL it started on —
//             index.html, the CSS and every app module, all from one cache — for its whole life.
//             The page offers the update instead, and the message below is taken only when the
//             user accepts, together with a reload: the one moment where swapping everything at
//             once is safe.
//
// What that guarantee does NOT cover: the vendor libraries (mermaid, katex, highlight.js). They
// are megabytes, they are imported only when a document actually needs them, and rendering needs
// the server anyway — so precaching them would make every install heavy and every missing file a
// failed install. They come from the network (HTTP-cached for a day), which means a page can pull
// a newer vendor build than the app module that imports it. Two things keep that tolerable: each
// library is pinned by its folder and only moves when we deliberately bump it, and such a deploy
// does change this file's version, so the update is at least offered. It is a weaker promise than
// the shell's, and it is not claimed to be the same one.

const CACHE_VERSION = "__MDR_CACHE_VERSION__";
const CACHE_NAME = "mdreader-shell-" + CACHE_VERSION;
const ASSETS = __MDR_ASSETS__;

// The shell by absolute path, resolved against this script's own URL (/sw.js), so "./css/app.css"
// is "/css/app.css" and "./" is "/".
const SHELL = new Set(ASSETS.map((asset) => new URL(asset, self.location.href).pathname));
const SHELL_ROOT = new URL("./", self.location.href).pathname;

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) => cache.addAll(ASSETS))
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      const names = await caches.keys();
      await Promise.all(names.filter((name) => name !== CACHE_NAME).map((name) => caches.delete(name)));
      await self.clients.claim();
    })()
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;

  // POST /api/render is never cached and never intercepted: rendering always needs the server.
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (request.mode === "navigate") {
    // ONLY the reader's own address. This origin also serves the public pages (/markdown-preview,
    // /about, …), which are rendered by the server and are not this app: answering them from the
    // reader's cache would replace a page with the wrong one the moment the worker is in charge.
    if (url.pathname !== SHELL_ROOT) return;
    event.respondWith(serveShell(request));
    return;
  }

  // Vendor libraries, /healthz, /api, the public pages' own /site/ assets, anything else: straight
  // to the network. The shell is the whole of what this worker will answer for.
  if (!SHELL.has(url.pathname)) return;
  event.respondWith(serveFromCache(url.pathname, request));
});

self.addEventListener("message", (event) => {
  // The page's "Update" action. See the note above: this is the only skipWaiting, and the page
  // reloads itself as soon as the new worker takes over.
  if (event.data && event.data.type === "mdr-skip-waiting") self.skipWaiting();
});

/** The reader's own page; offline, it comes out of the cache. Reached only for "/" (see above). */
async function serveShell(request) {
  const cached = await caches.match(SHELL_ROOT, { cacheName: CACHE_NAME });
  if (cached) return cached;
  return fetch(request);
}

async function serveFromCache(pathname, request) {
  const cached = await caches.match(pathname, { cacheName: CACHE_NAME });
  if (cached) return cached;

  // Only reachable if the cache was cleared under us (storage pressure, "clear site data"). The
  // network answer is used as it comes and is not written back: a cache must stay one whole shell.
  return fetch(request);
}
