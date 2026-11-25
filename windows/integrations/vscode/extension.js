const vscode = require('vscode');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');

function activate(context) {
  const output = vscode.window.createOutputChannel('Mason tasks');
  const watches = new Map();
  const readTasks = require('node:util').promisify(require('node:child_process').execFile);
  context.subscriptions.push(output, {dispose() { for (const stop of watches.values()) stop(); }});
  context.subscriptions.push(vscode.commands.registerCommand('mason.watchTasks', async () => {
    const folders = vscode.workspace.workspaceFolders || [];
    const folder = folders.length === 1 ? folders[0] : await vscode.window.showWorkspaceFolderPick();
    if (!folder || folder.uri.scheme !== 'file' || process.platform !== 'win32') return;
    const project = folder.uri.fsPath;
    if (watches.has(project)) { watches.get(project)(); return; }
    const cli = process.env.MASON_WORKER_CLI || path.join(process.env.LOCALAPPDATA, 'Programs', 'MasonAgent', 'mason.exe');
    const seen = new Map();
    let timer, closed = false;
    function stop() { closed = true; clearTimeout(timer); watches.delete(project); }
    async function refresh() {
      try {
        const result = await readTasks(cli, ['task-list', '--workspace', project], {windowsHide: true, timeout: 8000, maxBuffer: 4 * 1024 * 1024});
        if (closed) return;
        const tasks = JSON.parse(result.stdout.trim()).data;
        for (const {id, status} of tasks) {
          const key = status.State + (status.Decision?.Id || '');
          if (seen.get(id) === key) continue;
          const prior = seen.has(id);
          seen.set(id, key);
          output.appendLine(folder.name + ' · ' + id + ' · ' + status.State + ' · ' + status.Detail);
          if (status.Decision || (prior && !status.Active)) {
            void vscode.window.showInformationMessage('Mason · ' + folder.name + ' · ' + status.State, 'Show task log')
              .then(choice => { if (choice) output.show(true); });
          }
        }
        timer = setTimeout(refresh, 4000);
      } catch (error) {
        if (closed) return;
        stop();
        vscode.window.showErrorMessage('Could not attach to Mason tasks: ' + error.message);
      }
    }
    watches.set(project, stop);
    await refresh();
    if (!closed) output.show(true);
  }));
  context.subscriptions.push(vscode.commands.registerCommand('mason.captureSelection', async () => {
    try {
      const editor = vscode.window.activeTextEditor;
      const doc = editor?.document;
      const folder = doc && vscode.workspace.getWorkspaceFolder(doc.uri);
      if (!folder || doc.uri.scheme !== 'file' || process.platform !== 'win32') {
        throw new Error('Open a local Windows workspace file first.');
      }
      const selection = editor.selection.isEmpty ? doc.lineAt(editor.selection.start.line).range : editor.selection;
      if (doc.offsetAt(selection.end) - doc.offsetAt(selection.start) > 6000) {
        throw new Error('Select at most 6,000 characters.');
      }
      const diagnostics = vscode.languages.getDiagnostics(doc.uri)
        .filter(d => d.range.intersection(selection)).slice(0, 12)
        .map(d => [d.range.start.line + 1, vscode.DiagnosticSeverity[d.severity], d.message].join(' ')).join('\n').slice(0, 2000);
      const workspace = path.resolve(folder.uri.fsPath);
      const item = {version: 1, workspace, path: path.relative(workspace, doc.uri.fsPath), selection: doc.getText(selection),
        diagnostics, line: selection.start.line + 1, endLine: selection.end.line + 1, dirty: doc.isDirty, bufferVersion: doc.version};
      let diskHash = '';
      try {
        const stat = await fs.stat(doc.uri.fsPath);
        if (stat.size > 128 * 1024) throw new Error('Choose a source file up to 128 KiB.');
        diskHash = crypto.createHash('sha256').update(await fs.readFile(doc.uri.fsPath)).digest('hex');
      } catch (error) { if (error.code !== 'ENOENT') throw error; }
      const root = process.env.MASON_DATA_HOME || path.join(process.env.LOCALAPPDATA, 'MasonAgent');
      const inbox = path.join(root, 'editor-inbox');
      const key = crypto.createHash('sha256').update(workspace.toLowerCase()).digest('hex');
      const destination = path.join(inbox, key + '.json');
      const temporary = destination + '.' + crypto.randomUUID() + '.tmp';
      await fs.mkdir(inbox, {recursive: true});
      await fs.writeFile(temporary, JSON.stringify({...item, diskHash}), {flag:'wx'});
      try { await fs.rename(temporary, destination); } finally { await fs.rm(temporary, {force:true}); }
      vscode.window.showInformationMessage('Selection shared. In Mason’s project menu, choose Attach editor selection.');
    } catch (error) { vscode.window.showErrorMessage(error.message); }
  }));
}
module.exports = {activate};
