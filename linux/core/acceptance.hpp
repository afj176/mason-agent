// Acceptance scenarios a project keeps in mason.acceptance.json: for each requirement, a page and the steps that show
// it holds. Mason runs them in a real browser at a phone width and a desktop width and reports what it observed.
//
//   {"Version": 1, "Scenarios": [{"Id": "discount", "Requirement": "Ten percent off at three units", "Page": "index.html",
//     "Steps": [{"Op": "fill", "Target": "#qty", "Value": "3"}, {"Op": "money", "Target": "#total", "Value": "27.00"}]}]}
#pragma once
#include <thread>

#include "browser.hpp"
#include "model.hpp"
#include "shell.hpp"
#include "session.hpp"
#include "workspace.hpp"

namespace mason {

struct AcceptanceStep {
    std::string op, target, value;
};

struct AcceptanceScenario {
    std::string id, requirement, page;
    std::vector<AcceptanceStep> steps;
};

struct AcceptanceContract {
    std::string application;  ///< Empty for static pages.
    std::vector<AcceptanceScenario> scenarios;
};

struct AcceptanceResult {
    AcceptanceScenario scenario;
    bool passed = false;
    std::vector<std::string> detail;  ///< What each step observed, or where it stopped and why.
};

namespace acceptance {
constexpr const char* kFilename = "mason.acceptance.json";
bool exists(const Workspace& workspace);
/// Throws Error when the contract is not valid.
AcceptanceContract parse(const Workspace& workspace, const std::string& source);
AcceptanceContract read(const Workspace& workspace);
/// Runs every scenario at each width. A scenario passes when every step does at both widths and the page raises no script error.
/// A contract that names an application runs it in a throwaway copy of the project, a fresh one for each width.
/// `require_sandbox` refuses to run the application where the command sandbox is unavailable.
std::vector<AcceptanceResult> run(const Workspace& workspace, const AcceptanceContract& contract, const std::function<void(const std::string&)>& progress = nullptr, const CancelToken& cancel = nullptr,
                                  bool require_sandbox = false);
/// The results as the user and the model read them.
std::string receipt(const std::vector<AcceptanceResult>& results);
}  // namespace acceptance

/// An acceptance contract can name a Python WSGI application ("module:callable"). Mason copies the project to a
/// temporary folder, runs the application there inside the sandbox with no network, and passes the browser's requests
/// to it through files, so the application never opens a socket. The copy and its database are thrown away afterwards.
class VerificationApp : public browser::Backend {
public:
    /// Copies the project and starts its application. `sandboxed` runs it inside the command sandbox.
    VerificationApp(const Workspace& project, const std::string& entry, bool sandboxed, const CancelToken& cancel = nullptr);
    ~VerificationApp() override;
    VerificationApp(const VerificationApp&) = delete;
    VerificationApp& operator=(const VerificationApp&) = delete;
    const fs::path& folder() const { return folder_; }
    std::optional<browser::BackendReply> answer(const std::string& method, const std::string& target, const std::map<std::string, std::string>& headers, const std::string& body) override;
    /// The steps that look at the application itself rather than the page. Throws Error when the check fails.
    std::string check(const std::string& op, const std::string& target, const std::string& value, const std::string& captured);

private:
    void stop();
    fs::path folder_, mailbox_;
    std::thread thread_;
    CancelToken stop_ = make_cancel();
    std::mutex lock_;
    std::optional<CommandResult> ended_;
    std::vector<std::pair<std::string, std::string>> responses_;  ///< "METHOD /path" and its JSON body.
};

/// What a reviewing model found in a change, kept only when it points at a real line.
struct ReviewFindings {
    std::vector<ReviewComment> comments;
    int dropped = 0;  ///< Findings whose quoted evidence was not on the line they named; they are not shown as fact.
    std::vector<std::string> files;
};

namespace review {
/// Asks the model to read the files a session changed and report defects. Each finding must quote text from the line
/// it names; one that does not is dropped rather than shown. The findings come back as comments on those lines.
ReviewFindings findings(const Session& session, const Workspace& workspace, ChatModel& model, const CancelToken& cancel = nullptr);
/// What to tell the user about a review.
std::string summary(const ReviewFindings& found);
}  // namespace review

}  // namespace mason
