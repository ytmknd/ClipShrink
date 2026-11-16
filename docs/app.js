// ClipShrink Web — 動画の前後をトリムして小さな MP4 に書き出す（すべてブラウザー内で処理）
import {
  Input, BlobSource, ALL_FORMATS, Output, Mp4OutputFormat, BufferTarget, StreamTarget,
  Conversion, CanvasSink, Quality, canEncodeVideo, canEncodeAudio, ConversionCanceledError,
} from './vendor/mediabunny-1.65.1.min.mjs';

const $ = (id) => document.getElementById(id);
const SAMPLE_SECONDS = 5;
const THUMB_SIZE = 480;

const el = {
  drop: $('drop'), file: $('file'), pick: $('pick'), change: $('change'), editor: $('editor'),
  fileName: $('fileName'), fileMeta: $('fileMeta'),
  video: $('video'), videoMsg: $('videoMsg'), playerTime: $('playerTime'),
  playRange: $('playRange'), stop: $('stop'), setStart: $('setStart'), setEnd: $('setEnd'),
  thumbStart: $('thumbStart'), thumbEnd: $('thumbEnd'), capStart: $('capStart'), capEnd: $('capEnd'),
  rangeStart: $('rangeStart'), rangeEnd: $('rangeEnd'), timeStart: $('timeStart'), timeEnd: $('timeEnd'),
  lengthText: $('lengthText'),
  quality: $('quality'), targetMB: $('targetMB'), targetInfo: $('targetInfo'),
  codec: $('codec'), resolution: $('resolution'), fps: $('fps'), audio: $('audio'),
  exportBtn: $('export'), sampleBtn: $('sample'), cancelBtn: $('cancel'),
  progress: $('progress'), status: $('status'), result: $('result'),
  sampleDialog: $('sampleDialog'), sampleVideo: $('sampleVideo'), sampleInfo: $('sampleInfo'),
};

const state = {
  file: null, input: null, videoTrack: null, audioTrack: null, sink: null,
  duration: 0, width: 0, height: 0, fps: 0, canDecode: false, videoPlayable: true,
  start: 0, end: 0, stopAt: null, busy: false, conversion: null,
  objectUrl: null, sampleUrl: null, audioCodec: 'aac',
};

// ---------------- 対応チェック ----------------

const supported = 'VideoEncoder' in window && 'VideoDecoder' in window;
if (!supported) {
  $('unsupported').hidden = false;
  el.pick.disabled = true;
}

(async () => {
  if (!supported) return;
  try {
    if (!(await canEncodeVideo('hevc'))) {
      const opt = el.codec.querySelector('option[value="hevc"]');
      opt.disabled = true;
      opt.textContent += '（このブラウザーでは使えません）';
    }
    if (!(await canEncodeAudio('aac'))) state.audioCodec = 'opus';
  } catch { /* 判定できなくても続行 */ }
})();

// ---------------- 書式 ----------------

function formatTime(sec) {
  sec = Math.max(0, sec);
  const tenths = Math.round(sec * 10);
  const h = Math.floor(tenths / 36000);
  const m = Math.floor((tenths % 36000) / 600);
  const s = Math.floor((tenths % 600) / 10);
  const t = tenths % 10;
  const mmss = `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}.${t}`;
  return h > 0 ? `${h}:${mmss}` : mmss;
}

/** "90", "1:30", "1:30.5", "0:01:30.25" などを秒に変換。失敗時は null。 */
function parseTime(text) {
  const parts = text.trim().replace(/：/g, ':').replace(/．/g, '.').split(':');
  if (parts.length < 1 || parts.length > 3) return null;
  let total = 0;
  for (const p of parts) {
    if (!/^\d+(\.\d+)?$/.test(p)) return null;
    total = total * 60 + parseFloat(p);
  }
  return total;
}

function formatSize(bytes) {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  return `${Math.round(bytes / 1024)} KB`;
}



// ---------------- ファイルを開く ----------------

el.pick.addEventListener('click', () => el.file.click());
el.change.addEventListener('click', () => el.file.click());
el.file.addEventListener('change', () => {
  if (el.file.files?.[0]) openFile(el.file.files[0]);
  el.file.value = '';
});

