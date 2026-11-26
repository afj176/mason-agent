#include "generation.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>

namespace mason::generation {

const char* const kHelp =
    "/generation                  The generation settings kept for the model in use\n"
    "/generation NAME VALUE       Set one: temperature (0–2), top_p (0–1), top_k (1–1000), min_p (0–1), repeat_penalty (0–2), seed\n"
    "/generation NAME off         Leave one to the server's default\n"
    "/generation clear            Leave them all to the server's defaults\n"
    "They apply to this server and model whenever it works as the coding agent. Chats have their own options.";

const std::vector<GenerationField>& fields() {
    static const std::vector<GenerationField> all = {
        {"temperature", "Temperature", 0, 2, false}, {"top_p", "Top P", 0, 1, false}, {"top_k", "Top K", 1, 1000, true},
        {"min_p", "Min P", 0, 1, false},             {"repeat_penalty", "Repeat penalty", 0, 2, false}, {"seed", "Seed", 0, 2147483647, true},
    };
    return all;
}

namespace {

fs::path file(const fs::path& home) { return home / "generation.json"; }
std::string name(const ModelEndpoint& endpoint) { return endpoint.url + " " + endpoint.model; }

json all(const fs::path& home) {
    std::error_code failure;
    if (!fs::exists(file(home), failure)) return json::object();
    try {
        const json saved = json::parse(read_file(file(home)), nullptr, false);
        return saved.is_object() ? saved : json::object();
    } catch (const std::exception&) {
        return json::object();
    }
}

const GenerationField* field_named(const std::string& key) {
    for (const GenerationField& field : fields()) {
        if (field.key == key) return &field;
    }
    return nullptr;
}

void save(const std::map<std::string, double>& values, const ModelEndpoint& endpoint, const fs::path& home) {
    json saved = all(home);
    if (values.empty()) saved.erase(name(endpoint));
    else saved[name(endpoint)] = values;
    fs::create_directories(home);
    write_file_atomic(file(home), saved.dump(2));
}

}  // namespace

std::string shown(double value, const GenerationField& field) {
    char text[64];
    if (field.whole) std::snprintf(text, sizeof text, "%lld", static_cast<long long>(value));
    else std::snprintf(text, sizeof text, "%g", value);
    return text;
}

std::map<std::string, double> values(const ModelEndpoint& endpoint, const fs::path& home) {
    std::map<std::string, double> found;
    const json saved = all(home);
    if (!saved.contains(name(endpoint)) || !saved[name(endpoint)].is_object()) return found;
    for (const auto& [key, value] : saved[name(endpoint)].items()) {
        if (field_named(key) && value.is_number()) found[key] = value.get<double>();
    }
    return found;
}

json body(const ModelEndpoint& endpoint, const fs::path& home) {
    json body = json::object();
    for (const auto& [key, value] : values(endpoint, home)) {
        if (field_named(key)->whole) body[key] = static_cast<long long>(value);
        else body[key] = value;
    }
    return body;
}

std::string command(const std::string& rest, const std::optional<ModelEndpoint>& endpoint, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    if (!words.empty() && words[0] == "help") return kHelp;
    if (!endpoint) throw Error("No model is in use. Generation settings are kept for a model, so start or choose one first.");
    std::map<std::string, double> kept = values(*endpoint, home);
    if (words.empty()) {
        std::vector<std::string> lines = {"Generation settings for " + endpoint->model + " at " + endpoint->url + ":"};
        for (const GenerationField& field : fields()) {
            const auto found = kept.find(field.key);
            lines.push_back(field.key + "  ·  " + (found == kept.end() ? "the server's default" : shown(found->second, field)));
        }
        return join(lines, "\n");
    }
    if (words.size() == 1 && words[0] == "clear") {
        save({}, *endpoint, home);
        return "Generation settings for " + endpoint->model + " are left to the server's defaults.";
    }
    const GenerationField* field = words.size() == 2 ? field_named(replace_all(to_lower(words[0]), "-", "_")) : nullptr;
    if (!field) {
        std::vector<std::string> names;
        for (const GenerationField& each : fields()) names.push_back(each.key);
        throw Error("Use /generation NAME VALUE, with one of: " + join(names, ", ") + ".");
    }
    const std::string given = to_lower(words[1]);
    if (given == "off" || given == "default" || given == "none") {
        kept.erase(field->key);
        save(kept, *endpoint, home);
        return field->label + " is left to the server's default for " + endpoint->model + ".";
    }
    char* end = nullptr;
    const double value = std::strtod(words[1].c_str(), &end);
    const bool number = end && *end == '\0' && end != words[1].c_str() && std::isfinite(value);
    if (!number || value < field->low || value > field->high || (field->whole && value != std::round(value))) {
        throw Error(field->label + " is a " + (field->whole ? "whole number" : "number") + " from " + shown(field->low, *field) + " to " + shown(field->high, *field) + ", or off.");
    }
    kept[field->key] = value;
    save(kept, *endpoint, home);
    return field->label + " is " + shown(value, *field) + " for " + endpoint->model + ".";
}

}  // namespace mason::generation
