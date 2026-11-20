import { useState } from 'react'
import type { ClipCandidate, ClipSearch, ClipState, Project } from './bridge'

function position(us: number) {
  const ms = Math.round(us / 1000)
  return Math.floor(ms/60000) + ':' + String(Math.floor(ms/1000)%60).padStart(2,'0') + ',' + String(ms%1000).padStart(3,'0')
}

export function Clips({project, busy, playable, find, preview, mark, edit}: {
  edit(selectionId: string, startId: number): void
  project: Project; busy: boolean; playable: boolean; find(search: ClipSearch): void; preview(clip: ClipCandidate, edges?: boolean): void
  mark(selectionId:string, startId:number, state:ClipState): void
}) {
  const selection = project.selection
  const [search, setSearch] = useState<ClipSearch>(selection?.search ?? { mode:'structure', minSeconds:30, maxSeconds:60, count:10 })
  const speech = project.transcript?.segments
  const valid = Number.isInteger(search.minSeconds) && Number.isInteger(search.maxSeconds) && search.minSeconds >= 1 &&
    search.maxSeconds >= search.minSeconds && search.maxSeconds <= 180 && Number.isInteger(search.count) && search.count >= 1 && search.count <= 10
  const modeLabel = (mode: string) => mode === 'semantic' ? 'Смысловой анализ · Qwen 2.5 Coder 7B' : 'По структуре речи'
  return <section className="workflow clip-panel"><h2 className="subhead">Подбор фрагментов</h2>
    {speech?.length ? <>
      <div className="clip-fields"><label>Режим подбора<select disabled={busy} value={search.mode}
        onChange={e => setSearch({...search,mode:e.target.value as ClipSearch['mode']})}>
        <option value="structure">По структуре речи</option><option value="semantic">Смысловой анализ · локальная Ollama</option></select></label>
      {([['minSeconds','От, с',180],['maxSeconds','До, с',180],['count','Количество',10]] as const).map(([key,label,max]) =>
        <label key={key}>{label}<input type="number" min={1} max={max} disabled={busy} value={search[key] || ''}
          onChange={e=>setSearch({...search,[key]:e.currentTarget.valueAsNumber || 0})}/></label>)}</div>
      <button className="secondary" disabled={busy || !valid} onClick={()=>find(search)}>Найти фрагменты</button>
      {!valid && <p>Укажите диапазон от 1 до 180 секунд и количество от 1 до 10.</p>}
    </> : <p>{speech ? 'В расшифровке нет речи для подбора.' : 'Сначала распознайте речь.'}</p>}
    {selection && <div className="clip-results" aria-label="Предложения">
      <h3>{modeLabel(selection.search.mode)} · {selection.clips.length}</h3>
      {!selection.clips.length && <p>Подходящих фрагментов не найдено. Попробуйте другой диапазон длительности.</p>}
      {!!selection.clips.length && !playable && <p>Для просмотра нужен доступный предпросмотр со звуковой дорожкой расшифровки. При необходимости создайте прокси с этой дорожкой.</p>}
      <ol>{selection.clips.filter(c=>c.state !== 'rejected').map(c=><li key={c.startId}>
        <strong>{c.title}</strong><p>{position(c.startUs)} – {position(c.endUs)} · {((c.endUs-c.startUs)/1e6).toLocaleString('ru-RU')} с</p>
        {speech?.[c.startId].text !== c.title && <p>{speech?.[c.startId].text}</p>}
        <p>{c.reason}</p>{c.scores && <details><summary>Оценки модели</summary>
          <p>Самостоятельность {c.scores.independence}/4 · Ясность {c.scores.clarity}/4 · Начало {c.scores.opening}/4 · Завершённость {c.scores.completion}/4</p></details>}
        <div className="clip-actions"><button className="secondary clip-preview" disabled={!playable || busy} onClick={()=>preview(c)}>Посмотреть фрагмент</button>
          <button className="secondary clip-edges" disabled={!playable || busy} title="Первые и последние 3 секунды. Короткий фрагмент прозвучит целиком."
            onClick={()=>preview(c,true)}>Послушать границы</button>
          <button className="secondary" disabled={busy} onClick={()=>edit(selection.id,c.startId)}>Открыть в редакторе</button>
          <label><input type="checkbox" checked={c.state === 'selected'} disabled={busy} onChange={e=>mark(selection.id,c.startId,e.target.checked ? 'selected' : 'offered')}/> Выбрать</label>
          <button className="text-button" disabled={busy} onClick={()=>mark(selection.id,c.startId,'rejected')}>Отклонить</button></div>
      </li>)}</ol>
      {selection.clips.some(c=>c.state === 'rejected') && <details className="rejected-clips"><summary>Отклонённые фрагменты</summary>
        {selection.clips.filter(c=>c.state === 'rejected').map(c=><p key={c.startId}>{c.title} <button className="text-button" disabled={busy}
          onClick={()=>mark(selection.id,c.startId,'offered')}>Вернуть</button></p>)}</details>}
    </div>}
  </section>
}
