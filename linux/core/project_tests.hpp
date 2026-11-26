// A project's own test as the evidence for a requirement. `.mason/tests.json` names the test, the workflow that runs
// it, the format of its results and the files that define it. In Auto, a request that states the requirement gets a
// check: Mason runs the workflow before the work (the baseline) and again when the model says it is done, and the
// requirement holds only if the named test passes and the test's own files are as they were. The model cannot make a
// pinned test pass by editing it.
#pragma once
#include "acceptance.hpp"

namespace mason {

struct ProjectTest {
    std::string requirement, workflow;
    std::string format;  ///< tap, junit, trx, go-json or flutter-json.
    std::string report;  ///< For junit and trx: the report file the workflow writes, relative to the project.
    std::string test;
    std::vector<std::string> test_files, inputs;
    /// An extra check after the test passes: strings over an alphabet must survive encode then decode. {module, encode, decode, alphabet}.
    std::vector<std::string> round_trip = {};
    /// "interchangeable" when the function under test must give the same result with its two arguments exchanged.
    std::string argument_order = "";
};

namespace project_tests {

/// The project's named tests. Nothing when there is no manifest; an error when it is not valid.
std::vector<ProjectTest> load(const Workspace& workspace);
/// The test that stands for a requirement, if the project names one.
std::optional<ProjectTest> bound(const std::string& unit, const std::vector<ProjectTest>& tests);
/// Each test's outcome (passed, failed or skipped) by name. A name reported more than once has failed if any of them did.
std::map<std::string, std::string> outcomes(const std::string& format, const std::string& text);
/// The named test's outcome: by its exact name, or by the one reported name that ends with it after a separator.
std::optional<std::string> outcome_of(const std::string& name, const std::map<std::string, std::string>& outcomes);
/// A direct call of the one function a CommonJS module exports, with two literal arguments, taken from the test's own
/// files: {module, function, first argument as JSON, second argument as JSON}. Empty when there is none.
std::vector<std::string> example(const ProjectTest& test, const Workspace& workspace);
/// The check for a requirement the project names a test for: pinned now, with the baseline run recorded.
DerivedCheck check(const ProjectTest& test, int unit, const Workspace& workspace, bool require_sandbox, const CancelToken& cancel = nullptr);
DerivedResult verify(const DerivedCheck& check, const Workspace& workspace, bool require_sandbox, const CancelToken& cancel = nullptr);

}  // namespace project_tests
}  // namespace mason