for (const target of [el.drop, document.body]) {
  target.addEventListener('dragover', (e) => {
    if (state.busy) return;
    e.preventDefault();
    el.drop.classList.add('hover');
  });
  target.addEventListener('dragleave', () => el.drop.classList.remove('hover'));
  target.addEventListener('drop', (e) => {
    e.preventDefault();
    el.drop.classList.remove('hover');
    const f = e.dataTransfer?.files?.[0];
    if (f && !state.busy) openFile(f);
  });
}

async function openFile(file) {
  if (!supported) return;
  setStatus('読み込み中...');
  hideResult();

  // 前のファイルを片付ける
  state.input?.dispose?.();
  if (state.objectUrl) URL.revokeObjectURL(state.objectUrl);

  const input = new Input({ source: new BlobSource(file), formats: ALL_FORMATS });
  try {
    const videoTrack = await input.getPrimaryVideoTrack();
    if (!videoTrack) throw new Error('映像が含まれていないファイルです。');
    const audioTrack = await input.getPrimaryAudioTrack();
    const duration = await input.computeDuration();
    const canDecode = await videoTrack.canDecode();
    let fps = 0;
    try { fps = (await videoTrack.computePacketStats(120)).averagePacketRate; } catch { /* 不明 */ }

    Object.assign(state, {
      file, input, videoTrack, audioTrack, duration, canDecode, fps,
      width: videoTrack.displayWidth, height: videoTrack.displayHeight,
      start: 0, end: duration, stopAt: null, videoPlayable: true,
    });

    state.sink = canDecode
      ? new CanvasSink(videoTrack, state.width >= state.height ? { width: THUMB_SIZE } : { height: THUMB_SIZE })
      : null;

    el.fileName.textContent = file.name;
    el.fileMeta.textContent = [
      `長さ ${formatTime(duration)}`,
      `${state.width}×${state.height}`,
      fps ? `${Math.abs(fps - Math.round(fps)) < 0.05 ? Math.round(fps) : fps.toFixed(2)} fps` : null,
      videoTrack.codec ?? '不明な映像形式',
      audioTrack ? `音声 ${audioTrack.codec ?? ''}` : '音声なし',
      formatSize(file.size),
    ].filter(Boolean).join('　');

    el.drop.hidden = true;
    el.editor.hidden = false;

    state.objectUrl = URL.createObjectURL(file);
    el.video.src = state.objectUrl;
    el.videoMsg.hidden = true;

    for (const r of [el.rangeStart, el.rangeEnd]) r.max = String(duration);
    syncTrimUi();
    requestThumb('start', true);
    requestThumb('end', true);
    updateButtons();

    if (!canDecode) {
      showResult(
        `このブラウザーでは、この動画の映像形式（${videoTrack.codec ?? '不明'}）を読み込めないため書き出しできません。\n` +
        '最新の Chrome / Edge でお試しください。HEVC（iPhone の動画）は、Windows では GPU の HEVC 対応が必要です。',
        'error');
    }
    setStatus('');
  } catch (err) {
    input.dispose?.();
    setStatus('');
    showResult('動画を読み込めませんでした。\n' + (err?.message ?? err), 'error');
  }
}

// ---------------- プレーヤー ----------------

el.video.addEventListener('error', () => {
  if (!el.video.getAttribute('src')) return;
  state.videoPlayable = false;
  el.videoMsg.hidden = false;
  el.videoMsg.textContent = state.canDecode
    ? 'このブラウザーではこの動画をそのまま再生できません。\n右側の静止画で位置を確認できます。書き出しは行えます。'
    : 'このブラウザーではこの動画を再生できません。';
  updateButtons();
});

el.video.addEventListener('timeupdate', () => {
  el.playerTime.textContent = `再生位置 ${formatTime(el.video.currentTime)}`;
});

function tick() {
  if (state.stopAt !== null && !el.video.paused && el.video.currentTime >= state.stopAt) {
    el.video.pause();
    el.video.currentTime = state.stopAt;
    state.stopAt = null;
  }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);

el.playRange.addEventListener('click', () => {
  state.stopAt = state.end;
  el.video.currentTime = state.start;
  el.video.play().catch(() => {});
});
el.stop.addEventListener('click', () => { state.stopAt = null; el.video.pause(); });
el.setStart.addEventListener('click', () => { el.video.pause(); setStart(el.video.currentTime, false); });
el.setEnd.addEventListener('click', () => { el.video.pause(); setEnd(el.video.currentTime, false); });

