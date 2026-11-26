#!/usr/bin/env python3
"""A stand-in for a local model server, for checks that must run without a real model.

It speaks the OpenAI-compatible streaming protocol Mason uses and always decides the same way:
a request mentioning "shell" runs one command, any other request writes hello.txt, and once a
tool result has come back it answers "Done." A request without tools gets a one-line summary. Usage: stub-model.py PORT
"""
import json
import os
import sys
import time
from http.server import BaseHTTPRequestHandler, HTTPServer


def decide(messages):
    last_user = max((i for i, m in enumerate(messages) if m.get("role") == "user"), default=-1)
    if any(m.get("role") == "tool" for m in messages[last_user + 1:]):
        return None
    # Notes Mason adds (an undo, a summary, an agent's instructions) come first; what was typed is the last paragraph.
    text = str(messages[last_user].get("content", "")).rsplit("\n\n", 1)[-1] if last_user >= 0 else ""
    if "shell" in text.lower():
        return "run_shell", {"command": "echo built > made.txt && cat made.txt"}
    return "write_file", {"path": "hello.txt", "content": "hello from the stub model\n"}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        body = json.dumps({"object": "list", "data": [{"id": "stub", "object": "model"}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        # STUB_DELAY (seconds) makes the model slow, for checks that act while it is still working.
        time.sleep(float(os.environ.get("STUB_DELAY", "0")))
        request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        # A request that offers no tools is a plain question, such as a compaction summary: answer in text.
        call = decide(request.get("messages", [])) if request.get("tools") else None
        if call:
            delta = {"tool_calls": [{"index": 0, "id": "call_stub", "type": "function",
                                     "function": {"name": call[0], "arguments": json.dumps(call[1])}}]}
            finish = "tool_calls"
        else:
            delta, finish = {"content": "Done." if request.get("tools") else "Summary: the stub made a file when asked."}, "stop"
        events = [
            {"choices": [{"index": 0, "delta": delta, "finish_reason": None}]},
            {"choices": [{"index": 0, "delta": {}, "finish_reason": finish}], "usage": {"prompt_tokens": 100, "completion_tokens": 5}},
        ]
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()
        for event in events:
            self.wfile.write(b"data: " + json.dumps(event).encode() + b"\n\n")
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
