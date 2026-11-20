const fs = require('node:fs')
const [reference, recognition, output, cutText, lengthText] = process.argv.slice(2)
const cut = Number(cutText), length = Number(lengthText)
if(!output || !Number.isSafeInteger(cut) || !Number.isSafeInteger(length) || cut<0 || length<=0)
  throw new Error('Usage: node scripts/Measure-WordTiming.cjs source.srt words.json result.json cut-ms duration-ms')

const tokens = text => text.toLowerCase().replace(/[’']/g,'').match(/[\p{L}\p{N}]+/gu) ?? []
const ms = text => {
  const [h,m,s,f] = text.split(/[:,]/).map(Number)
  return ((h*60+m)*60+s)*1000+f
}
const rows = JSON.parse(fs.readFileSync(recognition,'utf8')).transcription
const words = rows.flatMap((row,index)=>tokens(row.text).map((text,i)=>({
  text, index, time: i===0 ? row.offsets.from : null
})))
const pairs = [], unmatched = []
for (const block of fs.readFileSync(reference,'utf8').replace(/\r/g,'').trim().split(/\n\s*\n/)) {
  const [id,line,...body] = block.split('\n')
  const referenceMs = ms(line.split(' --> ')[0])-cut
  if(referenceMs<0 || referenceMs>=length) continue
  const first = tokens(body.join(' '))
  // Disambiguate by textual context only. Never select the candidate closest to the reference time.
  let matches=[]
  for(const n of [4,3,2]) {
    if(first.length<n) continue
    matches=words.filter((word,k)=>word.time!==null && first.slice(0,n).every((w,i)=>words[k+i]?.text===w))
    if(matches.length===1) break
    if(matches.length>1) break
  }
  if(matches.length!==1){unmatched.push(Number(id));continue}
  const word=matches[0]
  pairs.push({cue:Number(id),startWord:first[0],recognitionRow:word.index,referenceMs,asrMs:word.time,errorMs:word.time-referenceMs})
}
const absolute=pairs.map(p=>Math.abs(p.errorMs)).sort((a,b)=>a-b)
const n=absolute.length
const result={cutMs:cut,durationMs:length,paired:n,unmatched,within250ms:absolute.filter(e=>e<=250).length,
  medianAbsoluteMs:n?(absolute[Math.floor((n-1)/2)]+absolute[Math.floor(n/2)])/2:null,pairs}
fs.writeFileSync(output,JSON.stringify(result,null,2)+'\n',{flag:'wx'})
console.log('%d matched, %d within 250 ms, median %d ms',n,result.within250ms,result.medianAbsoluteMs)
