// Offline support: cache the published app assets (list generated at publish time).
self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall()));
self.addEventListener('activate', event => event.waitUntil(onActivate()));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/];
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall() {
  const assetsRequests = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
    .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
  await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate() {
  const cacheKeys = await caches.keys();
  await Promise.all(cacheKeys
    .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
    .map(key => caches.delete(key)));
}

async function onFetch(event) {
  let cachedResponse = null;
  if (event.request.method === 'GET') {
    // For all navigation requests, try to serve index.html from cache.
    const shouldServeIndexHtml = event.request.mode === 'navigate' && !manifestUrlList.some(url => url === event.request.url);
    const request = shouldServeIndexHtml ? 'index.html' : event.request;
    const cache = await caches.open(cacheName);
    cachedResponse = await cache.match(request);
  }
  return cachedResponse || fetch(event.request);
}
