import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { ExportJob, ExportSelection, ExportState, Project, createBridge } from './bridge'

type Bridge = ReturnType<typeof createBridge>
type Edit = { id: string; title: string }

export function BatchExport({project, edits, busy, start}: {
  project: Project; edits: Edit[]; busy: boolean; start(selection: ExportSelection): void
}) {
  const [checked, setChecked] = useState<string[]>([])
  const [useCandidates, setUseCandidates] = useState(true)
  const [fps, setFps] = useState(30)
  const [srt, setSrt] = useState(false)
  const [manifest, setManifest] = useState(false)
  const [normalize, setNormalize] = useState(true)
  const [gpu, setGpu] = useState(false)
  const selectedCandidates = project.selection?.clips.filter(c => c.state === 'selected') ?? []
  const selectionId = useCandidates && selectedCandidates.length ? project.selection!.id : null
  if (!edits.length && !selectedCandidates.length) return null
  return <section className="workflow batch-export" aria-label="Пакетный экспорт">
    <h2 className="subhead">Пакетный экспорт</h2>
    <fieldset disabled={busy}>
      <legend>Клипы для экспорта</legend>
      {selectedCandidates.length > 0 && <label><input type="checkbox" checked={useCandidates}
        onChange={e => setUseCandidates(e.target.checked)} /> Из подбора · выбрано {selectedCandidates.length}</label>}
      {edits.length > 0 && <span className="batch-count">Сохранённые клипы · выбрано {checked.length}</span>}
      {edits.map(edit => <label key={edit.id}><input type="checkbox" checked={checked.includes(edit.id)}
        onChange={e => setChecked(e.target.checked ? [...checked, edit.id] : checked.filter(id => id !== edit.id))} /> {edit.title}</label>)}
    </fieldset>
    <fieldset disabled={busy}>
      <legend>MP4 · 1080 × 1920</legend>
      <label>Частота кадров <select value={fps} onChange={e => setFps(Number(e.target.value))}>
        <option value={30}>30 кадров/с</option><option value={60}>60 кадров/с</option></select></label>
      <label><input type="checkbox" checked={normalize} onChange={e => setNormalize(e.target.checked)} /> Нормализовать громкость</label>
      <label><input type="checkbox" checked={srt} onChange={e => setSrt(e.target.checked)} /> Сохранить SRT</label>
      <label><input type="checkbox" checked={manifest} onChange={e => setManifest(e.target.checked)} /> Сохранить манифест JSON</label>
      <label><input type="checkbox" checked={gpu} onChange={e => setGpu(e.target.checked)} /> Использовать NVENC, если доступен</label>
    </fieldset>
    <button className="primary" disabled={busy || (!checked.length && !selectionId) || !project.sourceAvailable}
      onClick={() => start({projectId: project.id, clipIds: checked, selectionId, candidateStarts: selectionId ? selectedCandidates.map(c => c.startId) : [], fps, includeSrt: srt, includeManifest: manifest,
        normalizeAudio: normalize, preferNvenc: gpu})}>Выбрать папку и экспортировать</button>
    {!project.sourceAvailable && <p>Чтобы экспортировать клипы, восстановите связь с исходным видео.</p>}
  </section>
}

const stateName: Record<ExportState, string> = {
  queued: 'В очереди', running: 'Экспортируется', completed: 'Готово', failed: 'Ошибка',
  cancelled: 'Отменено', interrupted: 'Прервано',
}
type Action = 'exports.cancel' | 'exports.retry' | 'exports.openFolder'

export function ExportQueue({host, activeJob}: {host: Bridge; activeJob: ReactNode}) {
  const [jobs, setJobs] = useState<ExportJob[] | null>(null)
  const [error, setError] = useState('')
  const [listError, setListError] = useState('')
  const [workerError, setWorkerError] = useState('')
  const [pending, setPending] = useState<string | null>(null)
  const [refresh, setRefresh] = useState(0)
  const actionLock = useRef(false)
  useEffect(() => {
    if (!host.available) return
    let stopped = false
    let timer: ReturnType<typeof setTimeout>
    async function read() {
      try {
        const next = await host.request('exports.list', {})
        if (!stopped) { setJobs(next.jobs); setWorkerError(next.issue ?? ''); setListError('') }
      } catch (cause) {
        if (!stopped) setListError(cause instanceof Error ? cause.message : 'Не удалось загрузить очередь.')
      } finally {
        if (!stopped) timer = setTimeout(read, 1000)
      }
    }
    void read()
    return () => { stopped = true; clearTimeout(timer) }
  }, [host, refresh])

  async function act(type: Action | 'exports.resume', jobId = '') {
    if (actionLock.current) return
    actionLock.current = true
    setError('')
    setPending(jobId)
    try {
      if (type === 'exports.resume') await host.request(type, {})
      else await host.request(type, {jobId})
      setRefresh(r => r + 1)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Не удалось выполнить действие.') }
    finally { setPending(null); actionLock.current = false }
  }
  return <section><header className="page-head"><h1>Очередь</h1></header>
    {activeJob}
    {(error || listError) && <p role="alert">{error || listError}</p>}
    {workerError && <div className="error"><span role="alert">{workerError}</span>
      <button className="secondary" disabled={pending !== null} onClick={() => void act('exports.resume')}>Возобновить очередь</button></div>}
    {!host.available ? <h2>Очередь недоступна</h2> : jobs === null && !listError ? <p role="status">Загружаем очередь…</p> :
      jobs?.length === 0 && !activeJob ? <div className="empty"><h2>Активных задач нет</h2><p>Выберите клипы для экспорта в проекте.</p></div> :
      <ol className="export-jobs">{jobs?.map(job => <li key={job.id} data-job-id={job.id} data-state={job.state}>
        <div><strong>{job.title}</strong><small>{stateName[job.state]} · редакция {job.revision}</small>
          {job.state === 'completed' && <small>{job.fileName}</small>}
          {job.error && <p>{job.error}</p>}
        </div>
        {job.state === 'running' && <label>{job.percent}% <progress aria-label="Экспорт клипа" value={job.percent} max={100}/></label>}
        {(job.state === 'queued' || job.state === 'running') && <button className="secondary" disabled={pending !== null}
          onClick={() => void act('exports.cancel', job.id)}>Отменить</button>}
        {['failed','cancelled','interrupted'].includes(job.state) && <button className="secondary" disabled={pending !== null}
          onClick={() => void act('exports.retry', job.id)}>Повторить</button>}
        {job.state === 'completed' && <button className="secondary" disabled={pending !== null}
          onClick={() => void act('exports.openFolder', job.id)}>Открыть папку</button>}
      </li>)}</ol>}
  </section>
}
