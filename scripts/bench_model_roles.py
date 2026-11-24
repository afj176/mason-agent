"""Compare a single local coding model against the same model with a separate visual verifier."""
import argparse
import hashlib
import json
import subprocess
import time
from pathlib import Path
from bench_maintenance import gpu

def evaluate(args, workspace, label):
    workspace.mkdir(parents=True); (workspace/'.mason').mkdir()
    request='Make the Acknowledge button display Recorded after a click'
    (workspace/'index.html').write_text('<!doctype html><title>Receipt</title><button id="ack">Acknowledge</button><output id="receipt"></output>',encoding='utf8')
    (workspace/'interaction.test.cjs').write_text("const fs=require('fs'),test=require('node:test'),assert=require('assert/strict');test('binding',()=>assert.match(fs.readFileSync('index.html','utf8'),/Recorded/));",encoding='utf8')
    (workspace/'mason.workflows.json').write_text(json.dumps({'version':1,'workflows':{'test':{'executable':'node','arguments':['--test','--test-reporter=tap','interaction.test.cjs']}}}))
    (workspace/'.mason/tests.json').write_text(json.dumps([{'requirement':request,'workflow':'test','format':'tap','report':'','test':'binding','testFiles':['interaction.test.cjs'],'inputs':['index.html']}]))
    checks={'Version':1,'Scenarios':[{'Id':'ack','Requirement':request,'Page':'index.html','Steps':[{'Op':'click','Target':'#ack','Value':''},{'Op':'text','Target':'#receipt','Value':'Recorded'}]}]}
    (workspace/'mason.acceptance.json').write_text(json.dumps(checks))
    revision=hashlib.sha256(b''.join(p.relative_to(workspace).as_posix().encode()+b'\0'+p.read_bytes() for p in sorted(workspace.rglob('*')) if p.is_file())).hexdigest()
    host=workspace.parent/'host'
    cmd=[str(args.cli),'exec','--workspace',str(workspace),'--auto','--endpoint',args.endpoint,'--model',args.model,'--profile',str(host),'--prompt',request]
    if label=='paired': cmd+=['--visual-endpoint',args.vision_endpoint,'--visual-model',args.vision_model]
    start=time.monotonic();samples=[]
    with (workspace.parent/'run.jsonl').open('wb') as log:
        proc=subprocess.Popen(cmd,stdout=log,stderr=subprocess.STDOUT,stdin=subprocess.DEVNULL)
        try:
            while proc.poll() is None:
                m=gpu()
                if m is not None:samples.append(m)
                if time.monotonic()-start>args.timeout: proc.terminate();break
                time.sleep(.8)
        finally:
            try:proc.wait(timeout=10)
            except subprocess.TimeoutExpired:proc.kill();proc.wait()
    sessions=list((host/'code-sessions').glob('*.json'))
    session=json.loads(sessions[0].read_text(encoding='utf-8-sig')) if sessions else {}
    plan=next((t.get('Acceptance') for t in reversed(session.get('Turns',[])) if t.get('Acceptance')),{})
    proof=[r for r in plan.get('Results',[]) if r['Check']['Kind']=='browser_flow']
    visual=[a for a in session.get('Activities',[]) if a['Tool']=='visual_review']
    return dict(configuration=label,model=args.model,visualModel=args.vision_model if label=='paired' else None,startingRevision=revision,
        state=session.get('State'),checks=plan.get('Results',[]),browserPassed=bool(proof) and all(r['Passed'] for r in proof),
        visualEvidence=[a['Result'] for a in visual if a['State']=='Completed'],elapsedSeconds=round(time.monotonic()-start,2),peakDeviceMemoryMiB=max(samples,default=None))

if __name__=='__main__':
    p=argparse.ArgumentParser()
    for flag in ['cli','endpoint','model','vision-endpoint','vision-model','output']:p.add_argument('--'+flag,required=True)
    p.add_argument('--timeout',type=int,default=300)
    args=p.parse_args(); args.cli=Path(args.cli).resolve(); root=Path(args.output).resolve();root.mkdir(exist_ok=False,parents=True)
    results=[]
    for name in ['single','paired']:
        results.append(evaluate(args,root/name/'project',name));(root/'comparison.json').write_text(json.dumps(results,indent=2));print(name,results[-1]['state'],results[-1]['browserPassed'],flush=True)
