export interface ClipSearch { mode: 'structure' | 'semantic'; minSeconds: number; maxSeconds: number; count: number }
export interface ClipCandidate { startId: number; endId: number; startUs: number; endUs: number; title: string; reason: string;
  state: ClipState; scores: { independence: number; clarity: number; opening: number; completion: number } | null }
export type ClipState = 'offered' | 'selected' | 'rejected'
export interface ClipSelection { id: string; search: ClipSearch; clips: ClipCandidate[] }

export interface CaptionStyle {
  preset: 'neutral' | 'contrast'; size: number; color: string; outline: number; background: boolean; position: number
}
export interface CropPoint { timeUs: number; frame: 'crop' | 'fit'; x: number; y: number; cut: boolean }
export interface CropTrajectory {
  intervalMs: number; points: CropPoint[]
  counts: {single: number; multiple: number; missing: number; sceneCuts: number}
}
export interface EditDecision {
  title: string; startUs: number; endUs: number; frame: 'crop' | 'fit' | 'auto'; x: number; y: number
  captionStyle?: CaptionStyle | null; trajectory?: CropTrajectory | null
}
export interface EditorCue { index: number; startUs: number; endUs: number; text: string; sourceStartUs: number; sourceEndUs: number }
export interface EditorClip { id: string; revision: number; audioTrack: number; edit: EditDecision;
  cues: EditorCue[] }
export interface CueChange { index: number; text: string; sourceStartUs?: number; sourceEndUs?: number }
export interface CaptionPreview { revision: number; url: string }
export interface Project {
  edits?: { id: string; title: string }[]
  selection?: ClipSelection | null
  id: string
  name: string
  updatedUtc: string
  durationUs: number
  width: number
  height: number
  videoCodec: string
  color: { isHdr: boolean; transfer: string | null; primaries: string | null; canToneMap: boolean } | null
  anamorphicAspect: { pixelRatio: string; frameRatio: string | null } | null
  container: string
  audioTracks: number
  proxyAudioTrack: number
  audioTrackLabels: string[]
  offlineAudioTracks: number[]
  fps: string | null
  frameIntervals: { frames: number; minMs: number; maxMs: number } | null
  transcript: { audioTrack: number; language: string; detectedLanguage: string; model: string; elapsedMs: number; peakRamBytes: number;
    segments: { startUs: number; endUs: number; text: string }[] } | null
  rotation: number
  sourceAvailable: boolean
  usingProxy: boolean
  proxyId: string | null
  proxyNeedsRefresh: boolean
  previewUrl: string | null
  thumbnailUrl: string | null
}

export interface Settings {
  ffmpeg: string | null
  ffprobe: string | null
  proxyEncoders: { libx264: boolean; aac: boolean } | null
  webView: string
  customFolder: boolean
  settingsError: string | null
}

