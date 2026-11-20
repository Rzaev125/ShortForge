const fs = require('node:fs')
const path = require('node:path')
const { createHash, randomBytes } = require('node:crypto')

const digest = bytes => createHash('sha256').update(bytes).digest()
const firstWord = text => text.toLowerCase().replace(/[’']/g, '').match(/[\p{L}\p{N}]+/u)?.[0]

function readCues(srt) {
  const cues = new Map()
  for (const block of srt.trim().replace(/\r/g, '').split(/\n\s*\n/)) {
    const [number, times, ...body] = block.split('\n')
    const position = /^(\d{2}):(\d{2}):(\d{2}),(\d{3}) --> /.exec(times)
    const cue = Number(number)
    if (!position || !Number.isSafeInteger(cue) || cue <= 0 || cues.has(cue) || !body.length)
      throw new Error('Invalid SRT cue')
    const [, hours, minutes, seconds, ms] = position.map(Number)
    cues.set(cue, {
      time: ((hours * 60 + minutes) * 60 + seconds) * 1000 + ms,
      context: body.join(' ').trim()
    })
  }
  return cues
}

// The audit uses a bounded two-minute mono PCM sample, never the source video.
function pcmData(wav) {
  if (wav.toString('ascii', 0, 4) !== 'RIFF' || wav.toString('ascii', 8, 12) !== 'WAVE' ||
      wav.readUInt32LE(4) + 8 !== wav.length) throw new Error('Invalid RIFF/WAVE file')
  let formatOK = false, data
  for (let pos = 12; pos + 8 <= wav.length;) {
    const size = wav.readUInt32LE(pos + 4), offset = pos + 8
    if (offset + size > wav.length) throw new Error('Truncated WAV chunk')
    const type = wav.toString('ascii', pos, pos + 4)
    if (type === 'fmt ') {
      formatOK = size >= 16 && wav.readUInt16LE(offset) === 1 && wav.readUInt16LE(offset + 2) === 1 &&
        wav.readUInt32LE(offset + 4) === 16000 && wav.readUInt32LE(offset + 8) === 32000 &&
        wav.readUInt16LE(offset + 12) === 2 && wav.readUInt16LE(offset + 14) === 16
    }
    if (type === 'data') {
      if (data || size === 0 || size % 2 !== 0) throw new Error('Invalid PCM data')
      data = wav.subarray(offset, offset + size)
    }
    pos = offset + size + size % 2
  }
  if (!formatOK || !data) throw new Error('Expected mono 16-bit PCM at 16000 Hz')
  return data
}

function loadDataset(sampleDir, srtPath) {
  const wavPath = path.join(sampleDir, 'audio.wav')
  if (fs.statSync(wavPath).size > 32 * 1024 * 1024) throw new Error('Use a short review sample')
  const files = [wavPath, path.join(sampleDir, 'comparison.json'), path.join(sampleDir, 'words.json'), srtPath]
    .map(file => fs.readFileSync(file))
  const [wav, comparisons, recognition, subtitles] = files
  const batchId = digest(Buffer.concat([Buffer.from('ShortForge acoustic word review v1\0'), ...files.map(digest)])).toString('hex')
  const pcm = pcmData(wav)
  const { cutMs, durationMs, pairs, paired } = JSON.parse(comparisons.toString('utf8'))
  const { transcription } = JSON.parse(recognition.toString('utf8'))
  const cues = readCues(subtitles.toString('utf8'))
  if (!Number.isSafeInteger(cutMs) || cutMs < 0 || !Number.isSafeInteger(durationMs) || durationMs <= 0 ||
      pcm.length !== durationMs * 32 || !Array.isArray(transcription) || !Array.isArray(pairs) ||
      !pairs.length || pairs.length !== paired) throw new Error('Inconsistent review dataset')

  const seenCues = new Set(), seenRows = new Set()
  const targets = pairs.map(({ cue, recognitionRow, startWord, referenceMs, asrMs }) => {
    const subtitle = cues.get(cue), asr = transcription[recognitionRow]
    if (seenCues.has(cue) || seenRows.has(recognitionRow) || !Number.isSafeInteger(recognitionRow) ||
        !asr || !subtitle || typeof startWord !== 'string' || !startWord ||
        startWord !== firstWord(subtitle.context) || startWord !== firstWord(asr.text) ||
        !Number.isSafeInteger(asrMs) || asrMs < 0 || asrMs >= durationMs || asr.offsets.from !== asrMs ||
        referenceMs !== subtitle.time - cutMs || referenceMs < 0 || referenceMs >= durationMs)
      throw new Error('Inconsistent target for cue ' + cue)
    seenCues.add(cue)
    seenRows.add(recognitionRow)
    // Vary each margin independently so the middle of the excerpt is not a timing hint.
    const jitter = digest(batchId + '/' + cue)
    const startMs = Math.max(0, Math.min(referenceMs, asrMs) - 1200 - jitter.readUInt16LE(0) % 2900)
    const endMs = Math.min(durationMs, Math.max(referenceMs, asrMs) + 1800 + jitter.readUInt16LE(2) % 2700)
    return { cue, word: startWord, context: subtitle.context, startMs, endMs, asrMs, referenceMs }
  })
  return { batchId, durationMs, targets, pcm }
}

function extract(pcm) {
  const header = Buffer.alloc(44)
  header.write('RIFF'); header.writeUInt32LE(36 + pcm.length, 4); header.write('WAVEfmt ', 8)
  header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22)
  header.writeUInt32LE(16000, 24); header.writeUInt32LE(32000, 28)
  header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34)
  header.write('data', 36); header.writeUInt32LE(pcm.length, 40)
  return Buffer.concat([header, pcm])
}

// Encode the maximum absolute amplitude of each 2 ms interval (32 signed 16-bit samples).
function envelope(pcm) {
  const bins = Buffer.alloc(Math.ceil(pcm.length / 64))
  for (let i = 0; i < bins.length; i++) {
    let peak = 0
    for (let offset = i * 64; offset < Math.min((i + 1) * 64, pcm.length); offset += 2)
      peak = Math.max(peak, Math.abs(pcm.readInt16LE(offset)))
    bins[i] = Math.round(255 * peak / 32768)
  }
  return bins.toString('base64')
}

function build(sampleDir, srtPath, destination) {
  const { batchId, targets, pcm } = loadDataset(sampleDir, srtPath)
  const template = fs.readFileSync(path.join(__dirname, 'word-review.html'), 'utf8')
  fs.mkdirSync(destination) // Refuse existing directories, including earlier reviews.
  const blind = targets.map(({ cue, word, context, startMs, endMs }) => {
    const samples = pcm.subarray(startMs * 32, endMs * 32)
    fs.writeFileSync(path.join(destination, cue + '.wav'), extract(samples), { flag: 'wx' })
    return { cue, word, context, startMs, endMs, peaks: envelope(samples) }
  })
  const json = JSON.stringify({ batchId, targets: blind }).replace(/[&<>]/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c])
  const html = template.replaceAll('__NONCE__', randomBytes(16).toString('hex')).replace('__BATCH__', () => json)
  fs.writeFileSync(path.join(destination, 'index.html'), html, { flag: 'wx' })
  console.log('%d excerpts · %s · %s', targets.length, batchId, path.join(destination, 'index.html'))
}

module.exports = { loadDataset }
if (require.main === module) {
  if (process.argv.length !== 5) throw new Error('Usage: node scripts/New-WordReview.cjs sample-directory source.srt new-output-directory')
  build(...process.argv.slice(2))
}
