"""Run a WSGI application in Mason's disposable LPAC workspace. The browser never opens a socket."""
import base64
import importlib
import io
import json
import os
from pathlib import Path
import sys
import time
import traceback
from urllib.parse import urlsplit, unquote
from wsgiref.util import setup_testing_defaults

mailbox = Path(__file__).resolve().parent
sys.path.insert(0, os.getcwd())
module, name = sys.argv[1].split(":")
application = getattr(importlib.import_module(module), name)

def publish(path, data):
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(data), encoding="utf-8")
    os.replace(temp, path)

def dispatch(request):
    url = urlsplit(request["path"])
    body = base64.b64decode(request["body"])
    environ = {}
    setup_testing_defaults(environ)
    environ.update(REQUEST_METHOD=request["method"], PATH_INFO=unquote(url.path, encoding="latin1"),
                   QUERY_STRING=url.query, SERVER_NAME="workspace.mason.invalid", SERVER_PORT="443",
                   CONTENT_TYPE=request["contentType"], CONTENT_LENGTH=str(len(body)),
                   HTTP_COOKIE=request["cookie"], HTTP_HOST="workspace.mason.invalid")
    environ["wsgi.url_scheme"] = "https"
    environ["wsgi.input"] = io.BytesIO(body)
    response = {}
    output = io.BytesIO()
    def write(data):
        if output.tell() + len(data) > 2 * 1024 * 1024:
            raise ValueError("Response exceeds 2 MiB")
        output.write(data)
    def start(status, headers, exc_info=None):
        response.update(status=int(status.split()[0]), headers=dict((k.lower(), v) for k, v in headers))
        return write
    result = application(environ, start)
    try:
        for data in result:
            write(data)
    finally:
        if hasattr(result, "close"):
            result.close()
    response["body"] = base64.b64encode(output.getvalue()).decode("ascii")
    return response

publish(mailbox / "ready.json", {"ready": True})
while not (mailbox / "stop").exists():
    for path in mailbox.glob("*.request"):
        try:
            response = dispatch(json.loads(path.read_text(encoding="utf-8-sig")))
        except Exception:
            error = traceback.format_exc(limit=5)
            print(error, file=sys.stderr, flush=True)
            response = {"status": 500, "headers": {}, "body": "", "error": error[-1600:]}
        path.unlink()
        publish(path.with_suffix(".response"), response)
    time.sleep(.015)