type Empty = Record<string, never>
type ProjectId = { projectId: string }
export interface ExportSelection {
  projectId: string; clipIds: string[]; selectionId: string | null; candidateStarts: number[]
  fps: number; includeSrt: boolean; includeManifest: boolean; normalizeAudio: boolean; preferNvenc: boolean
}
export type ExportState = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled' | 'interrupted'
export interface ExportJob {
  id: string; projectId: string; clipId: string; revision: number; title: string; fileName: string
  state: ExportState; percent: number; error: string | null
}
interface Commands {
  'exports.start': [ExportSelection, boolean]
  'exports.list': [Empty, {jobs: ExportJob[]; issue: string | null}]
  'exports.resume': [Empty, boolean]
  'exports.cancel': [{jobId: string}, boolean]
  'exports.retry': [{jobId: string}, boolean]
  'exports.openFolder': [{jobId: string}, boolean]
  'editor.create': [ProjectId & {selectionId?: string; startId?: number}, EditorClip]
  'editor.open': [ProjectId & {clipId: string}, EditorClip]
  'editor.autoFrame': [ProjectId & {clipId: string; revision: number; intervalMs: number}, EditorClip]
  'editor.save': [ProjectId & {clipId: string; revision: number; edit: EditDecision; cue: CueChange | null}, EditorClip]
  'editor.preview': [ProjectId & {clipId: string; revision: number; offsetUs: number}, CaptionPreview]
  'editor.srt': [ProjectId & {clipId: string; revision: number}, boolean]
  'editor.captionVideo': [ProjectId & {clipId: string; revision: number}, boolean]
  'clips.mark': [ProjectId & {selectionId:string; startId:number; state:ClipState}, Project]
  'clips.find': [ProjectId & ClipSearch, Project]
  'projects.list': [Empty, Project[]]
  'projects.open': [ProjectId, Project]
  'media.pick': [Empty, Project | null]
  'media.relink': [ProjectId, Project | null]
  'media.measureFrameIntervals': [ProjectId, Project]
  'media.createProxy': [ProjectId & { audioTrack?: number }, Project]
  'media.rebuildProxy': [ProjectId & { proxyId: string }, Project]
  'media.importDropped': [Empty, Project]
  'transcript.start': [ProjectId & { language: string; audioTrack: number }, Project]
  'editor.flushed': [{ jobId: string }, boolean]
  'jobs.cancel': [{ jobId: string }, boolean]
  'settings.get': [Empty, Settings]
  'settings.pickTools': [Empty, Settings]
  'settings.useAutomaticTools': [Empty, Settings]
}
type Command = keyof Commands
type Envelope = { version: 1; id: string; type: Command; payload: Commands[Command][0] }

export interface HostPort {
  postMessage(message: Envelope): void
  postMessageWithAdditionalObjects?(message: Envelope, files: File[]): void
  addEventListener(type: 'message', listener: (event: MessageEvent<unknown>) => void): void
}

declare global {
  interface Window {
    chrome?: { webview?: HostPort }
  }
}

export class HostError extends Error {
  readonly diagnosticId?: string
  readonly code?: string
  constructor(message: string, diagnosticId?: string, code?: string) {
    super(message)
    this.diagnosticId = diagnosticId
    this.code = code
  }
}

export const missingHost = 'Выбор файлов и проекты доступны в приложении ShortForge для Windows.'
const isObject = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value)
const nullableText = (value: unknown) => value === null || typeof value === 'string'
const nonNegative = (value: unknown) => Number.isSafeInteger(value) && Number(value) >= 0
function validStyle(value: unknown) {
  const integer = (v: unknown, lo: number, hi: number) => Number.isInteger(v) && Number(v) >= lo && Number(v) <= hi
  return isObject(value) && ['neutral','contrast'].includes(String(value.preset)) && integer(value.size,32,112) &&
    integer(value.outline,0,10) && integer(value.position,10,94) && typeof value.background === 'boolean' &&
    typeof value.color === 'string' && /^#[a-f\d]{6}$/i.test(value.color)
}

function validTrajectory(value: unknown, durationUs: number) {
  if (!isObject(value) || !nonNegative(value.intervalMs) || Number(value.intervalMs) < 100 || Number(value.intervalMs) > 2000 ||
      !isObject(value.counts) || !['single','multiple','missing','sceneCuts'].every(k => isObject(value.counts) && nonNegative(value.counts[k])) ||
      !Array.isArray(value.points) || !value.points.length || value.points.length > 3600 ||
      Number(value.counts.single) + Number(value.counts.multiple) + Number(value.counts.missing) !== value.points.length ||
      Number(value.counts.sceneCuts) > value.points.length) return false
  let previous = -1
  return value.points.every((point,index) => {
    if (!isObject(point) || !nonNegative(point.timeUs) || Number(point.timeUs) >= durationUs ||
        Number(point.timeUs) <= previous || (index === 0 && point.timeUs !== 0) ||
        !['crop','fit'].includes(String(point.frame)) || typeof point.cut !== 'boolean' ||
        ![point.x,point.y].every(n => typeof n === 'number' && n >= 0 && n <= 1)) return false
    previous = Number(point.timeUs)
    return true
  })
}

