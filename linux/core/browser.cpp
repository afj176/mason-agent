#include "browser.hpp"

#include <algorithm>
#include <arpa/inet.h>
#include <atomic>
#include <chrono>
#include <csignal>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <regex>
#include <sys/socket.h>
#include <sys/wait.h>
#include <thread>
#include <unistd.h>

namespace mason::browser {

namespace {

// Added at the top of every HTML page the server sends, so errors are recorded from the first script on.
const char* const kRecorder =
    "<script>window.__masonErrors=[];addEventListener('error',function(e){__masonErrors.push(String(e.message||e)+(e.filename?' at '+e.filename.split('/').pop()+':'+e.lineno:''))});"
    "addEventListener('unhandledrejection',function(e){__masonErrors.push('Unhandled rejection: '+String(e.reason))});"
    "(function(c){console.error=function(){__masonErrors.push(Array.prototype.map.call(arguments,String).join(' '));c.apply(console,arguments)}})(console.error);"
    "window.alert=function(m){__masonErrors.push('The page opened a dialog: '+m)};window.confirm=function(){return true};window.prompt=function(){return ''};</script>";

// The page that loads the project page in a frame, carries out the steps and writes the result into itself.
const char* const kHarness = R"html(<!doctype html><meta charset="utf-8"><title>Mason check</title>
<iframe id="f" style="width:1280px;height:800px;border:0"></iframe><pre id="mason-result"></pre>
<script>
(async function () {
  var q = new URLSearchParams(location.search), out = { steps: [], errors: [], title: '', text: '' }, f = document.getElementById('f');
  function done() { document.getElementById('mason-result').textContent = 'MASON:' + btoa(unescape(encodeURIComponent(JSON.stringify(out)))) + ':MASON'; }
  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  try {
    var steps = JSON.parse(decodeURIComponent(escape(atob(q.get('steps') || 'W10='))));
    var captured = null;
    function unlabel(v) { var a = v.indexOf(':'), b = v.indexOf(String.fromCharCode(10)), i = a < 0 ? b : b < 0 ? a : Math.min(a, b); return i > 0 && i <= 64 && /^[A-Za-z -]+$/.test(v.slice(0, i)) ? v.slice(i + 1).trim() : v; }
    function cents(t) { var n = Number(String(t).replace(/[$, ]/g, '')); return isNaN(n) ? null : Math.round(n * 100); }
    function amount(v) { var t = unlabel(v).replace(/[ ]/g, ''); return /^-?[$]?-?[0-9][0-9,]*([.][0-9]+)?$/.test(t) ? cents(t) : null; }
    function hasId(v, prefix) { if (v.length === 0 || v.length > 200) return false; var id = prefix.length > 0 && v.indexOf(prefix) === 0 ? v : unlabel(v); return id.indexOf(prefix) === 0 && id.length > prefix.length; }
    if (steps.length && steps[0].action === 'viewport') { f.style.width = Number(steps[0].width) + 'px'; steps.shift(); }
    await new Promise(function (r) { f.onload = r; f.src = q.get('page'); });
    await sleep(300);
    function doc() { return f.contentDocument; }
    function find(s) {
      if (s.selector) return doc().querySelector(s.selector);
      var want = String(s.text || '').trim().toLowerCase();
      var all = Array.prototype.slice.call(doc().querySelectorAll('button,a,input,select,textarea,label,summary,[role=button],[onclick],li,td,th,span,p,h1,h2,h3,h4,div'));
      function label(e) { return String(e.innerText || e.value || e.placeholder || e.getAttribute('aria-label') || '').trim().toLowerCase(); }
      return all.filter(function (e) { return label(e) === want; })[0] || all.filter(function (e) { return e.children.length === 0 && label(e).indexOf(want) >= 0; })[0] || null;
    }
    for (var i = 0; i < steps.length; i++) {
      var s = steps[i], r = { action: s.action, ok: false, detail: '' };
      try {
        var body = function () { return String(doc().body.innerText || ''); };
        if (s.action === 'click') { var e = find(s); if (!e) throw new Error('nothing on the page matches'); e.click(); await sleep(s.wait || 300); r.ok = true; }
        else if (s.action === 'fill') {
          var t = find(s); if (!t) throw new Error('nothing on the page matches');
          if (t.tagName === 'LABEL' && t.control) t = t.control;
          t.focus(); t.value = String(s.value == null ? '' : s.value);
          t.dispatchEvent(new Event('input', { bubbles: true })); t.dispatchEvent(new Event('change', { bubbles: true })); r.ok = true;
        }
        else if (s.action === 'expect_text') { r.ok = body().indexOf(String(s.text)) >= 0; if (!r.ok) r.detail = 'the page does not show it'; }
        else if (s.action === 'expect_no_text') { r.ok = body().indexOf(String(s.text)) < 0; if (!r.ok) r.detail = 'the page shows it'; }
        else if (s.action === 'wait') { await sleep(Math.min(Number(s.ms) || 500, 5000)); r.ok = true; }
        else if (s.action === 'accept') {
          var op = s.op, target = String(s.target || ''), expected = String(s.value == null ? '' : s.value);
          var nodes = function () { return target ? Array.prototype.slice.call(doc().querySelectorAll(target)).filter(function (e) { return e.getClientRects().length > 0; }) : []; };
          r.action = op + (target ? ' ' + target : '');
          if (op === 'reload') {
            out.errors = out.errors.concat((f.contentWindow.__masonErrors || []).slice(0, 20));
            await new Promise(function (done) { f.onload = done; f.contentWindow.location.reload(); });
            await sleep(300); r.ok = true;
          } else if (op === 'click' || op === 'double_click' || op === 'click_if_enabled' || op === 'fill') {
            var n = nodes()[0]; if (!n) throw new Error('nothing visible matches');
            if (op === 'click') n.click();
            else if (op === 'double_click') { n.click(); n.click(); n.dispatchEvent(new MouseEvent('dblclick', { bubbles: true })); }
            else if (op === 'click_if_enabled') { if (!n.disabled) n.click(); }
            else { n.focus(); n.value = expected; n.dispatchEvent(new Event('input', { bubbles: true })); n.dispatchEvent(new Event('change', { bubbles: true })); n.blur(); }
            await sleep(150); r.ok = true;
          } else {
            var started = Date.now(), actual = '', count = 0;
            while (true) {
              var values = nodes().map(function (e) { return String(e.innerText || '').trim(); });
              count = values.length; actual = count === 1 ? values[0] : values.slice(0, 8).join(' | ');
              var pass = false;
              if (op === 'count_distinct') { var seen = {}; values.forEach(function (v) { seen[v.toLowerCase()] = 1; }); pass = count === Number(expected) && values.every(function (v) { return v.length > 0; }) && Object.keys(seen).length === count; }
              else if (op === 'empty') pass = count === 0 || (count === 1 && actual.length === 0);
              else if (op === 'shelf_price') { var m = /[$][ ]*([0-9]+([.][0-9]{1,2})?)/.exec(actual); pass = count === 1 && !!m && cents(m[1]) === cents(expected); }
              else if (op === 'money') pass = count === 1 && amount(actual) !== null && amount(actual) === cents(expected);
              else if (op === 'text') pass = count === 1 && actual === expected;
              else if (op === 'capture') pass = count === 1 && hasId(actual, expected);
              else if (op === 'same') pass = count === 1 && actual === captured;
              else if (op === 'new_id') pass = count === 1 && hasId(actual, expected) && unlabel(actual) !== unlabel(captured || '');
              else throw new Error('unknown step');
              if (pass) break;
              if (Date.now() - started >= 2200) throw new Error("expected '" + (op === 'same' ? captured : expected) + "', observed '" + actual.slice(0, 160) + "' (" + count + ' visible)');
              await sleep(75);
            }
            if (op === 'capture' || op === 'new_id') captured = actual;
            r.ok = true; r.detail = actual.slice(0, 160);
          }
        }
        else throw new Error('unknown action; use click, fill, expect_text, expect_no_text or wait');
      } catch (error) { r.detail = String(error && error.message || error); }
      out.steps.push(r);
      if (!r.ok) break;
    }
    out.title = doc().title; out.text = String(doc().body.innerText || '').slice(0, 6000);
    out.errors = out.errors.concat((f.contentWindow.__masonErrors || []).slice(0, 20));
    out.controls = Array.prototype.slice.call(doc().querySelectorAll('button,a[href],input:not([type=hidden]):not([type=password]),select,textarea,[role=button]')).filter(function (e) { return e.getClientRects().length > 0; }).slice(0, 64).map(function (e) {
      var kind = e.tagName.toLowerCase(), selector = e.id ? '#' + e.id : kind + (e.name ? '[name="' + e.name + '"]' : '');
      return selector + ' | ' + kind + ' | ' + String(e.getAttribute('aria-label') || e.innerText || e.placeholder || e.value || e.name || e.type || '').trim().replace(/\s+/g, ' ').slice(0, 80);
    });
  } catch (error) { out.errors.push('The check could not run: ' + String(error && error.message || error)); }
  done();
})();
</script>
)html";

const char* const kAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

std::string base64(const std::string& data) {
    std::string out;
    for (size_t i = 0; i < data.size(); i += 3) {
        const unsigned a = static_cast<unsigned char>(data[i]);
        const unsigned b = i + 1 < data.size() ? static_cast<unsigned char>(data[i + 1]) : 0;
        const unsigned c = i + 2 < data.size() ? static_cast<unsigned char>(data[i + 2]) : 0;
        out += kAlphabet[a >> 2];
        out += kAlphabet[((a & 3) << 4) | (b >> 4)];
        out += i + 1 < data.size() ? kAlphabet[((b & 15) << 2) | (c >> 6)] : '=';
        out += i + 2 < data.size() ? kAlphabet[c & 63] : '=';
    }
    return out;
}

std::string unbase64(const std::string& text) {
    std::string out;
    unsigned buffer = 0;
    int bits = 0;
    for (const char c : text) {
        const char* at = std::strchr(kAlphabet, c);
        if (c == '=' || !at || !c) continue;
        buffer = (buffer << 6) | static_cast<unsigned>(at - kAlphabet);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out += static_cast<char>((buffer >> bits) & 0xFF);
        }
    }
    return out;
}

