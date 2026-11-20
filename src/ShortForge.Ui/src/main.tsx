import { useEffect, useRef, useState, type DragEvent } from 'react'
import { createRoot } from 'react-dom/client'
import {
  ArrowLeft, AudioLines, ChevronRight, CircleAlert, Clapperboard, FileVideo,
  Film, Folder, FolderOpen, ListVideo, MonitorPlay, Plus, RotateCcw, Settings2, Square,
} from 'lucide-react'
import { createBridge, HostError, missingHost, type Project, type Settings } from './bridge'
import { Clips } from './Clips'
import { BatchExport, ExportQueue } from './Exports'
import type { ExportSelection } from './bridge'
import { ClipEditor } from './ClipEditor'
import type { EditorClip } from './bridge'
import type { ClipCandidate, ClipSearch, ClipState } from './bridge'
import './style.css'

const host = createBridge(window.chrome?.webview)
const toolErrors = new Set(['missing_ffprobe', 'missing_ffmpeg', 'tool_settings', 'tool_unavailable', 'proxy_encode'])
const tabs = [
  { name: 'Проекты', icon: Folder },
  { name: 'Очередь', icon: ListVideo },
  { name: 'Настройки', icon: Settings2 },
] as const
type Tab = typeof tabs[number]['name']
const date = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })

function time(us: number) {
  const total = Math.floor(us / 1_000_000)
  const seconds = String(total % 60).padStart(2, '0')
  const minutes = Math.floor(total / 60)
  return minutes < 60 ? minutes + ':' + seconds : Math.floor(minutes / 60) + ':' + String(minutes % 60).padStart(2, '0') + ':' + seconds
}

function frameRate(value: string | null) {
  if (!value) return 'Не указана'
  const [numerator, denominator = '1'] = value.split('/')
  const fps = Number(numerator) / Number(denominator)
  return Number.isFinite(fps) && fps > 0
    ? fps.toLocaleString('ru-RU', { maximumFractionDigits: 3 }) + ' кадр/с'
    : 'Не указана'
}

function asset(url: string | null) {
  if (!url) return undefined
  try {
    const parsed = new URL(url, location.origin)
    return parsed.origin === location.origin && /^https?:$/.test(parsed.protocol) ? parsed.href : undefined
  } catch { return undefined }
}

function Thumb({ project }: { project: Project }) {
  const [failed, setFailed] = useState(false)
  const src = asset(project.thumbnailUrl)
  return <span className="thumb">{src && !failed ? <img src={src} alt="" loading="lazy" onError={() => setFailed(true)} /> : <Film />}</span>
}

function Library({ projects, busy, working, loading, failure, pick, open, drop, reload, invalidDrop }: {
  projects: Project[]; busy: boolean; working: boolean; loading: boolean; failure: boolean
  pick(): void; open(project: Project): void; drop(file: File): void; reload(): void; invalidDrop(): void
}) {
  const [over, setOver] = useState(false)
  function onDrop(event: DragEvent<HTMLElement>) {
    event.preventDefault()
    setOver(false)
    if (event.dataTransfer.files.length > 1) { invalidDrop(); return }
    const file = event.dataTransfer.files[0]
    if (file && !busy) drop(file)
  }
  return <section aria-label="Библиотека проектов" onDrop={onDrop}
    onDragOver={event => {
      event.preventDefault()
      if (!event.dataTransfer.types.includes('Files') || busy) return
      event.dataTransfer.dropEffect = 'copy'
      setOver(true)
    }}
    onDragLeave={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOver(false) }}>
    <header className="page-head"><h1>Проекты</h1>
      <button className="secondary" disabled={busy} onClick={pick}><Plus />Добавить видео</button>
    </header>
    {loading ? <p role="status">Открываем проекты…</p> : failure ?
      <button className="secondary" onClick={reload}><RotateCcw />Повторить загрузку проектов</button> : null}
    {!projects.length && !loading && !failure && <div className={'dropzone' + (over ? ' over' : '')}>
      <div className="frame-corners" aria-hidden="true"><i /><i /><i /><i /></div>
      <div className="film-art" aria-hidden="true"><span className="film-back" /><div className="film-cell"><i /><Clapperboard /><i /></div></div>
      <h2>{over ? 'Отпустите видео здесь' : 'Добавьте первое видео'}</h2>
      <p>Перетащите исходное видео сюда или выберите файл на компьютере.</p>
      <button className="primary" disabled={busy} onClick={pick}><FolderOpen />Выбрать файл</button>
      <small>Распространённые видеоформаты через FFmpeg</small>
    </div>}
    {projects.length > 0 && <div className={'project-list' + (over ? ' over' : '')}>
      <div className="section-head"><h2>Сохранённые проекты</h2><span>{projects.length}</span></div>
      <ul>{projects.map(project => <li key={project.id}>
        <button className="project-row" data-project-id={project.id} disabled={working} onClick={() => open(project)}>
          <Thumb key={project.thumbnailUrl} project={project} />
          <span className="row-body"><strong>{project.name}</strong><span>{time(project.durationUs)} · {project.width} × {project.height}</span>
          <span>Изменён {date.format(new Date(project.updatedUtc))}</span></span>
          <span className={'file-state' + (project.sourceAvailable ? '' : ' warning')}>{project.sourceAvailable ? 'Импортирован' : 'Исходник не найден'}</span>
          <ChevronRight className="chevron" />
        </button>
      </li>)}</ul>
    </div>}
  </section>
}