function validSelection(selection: unknown, transcript: unknown) {
  if (selection == null) return true
  if (!isObject(selection) || typeof selection.id !== 'string' || !isObject(selection.search) || !Array.isArray(selection.clips) ||
    !isObject(transcript) || !Array.isArray(transcript.segments)) return false
  const { mode, minSeconds, maxSeconds, count } = selection.search
  const validInt = (v: unknown, lo: number, hi: number) => Number.isInteger(v) && Number(v) >= lo && Number(v) <= hi
  if (!['structure','semantic'].includes(String(mode)) || !validInt(minSeconds, 1, 180) ||
    !validInt(maxSeconds, Number(minSeconds), 180) || !validInt(count, 1, 10) || selection.clips.length > Number(count)) return false
  const segments = transcript.segments
  return selection.clips.every(c => isObject(c) && validInt(c.startId, 0, segments.length-1) && validInt(c.endId, Number(c.startId), segments.length-1) &&
    segments[Number(c.startId)]?.startUs === c.startUs && segments[Number(c.endId)]?.endUs === c.endUs &&
    validInt(Number(c.endUs)-Number(c.startUs), Number(minSeconds)*1e6, Number(maxSeconds)*1e6) &&
    ['offered','selected','rejected'].includes(String(c.state)) && typeof c.title === 'string' && typeof c.reason === 'string' && (mode === 'structure' ? c.scores === null :
      isObject(c.scores) && ['independence','clarity','opening','completion'].every(k => isObject(c.scores) && validInt(c.scores[k], 0, 4))))
}

function isProject(value: unknown): value is Project {
  return isObject(value) && ['id', 'name', 'updatedUtc', 'videoCodec', 'container']
    .every(key => typeof value[key] === 'string') &&
    (value.edits === undefined || (Array.isArray(value.edits) && value.edits.every(e=>isObject(e) && typeof e.id === 'string' && typeof e.title === 'string'))) &&
    Number.isFinite(Date.parse(String(value.updatedUtc))) && validTranscript(value.transcript, Number(value.durationUs)) && validSelection(value.selection, value.transcript) &&
    (value.frameIntervals === null || (isObject(value.frameIntervals) && nonNegative(value.frameIntervals.frames) &&
      Number(value.frameIntervals.frames) >= 2 && Number(value.frameIntervals.frames) <= 48 &&
      typeof value.frameIntervals.minMs === 'number' && value.frameIntervals.minMs > 0 &&
      typeof value.frameIntervals.maxMs === 'number' && Number.isFinite(value.frameIntervals.maxMs) &&
      value.frameIntervals.maxMs >= value.frameIntervals.minMs)) &&
    ['durationUs', 'width', 'height', 'audioTracks'].every(key => nonNegative(value[key])) &&
    nonNegative(value.proxyAudioTrack) && Number(value.proxyAudioTrack) < Math.max(1, Number(value.audioTracks)) &&
    Array.isArray(value.audioTrackLabels) && value.audioTrackLabels.every(label => typeof label === 'string') &&
    Array.isArray(value.offlineAudioTracks) && value.offlineAudioTracks.every(track => nonNegative(track) && track < Number(value.audioTracks)) &&
    (value.audioTrackLabels.length === 0 || value.audioTrackLabels.length === value.audioTracks) &&
    typeof value.rotation === 'number' && Number.isFinite(value.rotation) &&
    (value.anamorphicAspect === null || (isObject(value.anamorphicAspect) &&
      typeof value.anamorphicAspect.pixelRatio === 'string' && nullableText(value.anamorphicAspect.frameRatio))) &&
    (value.color === null || (isObject(value.color) && typeof value.color.isHdr === 'boolean' &&
      nullableText(value.color.transfer) && nullableText(value.color.primaries) && typeof value.color.canToneMap === 'boolean')) &&
    typeof value.sourceAvailable === 'boolean' && typeof value.usingProxy === 'boolean' &&
    (value.usingProxy ? typeof value.proxyId === 'string' && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(value.proxyId) : value.proxyId === null) &&
    typeof value.proxyNeedsRefresh === 'boolean' &&
    ['fps', 'thumbnailUrl', 'previewUrl'].every(key => nullableText(value[key]))
}