function seekPlayer(t) {
  if (!state.videoPlayable || !el.video.getAttribute('src')) return;
  state.stopAt = null;
  el.video.pause();
  el.video.currentTime = t;
}

// ---------------- トリム ----------------

const minGap = () => Math.max(0.1, state.fps ? 1 / state.fps : 0.1);

function setStart(sec, seek = true) {
  state.start = Math.min(Math.max(0, sec), Math.max(0, state.duration - minGap()));
  if (state.end - state.start < minGap()) {
    state.end = Math.min(state.duration, state.start + minGap());
    requestThumb('end');
  }
  syncTrimUi();
  requestThumb('start');
  if (seek) seekPlayer(state.start);
}

function setEnd(sec, seek = true) {
  state.end = Math.min(Math.max(minGap(), sec), state.duration);
  if (state.end - state.start < minGap()) {
    state.start = Math.max(0, state.end - minGap());
    requestThumb('start');
  }
  syncTrimUi();
  requestThumb('end');
  if (seek) seekPlayer(endFrameTime());
}

const endFrameTime = () => Math.max(0, state.end - (state.fps ? 1 / state.fps : 0.04));

function syncTrimUi() {
  el.rangeStart.value = String(state.start);
  el.rangeEnd.value = String(state.end);
  el.timeStart.value = formatTime(state.start);
  el.timeEnd.value = formatTime(state.end);
  el.capStart.textContent = formatTime(state.start);
  el.capEnd.textContent = formatTime(state.end);
  el.lengthText.textContent = `${formatTime(state.end - state.start)}（元の長さ ${formatTime(state.duration)}）`;
  updateTargetInfo();
}

el.rangeStart.addEventListener('input', () => setStart(parseFloat(el.rangeStart.value)));
el.rangeEnd.addEventListener('input', () => setEnd(parseFloat(el.rangeEnd.value)));

for (const [box, isEnd] of [[el.timeStart, false], [el.timeEnd, true]]) {
  const commit = () => {
    if (box.value === formatTime(isEnd ? state.end : state.start)) return;
    const t = parseTime(box.value);
    if (t === null) { syncTrimUi(); return; }
    isEnd ? setEnd(t) : setStart(t);
  };
  box.addEventListener('change', commit);
  box.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); commit(); } });
}

document.querySelectorAll('[data-nudge]').forEach((b) => {
  b.addEventListener('click', () => {
    const d = parseFloat(b.dataset.d);
    b.dataset.nudge === 'end' ? setEnd(state.end + d) : setStart(state.start + d);
  });
});

// ---------------- 静止画（WebCodecs で取り出すので、再生できない形式でも表示できる） ----------------

const thumbTimers = {};
const thumbSeq = { start: 0, end: 0 };
let sinkQueue = Promise.resolve();

function requestThumb(which, immediate = false) {
  clearTimeout(thumbTimers[which]);
  thumbTimers[which] = setTimeout(() => drawThumb(which), immediate ? 0 : 120);
}

function drawThumb(which) {
  const sink = state.sink;
  if (!sink) return;
  const seq = ++thumbSeq[which];
  const t = which === 'start' ? state.start : endFrameTime();
  const canvas = which === 'start' ? el.thumbStart : el.thumbEnd;
  // CanvasSink への要求は順番に処理する
  sinkQueue = sinkQueue.then(async () => {
    if (seq !== thumbSeq[which] || sink !== state.sink) return;
    try {
      const wrapped = await sink.getCanvas(t) ?? await sink.getCanvas(Math.max(0, t - 0.5));
      if (!wrapped || seq !== thumbSeq[which] || sink !== state.sink) return;
      canvas.width = wrapped.canvas.width;
      canvas.height = wrapped.canvas.height;
      canvas.getContext('2d').drawImage(wrapped.canvas, 0, 0);
    } catch { /* 取り出せないフレームは無視 */ }
  });
}

// ---------------- 圧縮設定 ----------------

const mode = () => document.querySelector('input[name="mode"]:checked').value;

