#!/usr/bin/env python3
"""A stand-in for a language server, for checks that must run without clangd, csharp-ls or the like.

It speaks the Language Server Protocol over standard input and output. A definition request is answered with the
first line of the same file; a references request with two places in the project and one outside it; a
document-symbols request is preceded by the diagnostics for that file, as real servers publish them: one error
while the file's text mentions `count`, and none once it does not.

Like real servers it asks the client a question of its own after the greeting and reads on only once it is answered.
With `--pull` it publishes nothing and answers a request for a file's diagnostics instead.
"""
import json
import sys


def read():
    length = 0
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            return None
        if line in (b"\r\n", b"\n"):
            break
        if line.lower().startswith(b"content-length:"):
            length = int(line.split(b":")[1])
    return json.loads(sys.stdin.buffer.read(length))


def write(message):
    body = json.dumps(dict(message, jsonrpc="2.0")).encode()
    sys.stdout.buffer.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)
    sys.stdout.buffer.flush()


pull = "--pull" in sys.argv
texts = {}
waiting = []


def found(uri):
    if "count" not in texts.get(uri, ""):
        return []
    return [{"range": {"start": {"line": 2, "character": 11}, "end": {"line": 2, "character": 16}}, "severity": 1, "message": "use of undeclared identifier 'count'"},
            {"range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 3}}, "severity": 2, "message": "unused function"}]


while True:
    message = waiting.pop(0) if waiting else read()
    if message is None or message.get("method") == "exit":
        break
    method, params = message.get("method"), message.get("params") or {}
    if method == "initialize":
        capabilities = {"definitionProvider": True, "referencesProvider": True}
        if pull:
            capabilities["diagnosticProvider"] = {"interFileDependencies": False, "workspaceDiagnostics": False}
        write({"id": message["id"], "result": {"capabilities": capabilities}})
    elif method == "initialized":
        write({"id": 900, "method": "workspace/configuration", "params": {"items": [{"section": "fake"}]}})
        while True:
            answer = read()
            if answer is None:
                sys.exit(1)
            if answer.get("id") == 900 and "method" not in answer:
                if answer.get("result") != [None]:
                    sys.exit(2)
                break
            waiting.append(answer)
    elif method == "textDocument/didOpen":
        texts[params["textDocument"]["uri"]] = params["textDocument"]["text"]
    elif method == "textDocument/didChange":
        texts[params["textDocument"]["uri"]] = params["contentChanges"][0]["text"]
    elif method == "textDocument/diagnostic":
        write({"id": message["id"], "result": {"kind": "full", "items": found(params["textDocument"]["uri"])}})
    elif method == "textDocument/definition":
        write({"id": message["id"], "result": [{"uri": params["textDocument"]["uri"], "range": {"start": {"line": 0, "character": 4}, "end": {"line": 0, "character": 9}}}]})
    elif method == "textDocument/references":
        uri = params["textDocument"]["uri"]
        write({"id": message["id"], "result": [
            {"uri": uri, "range": {"start": {"line": 0, "character": 4}, "end": {"line": 0, "character": 9}}},
            {"uri": uri, "range": {"start": {"line": params["position"]["line"], "character": params["position"]["character"]}, "end": {"line": 0, "character": 0}}},
            {"uri": "file:///usr/include/stdio.h", "range": {"start": {"line": 10, "character": 0}, "end": {"line": 10, "character": 5}}}]})
    elif method == "textDocument/documentSymbol":
        uri = params["textDocument"]["uri"]
        if not pull:
            write({"method": "textDocument/publishDiagnostics", "params": {"uri": uri, "diagnostics": found(uri)}})
        write({"id": message["id"], "result": []})
    elif "id" in message:
        write({"id": message["id"], "result": None})
