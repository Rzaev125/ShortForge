import type { EditDecision } from './bridge'

// Keyframes use clip time; X/Y describe the position within the available crop travel.
export function cropPosition(edit: EditDecision, sourceTimeUs: number): {frame: 'fit' | 'crop'; x: number; y: number} {
  if (edit.frame !== 'auto') return {frame: edit.frame, x: edit.x, y: edit.y}
  const points = edit.trajectory?.points
  if (!points?.length) return {frame: 'fit', x: 0.5, y: 0.5}
  const time = Math.max(0, sourceTimeUs - edit.startUs)
  let low = 0, high = points.length
  while (low + 1 < high) {
    const middle = Math.floor((low + high) / 2)
    if (points[middle].timeUs <= time) low = middle
    else high = middle
  }
  const left = points[low], right = points[low + 1]
  if (!right || right.cut || left.frame !== 'crop' || right.frame !== 'crop') return left
  const weight = (time - left.timeUs) / (right.timeUs - left.timeUs)
  return {frame: 'crop', x: left.x + weight * (right.x-left.x), y: left.y + weight * (right.y-left.y)}
}
