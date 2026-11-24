const vscode = require('vscode');
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');

exports.run = async function () {
  const folder = vscode.workspace.workspaceFolders[0].uri.fsPath;
  const file = path.join(folder,'Billing.cs');
  const before = await fs.readFile(file,'utf8');
  const document = await vscode.workspace.openTextDocument(vscode.Uri.file(file));
  const editor = await vscode.window.showTextDocument(document);
  const text = '// unsaved IDE selection';
  await editor.edit(edit=>edit.insert(new vscode.Position(0,0),text+'\n'));
  editor.selection=new vscode.Selection(0,0,0,text.length);
  const collection=vscode.languages.createDiagnosticCollection('mason-fixture');
  try {
    collection.set(document.uri,[new vscode.Diagnostic(editor.selection,'fixture diagnostic',vscode.DiagnosticSeverity.Warning)]);
    await vscode.commands.executeCommand('mason.captureSelection');
    const inbox=path.join(process.env.MASON_DATA_HOME,'editor-inbox');
    const files=await fs.readdir(inbox);
    assert.equal(files.length,1);
    const item=JSON.parse(await fs.readFile(path.join(inbox,files[0]),'utf8'));
    assert.equal(item.selection,text); assert.equal(item.dirty,true);
    assert.match(item.diagnostics,/fixture diagnostic/);
    assert.equal(await fs.readFile(file,'utf8'),before);
    await fs.writeFile(path.join(process.env.MASON_DATA_HOME,'editor-qa.json'),JSON.stringify({selection:item.selection,dirty:item.dirty,diagnostics:item.diagnostics,diskUnchanged:true}));
  } finally {collection.dispose(); await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');}
};
