"""Exercise local Mason CLI runs on small existing repositories with independent host checks."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import threading
import time

CASES = {
    "regression": ("Fix half-open interval intersection", "ranges.cjs",
        "exports.intersects=(a,b)=>a[0]<=b[1] && b[0]<=a[1];",
        "const {intersects:f}=require('./ranges.cjs');",
        "assert.equal(f([1,3],[3,4]),false);",
        "assert.equal(f([-5,-1],[-2,2]),true); assert.equal(f([2,5],[-4,2]),false); assert.equal(f([8,9],[1,3]),false);"),
    "refactor": ("Share the normalization implementation between lookup and insert", "registry.cjs",
        "exports.lookup=(map,key)=>map.get(key.trim().toLowerCase());\nexports.insert=(map,key,value)=>map.set(key.trim().toLowerCase(),value);",
        "const api=require('./registry.cjs');",
        "const m=new Map();api.insert(m,' BOX ',9);assert.equal(api.lookup(m,'box'),9);assert.equal((require('fs').readFileSync('registry.cjs','utf8').match(/\\.toLowerCase\\s*\\(/g)||[]).length,1);",
        "assert.equal((require('node:fs').readFileSync('registry.cjs','utf8').match(/\\.toLowerCase\\s*\\(/g)||[]).length,1);const m=new Map();api.insert(m,' A ',0);assert.equal(api.lookup(m,'a'),0);api.insert(m,'a',3);assert.equal(m.size,1);assert.equal(api.lookup(m,' A '),3);"),
    "interruption": ("Make encode and decode round-trip spaces and plus signs", "codec.cjs",
        "exports.encode=s=>s.replaceAll(' ','+');\nexports.decode=s=>s.replaceAll('+',' ');",
        "const api=require('./codec.cjs');",
        "assert.equal(api.decode(api.encode('a + b')),'a + b');",
        "for(const s of ['++','%2B +','\\u03bb / & +','']) assert.equal(api.decode(api.encode(s)),s);"),
}

# Historical Mason release site. Exercise its published code against release asset names
# using a local response stub. The isolated browser model never needs network access.
SITE_COMMIT = 'd64948f5c7760a56b00bc5ce48a2e76a244bca62'
SITE_FILES = ('app.js', 'index.html', 'styles.css')
def historical_file(name):
    root = Path(__file__).resolve().parents[1]
    return subprocess.check_output(['git', '-C', str(root), 'show', SITE_COMMIT + ':site/' + name])

CASES['release-site'] = (
    'Show the download sizes for versioned Mason installer and portable assets matching the release tag. Preserve legacy unversioned names.',
    'app.js', None,
    """const source=require('node:fs').readFileSync('app.js','utf8'); const vm=require('node:vm');
