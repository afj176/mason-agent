#include "policy.hpp"

#include "experiments.hpp"

#include <algorithm>
#include <set>

namespace mason {

void ProjectPolicy::check(const std::string& tool, const std::string& workflow) const {
    const bool off = std::find(disabled_tools.begin(), disabled_tools.end(), tool) != disabled_tools.end();
    const bool unlisted = tool == "run_workflow" && workflows && std::find(workflows->begin(), workflows->end(), workflow) == workflows->end();
    if (off || unlisted) {
        throw Error("The project policy " + name + " does not allow " + (tool == "run_workflow" ? "the workflow " + workflow : tool) + ". The user set it; do not look for a way around it.");
    }
}

namespace policies {

namespace {

std::string real(const std::string& path) {
    std::error_code ignored;
    const fs::path resolved = fs::weakly_canonical(path, ignored);
    return resolved.empty() ? path : resolved.string();
}

fs::path file(const fs::path& home, const std::string& workspace) { return home / "project-policies" / (experiments::fingerprint(to_lower(real(workspace))) + ".json"); }

std::string described(const ProjectPolicy& policy) {
    auto listed = [](std::vector<std::string> items) {
        std::sort(items.begin(), items.end());
        return join(items, ", ");
    };
    std::string when = "never";
    if (policy.expires != 0) {
        char text[64] = "";
        std::tm local{};
        localtime_r(&policy.expires, &local);
        std::strftime(text, sizeof text, "%Y-%m-%d %H:%M", &local);
        when = std::string(text) + (policy.expires <= std::time(nullptr) ? " (lapsed)" : "");
    }
    return "Policy " + policy.name + "\ntools off   " + (policy.disabled_tools.empty() ? std::string("none") : listed(policy.disabled_tools)) + "\nworkflows   " +
           (policy.workflows ? (policy.workflows->empty() ? std::string("none may run") : listed(*policy.workflows)) : std::string("any")) + "\nhooks       " +
           (policy.hooks ? "on: workflows marked afterEdit or beforeVerify run by themselves in Auto" : "off") + "\nexpires     " + when;
}

}  // namespace

const char* const kUsage =
    "/policy                         This project's policy\n"
    "/policy disable TOOL…           Switch tools off for this project (enable TOOL… switches them back on)\n"
    "/policy workflows NAME…         The only workflows that may run (any: no limit)\n"
    "/policy hooks on|off            Whether workflows marked afterEdit or beforeVerify run by themselves, in Auto\n"
    "/policy expires DAYS            When the policy lapses (never: it stays)\n"
    "/policy name NAME               What the policy is called when it refuses something\n"
    "/policy clear                   Remove the policy";

std::optional<ProjectPolicy> load(const fs::path& home, const std::string& workspace, bool include_expired) {
    const fs::path path = file(home, workspace);
    std::error_code ignored;
    if (!fs::exists(path, ignored) || fs::file_size(path, ignored) > 32 * 1024) return std::nullopt;
    const json value = json::parse(read_file(path), nullptr, false);
    if (!value.is_object() || value.value("workspace", "") != real(workspace)) return std::nullopt;
    ProjectPolicy policy;
    policy.name = value.value("name", "");
    policy.workspace = value.value("workspace", "");
    if (value.contains("disabledTools") && value["disabledTools"].is_array()) {
        for (const json& item : value["disabledTools"]) {
            if (item.is_string()) policy.disabled_tools.push_back(item.get<std::string>());
        }
    }
    if (value.contains("workflows") && value["workflows"].is_array()) {
        policy.workflows.emplace();
        for (const json& item : value["workflows"]) {
            if (item.is_string()) policy.workflows->push_back(item.get<std::string>());
        }
    }
    policy.expires = static_cast<std::time_t>(value.value("expires", 0LL));
    policy.hooks = value.value("hooks", false);
    if (!include_expired && policy.expires != 0 && policy.expires <= std::time(nullptr)) return std::nullopt;
    return policy;
}

void set(const std::optional<ProjectPolicy>& policy, const fs::path& home, const std::string& workspace) {
    const fs::path path = file(home, workspace);
    if ((real(home.string()) + "/").rfind(real(workspace) + "/", 0) == 0) throw Error("Policies are kept outside the project.");
    if (!policy) {
        std::error_code ignored;
        fs::remove(path, ignored);
        return;
    }
    bool valid = !policy->name.empty() && utf8_length(policy->name) <= 80 && policy->disabled_tools.size() <= 128 && (!policy->workflows || policy->workflows->size() <= 32);
    std::vector<std::string> names = policy->disabled_tools;
    if (policy->workflows) names.insert(names.end(), policy->workflows->begin(), policy->workflows->end());
    for (const std::string& item : names) valid = valid && !item.empty() && item.size() <= 80;
    if (!valid) throw Error("A policy has a name of up to 80 characters, up to 128 tools and up to 32 workflows.");
    json value = {{"name", policy->name}, {"workspace", policy->workspace}, {"disabledTools", policy->disabled_tools}, {"expires", static_cast<long long>(policy->expires)}, {"hooks", policy->hooks}};
    if (policy->workflows) value["workflows"] = *policy->workflows;
    fs::create_directories(path.parent_path());
    write_file_atomic(path, value.dump(2));
}

std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    const std::string root = workspace.root();
    const std::optional<ProjectPolicy> existing = load(home, root, true);
    if (words.empty()) {
        return existing ? described(*existing) : std::string("This project has no policy: every tool is available, any workflow may run, and hooks are off.\n") + kUsage;
    }
    ProjectPolicy policy = existing.value_or(ProjectPolicy{"for " + fs::path(root).filename().string(), real(root), {}, std::nullopt, 0, false});
    const std::string verb = to_lower(words[0]);
    const std::vector<std::string> named(words.begin() + 1, words.end());
    if (verb == "disable") {
        if (named.empty()) throw Error("Name the tools to switch off.");
        std::set<std::string> all(policy.disabled_tools.begin(), policy.disabled_tools.end());
        all.insert(named.begin(), named.end());
        policy.disabled_tools.assign(all.begin(), all.end());
    } else if (verb == "enable") {
        if (named.empty()) throw Error("Name the tools to switch back on.");
        policy.disabled_tools.erase(std::remove_if(policy.disabled_tools.begin(), policy.disabled_tools.end(), [&](const std::string& tool) { return std::find(named.begin(), named.end(), tool) != named.end(); }),
                                    policy.disabled_tools.end());
    } else if (verb == "workflows") {
        if (named.empty()) throw Error("Name the workflows that may run, or any.");
        if (named.size() == 1 && to_lower(named[0]) == "any") policy.workflows.reset();
        else policy.workflows = named;
    } else if (verb == "hooks") {
        if (named.size() != 1 || (to_lower(named[0]) != "on" && to_lower(named[0]) != "off")) throw Error("Use /policy hooks on or /policy hooks off.");
        policy.hooks = to_lower(named[0]) == "on";
    } else if (verb == "expires") {
        if (named.size() != 1) throw Error("Use /policy expires DAYS, or /policy expires never.");
        if (to_lower(named[0]) == "never") {
            policy.expires = 0;
        } else {
            if (named[0].empty() || named[0].size() > 4 || named[0].find_first_not_of("0123456789") != std::string::npos || std::stoi(named[0]) < 1 || std::stoi(named[0]) > 3650) {
                throw Error("Use a number of days from 1 to 3650, or never.");
            }
            policy.expires = std::time(nullptr) + static_cast<std::time_t>(std::stoi(named[0])) * 86400;
        }
    } else if (verb == "name") {
        if (named.empty()) throw Error("Give the policy a name.");
        policy.name = join(named, " ");
    } else if (verb == "clear") {
        set(std::nullopt, home, root);
        return "Removed this project's policy.";
    } else {
        return kUsage;
    }
    set(policy, home, root);
    return described(policy);
}

}  // namespace policies
}  // namespace mason
