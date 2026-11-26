// Single operations for scripts: `mason exec`, `mason map`, `mason symbol`, `mason git-status`.
#pragma once
#include "model.hpp"
#include "workspace.hpp"

#include <set>

namespace mason::script {

/// The first arguments that select a script operation instead of the interactive session.
const std::set<std::string>& names();
extern const char* const kUsage;

/// Runs one operation. Each event is one line of JSON, `{"type": ..., "data": ...}`, given to `write`.
/// Returns the exit code: 0 completed, 2 ran and did not complete, 1 error. `model` replaces the endpoint in tests.
int run(const std::vector<std::string>& arguments, const std::function<void(const std::string&)>& write, ChatModel* model = nullptr, const CancelToken& cancel = nullptr);

}  // namespace mason::script
