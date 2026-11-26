// Generation settings kept for one model connection: how the model picks its words when it works as the coding
// agent. A setting left unset is not sent, so the server's own default applies. Top K, Min P and repeat penalty are
// llama.cpp's; another server may ignore them.
#pragma once

#include <map>

#include "model.hpp"
#include "util.hpp"

namespace mason {

struct GenerationField {
    std::string key, label;
    double low, high;
    bool whole;
};

namespace generation {
const std::vector<GenerationField>& fields();
/// The settings kept for a connection (a server and a model on it), by request field.
std::map<std::string, double> values(const ModelEndpoint& endpoint, const fs::path& home = data_home());
/// The same settings as they go in a request: whole numbers where the server expects them.
json body(const ModelEndpoint& endpoint, const fs::path& home = data_home());
/// A value as it is shown: no trailing zeros, and no decimal point for a whole-number field.
std::string shown(double value, const GenerationField& field);
/// The /generation command, shared by the command line and the app.
std::string command(const std::string& rest, const std::optional<ModelEndpoint>& endpoint, const fs::path& home = data_home());
extern const char* const kHelp;
}  // namespace generation

}  // namespace mason
