// Regression test for the eight-interval NASA review sheet. Its temporary Edge profile is discarded.
const assert = require('node:assert/strict')
const fs = require('node:fs/promises')
const path = require('node:path')
const { pathToFileURL } = require('node:url')
const { createRequire } = require('node:module')
const uiRequire = createRequire(path.join(__dirname, '../src/ShortForge.Ui/package.json'))
const { chromium } = uiRequire('playwright')

async function run() {
  const sheet = process.argv[2]
  if (!sheet) throw new Error('Pass the path of the generated NASA listening sheet.')
  const browser = await chromium.launch({ channel: 'msedge', headless: true })
  try {
    const page = await browser.newPage({ viewport: {width:1024,height:640}, deviceScaleFactor:1.25 })
    const requests = new Set()
    const errors = []
    page.on('request', request => requests.add(request.url()))
    page.on('pageerror', error => errors.push(error.message))
    await page.goto(pathToFileURL(path.resolve(sheet)).href)
    const words = page.getByLabel('Оба граничных слова целы на слух')
    const save = page.getByRole('button', {name:'Сохранить итог в JSON'})
    const scores = page.locator('#scores')
    assert.equal(await words.count(),8)
    assert.equal(await save.isDisabled(),true)
    assert.equal(await page.locator('details').count(),9)
    assert.match(await scores.innerText(),/Qwen — оценено 0 из 4/)
    for (const audio of await page.locator('audio').all()) {
      await audio.evaluate(async player => {
        await player.play()
        if (Math.abs(player.duration-6.5) > .02 || !Number.isFinite(player.duration)) throw new Error('Wrong edge audio duration')
        player.pause()
      })
    }

    async function exported() {
      const downloading = page.waitForEvent('download')
      await save.click()
      const file = await downloading
      assert.match(file.suggestedFilename(),/^shortforge-p04-[0-9a-f]{12}\.json$/)
      const result = JSON.parse(await fs.readFile(await file.path(),'utf8'))
      await file.delete()
      return result
    }

    for (let i=0;i<7;i++) await words.nth(i).selectOption('1')
    assert.equal(await save.isDisabled(),true)
    await page.reload()
    assert.equal(await words.nth(0).inputValue(),'1')
    assert.equal(await words.nth(7).inputValue(),'')
    await words.nth(7).selectOption('1')
    const positive = await exported()
    assert.equal(positive.complete,true)
    assert.equal(positive.thresholdMet,true)
    assert.equal(positive.summary.semantic.passed,3)
    assert.equal(positive.summary.semantic.total,4)
    assert.equal(positive.summary.structure.passed,2)
    assert.equal(positive.summary.structure.total,5)
    assert.equal(positive.rows.length,9)
    assert.equal(positive.films.length,3)
    assert.equal(positive.rows.every(r=>r.usefulness && r.sourceSha256 && r.startUs<r.endUs),true)

    await words.nth(2).selectOption('0')
    const negative = await exported()
    assert.equal(negative.thresholdMet,false)
    assert.equal(negative.summary.semantic.percent,50)
    assert.equal(negative.summary.structure.percent,20)
    const shared = negative.rows.filter(r=>r.number===3)
    assert.equal(shared.length,2)
    assert.equal(shared.every(r=>r.marks.words===0 && !r.passed),true)

    const details = page.locator('details').first()
    await details.locator('summary').click()
    const note = details.getByLabel('Полезность или интересность')
    await note.fill('')
    assert.equal(await save.isDisabled(),true)
    await page.reload()
    await page.locator('details').first().locator('summary').click()
    assert.equal(await note.inputValue(),'')
    await note.fill('Редакторская проверка')
    const independence = details.getByLabel('Самостоятельность')
    await independence.selectOption('')
    assert.equal(await save.isDisabled(),true)
    await independence.selectOption('1')
    assert.equal(await save.isDisabled(),false)
    assert.deepEqual(errors,[])
    assert.equal([...requests].every(url=>url.startsWith('file:')),true)
    console.log('PASS: eight local WAVs, nine proposals, draft reload, missing grades, shared boundaries, 75% and 50% Qwen, JSON export; zero external requests.')
  } finally { await browser.close() }
}

run().catch(error=>{console.error(error);process.exitCode=1})
