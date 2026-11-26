#include "preview.hpp"

#include "browser.hpp"

#include <chrono>
#include <thread>

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

namespace mason::preview {

// The same script as the Swift engine's (macos/Sources/MasonCore/Preview.swift); keep the two identical.
const char* const kScript = R"JS((function () {
  if (window.__masonPicker) return;
  var log = [];
  function note(kind, args) {
    try {
      log.push(kind + ': ' + Array.prototype.map.call(args, function (a) { return typeof a === 'string' ? a : JSON.stringify(a); }).join(' ').slice(0, 300));
      if (log.length > 20) log.shift();
    } catch (e) {}
  }
  ['error', 'warn'].forEach(function (kind) {
    var original = console[kind];
    console[kind] = function () { note(kind, arguments); return original.apply(console, arguments); };
  });
  window.addEventListener('error', function (event) {
    note('error', [event.message + ' (' + String(event.filename || '').split('/').pop() + ':' + event.lineno + ')']);
  });
  function selector(element) {
    var parts = [];
    while (element && element.nodeType === 1 && element !== document.documentElement) {
      if (element.id) { parts.unshift('#' + CSS.escape(element.id)); break; }
      var part = element.tagName.toLowerCase();
      var same = Array.prototype.filter.call(element.parentNode ? element.parentNode.children : [], function (c) { return c.tagName === element.tagName; });
      if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(element) + 1) + ')';
      parts.unshift(part);
      element = element.parentElement;
    }
    return parts.join(' > ');
  }
  var armed = false, box = null;
  // The element under the pointer is framed by a box laid over it, so the element itself is never changed.
  function mark(element) {
    if (!element) { if (box) box.style.display = 'none'; return; }
    if (!box) {
      box = document.createElement('div');
      box.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;border:2px solid #e8590c;border-radius:2px;box-sizing:border-box';
      (document.body || document.documentElement).appendChild(box);
    }
    var place = element.getBoundingClientRect();
    box.style.display = 'block';
    box.style.left = place.left + 'px'; box.style.top = place.top + 'px'; box.style.width = place.width + 'px'; box.style.height = place.height + 'px';
  }
  document.addEventListener('mouseover', function (event) { if (armed) mark(event.target); }, true);
  document.addEventListener('click', function (event) {
    if (!armed) return;
    event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation();
    var element = event.target;
    mark(null); armed = false;
    var label = String(element.getAttribute('aria-label') || element.innerText || element.value || element.getAttribute('alt') || element.getAttribute('placeholder') || '').trim().replace(/\s+/g, ' ').slice(0, 120);
    var picked = { selector: selector(element), kind: element.getAttribute('role') || element.tagName.toLowerCase(), label: label, html: element.outerHTML.slice(0, 600), console: log.slice() };
    try { window.webkit.messageHandlers.mason.postMessage(JSON.stringify(picked)); } catch (e) {}
  }, true);
  window.__masonPicker = { arm: function () { armed = true; }, disarm: function () { armed = false; mark(null); } };
})();)JS";

const char* const kArm = "window.__masonPicker && window.__masonPicker.arm();";
const char* const kDisarm = "window.__masonPicker && window.__masonPicker.disarm();";

std::optional<PreviewPick> pick(const std::string& message, const std::string& page) {
    const json object = json::parse(message, nullptr, false);
    if (!object.is_object() || !object.contains("selector") || !object["selector"].is_string()) return std::nullopt;
    auto text = [&](const char* key, size_t limit) {
        const std::string value = object.contains(key) && object[key].is_string() ? object[key].get<std::string>() : "";
        return utf8_length(value) > limit ? utf8_prefix(value, limit) : value;
    };
    PreviewPick found{page, object["selector"].get<std::string>(), text("kind", 40), text("label", 120), text("html", 600), {}};
    if (found.selector.empty() || found.selector.size() > 1000) return std::nullopt;
    if (object.contains("console") && object["console"].is_array()) {
        for (const json& line : object["console"]) {
            if (line.is_string()) found.console.push_back(utf8_length(line.get<std::string>()) > 300 ? utf8_prefix(line.get<std::string>(), 300) : line.get<std::string>());
        }
        if (found.console.size() > 20) found.console.erase(found.console.begin(), found.console.end() - 20);
    }
    return found;
}

std::string evidence(const PreviewPick& pick) {
    std::string text = "In " + pick.page + " I picked this element (what follows is copied from the page):\n- selector: `" + pick.selector + "`\n- kind: " + pick.kind +
                       (pick.label.empty() ? "" : ", label: “" + pick.label + "”") + "\n- HTML: `" + replace_all(pick.html, "\n", " ") + "`\n";
    if (pick.console.empty()) return text + "- console: no errors or warnings";
    text += "- console, latest last:";
    for (const std::string& line : pick.console) text += "\n    " + line;
    return text;
}

std::string verify_click(const Workspace& workspace, const std::string& page, const std::string& selector, const CancelToken& cancel) {
    std::string text = "Clicking `" + selector + "` in " + page + ", in a fresh browser:";
    for (int width : {390, 1280}) {
        const browser::Result result = browser::run(workspace, page, json::array({{{"action", "viewport"}, {"width", width}}, {{"action", "click"}, {"selector", selector}}}), cancel);
        std::string line = std::to_string(width) + " px: ";
        if (result.passed()) line += "clicked, and the page raised no error";
        else if (!result.steps.empty() && !result.steps.back().ok) line += "could not click: " + result.steps.back().detail;
        else {
            const std::vector<std::string> shown(result.errors.begin(), result.errors.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(3, result.errors.size())));
            line += "clicked, and the page raised: " + join(shown, "; ");
        }
        text += "\n" + line;
    }
    return text;
}

bool listening(int port) {
    const int descriptor = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (descriptor < 0) return false;
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(static_cast<uint16_t>(port));
    address.sin_addr.s_addr = inet_addr("127.0.0.1");
    const bool answered = connect(descriptor, reinterpret_cast<sockaddr*>(&address), sizeof address) == 0;
    close(descriptor);
    return answered;
}

DevServer::DevServer(const Workspace& workspace, const std::string& workflow, int port, const CancelToken& cancel) : port_(port) {
    if (port < 1024 || port > 65535) throw Error("Use a port from 1024 to 65535.");
    const auto known = workflows::load(workspace);
    const auto found = known.find(workflow);
    if (found == known.end()) throw Error("No workflow named " + workflow + " in mason.workflows.json.");
    if (listening(port)) throw Error("Something is already listening on port " + std::to_string(port) + ". Stop it, or choose another port.");
    id_ = jobs_.start(workflows::command_line(found->second), workspace, false);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(45);
    while (!listening(port)) {
        const std::string state = jobs_.observe(id_, false, 0);
        if (state.rfind("job " + id_ + ": running", 0) != 0) {
            jobs_.stop_all();
            throw Error("The workflow " + workflow + " ended before anything listened on port " + std::to_string(port) + ".\n" + (utf8_length(state) > 1200 ? utf8_suffix(state, 1200) : state));
        }
        if (std::chrono::steady_clock::now() > deadline || is_cancelled(cancel)) {
            jobs_.stop_all();
            throw Error("Nothing listened on port " + std::to_string(port) + " within 45 seconds of starting the workflow " + workflow + ".");
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
}

std::string DevServer::output() { return jobs_.observe(id_, false, 0); }

}  // namespace mason::preview
