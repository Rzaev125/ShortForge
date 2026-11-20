import { expect, test } from '@playwright/test'

const project = {
  id: 'test-project', name: "Обсуждение проекта O'Brien с очень длинным названием ".repeat(4),
  durationUs: 125_000_000, updatedUtc: '2026-11-18T09:00:00Z', width: 1280, height: 720,
  rotation: 0, audioTracks: 0, audioTrackLabels: [], offlineAudioTracks: [], proxyAudioTrack: 0, fps: '25/1', videoCodec: 'h264', container: 'matroska',
  sourceAvailable: true, usingProxy: false, proxyId: null, proxyNeedsRefresh: false, previewUrl: null, thumbnailUrl: null, color: null, anamorphicAspect: null, frameIntervals: null, transcript: null,
}

for (const [width, height] of [[1280, 800], [1440, 900]]) {
  for (const scale of [1.25, 1.5]) {
    test.describe(width + ' × ' + height + ' / ' + scale, () => {
      test.use({ viewport: { width: Math.floor(width / scale), height: Math.floor(height / scale) }, deviceScaleFactor: scale })
      test('сохранённые фразы и выбор дорожки доступны с клавиатуры', async ({ page }, info) => {
        const saved = { ...project, audioTracks: 2, audioTrackLabels: ['Original', 'Русский'],
          transcript: { model: 'small', language: 'ru', detectedLanguage: 'ru', audioTrack: 1,
            elapsedMs: 1000, peakRamBytes: 1000, segments: [{ startUs: 100000, endUs: 900000, text: 'Фраза <b>как текст</b>.' }] } }
        // Render an existing record to test the layout. Real recognition runs in Test-Asr.ps1.
        await page.addInitScript(saved => {
          const port = new EventTarget()
          Object.assign(window.chrome ??= {}, { webview: Object.assign(port, {
            postMessage({ type, id }: { type: string; id: string }) {
              const result = type === 'projects.list' ? [saved] : saved
              queueMicrotask(() => port.dispatchEvent(new MessageEvent('message', { data: { version: 1, id, ok: true, result } })))
            },
          }) })
        }, saved)
        await page.goto('/')
        await page.getByRole('button', { name: /Обсуждение проекта/ }).click()
        const language = page.getByLabel('Язык речи')
        await language.focus()
        await page.keyboard.press('Tab')
        const tracks = page.getByLabel('Дорожка для распознавания')
        await expect(tracks).toBeFocused()
        await expect(tracks).toHaveValue('1')
        await page.keyboard.press('Tab')
        await expect(page.getByRole('button', { name: 'Распознать речь' })).toBeFocused()
        await expect(page.getByText('Фраза <b>как текст</b>.', { exact: false })).toBeVisible()
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
        await page.screenshot({ path: info.outputPath('transcript.png') })
        const mode = page.getByLabel('Режим подбора')
        await mode.selectOption('semantic')
        await mode.focus()
        await page.keyboard.press('Tab')
        const min = page.getByRole('spinbutton', { name: 'От, с' })
        await expect(min).toBeFocused()
        await min.fill('120')
        await expect(page.getByRole('button', { name:'Найти фрагменты' })).toBeDisabled()
        await min.fill('15')
        await expect(page.getByRole('button', { name:'Найти фрагменты' })).toBeEnabled()
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
        await page.screenshot({ path: info.outputPath('clip-settings.png') })
      })
      test('пустая библиотека, навигация, проект и ошибка прокси', async ({ page }, info) => {
        // Test-only host fixture; production assets contain no example projects.
        await page.addInitScript(({ project }) => {
          const port = new EventTarget()
          const reply = (data: unknown) => port.dispatchEvent(new MessageEvent('message', { data }))
          Object.assign(window.chrome ??= {}, { webview: Object.assign(port, {
            postMessage({ type, id, payload }: { type: string; id: string; payload: { jobId?: string } }) {
              const settings = { ffmpeg: null, ffprobe: 'ffprobe', proxyEncoders: null, webView: 'test', customFolder: false, settingsError: null }
              const result = type === 'projects.list' ? [] : type === 'exports.list' ? {jobs: [], issue: null} : type === 'settings.get' ? settings : project
              queueMicrotask(() => {
                if (type === 'media.createProxy') {
                  reply({ version: 1, type: 'import.stage', payload: { jobId: id, stage: 'Создаём прокси для просмотра' } })
                  Object.assign(window, { proxyId: id, finishProxy: (success = false) => {
                    reply({ version: 1, type: 'import.stage', payload: { jobId: id, stage: '' } })
                    if (success) reply({ version: 1, id, ok: true, result: { ...project, usingProxy: true, proxyId: '240cb256-bddc-4b40-88a1-ff248ef387fd' } })
                    else reply({ version: 1, id, ok: false, error: {
                      code: 'encode', retryable: true, userMessage: 'Не удалось создать прокси', diagnosticId: 'test',
                    } })
                  } })
                } else if (type === 'jobs.cancel') {
                  Object.assign(window, { cancelledId: payload.jobId })
                  reply({ version: 1, id, ok: true, result: true })
                } else reply({ version: 1, id, ok: true, result })
              })
            },
          }) })
        }, { project })
        await page.goto('/')
        await expect(page.getByRole('heading', { name: 'Добавьте первое видео' })).toBeVisible()
        await page.screenshot({ path: info.outputPath('empty.png') })
        await page.keyboard.press('Tab')
        await expect(page.getByRole('button', { name: 'Проекты', exact: true })).toBeFocused()
        await page.getByRole('button', { name: 'Очередь' }).click()
        await expect(page.getByText('Активных задач нет')).toBeVisible()
        await page.screenshot({ path: info.outputPath('queue.png') })
        await page.getByRole('button', { name: 'Проекты', exact: true }).click()
        await page.getByRole('button', { name: 'Выбрать файл' }).click()
        await expect(page.getByText('В видео нет аудиодорожки.', { exact: false })).toBeVisible()
        await expect(page.getByRole('heading', { name: project.name.trim() })).toBeVisible()
        await page.screenshot({ path: info.outputPath('project.png') })
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
        await page.getByRole('button', { name: 'Создать прокси' }).click()
        await expect(page.getByRole('status')).toHaveText('Создаём прокси для просмотра')
        await expect(page.getByRole('button', { name: 'Отменить' })).toBeEnabled()
        await page.screenshot({ path: info.outputPath('processing.png') })
        await page.getByRole('button', { name: 'Отменить' }).click()
        expect(await page.evaluate(() => {
          const state = window as Window & { proxyId?: string; cancelledId?: string }
          return !!state.proxyId && state.cancelledId === state.proxyId
        })).toBe(true)
        await page.evaluate(() => (window as Window & { finishProxy(): void }).finishProxy())
        await expect(page.getByRole('alert')).toContainText('Не удалось создать прокси')
        await page.screenshot({ path: info.outputPath('error.png') })
        const back = page.getByRole('button', { name: 'Все проекты' })
        await back.focus()
        await page.keyboard.press('Enter')
        const row = page.getByRole('button', { name: /Обсуждение проекта/ })
        await expect(row).toBeFocused()
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
        await page.keyboard.press('Enter')
        const proxy = page.getByRole('button', { name: 'Создать прокси' })
        await proxy.click()
        await expect(page.getByRole('button', { name: 'Отменить' })).toBeEnabled()
        await page.getByRole('button', { name: 'Настройки' }).click()
        await expect(page.getByText('Не найден', { exact: true })).toBeVisible()
        await page.evaluate(() => (window as Window & { finishProxy(success: boolean): void }).finishProxy(true))
        await expect(page.getByRole('button', { name: 'Отменить' })).toHaveCount(0)
        await expect(page.getByRole('heading', { name: 'Настройки', exact: true })).toBeVisible()
      })
    })
  }
}