async function display(assets,tag='v2.1.1') {
 const sizes={}; const document={querySelectorAll:selector=>[{set textContent(value){sizes[selector.replace(/['"]/g,'')]=value;}}]};
 const fetch=async()=>({ok:true,json:async()=>({tag_name:tag,assets})});
 vm.runInNewContext(source,{window:{fetch},fetch,document},{timeout:1000});await new Promise(setImmediate);return sizes;
}
const row=(name,mb)=>({name,size:mb*1048576});
const installer='[data-size=installer]',portable='[data-size=portable]';""",
    "const r=await display([row('MasonAgent-2.1.1-Setup-x64.exe',7),row('MasonAgent-2.1.1-win-x64.zip',9)]);assert.equal(r[installer],'7 MB');assert.equal(r[portable],'9 MB');",
    "const r=await display([row('MasonAgent-3.0.0-Setup-x64.exe',12),row('MasonAgent-2.9.9-Setup-x64.exe',90),row('MasonAgent-win-x64.zip',5)],'v3.0.0');assert.equal(r[installer],'12 MB');assert.equal(r[portable],'5 MB');assert.equal((await display([row('MasonAgent-Setup-x64.exe',3)]))[installer],'3 MB');"
)

def gpu():
    try:
        result=subprocess.run(['nvidia-smi','--query-gpu=memory.used','--format=csv,noheader,nounits'],capture_output=True,text=True,timeout=3)
        return int(result.stdout.splitlines()[0]) if result.returncode==0 else None
    except (OSError,ValueError,IndexError,subprocess.TimeoutExpired):
        return None

def run_case(args, case, replica, label, endpoint, model):
    request, path, original, prepare, visible, hidden=CASES[case]
    base=args.output/f'{case}-{replica}-{label}'; workspace=base/'repo'; workspace.mkdir(parents=True)
    # Independent oracle and result records live outside the writable project.
    if case == 'release-site':
        for name in SITE_FILES:
            (workspace / name).write_bytes(historical_file(name))
        original = (workspace / path).read_text(encoding='utf8')
    source=workspace/path
    if not source.exists(): source.write_text(original,encoding='utf8')
    test="const test=require('node:test'), assert=require('node:assert/strict');\n"+prepare+"\ntest('contract',async()=>{"+visible+"});\n"
    (workspace/'contract.test.cjs').write_text(test,encoding='utf8')
    (workspace/'.mason').mkdir()
    manifest=[dict(requirement=request,workflow="suite",format="tap",report="",test="contract",testFiles=["contract.test.cjs"],inputs=[path])]
    if case == 'interruption':
        manifest[0]['roundTrip'] = dict(module=path, encode='encode', decode='decode', alphabet=' ab+%0123456789ABCDEF')
    (workspace/'.mason/tests.json').write_text(json.dumps(manifest),encoding='utf8')
    (workspace/'mason.workflows.json').write_text(json.dumps(dict(version=1, workflows=dict(suite=dict(executable="node",arguments=["--test","--test-reporter=tap","contract.test.cjs"])))),encoding='utf8')
    revision=hashlib.sha256(b''.join(p.relative_to(workspace).as_posix().encode()+b'\0'+p.read_bytes() for p in sorted(workspace.rglob('*')) if p.is_file())).hexdigest()
    # Run the checker with the same workspace sandbox as a Mason command (below). Never execute model-authored JS on the host.
    oracle="const assert=require('node:assert/strict');"+prepare+hidden
    start=time.monotonic(); peak=[gpu()]; stop=threading.Event()
    def sample():
        while not stop.wait(1):
            sample=gpu()
            if sample is not None: peak.append(sample)
    thread=threading.Thread(target=sample,daemon=True); thread.start()
    def invoke(command, log, interrupt=False):
        interrupted=False
        with log.open('wb') as out:
            proc=subprocess.Popen(command,stdout=out,stderr=subprocess.STDOUT,stdin=subprocess.DEVNULL)
            try:
                while proc.poll() is None:
                    if interrupt and source.read_text(encoding='utf8')!=original:
                        proc.terminate();interrupted=True;break
                    if time.monotonic()-start>args.timeout:
                        proc.terminate();break
                    time.sleep(.08)
            finally:
                try:proc.wait(timeout=10)
                except subprocess.TimeoutExpired:proc.kill();proc.wait()
        return interrupted
    cmd=[str(args.cli),'exec','--workspace',str(workspace),'--auto','--profile',str(base/'journal'),'--endpoint',endpoint,'--model',model]
    interrupted=False
    try:
        log=base/'run.jsonl'; interrupted=invoke(cmd+['--prompt',request],log,case=='interruption')
        def events(log):
            rows=[]
            for line in log.read_text(encoding='utf8',errors='replace').splitlines():
                try:rows.append(json.loads(line))
                except ValueError:pass
            return rows
        rows=events(log)
        if interrupted:
            sid=next(row['data']['Id'] for row in rows if row.get('type')=='run.started')
            second=base/'resume.jsonl';invoke(cmd+['--resume',sid,'--prompt','continue'],second);rows+=events(second)
        finished=next((row['data']['State'] for row in reversed(rows) if row.get('type')=='run.finished'),"Interrupted")
        checks=subprocess.run([str(args.checks),'--maintenance-oracle',str(workspace),oracle],capture_output=True,text=True,timeout=60)
        (base/'oracle.log').write_text(checks.stdout+checks.stderr,encoding='utf8')
        passed=checks.returncode==0 and (case!='interruption' or interrupted)
        complete=finished=='Completed'
        record=dict(case=case,replica=replica,configuration=label,model=model,revision=revision,state=finished,oraclePassed=passed,
            sourceCommit=SITE_COMMIT if case=='release-site' else None,
            falseCompletion=complete and not passed,missedValidFix=passed and not complete,interrupted=interrupted,
            humanInterventions=0,seconds=round(time.monotonic()-start,2),peakDeviceMemoryMiB=max((n for n in peak if n is not None),default=None))
        (base/'result.json').write_text(json.dumps(record,indent=2),encoding='utf8')
        return record
    finally: stop.set();thread.join(4)

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--cli',type=Path,required=True);parser.add_argument('--checks',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--endpoint',action='append',nargs=3,metavar=('LABEL','URL','MODEL'),required=True)
    parser.add_argument('--case',choices=CASES,action='append',dest='cases')
    parser.add_argument('--repeats',type=int,default=2);parser.add_argument('--timeout',type=int,default=240)
    args=parser.parse_args();args.output=args.output.resolve();args.cli=args.cli.resolve();args.checks=args.checks.resolve();args.output.mkdir(parents=True,exist_ok=False)
    records=[]
    for n in range(args.repeats):
        for case in (args.cases or CASES):
            for label,url,model in args.endpoint:
                record=run_case(args,case,n+1,label,url,model);records.append(record);print(json.dumps(record),flush=True)
                (args.output/'results.json').write_text(json.dumps(records,indent=2),encoding='utf8')
