// Checking a page of the project in a real browser.
#pragma once
#include <map>

#include "workspace.hpp"

namespace mason {

/// Loads a project page, optionally clicks, fills and looks for text, and reports what the page shows and any script
/// errors. It drives an installed Chromium-family browser headless, with no profile and no network beyond this
/// computer, against a small server that serves only the project.
namespace browser {

struct Step {
    std::string action;
    bool ok = false;
    std::string detail;
};

struct Result {
    std::string title, text;
    std::vector<std::string> errors;
    std::vector<Step> steps;
    /// What can be clicked or filled, each as "selector | kind | label".
    std::vector<std::string> controls;
    /// True when every step did what it was asked and the page raised no script error.
    bool passed() const;
};

struct Reply {
    int status = 404;
    std::string content_type, body;
};

/// Something that answers requests in place of the project's files: an application under test.
struct BackendReply {
    int status = 200;
    std::string content_type;
    std::vector<std::pair<std::string, std::string>> headers;
    std::string body;
};
class Backend {
public:
    virtual ~Backend() = default;
    /// The reply, or nothing to let the page server answer from the project's files and the harness.
    virtual std::optional<BackendReply> answer(const std::string& method, const std::string& target, const std::map<std::string, std::string>& headers, const std::string& body) = 0;
};
/// A page with the error recorder added at the top, after the doctype when there is one.
std::string with_recorder(const std::string& html);

/// The program of an installed Chromium-family browser, or empty. `MASON_BROWSER` names one explicitly.
std::string find();
/// What the page server answers for a request target. Only project files and the harness are served.
Reply respond(const std::string& target, const Workspace& workspace);
/// Loads `page` (a project-relative path to an HTML file) and carries out `steps`, a JSON array of objects such as
/// {"action": "click", "text": "Add"} or {"action": "fill", "selector": "#name", "value": "Ada"}.
/// `screenshot`, when given, is where a picture of the page is written after the steps.
Result run(const Workspace& workspace, const std::string& page, const json& steps = json::array(), const CancelToken& cancel = nullptr, Backend* backend = nullptr, const fs::path& screenshot = {});
/// The result as the model reads it.
std::string report(const Result& result, const std::string& page);

/// Serves the project's files on 127.0.0.1 for as long as it is kept, for the Website tab.
class PageServer {
public:
    explicit PageServer(const Workspace& workspace);
    ~PageServer();
    int port() const;
    /// The address of a project page, given its path relative to the project.
    std::string address_of(const std::string& page) const;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}  // namespace browser
}  // namespace mason
