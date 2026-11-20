const fs = require('node:fs')
const { loadDataset } = require('./New-WordReview.cjs')

function score(dataset, review) {
  if (review?.version !== 1 || review.batchId !== dataset.batchId)
    throw new Error('Разметка относится к другой записи или другой версии измерений.')
  if (!Array.isArray(review.rows) || review.rows.length !== dataset.targets.length)
    throw new Error('Число строк отличается от исходного набора заданий.')
  const rows = new Map(review.rows.map(row => [row?.cue, row]))
  const thirds = [0, 0, 0]
  const pairs = []
  let pending = 0, skipped = 0
  for (const target of dataset.targets) {
    const row = rows.get(target.cue)
    if (!row || !['marked', 'pending', 'skipped'].includes(row.status))
      throw new Error('Проверьте номера заданий и состояния строк в разметке.')
    if (row.status !== 'marked') {
      if (row.onsetMs !== null) throw new Error('У пропущенного задания не должно быть метки.')
      if (row.status === 'pending') pending++
      else skipped++
      continue
    }
    if (!Number.isSafeInteger(row.onsetMs) || row.onsetMs < target.startMs || row.onsetMs >= target.endMs)
      throw new Error('Метка выходит за пределы отрывка для задания ' + row.cue + '.')
    const third = Math.min(2, Math.floor(row.onsetMs * 3 / dataset.durationMs))
    thirds[third]++
    pairs.push({ cue: row.cue, word: target.word, manualMs: row.onsetMs, asrMs: target.asrMs,
      errorMs: target.asrMs - row.onsetMs })
  }
  const errors = pairs.map(pair => Math.abs(pair.errorMs)).sort((a,b) => a-b)
  const within = errors.filter(ms => ms <= 250).length
  const n = errors.length
  return {
    version: 1, batchId: dataset.batchId, targets: dataset.targets.length,
    marked: n, skipped, pending, markedByThird: thirds, within250ms: within,
    percentWithin250ms: n ? 100 * within / n : null,
    medianAbsoluteMs: n ? (errors[Math.floor((n-1)/2)] + errors[Math.floor(n/2)]) / 2 : null,
    maximumAbsoluteMs: n ? errors[n-1] : null,
    // Report only the measured subset. A draft never changes the product's timing mode.
    meetsSampleGoal: n >= 20 && thirds.filter(count => count > 0).length >= 2 ? within * 10 >= n * 9 : null,
    pairs
  }
}

async function main() {
  const [sample, srt, manual, output] = process.argv.slice(2)
  if (!output || process.argv.length !== 6)
    throw new Error('Usage: node scripts/Score-WordReview.cjs <sampleDir> <source.srt> <manual.json> <new-result.json>')
  const dataset = await loadDataset(sample, srt)
  const report = score(dataset, JSON.parse(fs.readFileSync(manual, 'utf8')))
  fs.writeFileSync(output, JSON.stringify(report, null, 2) + '\n', { flag: 'wx' })
  console.log('Отмечено %d/%d, пропущено %d, ожидает %d. В пределах ±250 мс: %d/%d.',
    report.marked, report.targets, report.skipped, report.pending, report.within250ms, report.marked)
}

if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1 })
module.exports = { score }
