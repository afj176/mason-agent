// Checking a page of the project in a real browser.
#pragma once
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
    /// True when every step did what it was asked and the page raised no script error.
    bool passed() const;
};

struct Reply {
    int status = 404;
    std::string content_type, body;
};

/// The program of an installed Chromium-family browser, or empty. `MASON_BROWSER` names one explicitly.
std::string find();
/// What the page server answers for a request target. Only project files and the harness are served.
Reply respond(const std::string& target, const Workspace& workspace);
/// Loads `page` (a project-relative path to an HTML file) and carries out `steps`, a JSON array of objects such as
/// {"action": "click", "text": "Add"} or {"action": "fill", "selector": "#name", "value": "Ada"}.
Result run(const Workspace& workspace, const std::string& page, const json& steps = json::array(), const CancelToken& cancel = nullptr);
/// The result as the model reads it.
std::string report(const Result& result, const std::string& page);

}  // namespace browser
}  // namespace mason
