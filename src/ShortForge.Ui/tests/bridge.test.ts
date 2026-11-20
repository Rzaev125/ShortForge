import { expect, it, vi } from 'vitest'
import { createBridge, type HostPort } from '../src/bridge'

function setup() {
  const events = new EventTarget()
  const port: HostPort = {
    postMessage: vi.fn(),
    postMessageWithAdditionalObjects: vi.fn(),
    addEventListener: events.addEventListener.bind(events),
  }
  const bridge = createBridge(port)
  const emit = (data: unknown) => events.dispatchEvent(new MessageEvent('message', { data }))
  return { bridge, port, emit }
}

it('matches a versioned response to the initiating request', async () => {
  const { bridge, port, emit } = setup()
  const pending = bridge.request('projects.list', {})
  const request = vi.mocked(port.postMessage).mock.calls[0][0]
  emit({ version: 2, id: request.id, ok: true, result: [42] })
  emit({ version: 1, id: 'unrelated', ok: true, result: [42] })
  emit({ version: 1, id: request.id, ok: true, result: [] })
  await expect(pending).resolves.toEqual([])
})

it.each([
  {frame:'crop',timeUs:0,x:.4, valid:true},
  {frame:'crop',timeUs:500000,x:.4, valid:false},
  {frame:'fit',timeUs:0,x:2, valid:false},
])('validates host framing samples before opening the editor (%j)', async sample => {
  const {bridge,port,emit}=setup()
  const pending=bridge.request('editor.open', {projectId:crypto.randomUUID(), clipId:crypto.randomUUID()})
  const {id}=vi.mocked(port.postMessage).mock.calls[0][0]
  const edit={title:'Клип',frame:'auto',x:.5,y:.5,startUs:1000000,endUs:1500000,
    trajectory:{intervalMs:500,points:[{timeUs:sample.timeUs,frame:sample.frame,x:sample.x,y:.5,cut:true}],
      counts:{single:1,multiple:0,missing:0,sceneCuts:0}}}
  const result={id:crypto.randomUUID(),revision:2,audioTrack:0,cues:[],edit}
  emit({version:1,id,ok:true,result})
  if(sample.valid) await expect(pending).resolves.toEqual(result)
  else await expect(pending).rejects.toThrow(/ответ/i)
})

it('reports damaged project data as a readable failure', async () => {
  const { bridge, port, emit } = setup()
  const pending = bridge.request('projects.list', {})
  const request = vi.mocked(port.postMessage).mock.calls[0][0]
  emit({ version: 1, id: request.id, ok: true, result: [{ id: 42 }] })
  await expect(pending).rejects.toThrow(/ответ/i)
})

it('sends a dropped file as an additional object, without reading it', async () => {
  const { bridge, port, emit } = setup()
  const file = new File(['hello'], "Лекция O'Brien.mp4")
  const pending = bridge.importFile(file)
  const [request, objects] = vi.mocked(port.postMessageWithAdditionalObjects!).mock.calls[0]
  expect(request.type).toBe('media.importDropped')
  expect(request.payload).toEqual({})
  expect(objects).toEqual([file])
  expect(port.postMessage).not.toHaveBeenCalled()
  emit({ version: 1, id: request.id, ok: false, error: { code: 'invalid', userMessage: 'Файл повреждён', diagnosticId: 'abc', retryable: false } })
  await expect(pending).rejects.toMatchObject({ message: 'Файл повреждён', diagnosticId: 'abc', code: 'invalid' })
})

it('rejects malformed encoder availability from the host', async () => {
  const { bridge, port, emit } = setup()
  const pending = bridge.request('settings.get', {})
  const { id } = vi.mocked(port.postMessage).mock.calls[0][0]
  emit({ version: 1, id, ok: true, result: {
    ffmpeg: 'ffmpeg', ffprobe: 'ffprobe', webView: 'test', customFolder: false,
    settingsError: null, proxyEncoders: { libx264: 'false', aac: true },
  } })
  await expect(pending).rejects.toThrow(/ответ/i)
})
