// Browser helpers for Open Story Studio (download, IndexedDB library, microphone, SVG coordinates).

export function download(fileName, mimeType, bytes) {
  const blob = new Blob([bytes], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

export function toSvgPoint(svgId, clientX, clientY) {
  const svg = document.getElementById(svgId);
  if (!svg) return [clientX, clientY];
  const point = svg.createSVGPoint();
  point.x = clientX;
  point.y = clientY;
  const ctm = svg.getScreenCTM();
  if (!ctm) return [clientX, clientY];
  const p = point.matrixTransform(ctm.inverse());
  return [p.x, p.y];
}

export function setPaused(audioId, paused) {
  const audio = document.getElementById(audioId);
  if (!audio) return;
  if (paused) audio.pause();
  else audio.play().catch(() => { /* autoplay may be blocked until a user gesture */ });
}

// ---------- IndexedDB library ----------

const DB_NAME = "open-story-studio";
const STORE = "packs";

function openDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE, { keyPath: "id" });
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function withStore(mode, action) {
  const db = await openDb();
  try {
    return await new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, mode);
      const result = action(tx.objectStore(STORE));
      tx.oncomplete = () => resolve(result.result);
      tx.onerror = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

export function dbPut(id, bytes) {
  return withStore("readwrite", s => s.put({ id, modified: Date.now(), data: bytes }));
}

export async function dbGet(id) {
  const item = await withStore("readonly", s => s.get(id));
  return item ? item.data : null;
}

export async function dbList() {
  const items = await withStore("readonly", s => s.getAll());
  return (items || []).map(i => ({ id: i.id, modified: i.modified }));
}

export function dbDelete(id) {
  return withStore("readwrite", s => s.delete(id));
}

// ---------- Microphone (records 16-bit mono WAVE) ----------

let recorder = null;

export async function startRecording() {
  if (!navigator.mediaDevices?.getUserMedia) return false;
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    const context = new AudioContext();
    const source = context.createMediaStreamSource(stream);
    const processor = context.createScriptProcessor(4096, 1, 1);
    const chunks = [];
    processor.onaudioprocess = e => chunks.push(new Float32Array(e.inputBuffer.getChannelData(0)));
    source.connect(processor);
    processor.connect(context.destination);
    recorder = { stream, context, source, processor, chunks };
    return true;
  } catch {
    return false;
  }
}

export async function stopRecording() {
  if (!recorder) return null;
  const { stream, context, source, processor, chunks } = recorder;
  recorder = null;
  processor.disconnect();
  source.disconnect();
  stream.getTracks().forEach(t => t.stop());
  const sampleRate = context.sampleRate;
  await context.close();
  return encodeWav(chunks, sampleRate);
}

function encodeWav(chunks, sampleRate) {
  const length = chunks.reduce((n, c) => n + c.length, 0);
  const buffer = new ArrayBuffer(44 + length * 2);
  const view = new DataView(buffer);
  const writeString = (offset, s) => [...s].forEach((ch, i) => view.setUint8(offset + i, ch.charCodeAt(0)));
  writeString(0, "RIFF");
  view.setUint32(4, 36 + length * 2, true);
  writeString(8, "WAVE");
  writeString(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeString(36, "data");
  view.setUint32(40, length * 2, true);
  let offset = 44;
  for (const chunk of chunks) {
    for (let i = 0; i < chunk.length; i++, offset += 2) {
      const s = Math.max(-1, Math.min(1, chunk[i]));
      view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7fff, true);
    }
  }
  return new Uint8Array(buffer);
}
