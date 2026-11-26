#!/bin/bash
# Checks an installed Mason Agent on a Mac end to end, with a stand-in model so no download is needed:
#   1. the installer put the app in /Applications and mason on PATH;
#   2. mason completes a request in Auto, where the command runs inside the macOS sandbox, then undoes what the
#      command created and compacts the conversation;
#   3. the app opens that session, takes a typed request, shows the approval sheet and writes the file once allowed.
# Screenshots of the app are left in $SHOTS. Run after installing dist/MasonAgent-<version>-macos.pkg.
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${WORK:-$(mktemp -d)}"
SHOTS="${SHOTS:-$WORK/screenshots}"
PORT=18400
export MASON_DATA_HOME="$WORK/home"
mkdir -p "$WORK/project" "$SHOTS"

APP="/Applications/Mason Agent.app"
[ -d "$APP" ] || { echo "The installer did not put the app in /Applications."; exit 1; }
[ "$(command -v mason)" = "/usr/local/bin/mason" ] || { echo "mason is not on PATH at /usr/local/bin/mason."; exit 1; }
mason --help >/dev/null

python3 "$HERE/Tests/stub-model.py" $PORT &
STUB=$!
APP_PID=""
trap 'kill $STUB ${APP_PID:-} 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -s -o /dev/null "http://127.0.0.1:$PORT/v1/models" && break; sleep 0.2; done

echo "--- command line, Auto ---"
cd "$WORK/project"
printf 'use the shell to make a file\n/status\n/exit\n' | mason --auto --endpoint "http://127.0.0.1:$PORT" --model stub | tee "$WORK/cli.txt"
[ "$(cat made.txt)" = "built" ] || { echo "The sandboxed command did not write made.txt."; exit 1; }
grep -q "Done." "$WORK/cli.txt" || { echo "The request did not finish with the model's answer."; exit 1; }

echo "--- command line, undo and compaction ---"
printf '/undo\n/compact\n/status\n/exit\n' | mason --resume --auto --endpoint "http://127.0.0.1:$PORT" --model stub | tee "$WORK/cli2.txt"
[ ! -e made.txt ] || { echo "/undo did not remove the file the command created."; exit 1; }
grep -q "replaced by a summary" "$WORK/cli2.txt" || { echo "/compact did not replace the conversation with a summary."; exit 1; }

# Hand the same session to the app in Review edits, so its next edit has to be approved.
printf '/exit\n' | mason --resume --review --endpoint "http://127.0.0.1:$PORT" --model stub >/dev/null

echo "--- app ---"
# Screenshots are taken in the dark theme, the one the website shows.
defaults write site.masonagent.MasonAgent appearance -string "Dark" || true
"$APP/Contents/MacOS/MasonAgent" &
APP_PID=$!
sleep 6
kill -0 $APP_PID
osascript -e 'tell application "System Events" to set frontmost of (first process whose unix id is '"$APP_PID"') to true'
sleep 2
screencapture -x "$SHOTS/1-session.png" || true
# Review edits is the default for a new request, so the write must wait for the approval sheet.
osascript -e 'tell application "System Events" to keystroke "write the hello file"' -e 'delay 1' \
          -e 'tell application "System Events" to keystroke return using command down'
sleep 5
screencapture -x "$SHOTS/2-approval.png" || true
[ ! -e hello.txt ] || { echo "The app wrote the file before it was approved."; exit 1; }
osascript -e 'tell application "System Events" to keystroke return'
for _ in $(seq 1 30); do [ -e hello.txt ] && break; sleep 0.5; done
sleep 2
screencapture -x "$SHOTS/3-done.png" || true
[ "$(cat hello.txt)" = "hello from the stub model" ] || { echo "The app did not write hello.txt after approval."; exit 1; }
kill -0 $APP_PID

echo "--- app, chat ---"
# New Chat (Shift-Command-N) opens the Chat pane with the message box focused.
osascript -e 'tell application "System Events" to keystroke "n" using {command down, shift down}'
sleep 2
osascript -e 'tell application "System Events" to keystroke "say hello"' -e 'delay 1' \
          -e 'tell application "System Events" to keystroke return using command down'
for _ in $(seq 1 30); do grep -rqs "Summary: the stub" "$MASON_DATA_HOME/chats" && break; sleep 0.5; done
sleep 1
screencapture -x "$SHOTS/4-chat.png" || true
grep -rqs "say hello" "$MASON_DATA_HOME/chats" || { echo "The app did not save the chat message."; exit 1; }
grep -rqs "Summary: the stub" "$MASON_DATA_HOME/chats" || { echo "The chat did not get an answer from the model."; exit 1; }
kill -0 $APP_PID

echo "--- app, a fuller session for the screenshot ---"
# Not a check: it only produces a picture of the app with a realistic session, so a failure here is reported and ignored.
(
  set +e
  kill $APP_PID; sleep 1
  DEMO="$WORK/ledger-lite"
  "$HERE/Scripts/demo-project.sh" "$DEMO" >/dev/null
  cd "$DEMO"
  printf '%s\n/exit\n' 'Bank exports with quoted payee names like "Smith, Jones & Co" break the importer: the row splits into too many columns. Use the csv module instead of split, and add a regression test.' \
    | mason --auto --endpoint "http://127.0.0.1:$PORT" --model stub > "$WORK/demo.txt" 2>&1
  printf '/exit\n' | mason --resume --review --endpoint "http://127.0.0.1:$PORT" --model stub >/dev/null 2>&1
  "$APP/Contents/MacOS/MasonAgent" &
  DEMO_PID=$!
  sleep 6
  osascript -e 'tell application "System Events" to set frontmost of (first process whose unix id is '"$DEMO_PID"') to true'
  sleep 2
  screencapture -x "$SHOTS/5-demo.png"
  kill $DEMO_PID
  grep -E "Completed|Failed" "$WORK/demo.txt" | tail -1
) || echo "The demo screenshot could not be made."

echo "Installed Mason Agent passed: PATH, sandboxed command, undo, compaction, an approved edit through the app, and a chat."
