import { useEffect, useRef, useState } from 'react'
import { cropPosition } from './cropPosition'
import type { CaptionPreview, CaptionStyle, CueChange, EditDecision, EditorClip, EditorCue, Project } from './bridge'

const sec = (us: number) => us / 1_000_000
const clamp = (n: number, low: number, high: number) => Math.min(high, Math.max(low, n))
const neutral: CaptionStyle = {preset:'neutral',size:64,color:'#FFFFFF',outline:3,background:false,position:85}
const contrast: CaptionStyle = {preset:'contrast',size:72,color:'#FFFFFF',outline:5,background:true,position:85}
type CuePatch = Pick<CueChange,'index'> & Partial<Omit<CueChange,'index'>>
type Change = { patch: Partial<EditDecision>; cue?: CuePatch; style?: Partial<CaptionStyle> }
type Step = { before: Change; after: Change }
type CuePlayback = {index:number; endUs:number | null}
function cueWarnings(cue:EditorCue,size:number) {
  const characters=Array.from(cue.text.trim()).length
  const speed=Math.ceil(characters/sec(cue.endUs-cue.startUs))
  const lineWidth=Math.max(1,Math.floor(28*64/size))
  const warnings:string[]=[]
  if(speed>20) warnings.push(speed+' зн./с. Сократите текст или увеличьте длительность фразы.')
  if(characters>2*lineWidth || cue.text.split(/\r?\n/).length>2)
    warnings.push('Текст может занять больше двух строк. Проверьте переносы в точном предпросмотре.')
  return warnings
}
type Props = {
  initial: EditorClip; project: Project; preview?: string
  save(revision: number, edit: EditDecision, cue: CueChange | null): Promise<EditorClip>
  render(revision: number, offsetUs: number, jobId: string): Promise<CaptionPreview>
  autoFrame(revision: number, intervalMs: number, jobId: string, progress: (message: string) => void): Promise<EditorClip>
  cancel(jobId: string): Promise<boolean>
  srt(revision: number): Promise<boolean>
  captionVideo(revision: number, jobId: string): Promise<boolean>
  flushed(jobId:string): Promise<boolean>
  close(clip: EditorClip): void
}

function Field({id,label,value,number,maxLength,min,max,step,commit,onDraft}: {
  id?:string; label:string; value:string; number?:boolean; maxLength?:number; min?:number; max?:number; step?:number
  commit(text:string):boolean; onDraft?(value:string):void
}) {
  const [draft,setDraft] = useState(value)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const typing = useRef(false)
  const sent = useRef(value)
  useEffect(()=>{
    if (!typing.current) {setDraft(value); sent.current=value}
  },[value])
  useEffect(()=>()=>clearTimeout(timer.current),[])
  function flush(text:string,blur:boolean){
    clearTimeout(timer.current)
    typing.current=false
    if(text!==sent.current && commit(text)) sent.current=text
    else if(blur && text!==sent.current) setDraft(value)
  }
  return <label>{label}<input id={id} type={number ? 'number' : 'text'} min={min} max={max} step={number ? step ?? 'any' : undefined}
    maxLength={maxLength} value={draft} onChange={e=>{
      const text=e.target.value
      typing.current=true
      setDraft(text)
      onDraft?.(text)
      clearTimeout(timer.current)
      timer.current=setTimeout(()=>flush(text,false),500)
    }}
    onKeyDown={e=>{if(e.key==='Enter') e.currentTarget.blur()}}
    onBlur={e=>flush(e.currentTarget.value,true)}/>{draft!==value && <small>Правка поля</small>}</label>
}