std::string percent_decode(const std::string& text) {
    std::string out;
    for (size_t i = 0; i < text.size(); i++) {
        if (text[i] == '%' && i + 2 < text.size() && std::isxdigit(static_cast<unsigned char>(text[i + 1])) && std::isxdigit(static_cast<unsigned char>(text[i + 2]))) {
            out += static_cast<char>(std::stoi(text.substr(i + 1, 2), nullptr, 16));
            i += 2;
        } else {
            out += text[i];
        }
    }
    return out;
}

std::string percent_encode(const std::string& text) {
    static const char* const hex = "0123456789ABCDEF";
    std::string out;
    for (const unsigned char c : text) {
        if (std::isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~' || c == '/') out += static_cast<char>(c);
        else { out += '%'; out += hex[c >> 4]; out += hex[c & 15]; }
    }
    return out;
}

std::string content_type(const std::string& path) {
    std::string kind = fs::path(path).extension().string();
    if (!kind.empty()) kind = to_lower(kind.substr(1));
    static const std::pair<const char*, const char*> known[] = {
        {"html", "text/html; charset=utf-8"}, {"htm", "text/html; charset=utf-8"}, {"js", "text/javascript; charset=utf-8"}, {"mjs", "text/javascript; charset=utf-8"},
        {"css", "text/css; charset=utf-8"}, {"json", "application/json"}, {"map", "application/json"}, {"svg", "image/svg+xml"}, {"png", "image/png"},
        {"jpg", "image/jpeg"}, {"jpeg", "image/jpeg"}, {"gif", "image/gif"}, {"webp", "image/webp"}, {"ico", "image/x-icon"}, {"woff2", "font/woff2"}, {"woff", "font/woff"},
        {"txt", "text/plain; charset=utf-8"}, {"md", "text/plain; charset=utf-8"}, {"csv", "text/plain; charset=utf-8"}, {"wasm", "application/wasm"},
    };
    for (const auto& [extension, type] : known) {
        if (kind == extension) return type;
    }
    return "application/octet-stream";
}

/// A server on 127.0.0.1 that answers GET requests for project files until it is destroyed.
class Server {
public:
    explicit Server(const Workspace& workspace) : workspace_(workspace) {
        listener_ = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = inet_addr("127.0.0.1");
        socklen_t length = sizeof address;
        if (listener_ < 0 || bind(listener_, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0 || getsockname(listener_, reinterpret_cast<sockaddr*>(&address), &length) != 0 ||
            listen(listener_, 16) != 0) {
            if (listener_ >= 0) close(listener_);
            throw Error("The page server could not start.");
        }
        port_ = ntohs(address.sin_port);
        thread_ = std::thread([this] { serve(); });
    }
    ~Server() {
        stopped_ = true;
        shutdown(listener_, SHUT_RDWR);
        close(listener_);
        if (thread_.joinable()) thread_.join();
    }
    int port() const { return port_; }

private:
    void serve() {
        while (!stopped_) {
            const int client = accept4(listener_, nullptr, nullptr, SOCK_CLOEXEC);
            if (client < 0) return;
            if (!stopped_) handle(client);
            close(client);
        }
    }
    void handle(int client) {
        timeval timeout{5, 0};
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof timeout);
        std::string request;
        char buffer[4096];
        while (request.size() < 16384 && request.find("\r\n\r\n") == std::string::npos) {
            const ssize_t count = recv(client, buffer, sizeof buffer, 0);
            if (count <= 0) break;
            request.append(buffer, static_cast<size_t>(count));
        }
        const std::vector<std::string> parts = split(request.substr(0, request.find("\r\n")), ' ', false);
        Reply reply{405, "text/plain; charset=utf-8", "Only GET is served."};
        if (parts.size() >= 2 && (parts[0] == "GET" || parts[0] == "HEAD")) reply = respond(parts[1], workspace_);
        std::string wire = "HTTP/1.1 " + std::to_string(reply.status) + (reply.status == 200 ? " OK" : " Error") + "\r\nContent-Type: " + reply.content_type +
                           "\r\nContent-Length: " + std::to_string(reply.body.size()) + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        if (parts.empty() || parts[0] != "HEAD") wire += reply.body;
        size_t sent = 0;
        while (sent < wire.size()) {
            const ssize_t count = send(client, wire.data() + sent, wire.size() - sent, MSG_NOSIGNAL);
            if (count <= 0) break;
            sent += static_cast<size_t>(count);
        }
    }

    Workspace workspace_;
    int listener_ = -1, port_ = 0;
    std::atomic<bool> stopped_{false};
    std::thread thread_;
};

}  // namespace

