'use strict';
const ts = require('./typescript/typescript.js');
const fs = require('node:fs');
const path = require('node:path');
const req = JSON.parse(fs.readFileSync(0, 'utf8'));
const root = '/mason-project/';
const map = new Map(Object.entries(req.files).map(([k,v])=>[root+k.replaceAll('\\','/'),v]));
const lib = path.join(__dirname,'typescript');
function read(name) {
  if (map.has(name)) return map.get(name);
  if (path.dirname(name) === lib && /^lib[.\w-]*\.d\.ts$/i.test(path.basename(name))) return fs.readFileSync(name,'utf8');
}
const options={allowJs:true,checkJs:true,noEmit:true,allowNonTsExtensions:true,target:ts.ScriptTarget.ESNext,
  module:ts.ModuleKind.ESNext,moduleResolution:ts.ModuleResolutionKind.Bundler,jsx:ts.JsxEmit.Preserve};
const host={getScriptFileNames:()=>[...map.keys()],getScriptVersion:()=> '1',getCurrentDirectory:()=>root,
  getCompilationSettings:()=>options,getDefaultLibFileName:()=>path.join(lib,ts.getDefaultLibFileName(options)),
  fileExists:n=>map.has(n)||(path.dirname(n)===lib && /^lib[.\w-]*\.d\.ts$/i.test(path.basename(n))&&fs.existsSync(n)),
  readFile:read,getScriptSnapshot:n=>{const source=read(n);return source===undefined?undefined:ts.ScriptSnapshot.fromString(source);}};
const service=ts.createLanguageService(host);
const file=root+req.path.replaceAll('\\','/');
const program=service.getProgram();
const source=program.getSourceFile(file);
function location(n,pos){const item=program.getSourceFile(n);const at=item.getLineAndCharacterOfPosition(pos);return {file:n.slice(root.length),line:at.line+1,column:at.character+1};}
let results;
if(req.mode==='diagnostics'){
  results=[...service.getSyntacticDiagnostics(file),...service.getSemanticDiagnostics(file)]
    .filter(d=>d.file && map.has(d.file.fileName)).slice(0,150).map(d=>({...location(d.file.fileName,d.start??0),code:d.code,message:ts.flattenDiagnosticMessageText(d.messageText,' ')}));
}else{
  const pos=source.getPositionOfLineAndCharacter(req.line-1,req.column-1);
  const hits=req.mode==='definitions'?service.getDefinitionAtPosition(file,pos):service.getReferencesAtPosition(file,pos);
  results=(hits||[]).filter(h=>map.has(h.fileName)).slice(0,150).map(h=>location(h.fileName,h.textSpan.start));
}
console.log(JSON.stringify({engine:'TypeScript '+ts.version,partial:req.partial,results}));