export function ClipEditor({initial,project,preview,save,close,flushed,render,cancel,srt,captionVideo,autoFrame}: Props) {
  const dialog = useRef<HTMLDialogElement>(null)
  const player = useRef<HTMLVideoElement>(null)
  const [clip,setClip] = useState(initial)
  const latest = useRef(initial)
  const tail = useRef(Promise.resolve())
  const writeFailed = useRef(false)
  const fieldInvalid = useRef(false)
  const [fieldEpoch,setFieldEpoch] = useState(0)
  const undo = useRef<Step[]>([])
  const redo = useRef<Step[]>([])
  const [pending,setPending] = useState(0)
  const [error,setError] = useState('')
  const [playError,setPlayError] = useState('')
  const [time,setTime] = useState(initial.edit.startUs)
  const [playing,setPlaying] = useState(false)
  const [mediaReady,setMediaReady] = useState(false)
  const [listening,setListening] = useState<number | null>(null)
  const audition = useRef<CuePlayback | null>(null)
  const [zoom,setZoom] = useState(1)
  const [guides,setGuides] = useState(false)
  const [filter,setFilter] = useState('')
  const [drag,setDrag] = useState<Partial<EditDecision>>({})
  const [draftStyle,setDraftStyle] = useState<Partial<CaptionStyle>>({})
  const [exact,setExact] = useState<(CaptionPreview & {offsetUs:number}) | null>(null)
  const [outputState,setOutputState] = useState<'preview'|'srt'|'video'|'auto'|null>(null)
  const [intervalMs,setIntervalMs] = useState(initial.edit.trajectory?.intervalMs ?? 500)
  const [analysisStage,setAnalysisStage] = useState('')
  const [outputMessage,setOutputMessage] = useState('')
  const [cancelPending,setCancelPending] = useState(false)
  const outputLock = useRef(false)
  const renderJob = useRef<string | null>(null)
  const capturedEpoch = useRef(0)
  const renderedPlayer = useRef<HTMLVideoElement>(null)
  const startDrag = useRef<{x:number;y:number;left:number;top:number;dx:number;dy:number} | null>(null)
  const edit = {...clip.edit,...drag}
  const position = cropPosition(edit,time)
  const analyzing = outputState === 'auto'
  const style = {...(edit.captionStyle ?? neutral),...draftStyle}
  const matchingAudio = project.usingProxy ? project.proxyAudioTrack===clip.audioTrack : project.audioTracks===1 && clip.audioTrack===0
  const available = preview && (project.audioTracks===0 || matchingAudio)
  useEffect(()=>{
    const modal = dialog.current
    const video = player.current
    modal?.showModal()
    function onVisibility() {
      if(document.hidden) stopAudition()
    }
    document.addEventListener('visibilitychange',onVisibility)
    return ()=>{
      document.removeEventListener('visibilitychange',onVisibility)
      audition.current=null
      video?.pause()
      modal?.close()
    }
  },[])
  useEffect(()=>{
    function onFlush(event:Event) {
      event.preventDefault()
      if(document.activeElement instanceof HTMLElement) document.activeElement.blur()
      const token=(event as CustomEvent<string>).detail
      void tail.current.then(()=>{if(!writeFailed.current) return flushed(token)}).catch(()=>{})
    }
    window.addEventListener('shortforge-flush-editor',onFlush)
    return ()=>window.removeEventListener('shortforge-flush-editor',onFlush)
  },[flushed])
  function invalidateOutput() {
    stopAudition()
    capturedEpoch.current++
    setExact(null)
    setOutputMessage('')
  }
  function invalid(message:string) {
    fieldInvalid.current=true
    setError(message)
    return false
  }
  function forgetOldTrajectory() {
    for (const history of [undo, redo]) {
      for (const step of history.current) {
        for (const edit of [step.before, step.after]) {
          if (edit.patch.frame === 'auto') edit.patch.frame = 'fit'
        }
      }
    }
  }
  function enqueue(change: Change | 'undo' | 'redo') {
    invalidateOutput()
    fieldInvalid.current=false
    setError('')
    setPending(n=>n+1)
    tail.current = tail.current.then(async()=>{
      const old = latest.current
      const step = typeof change === 'string' ? (change==='undo' ? undo : redo).current.at(-1) : undefined
      const next = typeof change !== 'string' ? change : change==='undo' ? step?.before : step?.after
      if (!next) return
      const decision = {...old.edit,...next.patch}
      if(next.style) decision.captionStyle={...(old.edit.captionStyle ?? neutral),...next.style}
      if (!Number.isSafeInteger(decision.startUs) || !Number.isSafeInteger(decision.endUs) ||
          decision.startUs < 0 || decision.endUs <= decision.startUs || decision.endUs > project.durationUs)
        throw new Error('Укажите In и Out в пределах исходника. Out должен быть позже In.')
      const oldCue = next.cue && old.cues.find(c=>c.index===next.cue?.index)
      if (next.cue && !oldCue) throw new Error('Фраза находится вне текущих границ.')
      let cue: CueChange | null = null
      let previousCue: CueChange | undefined
      if(next.cue && oldCue){
        cue={index:oldCue.index,text:next.cue.text ?? oldCue.text}
        previousCue={index:oldCue.index,text:oldCue.text}
        if(next.cue.sourceStartUs !== undefined || next.cue.sourceEndUs !== undefined){
          cue.sourceStartUs=next.cue.sourceStartUs ?? oldCue.sourceStartUs
          cue.sourceEndUs=next.cue.sourceEndUs ?? oldCue.sourceEndUs
          previousCue.sourceStartUs=oldCue.sourceStartUs
          previousCue.sourceEndUs=oldCue.sourceEndUs
        }
      }
      const patchKeys=Object.keys(next.patch) as (keyof EditDecision)[]
      if(next.style) patchKeys.push('captionStyle')
      const unchanged = patchKeys.every(k=>k==='captionStyle' ?
          Object.entries(decision.captionStyle ?? neutral).every(([key,v])=>(old.edit.captionStyle ?? neutral)[key as keyof CaptionStyle]===v) :
          old.edit[k]===decision[k]) && cue?.text===previousCue?.text &&
          cue?.sourceStartUs===previousCue?.sourceStartUs && cue?.sourceEndUs===previousCue?.sourceEndUs
      if (unchanged && !step) return
      const saved = unchanged ? old : await save(old.revision,decision,cue)
      if (change === 'undo') redo.current.push(undo.current.pop()!)
      else if (change === 'redo') undo.current.push(redo.current.pop()!)
      else {
        const previous = Object.fromEntries(patchKeys.map(k=>[k,old.edit[k]]))
        const after = Object.fromEntries(patchKeys.map(k=>[k,decision[k]]))
        undo.current.push({before:{patch:previous,cue:previousCue},after:{patch:after,cue:cue ?? undefined}})
        if(undo.current.length>100) undo.current.shift()
        redo.current = []
      }
      if (old.edit.trajectory && !saved.edit.trajectory) forgetOldTrajectory()
      writeFailed.current=false
      latest.current=saved
      setClip(saved)
    }).catch((e:unknown)=>{
      writeFailed.current=true
      setDraftStyle({})
      setFieldEpoch(n=>n+1)
      setError(e instanceof Error ? e.message : 'Не удалось сохранить правку.')
    })
      .finally(()=>setPending(n=>n-1))
  }
  function leave() {
    stopAudition()
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur()
    if(outputLock.current) return
    void tail.current.then(()=>{if(!writeFailed.current && !fieldInvalid.current) close(latest.current)})
  }
  async function createOutput(kind:'preview'|'srt'|'video'|'auto') {
    if(outputLock.current) return
    stopAudition()
    if(document.activeElement instanceof HTMLInputElement) document.activeElement.blur()
    outputLock.current=true
    const epoch=capturedEpoch.current
    try {
      await tail.current
      if(writeFailed.current || fieldInvalid.current || epoch!==capturedEpoch.current) return
      const {edit,revision}=latest.current
      setOutputState(kind)
      setOutputMessage('')
      if(kind==='srt'){
        if(await srt(revision)) setOutputMessage('Файл SRT сохранён.')
        return
      }
      player.current?.pause()
      const jobId=crypto.randomUUID()
      renderJob.current=jobId
      if(kind==='auto'){
        setAnalysisStage('Проверяем исходное видео…')
        renderedPlayer.current?.pause()
        const result = await autoFrame(revision, intervalMs, jobId, setAnalysisStage)
        if (result.id !== latest.current.id || result.revision <= revision || result.edit.frame !== 'auto')
          throw new Error('Не удалось подтвердить сохранение автокадра. Откройте клип снова.')
        setExact(null)
        if (edit.trajectory) forgetOldTrajectory()
        if (edit.frame !== 'auto') {
          undo.current.push({before:{patch:{frame:edit.frame}},after:{patch:{frame:'auto'}}})
          if (undo.current.length > 100) undo.current.shift()
        }
        redo.current = []
        latest.current = result
        setClip(result)
        return
      }
      if(kind==='video'){
        if(await captionVideo(revision,jobId)) setOutputMessage(latest.current.cues.length ? 'Клип с субтитрами сохранён в MP4.' : 'Клип сохранён в MP4.')
        return
      }
      setExact(null)
      const offsetUs=time>=edit.endUs ? 0 : clamp(time-edit.startUs,0,edit.endUs-edit.startUs-1)
      const result=await render(revision,offsetUs,jobId)
      if(result.revision!==revision) throw new Error('Редакция изменилась. Запустите предпросмотр ещё раз.')
      if(epoch===capturedEpoch.current && result.revision===latest.current.revision) setExact({...result,offsetUs})
    } catch(e) {
      if(epoch===capturedEpoch.current) setOutputMessage(e instanceof Error ? e.message : 'Не удалось подготовить файл.')
    } finally {
      renderJob.current=null
      outputLock.current=false
      setOutputState(null)
      setCancelPending(false)
    }
  }
  async function cancelRender() {
    const id=renderJob.current
    if(!id) return
    setCancelPending(true)
    try {
      if(!(await cancel(id)) && renderJob.current===id) setCancelPending(false)
    } catch(e) {
      if(renderJob.current===id) {
        setOutputMessage(e instanceof Error ? e.message : 'Не удалось отменить рендер.')
        setCancelPending(false)
      }
    }
  }
  function stopAudition(){
    if(!audition.current) return
    audition.current=null
    setListening(null)
    player.current?.pause()
  }
  function seek(value:number) {
    stopAudition()
    const us = clamp(value,latest.current.edit.startUs,latest.current.edit.endUs)
    if(player.current?.readyState) player.current.currentTime=sec(us)
    setTime(us)
  }
  async function listenToCue(index:number) {
    const previous=audition.current
    stopAudition()
    const v=player.current
    if(previous?.index===index || !v || !available || !mediaReady) return
    if(document.activeElement instanceof HTMLInputElement) document.activeElement.blur()
    const range:CuePlayback={index,endUs:null}
    audition.current=range
    setListening(index)
    v.pause()
    renderedPlayer.current?.pause()
    await tail.current
    if(audition.current!==range) return
    const saved=latest.current
    const cue=saved.cues.find(c=>c.index===index)
    if(writeFailed.current || fieldInvalid.current || !cue) {stopAudition();return}
    // Local cue times already include intersection with both clip boundaries.
    range.endUs=saved.edit.startUs+cue.endUs
    v.currentTime=sec(saved.edit.startUs+cue.startUs)
    try { await v.play() }
    catch {
      if(audition.current!==range) return
      stopAudition()
      setPlayError('Не удалось воспроизвести видео. Проверьте прокси в проекте.')
    }
  }
  function updatePlayhead(v:HTMLVideoElement) {
    if(v.paused || v.seeking) return
    const t=Math.round(v.currentTime*1e6)
    const end=audition.current?.endUs ?? latest.current.edit.endUs
    if(t>=end){v.pause();seek(end);return}
    setTime(t)
  }
  useEffect(()=>{
    const v=player.current
    if(v?.readyState && (v.currentTime<sec(clip.edit.startUs) || v.currentTime>sec(clip.edit.endUs))) seek(clip.edit.startUs)
  },[clip.edit.startUs,clip.edit.endUs])
  useEffect(()=>{
    if(!playing) return
    let frame=0
    function tick(){
      const v=player.current
      if(!v || v.paused) return
      updatePlayhead(v)
      frame=requestAnimationFrame(tick)
    }
    tick()
    return ()=>cancelAnimationFrame(frame)
  },[playing])
  function toggle(){
    const v=player.current
    if(!available || !v) return
    if(audition.current){stopAudition();return}
    if(!v.paused){v.pause();return}
    if(v.currentTime>=sec(clip.edit.endUs)-.001 || v.currentTime<sec(clip.edit.startUs)) seek(clip.edit.startUs)
    renderedPlayer.current?.pause()
    void v.play().catch(e=>{if(e.name!=='AbortError') setPlayError('Не удалось воспроизвести видео. Проверьте прокси в проекте.')})
  }
  function boundary(key:'startUs'|'endUs', text:string){
    const us=Math.round(Number(text)*1e6)
    if(!text.trim() || !Number.isSafeInteger(us) || (key==='startUs' ? us<0 || us>=edit.endUs : us<=edit.startUs || us>project.durationUs)){
      return invalid('Укажите In и Out в пределах исходника. Out должен быть позже In.')
    }
    enqueue({patch:{[key]:us}})
    return true
  }
  function finishSlide(key:'startUs'|'endUs'|'x'|'y', value:number){
    enqueue({patch:{[key]:value}})
    setDrag({})
  }
  function cueBoundary(cue:EditorCue,key:'startUs'|'endUs',text:string) {
    const offset=Math.round(Number(text)*1e6)
    if(!text.trim() || !Number.isSafeInteger(offset) || offset<0 || offset>clip.edit.endUs-clip.edit.startUs ||
        (key==='startUs' ? offset>=cue.endUs : offset<=cue.startUs)) {
      return invalid('Укажите начало и конец фразы в пределах клипа. Конец должен быть позже начала.')
    }
    if(offset===cue[key]) return true
    enqueue({patch:{},cue:{index:cue.index,[key==='startUs'?'sourceStartUs':'sourceEndUs']:clip.edit.startUs+offset}})
    return true
  }
  function styleNumber(key:'size'|'outline'|'position',label:string,min:number,max:number) {
    const inRange=(value:string)=>value.trim()!=='' && Number.isInteger(Number(value)) && Number(value)>=min && Number(value)<=max
    return <Field id={'caption-'+key} label={label} value={String((clip.edit.captionStyle ?? neutral)[key])}
      number min={min} max={max} step={1}
      onDraft={text=>{if(inRange(text)) setDraftStyle(d=>({...d,[key]:Number(text)}))}}
      commit={text=>{
        setDraftStyle(d=>{const next={...d}; delete next[key]; return next})
        if(!inRange(text)) return invalid(label+' — целое число от '+min+' до '+max+'.')
        enqueue({patch:{},style:{[key]:Number(text)}})
        return true
      }}/>
  }
  const length=edit.endUs-edit.startUs
  const span=Math.max(clip.edit.endUs-clip.edit.startUs,Math.round(project.durationUs/zoom))
  const left=clamp(Math.floor((clip.edit.startUs+clip.edit.endUs-span)/2),0,project.durationUs-span)
  const activeCue=clip.cues.find(c=>c.startUs<=time-clip.edit.startUs && time-clip.edit.startUs<c.endUs)
  return <dialog ref={dialog} className="clip-editor" aria-labelledby="edit-heading" onCancel={e=>{e.preventDefault();leave()}}
    onChange={e=>{
      const el=e.target
      if(el instanceof HTMLInputElement && el.type!=='search' && el.type!=='checkbox' && el.getAttribute('aria-label')!=='Позиция клипа') invalidateOutput()
    }}
    onKeyDown={e=>{
      if(analyzing || (e.target as HTMLElement).closest('input,textarea,select,video,[contenteditable=true]')) return
      if((e.ctrlKey || e.metaKey) && ['z','y'].includes(e.key.toLowerCase())){
        e.preventDefault();enqueue(e.key.toLowerCase()==='y' || e.shiftKey ? 'redo' : 'undo')
      } else if(e.code==='Space' && !(e.target as HTMLElement).closest('button')){e.preventDefault();toggle()}
      else if(e.key==='ArrowLeft' || e.key==='ArrowRight'){e.preventDefault();seek(time+(e.key==='ArrowLeft'?-100000:100000))}
    }}>
    <header><button className="secondary" disabled={!!outputState} onClick={leave}>К проекту</button><h2 id="edit-heading">Редактор клипа</h2>
      <span role="status">{pending ? 'Сохранение…' : error ? 'Проверьте правку' : 'Сохранено'}</span></header>
    {error && <div className="caption-edit-error"><p role="alert">{error}</p>
      <button className="text-button" disabled={!!pending} onClick={()=>{
        fieldInvalid.current=false;writeFailed.current=false;setError('');setDraftStyle({});setFieldEpoch(n=>n+1)
      }}>Вернуться к сохранённым значениям</button></div>}
    <div className="edit-grid">
      <section key={'props'+fieldEpoch} className="edit-props" inert={analyzing}><h3>Монтаж</h3>
        <Field label="Название клипа" maxLength={240} value={clip.edit.title} commit={text=>{
          if(!text.trim()) return invalid('Введите название клипа.')
          enqueue({patch:{title:text.trim()}});return true
        }}/>
        <div className="edit-times">{(['startUs','endUs'] as const).map((key,i)=>
          <Field key={key} label={i?'Out, с':'In, с'} number value={String(sec(clip.edit[key]))} commit={t=>boundary(key,t)}/>)}</div>
        <p>Длительность {sec(length).toFixed(3)} с · Исходник {sec(project.durationUs).toFixed(3)} с</p>
        {edit.trajectory && <small>После изменения In или Out автокадр нужно рассчитать снова.</small>}
        <div className="clip-actions"><button className="secondary" disabled={!undo.current.length || !!pending} onClick={()=>enqueue('undo')}>Отменить правку</button>
          <button className="secondary" disabled={!redo.current.length || !!pending} onClick={()=>enqueue('redo')}>Повторить правку</button></div>
        <h3>Кадр 9:16</h3>
        <label>Интервал анализа<select value={intervalMs} onChange={e=>setIntervalMs(Number(e.target.value))}>
          {[100,250,500,1000,2000].map(ms=><option value={ms} key={ms}>{ms} мс</option>)}</select></label>
        <div className="clip-actions">
          <button className="secondary" disabled={!!outputState || !project.sourceAvailable} onClick={()=>void createOutput('auto')}>Автокадр по лицу</button>
          {edit.frame !== 'auto' && edit.trajectory &&
            <button className="text-button" onClick={()=>enqueue({patch:{frame:'auto'}})}>Вернуть автокадр</button>}
        </div>
        {edit.frame === 'auto' && edit.trajectory && <>
          <p role="status">Автокадр включён · интервал {edit.trajectory.intervalMs} мс</p>
          {edit.trajectory.counts.multiple > 0 && <small>Сцены с несколькими лицами вписаны целиком.</small>}
          {edit.trajectory.counts.single === 0 && edit.trajectory.counts.multiple === 0 &&
            <small>Лицо не обнаружено. Видео вписано целиком.</small>}
          {edit.trajectory.counts.single > 0 && edit.trajectory.counts.multiple === 0 &&
            edit.trajectory.points.every(p=>p.frame === 'fit') &&
            <small>Чтобы лицо оставалось в кадре, клип вписан целиком.</small>}
          <button className="text-button" onClick={()=>enqueue({patch:{frame:position.frame,x:position.x,y:position.y}})}>
            Зафиксировать этот кадр</button>
        </>}
        <div className="clip-actions">{(['crop','fit'] as const).map(mode=><button className="secondary" key={mode} aria-pressed={edit.frame===mode}
          onClick={()=>enqueue({patch:{frame:mode}})}>{mode==='fit'?'Вписать':'Заполнить'}</button>)}</div>
        {edit.frame==='crop' && <>{(['x','y'] as const).map((k,i)=><label key={k}>{i?'По вертикали':'По горизонтали'}
          <input type="range" min={0} max={1} step={.001} value={edit[k]}
            onChange={e=>setDrag({[k]:e.target.valueAsNumber})} onPointerUp={e=>finishSlide(k,e.currentTarget.valueAsNumber)}
            onKeyUp={e=>finishSlide(k,e.currentTarget.valueAsNumber)} onBlur={e=>{if(drag[k]!==undefined) finishSlide(k,e.currentTarget.valueAsNumber)}}/></label>)}
          <button className="text-button" onClick={()=>enqueue({patch:{x:.5,y:.5}})}>Сбросить положение</button></>}
        <label><input type="checkbox" checked={guides} onChange={e=>setGuides(e.target.checked)}/> Безопасная зона · ориентировочно</label>
        <p>Паузы внутри клипа сохраняются.</p>
      </section>
      <section className="edit-monitor"><div className="edit-frame" onPointerDown={e=>{
        const v=player.current, box=e.currentTarget.getBoundingClientRect()
        if(analyzing || edit.frame!=='crop' || !v?.videoWidth) return
        const scale=Math.max(box.width/v.videoWidth,box.height/v.videoHeight)
        startDrag.current={x:e.clientX,y:e.clientY,left:edit.x,top:edit.y,dx:v.videoWidth*scale-box.width,dy:v.videoHeight*scale-box.height}
        e.currentTarget.setPointerCapture(e.pointerId)
      }} onPointerMove={e=>{
        const d=startDrag.current
        if(d) {invalidateOutput();setDrag({x:d.dx>1?clamp(d.left-(e.clientX-d.x)/d.dx,0,1):d.left,y:d.dy>1?clamp(d.top-(e.clientY-d.y)/d.dy,0,1):d.top})}
      }} onPointerUp={()=>{if(startDrag.current && (drag.x!==undefined || drag.y!==undefined)) enqueue({patch:drag});startDrag.current=null;setDrag({})}}
         onPointerCancel={()=>{startDrag.current=null;setDrag({})}}>
        {available ? <video ref={player} src={preview} preload="metadata" style={{objectFit:position.frame==='crop'?'cover':'contain',
          objectPosition:position.frame==='crop'?position.x*100+'% '+position.y*100+'%':'center'}}
          onLoadedMetadata={()=>{seek(clip.edit.startUs);setMediaReady(true)}} onPlay={e=>setPlaying(!e.currentTarget.paused)}
          onPause={e=>setPlaying(!e.currentTarget.paused)} onTimeUpdate={e=>updatePlayhead(e.currentTarget)}
          onEnded={()=>{stopAudition();setPlaying(false);setTime(clip.edit.endUs)}} onSeeked={e=>setTime(Math.round(e.currentTarget.currentTime*1e6))}
          onError={()=>{stopAudition();setPlayError('Для этого видео нужен совместимый прокси. Откройте проект для его создания.')}}/>
          : <p>Для просмотра откройте исходник или прокси с дорожкой клипа.</p>}
        {guides && <div className="edit-guides"/>}{activeCue && <div className="edit-caption" style={{
          bottom:(100-style.position)+'%',fontSize:(style.size/1080*100)+'cqw',color:style.color,
          WebkitTextStroke:(2*style.outline/1080*100)+'cqw black',
        }}><span style={{backgroundColor:style.background?'#111d':'transparent'}}>{activeCue.text}</span></div>}
      </div>
      {playError && <p role="alert">{playError}</p>}
      <button className="secondary" disabled={!available || !!playError} onClick={toggle}>{playing?'Пауза':'Воспроизвести клип'}</button>
      <output>Клип {sec(clamp(time-edit.startUs,0,length)).toFixed(3)} с · Исходник {sec(time).toFixed(3)} с</output>
      <small>Оформление в редакторе приблизительное.</small>
      <div className="caption-actions">
        <button className="secondary" id="caption-render" disabled={!!outputState || !project.sourceAvailable}
          onClick={()=>void createOutput('preview')}>Точный предпросмотр</button>
        <button className="secondary" id="caption-video" disabled={!!outputState || !project.sourceAvailable}
          onClick={()=>void createOutput('video')}>{clip.cues.length?'Сохранить MP4 с субтитрами':'Сохранить MP4'}</button>
        <button className="text-button" id="caption-srt" disabled={!!outputState || !clip.cues.length}
          onClick={()=>void createOutput('srt')}>Сохранить SRT</button>
      </div>
      {!project.sourceAvailable && <small>Для рендера укажите исходное видео в проекте.</small>}
      {analyzing && <div className="caption-render-status"><span role="status">{analysisStage}</span>
        <button className="text-button" disabled={cancelPending} onClick={()=>void cancelRender()}>Отменить анализ</button></div>}
      {(outputState==='preview' || outputState==='video') && <div className="caption-render-status">
        <span role="status">{outputState==='video'?'FFmpeg сохраняет клип…':'FFmpeg готовит фрагмент…'}</span>
        <button className="text-button" id="caption-cancel" disabled={cancelPending} onClick={()=>void cancelRender()}>
          {cancelPending?'Отмена…':outputState==='video'?'Отменить сохранение MP4':'Отменить предпросмотр'}</button></div>}
      {outputMessage && <p role="status">{outputMessage}</p>}
      {exact && exact.revision===clip.revision && !pending && <figure className="caption-result">
        <figcaption>Точный предпросмотр · с {sec(exact.offsetUs).toFixed(3)} с</figcaption>
        <video ref={renderedPlayer} key={exact.url} className="caption-rendered" controls src={exact.url} preload="auto"
          aria-label="Предпросмотр с вшитыми субтитрами" onPlay={()=>{stopAudition();player.current?.pause()}}
          onError={()=>{setExact(null);setOutputMessage('Не удалось открыть видео предпросмотра. Запустите рендер ещё раз.')}}/>
      </figure>}</section>
      <section key={'cues'+fieldEpoch} className="edit-cues" inert={analyzing}><h3>Субтитры</h3>
        <details className="caption-controls" open><summary>Оформление</summary>
          <label>Пресет<select id="caption-preset" value={style.preset} onChange={e=>{
            setDraftStyle({});enqueue({patch:{captionStyle:e.target.value==='contrast'?contrast:neutral}})
          }}><option value="neutral">Нейтральный</option><option value="contrast">Контрастный</option>
              <option disabled value="word">Подсветка слова</option></select></label>
          <small>Тайминг задан для фраз. Для подсветки нужны времена отдельных слов.</small>
          <div className="caption-fields">
            {styleNumber('size','Размер, px',32,112)}
            <label>Цвет<input id="caption-color" type="color" value={style.color} onChange={e=>{
              const color=e.currentTarget.value.toUpperCase()
              setDraftStyle(d=>({...d,color}))
              enqueue({patch:{},style:{color}})
              void tail.current.then(()=>setDraftStyle(d=>{
                if(d.color!==color) return d
                const next={...d}
                delete next.color
                return next
              }))
            }}/></label>
            {styleNumber('outline','Обводка, px',0,10)}
            {styleNumber('position','Позиция, % от верха',10,94)}
          </div>
          <label><input id="caption-background" type="checkbox" checked={style.background} onChange={e=>
            enqueue({patch:{},style:{background:e.target.checked}})}/> Тёмный фон</label>
        </details>
        <h3>Текст клипа</h3>
        {!!clip.cues.length && <label>Поиск по фразам<input type="search" value={filter} onChange={e=>setFilter(e.target.value)}/></label>}
        {!clip.cues.length && <p>В этом интервале нет распознанных фраз.</p>}
        {clip.cues.filter(c=>c.text.toLocaleLowerCase().includes(filter.toLocaleLowerCase())).map(c=>
          <div key={c.index} className="caption-cue"><div className="cue-actions">
            <button className="text-button" title="Перейти к началу фразы" onClick={()=>seek(clip.edit.startUs+c.startUs)}>
              {sec(c.startUs).toFixed(3)} – {sec(c.endUs).toFixed(3)} с</button>
            <button className="secondary" disabled={!available || !mediaReady || !!playError || !project.audioTracks || !!outputState}
              aria-label={(listening===c.index?'Остановить фразу ':'Прослушать фразу ')+(c.index+1)}
              onClick={()=>void listenToCue(c.index)}>{listening===c.index?'Остановить фразу':'Прослушать фразу'}</button>
          </div>
            <Field label={'Фраза '+(c.index+1)} maxLength={8000} value={c.text} commit={text=>{
              if(!text.trim()) return invalid('Введите текст фразы.')
              enqueue({patch:{},cue:{index:c.index,text}});return true
            }}/>
            <div className="cue-timing">
              <Field label="Начало в клипе, с" number min={0} max={sec(length)} value={String(sec(c.startUs))} commit={t=>cueBoundary(c,'startUs',t)}/>
              <Field label="Конец в клипе, с" number min={0} max={sec(length)} value={String(sec(c.endUs))} commit={t=>cueBoundary(c,'endUs',t)}/>
            </div>
            {cueWarnings(c,style.size).map(w=><p key={w} className="cue-warning">{w}</p>)}
          </div>)}</section>
    </div>
    <section className="edit-timeline" inert={analyzing}><h3>Шкала исходника</h3>
      <label>Масштаб<select value={zoom} onChange={e=>setZoom(Number(e.target.value))}>{[1,2,4,8,16].map(z=><option key={z} value={z}>{z}×</option>)}</select></label>
      <small>{sec(left).toFixed(3)} – {sec(left+span).toFixed(3)} с</small>
      {(['startUs','endUs'] as const).map((key,i)=><label key={key}>{i?'Out':'In'}
        <input aria-label={i?'Граница Out':'Граница In'} type="range" min={left} max={left+span} step="any" value={edit[key]}
          onChange={e=>{const value=Math.round(e.target.valueAsNumber)
            setDrag({[key]:i?clamp(value,clip.edit.startUs+1,project.durationUs):clamp(value,0,clip.edit.endUs-1)})}}
          onPointerUp={e=>finishSlide(key,Math.round(e.currentTarget.valueAsNumber))} onKeyUp={e=>finishSlide(key,Math.round(e.currentTarget.valueAsNumber))}
          onBlur={()=>{if(drag[key]!==undefined) finishSlide(key,drag[key]!)}}/></label>)}
      <label>Просмотр<input type="range" aria-label="Позиция клипа" min={edit.startUs} max={edit.endUs} step="any" value={clamp(time,edit.startUs,edit.endUs)}
        onChange={e=>seek(Math.round(e.target.valueAsNumber))}/></label>
    </section>
  </dialog>
}