bool Result::passed() const {
    return errors.empty() && std::all_of(steps.begin(), steps.end(), [](const Step& step) { return step.ok; });
}

std::string find() {
    std::vector<std::string> candidates;
    if (const char* named = std::getenv("MASON_BROWSER"); named && *named) candidates.push_back(named);
    for (const char* folder : {"/usr/bin", "/usr/local/bin", "/snap/bin", "/opt/google/chrome"}) {
        for (const char* name : {"google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser", "chrome"}) {
            candidates.push_back(std::string(folder) + "/" + name);
        }
    }
    for (const std::string& candidate : candidates) {
        std::error_code failure;
        if (access(candidate.c_str(), X_OK) == 0 && !fs::is_directory(candidate, failure)) return candidate;
    }
    return "";
}

Reply respond(const std::string& target, const Workspace& workspace) {
    const std::string path = target.substr(0, target.find('?'));
    if (path == "/__mason/harness") return {200, "text/html; charset=utf-8", kHarness};
    std::string relative = percent_decode(path);
    relative.erase(0, relative.find_first_not_of('/'));
    if (relative.empty() || relative.back() == '/') relative += "index.html";
    std::optional<std::string> data;
    try {
        data = workspace.data(relative);
    } catch (const std::exception&) {
        // Outside the project, or excluded content.
    }
    if (!data) return {404, "text/plain; charset=utf-8", "Not in the project."};
    const std::string kind = content_type(relative);
    if (starts_with(kind, "text/html") && is_valid_utf8(*data)) {
        // After the doctype when there is one, so the page keeps its rendering mode.
        static const std::regex doctype("<!doctype[^>]*>", std::regex::icase);
        std::smatch match;
        std::string page = *data;
        if (std::regex_search(page, match, doctype)) page.insert(static_cast<size_t>(match.position(0) + match.length(0)), kRecorder);
        else page = kRecorder + page;
        return {200, kind, page};
    }
    return {200, kind, *data};
}