document.querySelectorAll('input[name="mode"]').forEach((r) => r.addEventListener('change', () => {
  el.quality.disabled = mode() !== 'quality';
  el.targetMB.disabled = mode() !== 'target';
  updateTargetInfo();
}));
el.targetMB.addEventListener('input', updateTargetInfo);
el.audio.addEventListener('change', updateTargetInfo);

function audioBps() {
  return state.audioTrack ? parseInt(el.audio.value, 10) : 0;
}

/** 目標サイズから映像ビットレート (bps) を計算する。 */
function targetVideoBps(length) {
  const total = (parseFloat(el.targetMB.value) || 1) * 1024 * 1024 * 8 / Math.max(0.1, length);
  return Math.max(50_000, Math.floor(total * 0.95 - audioBps()));
}

function updateTargetInfo() {
  if (mode() !== 'target' || !state.file) { el.targetInfo.textContent = ''; return; }
  const kbps = Math.round(targetVideoBps(state.end - state.start) / 1000);
  el.targetInfo.textContent = `→ 映像 約 ${kbps} kbps` + (kbps < 150 ? '（かなり粗くなります。解像度を下げるのがおすすめ）' : '');
}

/**
 * 現在の設定から Conversion のオプションを作る。fullLength は目標サイズ計算に使う長さ。
 * videoBps を渡すと目標サイズモードの映像ビットレートをその値に固定する（再調整用）。
 */
function conversionOptions(start, end, fullLength, videoBps) {
  const video = { codec: el.codec.value, forceTranscode: true };

  // 出力サイズは幅・高さとも偶数で明示する（H.264/H.265 は奇数サイズを受け付けない。
  // 例: 1364×766 で画素比補正があると表示サイズが 1364×767 になる）
  const { width: w, height: h } = outputSize();
  video.width = w;
  video.height = h;
  video.fit = 'fill'; // 偶数への丸めは 1px 程度なので比率の変化は無視できる

  const fpsCap = parseInt(el.fps.value, 10);
  if (fpsCap && (!state.fps || state.fps > fpsCap + 0.5)) video.frameRate = fpsCap;

  video.quality = mode() === 'target'
    ? new Quality({ bitrate: videoBps ?? targetVideoBps(fullLength), bitrateMode: 'constant' }) // 目標サイズは固定ビットレートで守る
    : new Quality(parseFloat(el.quality.value));

  const abps = audioBps();
  const audio = abps > 0
    ? { codec: state.audioCodec, quality: new Quality({ bitrate: abps }), forceTranscode: true }
    : { discard: true };

  return { trim: { start, end }, video, audio, tracks: 'primary', showWarnings: false };
}

/** 解像度設定（短い辺）を反映した出力サイズ。拡大はしない。 */
function outputSize() {
  const sw = state.width, sh = state.height;
  const shortSide = Math.min(sw, sh);
  const target = parseInt(el.resolution.value, 10);
  const scale = target && shortSide > target ? target / shortSide : 1;
  const floorEven = (n) => Math.max(2, Math.floor(n / 2) * 2);
  return { width: floorEven(sw * scale), height: floorEven(sh * scale) };
}

const DISCARD_REASONS = {
  undecodable_source_codec: 'このブラウザーでは元の動画の形式を読み込めません',
  no_encodable_target_codec: 'このブラウザーでは指定のコーデックで書き出せません',
  unknown_source_codec: '元の動画の形式が不明です',
};

async function convert(start, end, fullLength, target, onProgress, videoBps) {
  const output = new Output({
    format: new Mp4OutputFormat({ fastStart: target instanceof BufferTarget ? 'in-memory' : false }),
    target,
  });
  const conversion = await Conversion.init({
    input: state.input, output, ...conversionOptions(start, end, fullLength, videoBps),
  });

  const lostVideo = conversion.discardedTracks.find((d) => d.track.type === 'video');
  if (!conversion.isValid || lostVideo) {
    const reason = lostVideo ?? conversion.discardedTracks[0];
    throw new Error(DISCARD_REASONS[reason?.reason] ?? `変換できません（${reason?.reason ?? '不明な理由'}）`);
  }
  const lostAudio = conversion.discardedTracks.find((d) => d.track.type === 'audio' && d.reason !== 'discarded_by_user');

  conversion.onProgress = (p) => onProgress(p);
  state.conversion = conversion;
  try {
    await conversion.execute();
  } finally {
    state.conversion = null;
  }
  return { lostAudio: Boolean(lostAudio) };
}

