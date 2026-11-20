import { expect, it } from 'vitest'
import type { EditDecision } from '../src/bridge'
import { cropPosition } from '../src/cropPosition'

const edit: EditDecision = {
  title:'Клип', frame:'auto', startUs:8_000_000, endUs:11_000_000, x:.4, y:.55,
  trajectory:{intervalMs:500,counts:{single:4,multiple:0,missing:1,sceneCuts:1},points:[
    {timeUs:0,frame:'crop',x:0,y:.25,cut:true},
    {timeUs:500_000,frame:'crop',x:.6,y:.75,cut:false},
    {timeUs:1_000_000,frame:'crop',x:1,y:1,cut:true},
    {timeUs:1_500_000,frame:'fit',x:.5,y:.5,cut:false},
    {timeUs:2_000_000,frame:'crop',x:0,y:0,cut:false},
  ]}
}
it('uses the saved source In and interpolates only within a continuous crop', () => {
  expect(cropPosition(edit,8_250_000)).toMatchObject({frame:'crop',x:.3,y:.5})
  expect(cropPosition(edit,8_999_999)).toMatchObject({frame:'crop',x:.6,y:.75})
  expect(cropPosition(edit,9_000_000)).toMatchObject({frame:'crop',x:1,y:1})
  expect(cropPosition(edit,9_499_999).frame).toBe('crop')
  expect(cropPosition(edit,9_999_999).frame).toBe('fit')
  expect(cropPosition(edit,10_900_000)).toMatchObject({frame:'crop',x:0,y:0})
})
it('uses the manual frame even with a saved trajectory', () => {
  expect(cropPosition({...edit,frame:'crop'},8_250_000)).toEqual({frame:'crop',x:.4,y:.55})
  expect(cropPosition({...edit,frame:'fit'},9_000_000).frame).toBe('fit')
})
