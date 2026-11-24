"""Exercise the installed CLI with redirected pipes and an independent terminal client."""
import argparse
import json
import subprocess
import time
from pathlib import Path


def main(cli, area):
    area.mkdir(parents=True, exist_ok=False)
    project = area / "project"
    profile = area / "profile"
    project.mkdir()
    profile.mkdir()

    def invoke(*args):
        command = [str(cli), *args, "--task-home", str(profile)]
        with subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, text=True) as proc:
            try:
                out, err = proc.communicate(timeout=15)
            except subprocess.TimeoutExpired:
                proc.kill()
                raise RuntimeError("Detached CLI failed to release the client's pipes")
            if proc.returncode:
                raise RuntimeError(err or out)
            return json.loads(out)["data"]

    try:
        program = ("const rl=require('node:readline').createInterface({input:process.stdin});"
                   "let count=0;rl.on('line',s=>{console.log('receipt '+s);count++});"
                   "rl.on('close',()=>console.log('count='+count))")
        task = invoke("terminal-start", "--workspace", str(project),
                      "--executable", "node", "--arguments", json.dumps(["-e", program]))["id"]
        for i in range(12):
            invoke("terminal-send", "--id", task, "--line", str(i))
        invoke("terminal-eof", "--id", task)
        until = time.monotonic() + 45
        while True:
            result = invoke("task-show", "--id", task)
            if not result["status"]["Active"]:
                break
            if time.monotonic() >= until:
                raise RuntimeError("The terminal did not exit after EOF")
            time.sleep(.25)
        events = [json.loads(row) for row in result["events"]]
        output = next(e["detail"]["output"] for e in events if e["type"] == "terminal.exit")
        expected = [f"receipt {i}" for i in range(12)] + ["count=12"]
        if result["status"]["State"] != "Completed" or output.splitlines() != expected:
            raise RuntimeError("Terminal input, ordering or exit status did not match: " + output)
        print("PASS detached CLI releases pipes, receives twelve ordered lines and exits at EOF")
    finally:
        for request in (profile / "background-tasks").glob("*/request.json"):
            subprocess.run([str(cli), "task-cancel", "--id", request.parent.name, "--task-home", str(profile)],
                           stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("cli", type=Path)
    parser.add_argument("area", type=Path)
    options = parser.parse_args()
    main(options.cli.resolve(), options.area.resolve())
