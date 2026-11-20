// Compare the saved MP4s to the original picture, separately from ShortForge's rendering code.
const fs = require('node:fs')
const path = require('node:path')
const { execFileSync, spawn } = require('node:child_process')
const assert = require('node:assert/strict')
const [output, bin] = process.argv.slice(2)
const ffmpeg = path.join(bin, 'ffmpeg.exe'), ffprobe = path.join(bin, 'ffprobe.exe')
const detector = path.resolve(__dirname, '../tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.FaceDetector.exe')
const read = file => JSON.parse(fs.readFileSync(file, 'utf8'))
const width = 180, height = 320, cropWidth = 202, sourceWidth = 640
const rounded = x => +x.toFixed(3)

function frame(file, t, filter) {
  const data = execFileSync(ffmpeg, ['-v','error','-ss',String(t),'-i',file,'-map','0:v:0','-frames:v','1',
    '-vf',filter+',format=gray','-f','rawvideo','pipe:1'], {windowsHide:true})
  assert.equal(data.length, width * height)
  return data
}

function measure(original, composed) {
  let sum = 0, guideExcess = 0, guidePixels = 0
  for (let y=0; y<height; y++) for (let x=0; x<width; x++) {
    const i = y*width+x
    sum += Math.abs(original[i]-composed[i])
    const side = (Math.abs(x-width*.08)<2 || Math.abs(x-width*.92)<2) && y>=height*.08 && y<=height*.85
    const cap = (Math.abs(y-height*.08)<2 || Math.abs(y-height*.85)<2) && x>=width*.08 && x<=width*.92
    if ((side || cap) && original[i]<130) {
      guidePixels++
      if (composed[i]-original[i]>45) guideExcess++
    }
  }
  return {mae:rounded(sum/original.length), brightGuideFraction:guidePixels?rounded(guideExcess/guidePixels):0}
}

function at(points, time) {
  const i = points.findLastIndex(p => p.timeUs<=time+1)
  const left = points[i], right = points[i+1]
  if (!right || right.cut || left.frame==='fit' || right.frame==='fit') return left
  const ratio = (time-left.timeUs)/(right.timeUs-left.timeUs)
  return {...left, x:left.x+(right.x-left.x)*ratio}
}

async function detect(file, start, duration) {
  const decode = spawn(ffmpeg,['-v','error','-ss',String(start),'-t',String(duration),'-i',file,'-an','-vf',
    'fps=4:start_time=0:round=up:eof_action=pass,format=gray','-f','image2pipe','-c:v','pgm','pipe:1'],{windowsHide:true})
  const faces = spawn(detector,[],{windowsHide:true})
  decode.stdout.pipe(faces.stdin)
  let data = ''
  faces.stdout.on('data', block => { data+=block.toString('ascii') })
  const ended = p=>new Promise((resolve,reject)=>{p.on('error',reject);p.on('close',code=>code===0?resolve():reject(Error('Detector/decode exit '+code)))})
  await Promise.all([ended(decode),ended(faces)])
  const lines = data.trim().split(/\r?\n/)
  assert.equal(lines.shift(),'READY')
  return lines.map(line=>{
    const [w,h,cut,...coords] = line.split(' ').map(Number)
    assert.equal(w,640);assert.equal(h,360);assert.equal(coords.length%4,0)
    const boxes=[]
    for(let i=0;i<coords.length;i+=4) boxes.push(coords.slice(i,i+4))
    return {cut,boxes}
  })
}

