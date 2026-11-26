#include "schedules.hpp"

#include <algorithm>
#include <set>

namespace mason {

namespace {

const char* const kDays[] = {"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"};
const char* const kCadences[] = {"hourly", "daily", "weekdays", "weekly"};

std::string iso(std::time_t when) {
    std::tm parts{};
    gmtime_r(&when, &parts);
    char buffer[32];
    std::strftime(buffer, sizeof buffer, "%Y-%m-%dT%H:%M:%SZ", &parts);
    return buffer;
}

std::time_t from_iso(const std::string& text) {
    std::tm parts{};
    if (!strptime(text.c_str(), "%Y-%m-%dT%H:%M:%SZ", &parts)) return 0;
    return timegm(&parts);
}

std::string two(int value) {
    char buffer[8];
    std::snprintf(buffer, sizeof buffer, "%02d", value);
    return buffer;
}

std::string local_text(std::time_t when) {
    std::tm parts{};
    localtime_r(&when, &parts);
    char buffer[40];
    std::strftime(buffer, sizeof buffer, "%a %e %b %H:%M", &parts);
    return buffer;
}

}  // namespace

std::time_t Schedule::next(std::time_t after) const {
    std::tm parts{};
    localtime_r(&after, &parts);
    parts.tm_sec = 0;
    parts.tm_isdst = -1;
    if (cadence == Cadence::Hourly) {
        parts.tm_min = minute;
        std::time_t candidate = mktime(&parts);
        if (candidate <= after) {
            parts.tm_hour += 1;
            parts.tm_isdst = -1;
            candidate = mktime(&parts);
        }
        return candidate;
    }
    parts.tm_hour = hour;
    parts.tm_min = minute;
    std::time_t candidate = mktime(&parts);
    for (int tries = 0; tries < 8; tries++) {
        std::tm day{};
        localtime_r(&candidate, &day);
        const bool fits = cadence == Cadence::Daily || (cadence == Cadence::Weekdays && day.tm_wday != 0 && day.tm_wday != 6) || (cadence == Cadence::Weekly && day.tm_wday == weekday);
        if (candidate > after && fits) return candidate;
        // The same clock time on the next day, whatever daylight saving does in between.
        day.tm_mday += 1;
        day.tm_hour = hour;
        day.tm_min = minute;
        day.tm_sec = 0;
        day.tm_isdst = -1;
        candidate = mktime(&day);
    }
    return candidate;
}

std::string Schedule::summary() const {
    const std::string time = two(hour) + ":" + two(minute);
    switch (cadence) {
        case Cadence::Hourly: return "Every hour at :" + two(minute);
        case Cadence::Daily: return "Every day at " + time;
        case Cadence::Weekdays: return "Weekdays at " + time;
        case Cadence::Weekly: return std::string("Every ") + kDays[std::clamp(weekday, 0, 6)] + " at " + time;
    }
    return time;
}

namespace schedules {

const char* const kUsage =
    "/schedule                                       list schedules\n"
    "/schedule add NAME hourly :MM [auto] -- REQUEST\n"
    "/schedule add NAME daily|weekdays HH:MM [auto] -- REQUEST\n"
    "/schedule add NAME weekly DAY HH:MM [auto] -- REQUEST\n"
    "/schedule on|off|remove NAME";

std::vector<Schedule> load(const fs::path& home) {
    std::vector<Schedule> found;
    try {
        const json parsed = json::parse(read_file(home / "schedules.json"), nullptr, false);
        if (!parsed.is_array()) return found;
        for (const json& item : parsed) {
            if (!item.is_object()) continue;
            Schedule schedule;
            schedule.id = item.value("id", new_id());
            schedule.name = item.value("name", "");
            schedule.workspace = item.value("workspace", "");
            schedule.prompt = item.value("prompt", "");
            if (item.contains("endpoint") && item["endpoint"].is_object()) schedule.endpoint = {item["endpoint"].value("url", ""), item["endpoint"].value("model", ""), ""};
            schedule.automatic = item.value("auto", false);
            const std::string cadence = item.value("cadence", "daily");
            for (int index = 0; index < 4; index++) {
                if (cadence == kCadences[index]) schedule.cadence = static_cast<Schedule::Cadence>(index);
            }
            schedule.hour = item.value("hour", 9);
            schedule.minute = item.value("minute", 0);
            schedule.weekday = item.value("weekday", 1);
            schedule.enabled = item.value("enabled", true);
            schedule.created = from_iso(item.value("created", ""));
            schedule.last_run = from_iso(item.value("lastRun", ""));
            schedule.last_session = item.value("lastSession", "");
            schedule.last_outcome = item.value("lastOutcome", "");
            found.push_back(std::move(schedule));
        }
    } catch (const std::exception&) {
        // No file yet, or one that cannot be read: no schedules.
    }
    return found;
}

void save(const std::vector<Schedule>& all, const fs::path& home) {
    if (all.size() > kLimit) throw Error("Keep up to " + std::to_string(kLimit) + " schedules.");
    std::set<std::string> names;
    json list = json::array();
    for (const Schedule& schedule : all) {
        const std::string name = trim(schedule.name), prompt = trim(schedule.prompt);
        std::error_code failure;
        if (name.empty() || utf8_length(name) > 80 || name.find(' ') != std::string::npos || prompt.empty() || utf8_length(prompt) > 16000 || !fs::is_directory(schedule.workspace, failure) ||
            schedule.hour < 0 || schedule.hour > 23 || schedule.minute < 0 || schedule.minute > 59 || schedule.weekday < 0 || schedule.weekday > 6) {
            throw Error("A schedule needs a one-word name, a request of up to 16,000 characters, a time, and an existing project folder.");
        }
        if (!names.insert(to_lower(name)).second) throw Error("Each schedule needs its own name.");
        json item = {{"id", schedule.id}, {"name", schedule.name}, {"workspace", schedule.workspace}, {"prompt", schedule.prompt},
                     {"endpoint", {{"url", schedule.endpoint.url}, {"model", schedule.endpoint.model}}}, {"auto", schedule.automatic},
                     {"cadence", kCadences[static_cast<int>(schedule.cadence)]}, {"hour", schedule.hour}, {"minute", schedule.minute}, {"weekday", schedule.weekday},
                     {"enabled", schedule.enabled}, {"created", iso(schedule.created)}, {"lastOutcome", schedule.last_outcome}};
        if (schedule.last_run > 0) item["lastRun"] = iso(schedule.last_run);
        if (!schedule.last_session.empty()) item["lastSession"] = schedule.last_session;
        list.push_back(std::move(item));
    }
    fs::create_directories(home);
    write_file_atomic(home / "schedules.json", list.dump(2));
}

std::vector<Schedule> due(const std::vector<Schedule>& all, std::time_t now) {
    std::vector<Schedule> found;
    for (const Schedule& schedule : all) {
        if (schedule.enabled && schedule.next(std::max(schedule.last_run, schedule.created)) <= now) found.push_back(schedule);
    }
    return found;
}

std::optional<Session> run(const Schedule& schedule, const fs::path& home, SessionStore& store, ChatModel* model, const CancelToken& cancel) {
    std::string outcome;
    std::optional<Session> result;
    try {
        std::unique_ptr<OpenAIClient> owned;
        if (!model) {
            const auto listed = OpenAIClient::models(schedule.endpoint.url);
            if (!listed || std::find(listed->begin(), listed->end(), schedule.endpoint.model) == listed->end()) {
                throw Error("Skipped: " + schedule.endpoint.model + " was not answering at " + schedule.endpoint.url + ".");
            }
            owned = std::make_unique<OpenAIClient>(schedule.endpoint);
            model = owned.get();
        }
        const Workspace workspace(schedule.workspace);
        Session session = store.create(workspace.root(), schedule.automatic ? Permission::Auto : Permission::ReadOnly);
        session.title = "Scheduled: " + schedule.name;
        session.ask_only = !schedule.automatic;
        Agent(store, *model, [](const ApprovalRequest&) { return false; }, [](const AgentEvent&) {}).run(session, schedule.prompt, cancel);
        outcome = session.state + (session.detail.empty() ? "" : ": " + session.detail);
        result = session;
    } catch (const std::exception& error) {
        outcome = error.what();
    }
    // Another run or an edit may have changed the file meanwhile, so only this schedule's record is updated.
    std::vector<Schedule> all = load(home);
    for (Schedule& item : all) {
        if (item.id != schedule.id) continue;
        item.last_run = std::time(nullptr);
        item.last_session = result ? result->id : "";
        item.last_outcome = utf8_prefix(outcome, 300);
        try {
            save(all, home);
        } catch (const std::exception&) {
        }
        break;
    }
    return result;
}

std::string command(const std::string& arguments, const fs::path& home, const std::string& workspace, const std::optional<ModelEndpoint>& endpoint, std::time_t now) {
    std::vector<Schedule> all = load(home);
    const size_t separator = arguments.find(" -- ");
    const std::string head = separator == std::string::npos ? arguments : arguments.substr(0, separator);
    const std::string prompt = separator == std::string::npos ? "" : trim(arguments.substr(separator + 4));
    const std::vector<std::string> words = split(head, ' ', false);
    auto index = [&](size_t word) -> size_t {
        const std::string wanted = word < words.size() ? to_lower(words[word]) : "";
        for (size_t i = 0; i < all.size(); i++) {
            if (to_lower(all[i].name) == wanted) return i;
        }
        throw Error("No schedule named " + wanted + ". /schedule lists them.");
    };
    const std::string verb = words.empty() ? "list" : to_lower(words[0]);
    if (verb == "list") {
        if (all.empty()) return std::string("No schedules. Add one with:\n") + kUsage;
        std::string text;
        for (const Schedule& schedule : all) {
            const std::string next = schedule.enabled ? "next " + local_text(schedule.next(std::max({schedule.last_run, schedule.created, now}))) : "off";
            const std::string last = schedule.last_run > 0 ? "last " + local_text(schedule.last_run) + (schedule.last_outcome.empty() ? "" : " · " + schedule.last_outcome) : "not run yet";
            text += (text.empty() ? "" : "\n") + schedule.name + "  ·  " + schedule.summary() + "  ·  " + (schedule.automatic ? "Auto" : "read-only") + "  ·  " + next + "\n    " +
                    fs::path(schedule.workspace).filename().string() + ": " + utf8_prefix(schedule.prompt, 80) + "\n    " + last;
        }
        return text;
    }
    if (verb == "add") {
        if (separator == std::string::npos || words.size() < 4) throw Error(std::string("Use:\n") + kUsage);
        Schedule schedule;
        int cadence = -1;
        for (int i = 0; i < 4; i++) {
            if (to_lower(words[2]) == kCadences[i]) cadence = i;
        }
        if (cadence < 0) throw Error(std::string("Use:\n") + kUsage);
        if (!endpoint) throw Error("A schedule uses the model that is running now. Start one first.");
        schedule.cadence = static_cast<Schedule::Cadence>(cadence);
        size_t at = 3;
        if (schedule.cadence == Schedule::Cadence::Weekly) {
            const std::string day = to_lower(words[at]);
            int found = -1;
            for (int i = 0; i < 7; i++) {
                if (day.size() >= 3 && starts_with(to_lower(kDays[i]), day)) found = i;
            }
            if (found < 0) throw Error("Name the day for a weekly schedule, such as Monday.");
            schedule.weekday = found;
            at++;
        }
        const std::string time = at < words.size() ? words[at] : "";
        const size_t colon = time.find(':');
        const std::string hours = colon == std::string::npos ? "" : time.substr(0, colon), minutes = colon == std::string::npos ? "" : time.substr(colon + 1);
        auto number = [](const std::string& digits) { return !digits.empty() && digits.size() <= 2 && digits.find_first_not_of("0123456789") == std::string::npos ? std::stoi(digits) : -1; };
        schedule.hour = schedule.cadence == Schedule::Cadence::Hourly ? 0 : number(hours);
        schedule.minute = number(minutes);
        if (colon == std::string::npos || schedule.hour < 0 || schedule.hour > 23 || schedule.minute < 0 || schedule.minute > 59) throw Error("Give the time as HH:MM, or :MM for an hourly schedule.");
        for (size_t i = at + 1; i < words.size(); i++) schedule.automatic = schedule.automatic || to_lower(words[i]) == "auto";
        schedule.name = words[1];
        schedule.workspace = workspace;
        schedule.prompt = prompt;
        schedule.endpoint = *endpoint;
        schedule.created = now;
        all.push_back(schedule);
        save(all, home);
        return "Scheduled " + schedule.name + ": " + to_lower(schedule.summary()) + ", " + (schedule.automatic ? "in Auto (edits and sandboxed commands)" : "read-only") + ", with " + endpoint->model +
               ". It runs while the Mason app is open, or when mason --run-due is run.";
    }
    if (verb == "on" || verb == "off") {
        Schedule& schedule = all[index(1)];
        schedule.enabled = verb == "on";
        // Switching on counts from now, so a long-paused schedule does not fire at once for a time long past.
        if (schedule.enabled) schedule.created = now;
        save(all, home);
        return schedule.name + " is " + verb + ".";
    }
    if (verb == "remove") {
        const size_t at = index(1);
        const std::string removed = all[at].name;
        all.erase(all.begin() + static_cast<std::ptrdiff_t>(at));
        save(all, home);
        return "Removed " + removed + ".";
    }
    throw Error(std::string("Use:\n") + kUsage);
}

}  // namespace schedules

}  // namespace mason
