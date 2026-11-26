#!/usr/bin/env python3
"""A stand-in for a language server, for checks that must run without clangd, csharp-ls or the like.

It speaks the Language Server Protocol over standard input and output. A definition request is answered with the
first line of the same file; a references request with two places in the project and one outside it; a
document-symbols request is preceded by one diagnostic for the opened file, as real servers publish them.
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


opened = ""
while True:
    message = read()
    if message is None or message.get("method") == "exit":
        break
    method, params = message.get("method"), message.get("params") or {}
    if method == "initialize":
        write({"id": message["id"], "result": {"capabilities": {"definitionProvider": True, "referencesProvider": True}}})
    elif method == "textDocument/didOpen":
        opened = params["textDocument"]["uri"]
    elif method == "textDocument/definition":
        write({"id": message["id"], "result": [{"uri": params["textDocument"]["uri"], "range": {"start": {"line": 0, "character": 4}, "end": {"line": 0, "character": 9}}}]})
    elif method == "textDocument/references":
        uri = params["textDocument"]["uri"]
        write({"id": message["id"], "result": [
            {"uri": uri, "range": {"start": {"line": 0, "character": 4}, "end": {"line": 0, "character": 9}}},
            {"uri": uri, "range": {"start": {"line": params["position"]["line"], "character": params["position"]["character"]}, "end": {"line": 0, "character": 0}}},
            {"uri": "file:///usr/include/stdio.h", "range": {"start": {"line": 10, "character": 0}, "end": {"line": 10, "character": 5}}}]})
    elif method == "textDocument/documentSymbol":
        write({"method": "textDocument/publishDiagnostics", "params": {"uri": opened, "diagnostics": [
            {"range": {"start": {"line": 2, "character": 11}, "end": {"line": 2, "character": 16}}, "severity": 1, "message": "use of undeclared identifier 'count'"}]}})
        write({"id": message["id"], "result": []})
    elif "id" in message:
        write({"id": message["id"], "result": None})