// ---------------- 書き出し ----------------

el.cancelBtn.addEventListener('click', () => state.conversion?.cancel());

el.exportBtn.addEventListener('click', async () => {
  if (!state.file || state.busy) return;
  hideResult();
  const suggestedName = state.file.name.replace(/\.[^.]+$/, '') + '_trim.mp4';

  // Chrome / Edge では保存先へ直接書き込む（大きな動画でもメモリを圧迫しない）
  let handle = null;
  let fileWritable = null;
  let canceled = false;
  let target;
  if ('showSaveFilePicker' in window) {
    try {
      handle = await window.showSaveFilePicker({
        suggestedName,
        types: [{ description: 'MP4 動画', accept: { 'video/mp4': ['.mp4'] } }],
      });
    } catch (err) {
      if (err?.name === 'AbortError') return; // 保存ダイアログでキャンセル
      handle = null;
    }
  }
  const makeTarget = async () => {
    if (!handle) return new BufferTarget();
    const writable = await handle.createWritable(); // 再調整時は前回の内容を破棄して書き直す
    fileWritable = writable;
    canceled = false;
    const proxy = new WritableStream({
      write: (chunk) => writable.write(chunk),
      // 中止したときは書きかけの内容を確定させない
      close: () => (canceled ? writable.abort() : writable.close()),
      abort: (reason) => writable.abort(reason),
    });
    return new StreamTarget(proxy, { chunked: true });
  };

  const length = state.end - state.start;
  const isTarget = mode() === 'target';
  const goalBytes = isTarget ? (parseFloat(el.targetMB.value) || 1) * 1024 * 1024 : 0;
  const MAX_ATTEMPTS = 3;

  setBusy(true);
  const wakeLock = await navigator.wakeLock?.request('screen').catch(() => null);
  const started = performance.now();
  try {
    // 目標サイズモードでは、ブラウザーのエンコーダーが目標を超えた場合に
    // 実測サイズからビットレートを補正して書き直す（デスクトップ版の 2 パスの代わり）
    let bps = isTarget ? targetVideoBps(length) : undefined;
    let target, outBytes, lostAudio;
    for (let attempt = 1; ; attempt++) {
      target = await makeTarget();
      const label = attempt === 1 ? '書き出し中' : `目標サイズに合わせて再調整中（${attempt}/${MAX_ATTEMPTS}）`;
      ({ lostAudio } = await convert(state.start, state.end, length, target, (p) => {
        el.progress.value = p;
        setStatus(`${label}... ${Math.round(p * 100)}%`);
      }, bps));
      outBytes = handle ? (await handle.getFile()).size : target.buffer.byteLength;

      if (!isTarget || outBytes <= goalBytes * 1.05 || attempt >= MAX_ATTEMPTS || bps <= 30_000) break;
      const audioBytes = (audioBps() * length) / 8;
      const factor = (goalBytes * 0.97 - audioBytes) / Math.max(1, outBytes - audioBytes);
      bps = Math.max(30_000, Math.floor(bps * Math.min(0.95, Math.max(0.2, factor))));
    }

    if (!handle) download(new Blob([target.buffer], { type: 'video/mp4' }), suggestedName);

    el.progress.value = 1;
    const secs = ((performance.now() - started) / 1000).toFixed(1);
    setStatus(`完了（${secs} 秒）`);
    const ratio = (outBytes / state.file.size) * 100;
    let msg = `書き出しが完了しました${handle ? `：${handle.name}` : '（ダウンロードフォルダーに保存）'}\n` +
      `元の動画: ${formatSize(state.file.size)}　→　書き出し: ${formatSize(outBytes)}（元の ${ratio.toFixed(1)}%）`;
    if (lostAudio) msg += '\n※ 音声はこのブラウザーで変換できなかったため含まれていません。';
    if (isTarget && outBytes > goalBytes * 1.05) {
      msg += `\n※ 目標の ${formatSize(goalBytes)} まで小さくできませんでした。解像度やフレームレートを下げてください。`;
    }
    if (outBytes >= state.file.size) msg += '\n※ 元の動画より大きくなりました。画質を下げるか、解像度を下げてください。';
    showResult(msg, outBytes >= state.file.size ? 'error' : 'ok');
  } catch (err) {
    canceled = true;
    try { await fileWritable?.abort(); } catch { /* 既に閉じている */ }
    el.progress.value = 0;
    if (err instanceof ConversionCanceledError) {
      setStatus('中止しました');
    } else {
      setStatus('失敗しました');
      showResult('書き出しに失敗しました。\n' + (err?.message ?? err), 'error');
    }
  } finally {
    wakeLock?.release?.().catch(() => {});
    setBusy(false);
  }
});