function Workspace({ project, back, relink, createProxy, rebuildProxy, measureFrameIntervals, transcribe, findClips, markClip, startExport, busy }: {
  project: Project; back(): void; relink(): void; createProxy(track?: number): void; rebuildProxy(proxyId: string): void; measureFrameIntervals(): void; busy: boolean
  transcribe(language: string, track: number): void; findClips(search: ClipSearch): void
  markClip(selectionId:string,startId:number,state:ClipState): void
  startExport(selection: ExportSelection): void
}) {
  const [failed, setFailed] = useState(false)
  const [track, setTrack] = useState(project.proxyAudioTrack)
  const transcript = project.transcript
  const [language, setLanguage] = useState(transcript?.language ?? 'auto')
  const [speechTrack, setSpeechTrack] = useState(transcript?.audioTrack ?? project.proxyAudioTrack)
  const player = useRef<HTMLVideoElement>(null)
  const clipPreview = useRef<{end: number; skipAt: number | null} | null>(null)
  const [checkingEdges, setCheckingEdges] = useState(false)
  const raf = useRef(0)
  const [playError, setPlayError] = useState(false)
  const [editing, setEditing] = useState<EditorClip | null>(null)
  const [edits, setEdits] = useState(project.edits ?? [])
  const [editorError, setEditorError] = useState('')
  const [opening, setOpening] = useState(false)
  const openingRef = useRef(false)
  function remember(clip: EditorClip) {
    setEdits(list=>[...list.filter(e=>e.id !== clip.id), {id:clip.id,title:clip.edit.title}])
  }
  async function enterEditor(open: () => Promise<EditorClip>) {
    if (openingRef.current) return
    openingRef.current = true
    setOpening(true)
    setEditorError('')
    try {
      const clip = await open()
      player.current?.pause()
      remember(clip)
      setEditing(clip)
    } catch (err) { setEditorError(err instanceof Error ? err.message : 'Не удалось открыть клип.') }
    finally { openingRef.current = false; setOpening(false) }
  }
  useEffect(() => () => cancelAnimationFrame(raf.current), [])
  function watchClip() {
    cancelAnimationFrame(raf.current)
    const video = player.current
    const part = clipPreview.current
    if (!video || !part || video.paused) return
    if (video.currentTime >= part.end) {
      clipPreview.current = null
      video.pause()
      video.currentTime = part.end
      setCheckingEdges(false)
      return
    }
    if (part.skipAt !== null && !video.seeking && video.currentTime >= part.skipAt) {
      part.skipAt = null
      if (video.currentTime < part.end - 3) video.currentTime = part.end - 3
    }
    raf.current = requestAnimationFrame(watchClip)
  }
  function previewClip(clip: ClipCandidate, edges = false) {
    const video = player.current
    if (!video) return
    const start = clip.startUs / 1e6, end = clip.endUs / 1e6
    const part = { end, skipAt: edges && end - start > 6 ? start + 3 : null }
    clipPreview.current = part
    setCheckingEdges(edges)
    setPlayError(false)
    video.currentTime = start
    video.scrollIntoView({block:'center'})
    void video.play().then(watchClip).catch(() => {
      if (clipPreview.current !== part) return
      clipPreview.current = null
      setCheckingEdges(false)
      setPlayError(true)
    })
  }
  const multipleTracks = project.audioTrackLabels.length > 1
  const hdr = project.color?.isHdr === true
  const unsupportedHdr = hdr && !project.color?.canToneMap
  const createLabel = hdr ? 'Создать SDR-прокси' : 'Создать прокси'
  const canUseTrack = (index: number) => project.sourceAvailable || project.offlineAudioTracks.includes(index)
  const preview = asset(project.previewUrl)
  const metadata = [
    ['Длительность', time(project.durationUs)], ['Размер кадра', project.width + ' × ' + project.height],
    ['Видеокодек', project.videoCodec], ['Контейнер', project.container], ['Средняя частота кадров', frameRate(project.fps)],
    ['Поворот', project.rotation + '°'], ['Аудиодорожки', String(project.audioTracks)],
  ]
  if (project.frameIntervals) {
    const { frames, minMs, maxMs } = project.frameIntervals
    const format = new Intl.NumberFormat('ru-RU', { maximumSignificantDigits: 6 })
    const interval = minMs === maxMs ? format.format(minMs) : format.format(minMs) + '–' + format.format(maxMs)
    metadata.push(['Интервалы в начале', interval + ' мс'], ['Измерено кадров', String(frames)])
  }
  if (project.color) {
    const { transfer, primaries } = project.color
    const curve = transfer === 'smpte2084' ? 'PQ' : transfer === 'arib-std-b67' ? 'HLG' : transfer
    const gamut = primaries === 'bt2020' ? 'BT.2020' : primaries
    metadata.push(['Параметры цвета', [hdr && 'HDR', curve, gamut].filter(Boolean).join(' · ')])
  }
  if (project.anamorphicAspect) {
    const { pixelRatio, frameRatio } = project.anamorphicAspect
    metadata.push(['Пропорции пикселя', pixelRatio])
    if (frameRatio) metadata.push([project.rotation ? 'Пропорции до поворота' : 'Пропорции кадра', frameRatio])
  }
  return <section>
    <button className="back" onClick={back}><ArrowLeft />Все проекты</button>
    <header className="page-head"><h1 className="filename" title={project.name}>{project.name}</h1></header>
    {!project.sourceAvailable && <div className="missing"><CircleAlert /><div><strong>Исходник не найден</strong>
      <p>{project.usingProxy && !failed ? 'Сохранённый прокси доступен для просмотра. Для обработки укажите исходное видео.' :
        'Укажите файл, если вы перенесли или переименовали его.'}</p></div>
      <button className="secondary" disabled={busy} onClick={relink}><FolderOpen />Найти файл</button></div>}
    <div className="workspace">
      <div><h2 className="subhead">{project.usingProxy ? (hdr ? 'Предпросмотр · SDR-прокси' : 'Предпросмотр · Прокси') : 'Предпросмотр'}</h2>
        {checkingEdges && <p role="status">Прослушивание начала и конца фрагмента</p>}
        <div className="preview">
          {preview && !failed ? <video ref={player} src={preview} controls preload="metadata" poster={asset(project.thumbnailUrl)}
            onPlay={watchClip} onTimeUpdate={watchClip}
            onEnded={() => { clipPreview.current = null; setCheckingEdges(false) }}
            onLoadedMetadata={event => { if (!event.currentTarget.videoWidth) setFailed(true) }}
            onError={() => setFailed(true)} aria-label={'Предпросмотр видео «' + project.name + '»'} /> :
            <div className="preview-fallback"><MonitorPlay /><p>{project.sourceAvailable ? 'Предпросмотр недоступен' : 'Видео недоступно'}</p>
              {project.sourceAvailable && !project.usingProxy && !hdr ?
                <><small>Для просмотра можно создать локальную копию H.264/AAC.</small>
                  {!multipleTracks && <button className="secondary" disabled={busy} onClick={() => createProxy()}><MonitorPlay />Создать прокси</button>}</> :
                failed && (!hdr || project.usingProxy) && <>
                  {project.usingProxy && <small>{project.sourceAvailable ? 'Не удалось воспроизвести сохранённый прокси.' :
                    'Не удалось воспроизвести прокси. Чтобы создать новую копию, нажмите «Найти файл».'}</small>}
                  <button className="secondary" disabled={busy} onClick={() => setFailed(false)}><RotateCcw />Повторить</button>
                  {project.sourceAvailable && !unsupportedHdr && project.proxyId &&
                    <button className="secondary" disabled={busy} onClick={() => rebuildProxy(project.proxyId!)}><MonitorPlay />Пересоздать прокси</button>}
                </>}</div>}
        </div>
        {unsupportedHdr && project.sourceAvailable && <div className="notice"><MonitorPlay /><div>
          <p id="hdr-proxy-note">Для SDR-прокси нужны PQ или HLG, BT.2020, матрица BT.2020 NC и известный диапазон. Dolby Vision пока не поддерживается.</p>
          <button className="secondary" disabled aria-describedby="hdr-proxy-note">Создать прокси</button>
        </div></div>}
        {hdr && !unsupportedHdr && project.sourceAvailable && !project.usingProxy && <div className="notice"><div>
          <p>Можно преобразовать HDR в SDR для просмотра на обычном экране.</p>
          {!multipleTracks && <button className="secondary" disabled={busy} onClick={() => createProxy()}><MonitorPlay />{createLabel}</button>}
        </div></div>}
        {multipleTracks && !unsupportedHdr && <div className="audio-choice">
          {project.usingProxy && <p>В прокси звучит дорожка {project.proxyAudioTrack + 1}</p>}
          <label htmlFor="proxy-audio">Звуковая дорожка для прокси</label>
          <select id="proxy-audio" value={track} disabled={busy || (!project.sourceAvailable && project.offlineAudioTracks.length === 0)}
            onChange={event => setTrack(Number(event.currentTarget.value))}>
            {project.audioTrackLabels.map((label, index) => <option key={index} value={index} disabled={!canUseTrack(index)}>
              {index + 1}{label && ' · ' + label}{!canUseTrack(index) && ' — нужен исходник'}
            </option>)}
          </select>
          {!project.sourceAvailable && project.offlineAudioTracks.length < project.audioTracks &&
            <p>Для дорожек без сохранённого прокси нажмите «Найти файл».</p>}
          {canUseTrack(track) && (!project.usingProxy || track !== project.proxyAudioTrack || (project.proxyNeedsRefresh && project.sourceAvailable)) &&
            <button className="secondary" disabled={busy} onClick={() => createProxy(track)}><MonitorPlay />
              {project.usingProxy ? 'Применить дорожку' : project.sourceAvailable ? createLabel : 'Открыть прокси'}</button>}
        </div>}
        {!hdr && !multipleTracks && project.proxyNeedsRefresh && project.sourceAvailable && <div className="notice"><div>
          <p>Обновите прокси, чтобы сохранить точное время кадров при переменной частоте.</p>
          <button className="secondary" disabled={busy} onClick={() => createProxy()}><RotateCcw />Обновить прокси</button>
        </div></div>}
      </div>
      <div className="props"><h2 className="subhead">Сведения об исходнике</h2>
        <dl>{metadata.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
        {!project.frameIntervals && <button className="secondary" disabled={busy || !project.sourceAvailable}
          title={project.sourceAvailable ? 'По меткам показа первых 48 кадров исходника' : 'Для измерения укажите исходник через «Найти файл»'}
          onClick={measureFrameIntervals}>Измерить интервалы кадров</button>}
      </div>
    </div>
    <div className="workflow"><h2 className="subhead">Расшифровка</h2>
      {project.audioTracks > 0 && <div className="audio-choice">
        <label htmlFor="asr-language">Язык речи</label>
        <select id="asr-language" disabled={busy} value={language} onChange={e => setLanguage(e.target.value)}>
          <option value="auto">Определить автоматически</option><option value="ru">Русский</option>
          <option value="en">Английский</option><option value="az">Азербайджанский</option>
        </select>
        {project.audioTracks > 1 && <><label htmlFor="asr-track">Дорожка для распознавания</label>
          <select id="asr-track" disabled={busy} value={speechTrack} onChange={e => setSpeechTrack(Number(e.target.value))}>
            {Array.from({ length: project.audioTracks }, (_, i) => <option key={i} value={i}>{i+1} {project.audioTrackLabels[i]}</option>)}
          </select></>}
      </div>}
      <button className="secondary" disabled={busy || !project.sourceAvailable || !project.audioTracks}
        onClick={() => transcribe(language, speechTrack)}><AudioLines />Распознать речь</button>
      {!project.audioTracks && <p>В видео нет аудиодорожки.</p>}
      {!project.sourceAvailable && <p>Для распознавания укажите оригинал через «Найти файл».</p>}
      {transcript && <div className="speech-result">
        <p>{transcript.model} · Дорожка {transcript.audioTrack + 1}{transcript.segments.length > 0 && ' · Язык ' + transcript.detectedLanguage}</p>
        {transcript.segments.length ? <><p>Выберите фразу для перехода к её примерному началу. Время указано для сегмента целиком.</p>
          <ol>{transcript.segments.map(s => <li key={s.startUs}>
            <button className="text-button" disabled={!preview || failed} onClick={() => {
              clipPreview.current = null
              setCheckingEdges(false)
              if (player.current) player.current.currentTime = s.startUs / 1_000_000
            }}><span>{time(s.startUs)}</span> {s.text}</button></li>)}</ol></> : <p>Речь не обнаружена.</p>}
      </div>}
    </div>
    <Clips project={project} busy={busy || opening} find={findClips} preview={previewClip} mark={markClip}
      edit={(selectionId,startId)=>void enterEditor(()=>host.request('editor.create',{projectId:project.id,selectionId,startId}))}
      playable={!!preview && !failed && (project.audioTracks < 2 || (project.usingProxy && project.proxyAudioTrack === transcript?.audioTrack))}/>
    {playError && <p role="alert">Не удалось начать просмотр фрагмента.</p>}
    <section className="workflow"><h2 className="subhead">Монтаж клипов</h2>
      <div className="clip-actions"><button className="secondary" disabled={busy || opening}
        onClick={()=>void enterEditor(()=>host.request('editor.create',{projectId:project.id}))}>Создать клип вручную</button>
        {edits.map(e=><button className="text-button" disabled={busy || opening} key={e.id}
          onClick={()=>void enterEditor(()=>host.request('editor.open',{projectId:project.id,clipId:e.id}))}>{e.title}</button>)}</div>

      {editorError && <p role="alert">{editorError}</p>}
    </section>
    <BatchExport project={project} edits={edits} busy={busy || opening || !!editing} start={startExport}/>
    {editing && <ClipEditor key={editing.id} initial={editing} project={project} preview={preview}
      flushed={jobId=>host.request('editor.flushed',{jobId})}
      save={(revision,{title,startUs,endUs,frame,x,y,captionStyle},cue)=>host.request('editor.save',{
        projectId:project.id,clipId:editing.id,revision,edit:{title,startUs,endUs,frame,x,y,captionStyle},cue})}
      autoFrame={async (revision,intervalMs,id,progress) => {
        const unsubscribe = host.subscribe(event => {
          if (event.type === 'import.stage' && event.jobId === id && event.stage)
            progress((stages[event.stage] ?? event.stage) + (event.percent === undefined ? '' : ' · ' + event.percent + '%'))
        })
        try { return await host.request('editor.autoFrame',{projectId:project.id,clipId:editing.id,revision,intervalMs},{id}) }
        finally { unsubscribe() }
      }}
      render={(revision,offsetUs,id)=>host.request('editor.preview',{projectId:project.id,clipId:editing.id,revision,offsetUs},{id})}
      cancel={jobId=>host.request('jobs.cancel',{jobId})}
      srt={revision=>host.request('editor.srt',{projectId:project.id,clipId:editing.id,revision})}
      captionVideo={(revision,id)=>host.request('editor.captionVideo',{projectId:project.id,clipId:editing.id,revision},{id})}
      close={clip=>{remember(clip);setEditing(null)}}/>}
  </section>
}

function Tools({ settings, busy, pick, reload, useAutomatic }: {
  settings: Settings | null; busy: boolean; pick(): void; reload(): void; useAutomatic(): void
}) {
  const checkButton = useRef<HTMLButtonElement>(null)
  const restoring = useRef(false)
  useEffect(() => {
    if (!busy && restoring.current) {
      restoring.current = false
      if (!settings?.customFolder && document.activeElement === document.body) checkButton.current?.focus()
    }
  }, [settings, busy])
  return <section><header className="page-head"><h1>Настройки</h1></header>
    <div className="settings-panel"><div className="section-head"><h2>Инструменты видео</h2>
      <button className="secondary" onClick={pick} disabled={busy}><FolderOpen />Выбрать папку</button></div>
      {settings?.settingsError ? <p className="warning" role="alert">{settings.settingsError}</p> :
        <p>{settings?.customFolder ? 'Используется выбранная вручную папка.' : 'По умолчанию ShortForge ищет инструменты автоматически.'}</p>}
      {settings ? <dl>{[['FFmpeg', settings.ffmpeg], ['ffprobe', settings.ffprobe], ['WebView2', settings.webView]].map(([label, value]) =>
        <div key={label}><dt>{label}</dt><dd className={value ? undefined : 'warning'}>{value ?? (settings.settingsError ? 'Не проверен' : 'Не найден')}</dd></div>)}
        <div><dt>Кодировщики прокси</dt><dd>{settings.proxyEncoders
          ? <><span>libx264 — {settings.proxyEncoders.libx264 ? 'доступен' : 'отсутствует'}</span><br />
              <span>AAC — {settings.proxyEncoders.aac ? 'доступен' : 'отсутствует'}</span></>
          : 'Не проверены'}</dd></div></dl> :
        <p role="status">{busy && host.available ? 'Проверяем инструменты…' : 'Данные об инструментах недоступны.'}</p>}
      {settings && !settings.settingsError && (!settings.ffprobe || !settings.ffmpeg) &&
        <p>{!settings.ffprobe ? 'Для импорта видео нужен работающий ffprobe.exe.' : 'Импорт доступен. Для миниатюр и прокси нужен работающий ffmpeg.exe.'}</p>}
      {settings?.ffmpeg && !settings.proxyEncoders &&
        <p className="warning">Не удалось прочитать список кодировщиков FFmpeg. Нажмите «Проверить снова».</p>}
      {settings?.proxyEncoders && (!settings.proxyEncoders.libx264 || !settings.proxyEncoders.aac) &&
        <p className="warning">{!settings.proxyEncoders.libx264 ? 'Для видео в прокси нужен libx264.' : 'Для звука в прокси нужен AAC.'} Выберите папку со сборкой FFmpeg, поддерживающей нужные кодировщики.</p>}
      <div className="tool-actions"><button ref={checkButton} className="secondary" disabled={busy} onClick={reload}><RotateCcw />Проверить снова</button>
        {settings?.customFolder && <button className="secondary" disabled={busy} onClick={() => {
          restoring.current = true
          useAutomatic()
        }}>Включить автоматический поиск</button>}</div>
    </div>
  </section>
}

const stages: Record<string, string> = {
  asrTools: 'Проверяем модели распознавания', asrAudio: 'Извлекаем звук из оригинала', asrListen: 'Распознаём речь на CPU',
  fingerprint: 'Проверяем исходный файл',
  faceAnalysis: 'Ищем лица в клипе',
  frameIntervals: 'Измеряем интервалы кадров',
  proxyVerify: 'Проверяем готовый прокси',
  clipStructure: 'Подбираем фрагменты по структуре речи', clipSemantic: 'Подбираем фрагменты в локальной Ollama',
  probe: 'Читаем метаданные', thumbnail: 'Создаём миниатюру', proxy: 'Готовим видео для просмотра', save: 'Сохраняем проект',
}

function Elapsed({ started }: { started: number }) {
  const [elapsed, setElapsed] = useState(() => performance.now() - started)
  useEffect(() => {
    const timer = window.setInterval(() => setElapsed(performance.now() - started), 1000)
    return () => window.clearInterval(timer)
  }, [started])
  return <small aria-label="Прошедшее время">{time(elapsed * 1000)}</small>
}

function App() {
  const [tab, setTab] = useState<Tab>('Проекты')
  const [project, setProject] = useState<Project | null>(null)
  const [projects, setProjects] = useState<Project[]>([])
  const [settings, setSettings] = useState<Settings | null>(null)
  const [readingSettings, setReadingSettings] = useState(false)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(host.available)
  const [listFailed, setListFailed] = useState(false)
  const [job, setJob] = useState<{ id: string; stage: string; percent?: number; started: number } | null>(null)
  const stage = job?.stage ?? ''
  const [cancelPending, setCancelPending] = useState(false)
  const [error, setError] = useState<HostError | null>(null)
  const main = useRef<HTMLElement>(null)
  const locked = useRef(false)
  const libraryVersion = useRef(0)
  const previousView = useRef<{ tab: Tab; projectId?: string } | null>(null)
  const navigationVersion = useRef(0)
  const actionFocus = useRef<{ button: HTMLButtonElement; view: number } | null>(null)

  function report(cause: unknown) {
    setError(cause instanceof HostError ? cause : new HostError('Не удалось выполнить действие. Попробуйте ещё раз.'))
  }
  async function loadLibrary() {
    const version = ++libraryVersion.current
    setLoading(true)
    setListFailed(false)
    try {
      const saved = await host.request('projects.list', {})
      if (version === libraryVersion.current) { setProjects(saved); setError(null) }
    } catch (cause) {
      if (version === libraryVersion.current) { setListFailed(true); report(cause) }
    } finally { if (version === libraryVersion.current) setLoading(false) }
  }
  function receiveProject(value: Project, open: boolean) {
    ++libraryVersion.current
    setLoading(false)
    setListFailed(false)
    if (open) {
      setProject(value)
      setTab('Проекты')
    } else setProject(current => current?.id === value.id ? value : current)
    setProjects(items => [value, ...items.filter(item => item.id !== value.id)].sort((a, b) => Date.parse(b.updatedUtc) - Date.parse(a.updatedUtc)))
  }
  useEffect(() => {
    if (host.available) void loadLibrary()
    return host.subscribe(event => {
      if (event.type === 'import.stage') {
        if (event.stage) {
          const now = performance.now()
          setJob(current => ({
            id: event.jobId, stage: stages[event.stage] ?? event.stage, percent: event.percent,
            started: current?.id === event.jobId ? current.started : now,
          }))
        }
        else setJob(current => current?.id === event.jobId ? null : current)
      } else if (event.type === 'import.error') { report(event.error); setJob(null); setCancelPending(false) }
      else {
        setJob(null)
        setCancelPending(false)
        void host.request('projects.open', { projectId: event.projectId }).then(value => receiveProject(value, false)).catch(report)
      }
    })
  }, [])
  useEffect(() => {
    const previous = previousView.current
    if (previous && (previous.tab !== tab || previous.projectId !== project?.id)) {
      const row = previous.projectId && tab === 'Проекты' && !project
        ? main.current?.querySelector<HTMLButtonElement>(`button[data-project-id="${CSS.escape(previous.projectId)}"]:enabled`)
        : null
      const target = row ?? main.current
      target?.focus()
    }
    previousView.current = { tab, projectId: project?.id }
  }, [tab, project?.id])

  useEffect(() => {
    if (tab !== 'Настройки' || !host.available || settings) return
    let active = true
    setReadingSettings(true)
    host.request('settings.get', {}).then(value => {
      if (active) { setReadingSettings(false); setSettings(value) }
    }).catch(cause => {
      if (active) { setReadingSettings(false); report(cause) }
    })
    return () => { active = false }
  }, [tab, settings])

  useEffect(() => {
    if (busy || stage) return
    const saved = actionFocus.current
    actionFocus.current = null
    if (saved && saved.view === navigationVersion.current && saved.button.isConnected &&
        !saved.button.disabled && document.activeElement === document.body) {
      saved.button.focus()
    }
  }, [busy, stage])

  async function run(task: () => Promise<void>) {
    if (locked.current) return
    locked.current = true
    const button = document.activeElement
    if (button instanceof HTMLButtonElement && main.current?.contains(button)) {
      actionFocus.current = { button, view: navigationVersion.current }
    }
    setBusy(true)
    setError(null)
    try { await task() } catch (cause) { report(cause) }
    finally { locked.current = false; setBusy(false) }
  }
  function media(action: () => Promise<Project | null>) {
    const originView = navigationVersion.current
    void run(async () => {
      try {
        const value = await action()
        if (value) receiveProject(value, originView === navigationVersion.current)
      } finally { setCancelPending(false) }
    })
  }
  async function cancel() {
    if (!job) return
    setCancelPending(true)
    try { await host.request('jobs.cancel', { jobId: job.id }) } catch (cause) { report(cause) }
    finally { setCancelPending(false) }
  }
  function readSettings() {
    void run(async () => setSettings(await host.request('settings.get', {})))
  }
  function navigate(next: Tab) {
    ++navigationVersion.current
    setTab(next)
    setProject(null)
  }
  const jobStatus = job && <div className="job"><FileVideo /><span role="status">{job.stage}</span>
    {job.percent !== undefined && <span role="progressbar" aria-label={job.stage}
      aria-valuemin={0} aria-valuemax={100} aria-valuenow={job.percent}>{job.percent}%</span>}
    <Elapsed key={job.id} started={job.started} />
    <button className="secondary" disabled={cancelPending} onClick={cancel}><Square />Отменить</button></div>
  return <div className="app">
    <aside>
      <div className="brand"><span className="brand-icon"><Clapperboard /></span>ShortForge</div>
      <nav aria-label="Основная навигация">{tabs.map(({ name, icon: Icon }) =>
        <button key={name} className={'nav-item' + (tab === name ? ' active' : '')} aria-current={tab === name ? 'page' : undefined}
          title={name} onClick={() => navigate(name)}><Icon /><span>{name}</span></button>)}</nav>
      <div className="local-status"><span className={'status-dot' + (stage ? ' working' : '')} />
        <span>{stage ? 'Идёт обработка' : host.available ? 'Локальный режим' : 'Без подключения к Windows'}</span></div>
    </aside>
    <main ref={main} tabIndex={-1}>
      {!host.available && <div className="notice"><MonitorPlay /><span>{missingHost}</span></div>}
      {error && <div className="error" role="alert"><CircleAlert /><div><strong>{error.message}</strong>
        {error.diagnosticId && <small>Код диагностики: {error.diagnosticId}</small>}</div>
        {tab !== 'Настройки' && error.code && toolErrors.has(error.code) &&
          <button className="text-button" onClick={() => {
            setError(null)
            setSettings(null)
            navigate('Настройки')
          }}>Открыть диагностику</button>}
        <button className="text-button" onClick={() => setError(null)}>Закрыть</button></div>}
      {tab !== 'Очередь' && jobStatus}
      {tab === 'Проекты' && (project ? <Workspace key={project.id + project.updatedUtc + project.sourceAvailable + project.previewUrl}
        project={project} back={() => navigate('Проекты')} busy={busy || !!stage}
        startExport={selection => {
          const origin = navigationVersion.current
          void run(async () => {
            if (await host.request('exports.start', selection) && origin === navigationVersion.current) navigate('Очередь')
          })
        }}
        markClip={(selectionId,startId,state) => media(() => host.request('clips.mark',{projectId:project.id,selectionId,startId,state}))}
        findClips={({mode,minSeconds,maxSeconds,count}) => media(() => host.request('clips.find', {projectId:project.id,mode,minSeconds,maxSeconds,count}))}
        transcribe={(language, audioTrack) => media(() => host.request('transcript.start', { projectId: project.id, language, audioTrack }))}
        createProxy={audioTrack => media(() => host.request('media.createProxy', { projectId: project.id, audioTrack }))}
        rebuildProxy={proxyId => media(() => host.request('media.rebuildProxy', { projectId: project.id, proxyId }))}
        measureFrameIntervals={() => media(() => host.request('media.measureFrameIntervals', { projectId: project.id }))}
        relink={() => media(() => host.request('media.relink', { projectId: project.id }))} /> :
        <Library projects={projects} busy={busy || loading || !!stage || !host.available} working={busy || !!stage} loading={loading} failure={listFailed}
          reload={() => void loadLibrary()} pick={() => media(() => host.request('media.pick', {}))}
          invalidDrop={() => report(new HostError('Перетащите один локальный видеофайл за раз.'))}
          open={value => media(() => host.request('projects.open', { projectId: value.id }))}
          drop={file => media(() => host.importFile(file))} />)}
      {tab === 'Очередь' && <ExportQueue host={host} activeJob={jobStatus}/>}
      {tab === 'Настройки' && <Tools settings={settings} busy={busy || readingSettings || !!stage || !host.available} reload={readSettings}
        useAutomatic={() => void run(async () => setSettings(await host.request('settings.useAutomaticTools', {})))}
        pick={() => void run(async () => setSettings(await host.request('settings.pickTools', {})))} />}
    </main>
  </div>
}

// Never let a file dropped outside the library navigate the WebView away from its UI.
window.addEventListener('dragover', event => event.preventDefault())
window.addEventListener('drop', event => event.preventDefault())
createRoot(document.getElementById('root')!).render(<App />)