// Find the actual source column used in every exported motion frame by matching
// the decoded pictures. Read at most one second per subprocess.
function decodedMotion(source, full, start, duration, points) {
  const measured=[]
  let worstDisagreement=0, maximumJump=0, maximumChange=0, previousJump=0
  const frames=Math.round(duration*30)
  for(let first=0;first<frames;first+=30){
    const count=Math.min(30,frames-first)
    function chunk(file,offset,filter){
      return execFileSync(ffmpeg,['-v','error','-ss',offset.toFixed(6),'-i',file,'-frames:v',String(count),
        '-vf',filter+',format=gray','-f','rawvideo','pipe:1'],{windowsHide:true,maxBuffer:8*1024*1024})
    }
    const src=chunk(source,start+first/30,'scale=640:360'), out=chunk(full,first/30,'scale=202:360')
    assert.equal(src.length,count*640*360);assert.equal(out.length,count*202*360)
    for(let i=0;i<count;i++){
      let best=Infinity, left=0
      for(let x=0;x<=438;x+=2){
        let error=0
        for(let row=4;row<353;row+=11) for(let col=4;col<198;col+=9)
          error+=Math.abs(src[(i*360+row)*640+x+col]-out[(i*360+row)*202+col])
        if(error<best){best=error;left=x}
      }
      const expected=at(points,(first+i)*1e6/30).x*438
      worstDisagreement=Math.max(worstDisagreement,Math.abs(expected-left))
      if(measured.length){
        const jump=left-measured[measured.length-1]
        maximumJump=Math.max(maximumJump,Math.abs(jump))
        if(measured.length>1) maximumChange=Math.max(maximumChange,Math.abs(jump-previousJump))
        previousJump=jump
      }
      measured.push(left)
    }
  }
  const result={frames,sourceColumnByFrame:measured,maximumFrameJumpPx:maximumJump,maximumChangeOfJumpPx:maximumChange,
    worstDisagreementWithPathPx:rounded(worstDisagreement)}
  assert.ok(maximumJump<=6 && maximumChange<=4 && worstDisagreement<=3,JSON.stringify(result))
  return result
}