function download(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

// ---------------- 圧縮プレビュー ----------------

el.sampleBtn.addEventListener('click', async () => {
  if (!state.file || state.busy) return;
  hideResult();
  const start = state.start;
  const end = Math.min(state.end, start + SAMPLE_SECONDS);
  const target = new BufferTarget();

  setBusy(true);
  try {
    await convert(start, end, state.end - state.start, target, (p) => {
      el.progress.value = p;
      setStatus(`圧縮プレビュー作成中... ${Math.round(p * 100)}%`);
    });
    el.progress.value = 1;
    setStatus('圧縮プレビュー作成完了');

    const blob = new Blob([target.buffer], { type: 'video/mp4' });
    const fullLength = state.end - state.start;
    const estimate = mode() === 'target'
      ? (parseFloat(el.targetMB.value) || 0) * 1024 * 1024
      : blob.size * (fullLength / Math.max(0.1, end - start));

    if (state.sampleUrl) URL.revokeObjectURL(state.sampleUrl);
    state.sampleUrl = URL.createObjectURL(blob);
    el.sampleVideo.src = state.sampleUrl;
    el.sampleInfo.textContent =
      `${(end - start).toFixed(1)} 秒で ${formatSize(blob.size)}　→　全体（${formatTime(fullLength)}）の推定 約 ${formatSize(estimate)}`;
    el.sampleDialog.showModal();
    el.sampleVideo.play().catch(() => {});
  } catch (err) {
    el.progress.value = 0;
    if (err instanceof ConversionCanceledError) setStatus('中止しました');
    else { setStatus('失敗しました'); showResult('圧縮プレビューを作れませんでした。\n' + (err?.message ?? err), 'error'); }
  } finally {
    setBusy(false);
  }
});

el.sampleDialog.addEventListener('close', () => el.sampleVideo.pause());

el.sampleVideo.addEventListener('error', () => {
  if (el.sampleVideo.getAttribute('src')) {
    el.sampleInfo.textContent += '\nこの圧縮プレビューはこのブラウザーで再生できません（H.265 の場合は H.264 にすると確認できます）。';
  }
});

// ---------------- 状態表示 ----------------

function setBusy(busy) {
  state.busy = busy;
  el.cancelBtn.disabled = !busy;
  for (const c of [el.exportBtn, el.sampleBtn, el.change, el.codec, el.resolution, el.fps, el.audio,
    ...document.querySelectorAll('input[name="mode"]')]) c.disabled = busy;
  el.quality.disabled = busy || mode() !== 'quality';
  el.targetMB.disabled = busy || mode() !== 'target';
  if (busy) el.progress.value = 0;
  updateButtons();
}

function updateButtons() {
  const loaded = Boolean(state.file);
  const canPlay = loaded && state.videoPlayable;
  el.playRange.disabled = !canPlay;
  el.stop.disabled = !canPlay;
  el.setStart.disabled = !canPlay || state.busy;
  el.setEnd.disabled = !canPlay || state.busy;
  el.exportBtn.disabled = !loaded || !state.canDecode || state.busy;
  el.sampleBtn.disabled = !loaded || !state.canDecode || state.busy;
}

function setStatus(text) { el.status.textContent = text; }

function showResult(text, kind) {
  el.result.hidden = false;
  el.result.className = 'notice' + (kind ? ` ${kind}` : '');
  el.result.textContent = text;
}

function hideResult() { el.result.hidden = true; }

window.addEventListener('beforeunload', (e) => {
  if (state.busy) e.preventDefault();
});