Result run(const Workspace& workspace, const std::string& page, const json& steps, const CancelToken& cancel) {
    const std::string program = find();
    if (program.empty()) {
        throw Error("No Chrome, Chromium, Edge or Brave was found on this computer, so the page cannot be checked. Install one, or set MASON_BROWSER to its program.");
    }
    std::string kind = fs::path(page).extension().string();
    if (!workspace.exists(page) || (to_lower(kind) != ".html" && to_lower(kind) != ".htm")) throw Error(page + " is not an HTML file in the project.");
    if (!steps.is_array() || steps.size() > 40) throw Error("steps must be a list of at most 40 steps.");

    Server server(workspace);
    const fs::path profile = fs::temp_directory_path() / ("mason-browser-" + new_id().substr(0, 12));
    struct Cleanup {
        fs::path folder;
        ~Cleanup() { std::error_code ignored; fs::remove_all(folder, ignored); }
    } cleanup{profile};
    const std::string address = "http://127.0.0.1:" + std::to_string(server.port()) + "/__mason/harness?page=" + replace_all(percent_encode("/" + page), "/", "%2F") + "&steps=" +
                                replace_all(replace_all(base64(steps.dump()), "+", "%2B"), "=", "%3D");
    // No profile, no extensions, no first-run screens, and no name resolves except this computer.
    std::vector<std::string> arguments = {program, "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
                                          "--mute-audio", "--user-data-dir=" + profile.string(), "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 127.0.0.1", "--window-size=1320,900",
                                          "--virtual-time-budget=20000", "--dump-dom", address};
    int out[2];
    if (pipe2(out, O_CLOEXEC) != 0) throw Error("The browser could not start.");
    std::vector<char*> argv;
    for (std::string& argument : arguments) argv.push_back(argument.data());
    argv.push_back(nullptr);
    const pid_t child = fork();
    if (child < 0) { close(out[0]); close(out[1]); throw Error("The browser could not start."); }
    if (child == 0) {
        setsid();
        const int null = open("/dev/null", O_RDWR);
        dup2(null, STDIN_FILENO);
        dup2(out[1], STDOUT_FILENO);
        dup2(null, STDERR_FILENO);
        execv(argv[0], argv.data());
        _exit(127);
    }
    close(out[1]);
    std::string dom;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(45);
    bool stopped = false;
    char buffer[8192];
    while (true) {
        pollfd waiting{out[0], POLLIN, 0};
        if (poll(&waiting, 1, 200) > 0) {
            const ssize_t count = read(out[0], buffer, sizeof buffer);
            if (count <= 0) break;
            if (dom.size() < 8 * 1024 * 1024) dom.append(buffer, static_cast<size_t>(count));
        }
        if (is_cancelled(cancel) || std::chrono::steady_clock::now() > deadline) { stopped = true; break; }
    }
    close(out[0]);
    kill(-child, SIGKILL);
    int status = 0;
    waitpid(child, &status, 0);
    check_cancel(cancel);

    const size_t start = dom.find("MASON:");
    const size_t end = start == std::string::npos ? std::string::npos : dom.find(":MASON", start + 6);
    const json object = end == std::string::npos ? json() : json::parse(unbase64(dom.substr(start + 6, end - start - 6)), nullptr, false);
    if (!object.is_object()) {
        throw Error("The browser gave no result for " + page + (stopped ? ". It did not finish within 45 seconds." : ". It may have been stopped, or it could not load the page."));
    }
    Result result;
    result.title = object.value("title", "");
    result.text = object.value("text", "");
    if (object.contains("errors") && object["errors"].is_array()) {
        for (const json& item : object["errors"]) {
            if (item.is_string()) result.errors.push_back(item.get<std::string>());
        }
    }
    if (object.contains("controls") && object["controls"].is_array()) {
        for (const json& item : object["controls"]) {
            if (item.is_string()) result.controls.push_back(item.get<std::string>());
        }
    }
    if (object.contains("steps") && object["steps"].is_array()) {
        for (const json& item : object["steps"]) {
            if (item.is_object()) result.steps.push_back({item.value("action", ""), item.value("ok", false), item.value("detail", "")});
        }
    }
    return result;
}

std::string report(const Result& result, const std::string& page) {
    std::string text = std::string(result.passed() ? "PASSED" : "FAILED") + ": " + page + (result.title.empty() ? "" : "  ·  title: " + result.title);
    for (size_t i = 0; i < result.steps.size(); i++) {
        const Step& step = result.steps[i];
        text += "\n" + std::to_string(i + 1) + ". " + step.action + ": " + (step.ok ? "ok" : "FAILED" + (step.detail.empty() ? "" : " (" + step.detail + ")"));
    }
    if (!result.errors.empty()) {
        text += "\nScript errors:";
        for (const std::string& error : result.errors) text += "\n  " + error;
    }
    if (!result.controls.empty()) {
        text += "\nControls (selector | kind | label):";
        for (const std::string& control : result.controls) text += "\n  " + control;
    }
    text += "\nWhat the page shows:\n" + (result.text.empty() ? std::string("  (nothing)") : result.text);
    return text;
}

}  // namespace mason::browser