async function scenario(kind, source, offset, samples) {
  const dir=path.join(output,kind)
  const {edit}=read(path.join(dir,'trajectory.json'))
  const start=edit.startUs/1e6, duration=(edit.endUs-edit.startUs)/1e6
  const {points}=edit.trajectory
  const full=path.join(dir,'export.mp4'), preview=path.join(dir,'preview.mp4')
  const evidence={kind,counts:edit.trajectory.counts}
  for(const [file,expected] of [[full,duration],[preview,Math.min(5,duration-offset)]]) {
    const probe=JSON.parse(execFileSync(ffprobe,['-v','error','-show_streams','-show_format','-of','json',file]))
    assert.equal(probe.streams.length,1)
    const s=probe.streams[0]
    assert.equal(s.codec_name,'h264'); assert.equal(s.width,1080); assert.equal(s.height,1920)
    assert.equal(s.sample_aspect_ratio,'1:1'); assert.equal(s.avg_frame_rate,'30/1'); assert.equal(s.pix_fmt,'yuv420p')
    assert.ok(Math.abs(+probe.format.duration-expected)<.045)
    fs.writeFileSync(file+'.ffprobe.json',JSON.stringify(probe,null,2))
  }

  const detected=await detect(source,start,duration)
  assert.equal(detected.length,points.length)
  evidence.faces=detected
  if(kind==='motion') {
    let minimumMargin=Infinity, maximumStep=0, maximumAcceleration=0, priorDx=0
    for(let i=0;i<points.length;i++) {
      assert.equal(points[i].frame,'crop'); assert.equal(detected[i].boxes.length,1)
      const [faceX,,faceWidth]=detected[i].boxes[0], left=points[i].x*(sourceWidth-cropWidth)
      minimumMargin=Math.min(minimumMargin,faceX-left,left+cropWidth-faceX-faceWidth)
      if(i) {
        const dx=(points[i].x-points[i-1].x)*(sourceWidth-cropWidth)
        maximumStep=Math.max(maximumStep,Math.abs(dx))
        if(i>1) maximumAcceleration=Math.max(maximumAcceleration,Math.abs(dx-priorDx))
        priorDx=dx
      }
    }
    const xs=points.map(p=>p.x*(sourceWidth-cropWidth))
    const travel=Math.max(...xs)-Math.min(...xs)
    assert.ok(minimumMargin>=12 && travel>=45 && maximumStep<=32 && maximumAcceleration<=27,
      JSON.stringify({minimumMargin,travel,maximumStep,maximumAcceleration}))
    Object.assign(evidence,{minimumFaceMarginPx:rounded(minimumMargin),travelPx:rounded(travel),
      maximumStepPxPer250ms:rounded(maximumStep),maximumDeltaOfStepPx:rounded(maximumAcceleration)})
    evidence.frameMotion=decodedMotion(source,full,start,duration,points)
  } else {
    for(let i=0;i<points.length;i++){
      const t=points[i].timeUs/1e6
      const count=t<1.5 || t>=4 && t<5 || t>=6 && t<6.5 ? 0 : t<4 ? 2 : 1
      assert.equal(detected[i].boxes.length,count,'detected faces at '+t)
      assert.equal(points[i].frame,t<5?'fit':'crop','frame mode at '+t)
      if(t>=6 && t<6.5) assert.equal(points[i].x,points[23].x,'camera during occlusion')
      assert.equal(!!detected[i].cut,[1.5,4,5,9].includes(t),'shot boundary at '+t)
      if(t===9) assert.ok(points[i].cut && points[i].x>.88 && points[i-1].x<.65,'reset on cut')
    }
  }

  evidence.live=read(path.join(dir,'live.json'))
  for(const {sourceSeconds,frame:live} of evidence.live) {
    const position=at(points,(sourceSeconds-start)*1e6)
    assert.equal(live.fit,position.frame==='fit'?'contain':'cover','live objectFit at '+sourceSeconds)
    if(position.frame==='crop') assert.ok(Math.abs(live.x-position.x)<.001,'live crop position at '+sourceSeconds)
  }
  evidence.decoded=[]
  for(const clipSeconds of samples){
    const expected=at(points,clipSeconds*1e6)
    const target=frame(full,clipSeconds,'scale='+width+':'+height)
    const x=Math.round(expected.x*(sourceWidth-cropWidth))
    let best
    for(const delta of expected.frame==='fit'?[0]:[-2,0,2]) {
      const geometry=expected.frame==='fit' ? 'scale=180:102,pad=180:320:0:109' :
        'crop=202:360:'+Math.max(0,Math.min(sourceWidth-cropWidth,x+delta))+':0,scale=180:320'
      const reference=frame(source,start+clipSeconds,geometry)
      const result=measure(reference,target)
      if(!best || result.mae<best.mae) best=result
    }
    const row={clipSeconds,mode:expected.frame,expectedX:rounded(x),sourceVsExport:best}
    if(clipSeconds>=offset && clipSeconds<offset+5){
      row.previewVsExport=measure(frame(preview,rounded(clipSeconds-offset),'scale=180:320'),target)
      assert.ok(row.previewVsExport.mae<3,JSON.stringify(row))
    }
    evidence.decoded.push(row)
    assert.ok(best.mae<4 && best.brightGuideFraction<.015,JSON.stringify(row))
  }
  return evidence
}

async function main() {
  const motion=await scenario('motion',path.join(output,"Sellers O'Brien.mp4"),2,[.5,2,2.7,3.5,3.9,4.5,5.7,6.8,7.5])
  const cases=await scenario('cases',path.join(output,'Controlled cases.mp4'),5.5,[.5,2.5,4.5,5.5,6.25,7,8.5,9.3,10])
  for(const [name,filter] of [['motion','fps=2,scale=90:160,tile=9x2:padding=2:color=white'],
    ['cases','fps=1,scale=90:160,tile=6x2:padding=2:color=gray']]){
    execFileSync(ffmpeg,['-v','error','-y','-i',path.join(output,name,'export.mp4'),'-vf',filter,
      '-frames:v','1','-update','1',path.join(output,name+'-filmstrip.jpg')],{windowsHide:true})
  }
  fs.writeFileSync(path.join(output,'decoded.json'),JSON.stringify({motion,cases},null,2))
  console.log('PASS 18 source/export pairs, overlapping exact previews, all decoded frames of natural motion, live editor and controlled cases.')
}
main().catch(error=>{console.error(error);process.exitCode=1})
