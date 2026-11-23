import json
import pathlib
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent / "python"))
import jedi

request = json.load(sys.stdin)
with tempfile.TemporaryDirectory(prefix="mason-symbols-") as folder:
    root = pathlib.Path(folder).resolve()
    for name, source in request["files"].items():
        dest = (root / name).resolve()
        if not dest.is_relative_to(root):
            raise ValueError("Invalid snapshot path")
        dest.parent.mkdir(exist_ok=True, parents=True)
        dest.write_text(source, encoding="utf-8")
    jedi.settings.cache_directory = str(root / ".jedi-cache")
    project = jedi.Project(root, sys_path=[str(root)], smart_sys_path=False, load_unsafe_extensions=False)
    script = jedi.Script(path=root / request["path"], project=project)
    results = []
    if request["mode"] == "diagnostics":
        for error in script.get_syntax_errors():
            results.append(dict(file=request["path"], line=error.line, column=error.column+1, message=error.get_message()))
    else:
        pos = (request["line"], request["column"]-1)
        hits = script.goto(*pos, follow_imports=True) if request["mode"] == "definitions" else script.get_references(*pos, scope="project")
        for hit in hits:
            if hit.module_path and hit.module_path.is_relative_to(root):
                results.append(dict(file=hit.module_path.relative_to(root).as_posix(),line=hit.line,column=hit.column+1))
    print(json.dumps(dict(engine="Jedi "+jedi.__version__,partial=request["partial"],results=results[:150])))
