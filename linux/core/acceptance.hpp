// Acceptance scenarios a project keeps in mason.acceptance.json: for each requirement, a page and the steps that show
// it holds. Mason runs them in a real browser at a phone width and a desktop width and reports what it observed.
//
//   {"Version": 1, "Scenarios": [{"Id": "discount", "Requirement": "Ten percent off at three units", "Page": "index.html",
//     "Steps": [{"Op": "fill", "Target": "#qty", "Value": "3"}, {"Op": "money", "Target": "#total", "Value": "27.00"}]}]}
#pragma once
#include "model.hpp"
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
std::vector<AcceptanceResult> run(const Workspace& workspace, const AcceptanceContract& contract, const std::function<void(const std::string&)>& progress = nullptr, const CancelToken& cancel = nullptr);
/// The results as the user and the model read them.
std::string receipt(const std::vector<AcceptanceResult>& results);
}  // namespace acceptance

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
