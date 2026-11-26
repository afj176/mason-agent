// A task carried out on one of the project's pages by a model, a step at a time. Each turn the model is shown what
// the page shows and its controls, and chooses one thing to do. Mason then loads the page afresh in a real browser
// and replays every step so far, so what the model sees next is what those steps really lead to. The browser has no
// network beyond this computer, and the page's text is given to the model as data.
#pragma once
#include "model.hpp"
#include "workspace.hpp"

namespace mason {

struct BrowseOutcome {
    bool completed = false;
    std::string answer;
    json steps = json::array();        ///< The steps that were carried out, in order.
    std::vector<std::string> errors;   ///< Script errors the page raised on the last load.
    int turns = 0;
};

namespace browse {
BrowseOutcome run(const Workspace& workspace, const std::string& page, const std::string& task, ChatModel& model, int limit = 12, const CancelToken& cancel = nullptr);
}

}  // namespace mason