function validTranscript(value: unknown, duration: number) {
  if (value === null) return true
  if (!isObject(value) || !nonNegative(value.audioTrack) || typeof value.model !== 'string' ||
    typeof value.detectedLanguage !== 'string' || typeof value.language !== 'string' ||
    !nonNegative(value.elapsedMs) || !nonNegative(value.peakRamBytes) || !Array.isArray(value.segments)) return false
  let previousEnd = 0
  return value.segments.every(s => {
    if (!isObject(s) || !nonNegative(s.startUs) || !nonNegative(s.endUs) || Number(s.startUs) < previousEnd ||
      Number(s.endUs) <= Number(s.startUs) || Number(s.endUs) > duration || typeof s.text !== 'string') return false
    previousEnd = Number(s.endUs)
    return true
  })
}

function validResult(command: Command, value: unknown) {
  switch (command) {
    case 'exports.start':
    case 'exports.resume':
    case 'exports.retry':
    case 'exports.cancel':
    case 'exports.openFolder': return typeof value === 'boolean'
    case 'exports.list': return isObject(value) && nullableText(value.issue) && Array.isArray(value.jobs) && value.jobs.every(job => isObject(job) &&
      ['id','projectId','clipId','title','fileName'].every(key => typeof job[key] === 'string') &&
      nonNegative(job.revision) && Number(job.revision) > 0 && nonNegative(job.percent) && Number(job.percent) <= 100 &&
      ['queued','running','completed','failed','cancelled','interrupted'].includes(String(job.state)) && nullableText(job.error))
    case 'clips.mark':
    case 'clips.find':
    case 'transcript.start': return isProject(value)
    case 'editor.open':
    case 'editor.autoFrame':
    case 'editor.save':
    case 'editor.create': {
      if (!isObject(value) || typeof value.id !== 'string' || !nonNegative(value.revision) || Number(value.revision) < 1 ||
        !nonNegative(value.audioTrack) || !isObject(value.edit) || !Array.isArray(value.cues)) return false
      const e = value.edit
      return typeof e.title === 'string' && ['crop','fit','auto'].includes(String(e.frame)) &&
        (e.trajectory == null ? e.frame !== 'auto' : validTrajectory(e.trajectory, Number(e.endUs)-Number(e.startUs))) &&
        (e.captionStyle == null || validStyle(e.captionStyle)) &&
        [e.x,e.y].every(n=>typeof n === 'number' && n >= 0 && n <= 1) &&
        nonNegative(e.startUs) && nonNegative(e.endUs) && Number(e.startUs) < Number(e.endUs) &&
        value.cues.every(c=>isObject(c) && nonNegative(c.index) && nonNegative(c.startUs) && nonNegative(c.endUs) &&
          nonNegative(c.sourceStartUs) && nonNegative(c.sourceEndUs) &&
          Math.max(Number(c.sourceStartUs), Number(e.startUs))-Number(e.startUs) === c.startUs &&
          Math.min(Number(c.sourceEndUs), Number(e.endUs))-Number(e.startUs) === c.endUs &&
          Number(c.startUs) < Number(c.endUs) && typeof c.text === 'string')
    }
    case 'editor.preview':
      return isObject(value) && nonNegative(value.revision) && Number(value.revision) > 0 && typeof value.url === 'string' &&
        /^https:\/\/app\.shortforge\.local\/caption-preview\/[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(value.url)
    case 'editor.captionVideo':
    case 'editor.srt': return typeof value === 'boolean'
    case 'projects.list': return Array.isArray(value) && value.every(isProject)
    case 'media.pick':
    case 'media.relink': return value === null || isProject(value)
    case 'projects.open':
    case 'media.measureFrameIntervals':
    case 'media.createProxy':
    case 'media.rebuildProxy':
    case 'media.importDropped': return isProject(value)
    case 'editor.flushed':
    case 'jobs.cancel': return typeof value === 'boolean'
    case 'settings.get':
    case 'settings.useAutomaticTools':
    case 'settings.pickTools': return isObject(value) && nullableText(value.ffmpeg) &&
      nullableText(value.ffprobe) && (value.proxyEncoders === null ||
        (isObject(value.proxyEncoders) && typeof value.proxyEncoders.libx264 === 'boolean' &&
          typeof value.proxyEncoders.aac === 'boolean')) && typeof value.webView === 'string' &&
      typeof value.customFolder === 'boolean' && nullableText(value.settingsError)
  }
}

function asHostError(error: unknown) {
  if (isObject(error) && typeof error.userMessage === 'string' && error.userMessage.trim()) {
    return new HostError(error.userMessage, typeof error.diagnosticId === 'string' ? error.diagnosticId : undefined,
      typeof error.code === 'string' ? error.code : undefined)
  }
  return new HostError('Не удалось прочитать ответ приложения.')
}

type HostEvent = { type: 'import.stage'; stage: string; jobId: string; percent?: number } |
  { type: 'projects.changed'; projectId: string } | { type: 'import.error'; error: HostError }

export function createBridge(port?: HostPort) {
  const listeners = new Set<(event: HostEvent) => void>()
  const pending = new Map<string, (response: Record<string, unknown>) => void>()
  const emit = (event: HostEvent) => listeners.forEach(listener => listener(event))

  port?.addEventListener('message', ({ data }) => {
    if (!isObject(data) || data.version !== 1) return
    if (typeof data.id === 'string') {
      const handler = pending.get(data.id)
      pending.delete(data.id)
      handler?.(data)
      return
    }
    if (!isObject(data.payload)) return
    if (data.type === 'import.stage' && typeof data.payload.stage === 'string' && typeof data.payload.jobId === 'string') {
      const percent = data.payload.percent
      if (percent != null && (typeof percent !== 'number' || !Number.isInteger(percent) || percent < 0 || percent > 100)) return
      emit({ type: 'import.stage', stage: data.payload.stage, jobId: data.payload.jobId, percent: percent ?? undefined })
    } else if (data.type === 'projects.changed' && typeof data.payload.projectId === 'string') {
      emit({ type: 'projects.changed', projectId: data.payload.projectId })
    } else if (data.type === 'import.error') {
      emit({ type: 'import.error', error: asHostError(data.payload) })
    }
  })

  function request<K extends Command>(type: K, payload: Commands[K][0], options?: { file?: File; id?: string }): Promise<Commands[K][1]> {
    return new Promise((resolve, reject) => {
      if (!port) { reject(new HostError(missingHost)); return }
      const id = options?.id ?? crypto.randomUUID()
      const file = options?.file
      const envelope: Envelope = { version: 1, id, type, payload }
      pending.set(id, data => {
        if (data.ok === true && validResult(type, data.result)) resolve(data.result as Commands[K][1])
        else reject(asHostError(data.error))
      })
      try {
        if (file) {
          if (!port.postMessageWithAdditionalObjects) throw new HostError('Перетаскивание недоступно. Нажмите «Выбрать файл».')
          port.postMessageWithAdditionalObjects(envelope, [file])
        } else port.postMessage(envelope)
      } catch (error) {
        pending.delete(id)
        reject(error instanceof HostError ? error : new HostError('Не удалось связаться с приложением. Откройте ShortForge снова.'))
      }
    })
  }

  return {
    available: Boolean(port), request,
    importFile: (file: File) => request('media.importDropped', {}, {file}),
    subscribe(listener: (event: HostEvent) => void) {
      listeners.add(listener)
      return () => { listeners.delete(listener) }
    },
  }
}
