// Compare published Praat word intervals with whisper.cpp word-mode JSON.
// All pairing decisions use text only. Timing is used after the pairs are fixed.
const fs = require('node:fs')
const [gridPath, tierName, asrPath, outputPath, startText, durationText] = process.argv.slice(2)
const startMs = Number(startText)
const durationMs = Number(durationText)
if (!outputPath || !Number.isSafeInteger(startMs) || startMs < 0 ||
    !Number.isSafeInteger(durationMs) || durationMs <= 0)
  throw new Error('Usage: node scripts/Measure-AcousticGold.cjs input.TextGrid tier words.json output.json source-start-ms duration-ms')

const tiers = fs.readFileSync(gridPath, 'utf8').split(/^\s*item \[\d+\]:\s*$/m)
const tier = tiers.find(block => block.match(/^\s*name = "([^"]+)"\s*$/m)?.[1] === tierName)
if (!tier) throw new Error('Missing reference tier: ' + tierName)
const interval = /intervals \[(\d+)\]:\s*xmin = ([\d.]+)\s*xmax = ([\d.]+)\s*text = "((?:[^"]|"")*)"/g
function wordKey(text) {
  // Exclude tags, multiple words, digits and compounds that require token splitting.
  const word = text.trim().toLowerCase().replaceAll('’', "'").replace(/^[.,!?:;"“”]+|[.,!?:;"“”]+$/g, '')
  return /^\p{L}+(?:'\p{L}+)*$/u.test(word) ? word : ''
}
const reference = []
for (const match of tier.matchAll(interval)) {
  const fromMs = Math.round(Number(match[2]) * 1000) - startMs
  const toMs = Math.round(Number(match[3]) * 1000) - startMs
  const key = wordKey(match[4])
  if (key && fromMs >= 0 && toMs <= durationMs)
    reference.push({key, interval: Number(match[1]), fromMs, toMs})
}
const recognition = []
for (const [row, entry] of JSON.parse(fs.readFileSync(asrPath, 'utf8')).transcription.entries()) {
  const key = wordKey(entry.text)
  if (key) {
    if (!Number.isSafeInteger(entry.offsets.from) || !Number.isSafeInteger(entry.offsets.to))
      throw new Error('Invalid offsets in recognition row ' + row)
    recognition.push({key, row, fromMs: entry.offsets.from, toMs: entry.offsets.to})
  }
}
function indexByKey(words) {
  const index = new Map()
  words.forEach(({key}, i) => {
    if (!index.has(key)) index.set(key, [])
    index.get(key).push(i)
  })
  return index
}
const referenceIndex = indexByKey(reference)
const recognitionIndex = indexByKey(recognition)
const pairs = []
const unmatched = []
for (const [i, word] of reference.entries()) {
  if (word.key.replaceAll("'", '').length < 4 || referenceIndex.get(word.key).length !== 1) continue
  const candidates = recognitionIndex.get(word.key) ?? []
  const candidate = candidates[0]
  const confirmed = candidates.length === 1 && [-1, 1].some(delta =>
    reference[i + delta] && reference[i + delta].key === recognition[candidate + delta]?.key)
  if (!confirmed) {
    const reason = !candidates.length ? 'absent' : candidates.length > 1 ? 'repeated' : 'context'
    unmatched.push({...word, reason})
    continue
  }
  const asr = recognition[candidate]
  pairs.push({...word, asrRow: asr.row, asrFromMs: asr.fromMs, asrToMs: asr.toMs,
    onsetErrorMs: asr.fromMs - word.fromMs, endErrorMs: asr.toMs - word.toMs})
}
function summarize(errors) {
  if (!errors.length) return {paired: 0, within250ms: 0}
  const absolute = errors.map(Math.abs).sort((a, b) => a - b)
  const middle = (errors.length - 1) / 2
  return {paired: errors.length, within250ms: absolute.filter(x => x <= 250).length,
    medianAbsoluteMs: (absolute[Math.floor(middle)] + absolute[Math.ceil(middle)]) / 2,
    minSignedMs: Math.min(...errors), maxSignedMs: Math.max(...errors)}
}
const thirds = [0, 1, 2].map(n => {
  const lo = n * durationMs / 3
  const hi = (n + 1) * durationMs / 3
  const subset = pairs.filter(p => p.fromMs >= lo && p.fromMs < hi)
  return {startMs: lo, endMs: hi, ...summarize(subset.map(p => p.onsetErrorMs))}
})
const summary = {tierName, startMs, durationMs, eligible: pairs.length + unmatched.length,
  onsets: summarize(pairs.map(p => p.onsetErrorMs)), ends: summarize(pairs.map(p => p.endErrorMs)), thirds}
fs.writeFileSync(outputPath, JSON.stringify({...summary, pairs, unmatched}, null, 2) + '\n', {flag: 'wx'})
console.log(JSON.stringify(summary, null, 2))
