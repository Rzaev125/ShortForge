// Independent acceptance: decode the files produced by the WPF batch and play them in Edge's local media player.
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const { execFileSync } = require('node:child_process')
const { pathToFileURL } = require('node:url')
const { createRequire } = require('node:module')
const { chromium } = createRequire(path.join(__dirname, '../src/ShortForge.Ui/package.json'))('playwright')
const bin = process.env.SHORTFORGE_FFMPEG_BIN
const report = {}

function media(tool, args) {
  return execFileSync(path.join(bin, tool + '.exe'), args, { windowsHide: true, maxBuffer: 16 * 1024 * 1024 })
}
function files(dir, ext) {
  return fs.readdirSync(dir, { recursive: true, withFileTypes: true })
    .filter(f => f.isFile() && f.name.endsWith(ext)).map(f => path.join(f.parentPath, f.name))
}
function probe(file) {
  return JSON.parse(media('ffprobe', ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', file]))
}
function fastStart(file) {
  const fd = fs.openSync(file, 'r'), size = fs.fstatSync(fd).size, header = Buffer.alloc(16)
  try {
    for (let offset = 0; offset + 8 <= size;) {
      fs.readSync(fd, header, 0, 16, offset)
      const tag = header.toString('ascii', 4, 8)
      if (tag === 'moov') return true
      if (tag === 'mdat') return false
      const len = header.readUInt32BE(0)
      const length = len === 1 ? Number(header.readBigUInt64BE(8)) : len
      if (length < 8 || offset + length > size) break
      offset += length
    }
    return false
  } finally { fs.closeSync(fd) }
}
function checkStreams(file, duration) {
  const info = probe(file)
  const v = info.streams.find(s => s.codec_type === 'video'), a = info.streams.find(s => s.codec_type === 'audio')
  assert.equal(info.streams.length, 2)
  assert.deepEqual([v.codec_name, v.width, v.height, v.pix_fmt, v.sample_aspect_ratio, v.avg_frame_rate, v.r_frame_rate],
    ['h264', 1080, 1920, 'yuv420p', '1:1', '30/1', '30/1'])
  assert.equal(a.codec_name, 'aac')
  assert(info.format.format_name.split(',').includes('mp4'))
  assert(fastStart(file), 'moov must precede mdat')
  for (const stream of [v, a, info.format]) assert(Math.abs(Number(stream.duration) - duration) <= .05)
  return info
}
function srtStamp(us) {
  return new Date(Math.floor(us / 1000)).toISOString().slice(11, 23).replace('.', ',')
}
function verifyCaptions(file, clip) {
  const sidecars = files(path.dirname(file), '.srt'), manifests = files(path.dirname(file), '.json')
  assert.equal(sidecars.length, 1); assert.equal(manifests.length, 1)
  const srt = fs.readFileSync(sidecars[0], 'utf8').replace(/^\uFEFF/, '').replaceAll('\r\n', '\n').trim()
    .replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&#123;', '{').replaceAll('&#125;', '}').replaceAll('&amp;', '&')
  const expected = clip.cues.map((c, i) => [i + 1, srtStamp(c.startUs) + ' --> ' + srtStamp(c.endUs), c.text].join('\n')).join('\n\n')
  assert.equal(srt, expected)
  const manifest = JSON.parse(fs.readFileSync(manifests[0], 'utf8').replace(/^\uFEFF/, ''))
  assert.equal(manifest.title, clip.title)
  assert.deepEqual([manifest.sourceStartUs, manifest.sourceEndUs, manifest.fps, manifest.encoder, manifest.normalizedAudio],
    [clip.startUs, clip.endUs, 30, 'libx264', true])
  assert.equal(manifest.video, path.basename(file))
  assert.equal(manifest.subtitles, path.basename(sidecars[0]))
  function green(time) {
    const pixels = media('ffmpeg', ['-v', 'error', '-nostdin', '-ss', time, '-i', file, '-frames:v', '1',
      '-an', '-vf', 'scale=180:320', '-pix_fmt', 'rgb24', '-f', 'rawvideo', 'pipe:1'])
    assert.equal(pixels.length, 180 * 320 * 3)
    let count = 0
    for (let i = 180 * 200 * 3; i < pixels.length; i += 3)
      if (pixels[i + 1] > 110 && pixels[i + 1] > 55 + Math.max(pixels[i], pixels[i + 2])) count++
    return count
  }
  const before = green('0.100'), during = green('0.600'), after = green('1.400')
  assert(during > Math.max(before, after) + 20, 'Green caption was not present only in its saved interval')
  return { beforePixels: before, duringPixels: during, afterPixels: after, cues: clip.cues.length }
}
async function playInEdge(browser, file) {
  const page = await browser.newPage()
  try {
    await page.route(/^https?:/, route => route.abort())
    await page.goto(pathToFileURL(file).href)
    await page.waitForSelector('video')
    const playback = await page.evaluate(async () => {
      const video = document.querySelector('video')
      video.pause(); video.currentTime = 0; video.muted = false
      let frames = 0
      function onFrame() { frames++; if (!video.ended) video.requestVideoFrameCallback(onFrame) }
      video.requestVideoFrameCallback(onFrame)
      const ended = new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('Local media player stalled')), 25000)
        video.addEventListener('ended', () => {clearTimeout(timer); resolve()}, { once: true })
        video.addEventListener('error', () => reject(new Error('Local media player failed')), { once: true })
      })
      await video.play(); await ended
      return { duration: video.duration, end: video.currentTime, frames, audioBytes: video.webkitAudioDecodedByteCount }
    })
    assert(playback.frames >= 3 && Math.abs(playback.end - playback.duration) < .1 && playback.audioBytes > 0)
    return playback
  } finally { await page.close() }
}
function pulseTimes(file, isProxy = false) {
  const decode = args => media('ffmpeg', ['-nostdin', '-v', 'error', '-i', file, ...args, 'pipe:1'])
  const gray = decode(['-an', '-vf', 'scale=1:1', '-pix_fmt', 'gray', '-f', 'rawvideo'])
  const pcm = decode(['-vn', '-ac', '1', '-ar', '8000', '-f', 's16le'])
  const video = [], audio = []
  for (let i = 0; i < gray.length; i++)
    if (gray[i] > 180 && (i === 0 || gray[i - 1] <= 180)) video.push(i / 30)
  for (let i = 0; i < pcm.length / 2; i++)
    if (Math.abs(pcm.readInt16LE(i * 2)) > 350 && (!audio.length || i / 8000 - audio.at(-1) > .3))
      audio.push(i / 8000)
  assert.equal(video.length, 3)
  assert.equal(audio.length, isProxy ? 0 : 3)
  return { video, audio }
}
async function renderSyncEvidence(file, output, browser) {
  const gray = media('ffmpeg', ['-v','error','-i',file,'-an','-vf','scale=1:1','-pix_fmt','gray','-f','rawvideo','pipe:1'])
  const pcm = media('ffmpeg', ['-v','error','-i',file,'-vn','-ac','1','-ar','48000','-f','s16le','pipe:1'])
  const panels = [0.5, 2.5, 4.5].map((start, index) => {
    const left = 20 + index * 305, top = 60, width = 270, duration = 0.3
    const frames = Array.from({length: 9}, (_, column) => {
      const luma = gray[Math.round(start * 30) + column]
      return `<rect x="${left + column * 30}" y="${top}" width="30" height="30" fill="rgb(${luma},${luma},${luma})"/>`
    })
    const wave = Array.from({length: width}, (_, col) => {
      const first = Math.round(48000 * (start + col * duration / width))
      const last = Math.round(48000 * (start + (col + 1) * duration / width))
      let min = 0, max = 0
      for (let sample = first; sample < last; sample++) {
        const value = pcm.readInt16LE(2 * sample) / 32768
        min = Math.min(min, value); max = Math.max(max, value)
      }
      return `M${left + col},${124 - 45 * max}V${124 - 45 * min}`
    }).join('')
    const ticks = [0,1,2,3].map(tick => `<path d="M${left + tick*90},90v64" stroke="#9aa3ad" stroke-dasharray="2 3"/>
      <text x="${left + tick*90}" y="178" text-anchor="middle">${(start + tick/10).toFixed(1)}</text>`)
    return [...frames, ...ticks, `<path d="${wave}" stroke="#19679b"/>`].join('')
  })
  const page = await browser.newPage({viewport:{width:930,height:205},deviceScaleFactor:1})
  try {
    await page.setContent(`<style>*{margin:0} text{font:15px 'Segoe UI',sans-serif;fill:#16283a}</style>
      <svg width="930" height="205" xmlns="http://www.w3.org/2000/svg"><rect width="100%" height="100%" fill="white"/>
      <text x="20" y="29">Яркость каждого кадра и звуковая волна · время от начала экспортированного клипа, с</text>
      ${panels.join('')}</svg>`)
    await page.screenshot({path:path.join(output,'av-sync.png')})
  } finally { await page.close() }
}
async function main() {
  const [output, sync, syntheticSource] = process.argv.slice(2)
  const expected = JSON.parse(fs.readFileSync(path.join(output, 'expected.json')))
  const mp4s = files(path.join(output, "Экспорт O'Brien"), '.mp4')
  assert.equal(mp4s.length, 3)
  const browser = await chromium.launch({channel: 'msedge', headless: true, args: ['--autoplay-policy=no-user-gesture-required']})
  try {
    const syncVideo = files(path.join(sync, "Экспорт O'Brien"), '.mp4')
    assert.equal(syncVideo.length, 1)
    await renderSyncEvidence(syncVideo[0], output, browser)
    report.player = 'Microsoft Edge ' + browser.version()
    report.clips = []
    for (const clip of expected) {
      const duration = (clip.endUs - clip.startUs) / 1e6
      const matches = mp4s.filter(file => Math.abs(Number(probe(file).format.duration) - duration) < .05)
      assert.equal(matches.length, 1)
      const file = matches[0], info = checkStreams(file, duration)
      fs.writeFileSync(path.join(output, 'ffprobe-' + (report.clips.length + 1) + '.json'), JSON.stringify(info, null, 2))
      report.clips.push({ title: clip.title, file: path.relative(output, file), duration, captions: verifyCaptions(file, clip),
        playback: await playInEdge(browser, file) })
    }
  } finally { await browser.close() }
  const synced = files(path.join(sync, "Экспорт O'Brien"), '.mp4'), proxies = files(path.join(sync, 'data', 'proxies'), '.mp4')
  assert.equal(synced.length, 1); assert.equal(proxies.length, 1)
  checkStreams(synced[0], 5.2)
  const original = pulseTimes(syntheticSource), exported = pulseTimes(synced[0]), proxy = pulseTimes(proxies[0], true)
  for (let i = 0; i < 3; i++) {
    assert(Math.abs(original.video[i] - original.audio[i]) < .035)
    assert(Math.abs(exported.video[i] - exported.audio[i]) < .035)
    assert(Math.abs(exported.video[i] + .4 - original.video[i]) < .035)
    assert(Math.abs(exported.audio[i] + .4 - original.audio[i]) < .035)
  }
  report.sync = {original, exported, proxy, clipIn: .4, maxAvDeltaMs: Math.max(...exported.video.map((v, i) => Math.abs(v - exported.audio[i]) * 1000))}
  fs.writeFileSync(path.join(output, 'decoded.json'), JSON.stringify(report, null, 2))
  console.log('PASS external local Edge playback to end, audio/video decoding, three saved edits, ffprobe streams/faststart, timed captions, original-source sync markers.', JSON.stringify(report))
}
main().catch(error => { console.error(error); process.exitCode = 1 })
