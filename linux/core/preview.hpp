// What the Website tab is built on: a development server started from a project workflow, the script that lets the
// user pick an element, and the note a pick becomes. The server for the project's own pages is browser::PageServer.
#pragma once
#include "tools.hpp"

namespace mason {

struct PreviewPick {
    std::string page, selector, kind, label, html;
    std::vector<std::string> console;
};

namespace preview {

/// Added to every page the Website tab shows, before the page's own scripts. It keeps the latest console errors and
/// warnings, and waits to be armed; once armed, the next click picks an element instead of using it, and the pick is
/// posted to the app as JSON through the message handler named "mason".
extern const char* const kScript;
/// Content rules for the Website panel's web view: nothing is loaded from anywhere but this computer.
extern const char* const kLoopbackOnly;
/// Run in the page when the user chooses to pick an element.
extern const char* const kArm;
extern const char* const kDisarm;

/// The pick a page posted, or nothing when the message is not one.
std::optional<PreviewPick> pick(const std::string& message, const std::string& page);
/// The pick as a note for the next request. Everything in it came from the page, so it is quoted as data.
std::string evidence(const PreviewPick& pick);
/// Clicks the picked element in a fresh headless browser at a phone width and a desktop width, and says what happened.
std::string verify_click(const Workspace& workspace, const std::string& page, const std::string& selector, const CancelToken& cancel = nullptr);
/// True when something accepts connections on 127.0.0.1 at `port`.
bool listening(int port);
/// The body of a page fetched from a server on 127.0.0.1, or nothing when it did not answer with success.
std::optional<std::string> fetch(int port, const std::string& path);

/// A development server started from one of the project's workflows, for the Website tab to show. It runs with the
/// user's account, as the workflow would from a terminal, and is stopped when this is destroyed.
class DevServer {
public:
    /// Starts the workflow and waits until something answers on the port.
    DevServer(const Workspace& workspace, const std::string& workflow, int port, const CancelToken& cancel = nullptr);
    std::string address() const { return "http://127.0.0.1:" + std::to_string(port_) + "/"; }
    std::string output();

private:
    Jobs jobs_;
    std::string id_;
    int port_;
};

}  // namespace preview
}  // namespace mason
