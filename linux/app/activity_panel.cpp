// The Activity panel of the side panel: the run shown as scenes. Consecutive calls of one kind form a scene, and each
// kind has its own lens: a diff for edits, a summary-first terminal for commands, a map for reads, the page for browser
// checks, the plan, and a receipt when the run ends. The panel follows the newest scene until the user picks another.
// Every fact the inline rows show (state, subject, result, skipped repeats) is still under Step details or Raw.
#include "window.hpp"

#include "scenes.hpp"

#include <algorithm>
#include <map>
#include <set>

namespace mason::app {

namespace {

using scenes::Kind;
using scenes::Scene;

const char* const kPlan = "plan";
const char* const kEnd = "end";

/// What a lens can open: a file in the Files panel, another panel, a project page, or Settings.
struct Opener {
    std::function<void(const std::string&)> file, panel, page;
    std::function<void()> settings;
};

std::string key_of(const Scene& scene) { return "s" + std::to_string(scene.first); }

std::string clip(const std::string& text, size_t limit) { return utf8_length(text) > limit ? utf8_prefix(text, limit - 1) + "…" : text; }

/// A small rounded pill in one of the state tones: done (good), recheck (warning), ready (accent), blocked (neutral), danger.
Gtk::Label* pill(const std::string& text, const std::string& tone) {
    auto* label = Gtk::make_managed<Gtk::Label>(text);
    label->add_css_class("state-pill");
    label->add_css_class("state-" + tone);
    label->set_valign(Gtk::Align::CENTER);
    return label;
}

std::string state_tone(const std::string& state) {
    if (state == "completed") return "done";
    if (state == "running") return "ready";
    if (state == "skipped") return "recheck";
    if (state == "failed" || state == "denied") return "danger";
    return "blocked";
}

std::string state_words(const std::string& state) {
    if (state == "denied") return "Declined";
    if (state == "skipped") return "Repeat skipped";
    if (state == "running") return "Running";
    if (state == "completed") return "Done";
    if (state == "failed") return "Failed";
    return state;
}

std::string ending_tone(const std::string& tone) {
    return tone == "success" ? "done" : tone == "warning" ? "recheck" : tone == "accent" ? "ready" : tone == "danger" ? "danger" : "blocked";
}

Gtk::Box* row(int spacing = 6) { return Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, spacing); }
Gtk::Box* column(int spacing = 8) { return Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, spacing); }

Gtk::Box* card() {
    auto* box = column(8);
    box->add_css_class("viewer-card");
    return box;
}

Gtk::Label* mono(const std::string& text, bool wrap = true) {
    auto* label = text_label(text);
    label->add_css_class("monospace");
    label->add_css_class("caption");
    label->set_wrap(wrap);
    return label;
}

Gtk::Label* bold(const std::string& text) {
    auto* label = Gtk::make_managed<Gtk::Label>();
    label->set_markup("<b>" + escape(text) + "</b>");
    label->set_xalign(0);
    return label;
}

Gtk::Button* link_button(const std::string& text, const std::function<void()>& action) {
    auto* button = Gtk::make_managed<Gtk::Button>(text);
    button->add_css_class("flat");
    button->add_css_class("changed-link");
    button->set_valign(Gtk::Align::CENTER);
    // Acting rebuilds the panel, which removes this button, so it happens after the click is over.
    button->signal_clicked().connect([action] { Glib::signal_idle().connect_once(action); });
    return button;
}

/// Each file as the session changed it, from its original to now: requests that were undone are left out.
std::vector<FileChange> merged_changes(const Session& session) {
    std::vector<FileChange> merged;
    for (const Turn& turn : session.turns) {
        if (turn.undone) continue;
        for (const FileChange& change : turn.changes) {
            auto existing = std::find_if(merged.begin(), merged.end(), [&](const FileChange& item) { return item.path == change.path; });
            if (existing != merged.end()) { existing->after = change.after; existing->after_blob = change.after_blob; existing->after_missing = change.after_missing; }
            else merged.push_back(change);
        }
    }
    return merged;
}

DiffCounts counts_of(const FileChange& change) { return change.opaque() ? DiffCounts{} : diff_counts(change.before, change.deleted() ? "" : change.after); }

/// The changed lines with context, tinted, at most `limit` rows.
Gtk::Widget* diff_block(const FileChange& change, size_t limit = 160) {
    auto* lines = column(0);
    lines->add_css_class("code-block");
    size_t count = 0;
    for (const DiffLine& line : visible_rows(diff_lines(change.before, change.deleted() ? "" : change.after))) {
        if (++count > limit) {
            lines->append(*dim_label("More lines below · open Changes to see all of it"));
            break;
        }
        char number[16];
        std::snprintf(number, sizeof number, "%5d ", line.number);
        auto* text = Gtk::make_managed<Gtk::Label>(line.kind == 0 ? "      ⋯" : std::string(number) + line.kind + " " + replace_all(line.text, "\t", "    "));
        text->set_xalign(0);
        text->set_selectable(true);
        text->set_hexpand(true);
        text->add_css_class("monospace");
        text->add_css_class("caption");
        if (line.kind == '+') text->add_css_class("diff-added");
        else if (line.kind == '-') text->add_css_class("diff-removed");
        else if (line.kind == 0) text->add_css_class("dim-label");
        lines->append(*text);
    }
    auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::NEVER);
    scroll->set_child(*lines);
    return scroll;
}

/// One call as the inline rows show it: the tool, its state, what it was about, and what came back.
Gtk::Widget* call_card(const Activity& activity, bool raw) {
    auto* box = card();
    auto* top = row(8);
    auto* name = bold(label_of(activity.tool));
    name->set_hexpand(true);
    top->append(*name);
    top->append(*pill(state_words(activity.state), state_tone(activity.state)));
    box->append(*top);
    if (!activity.subject.empty()) box->append(*mono(raw ? activity.subject : scenes::command_text(activity)));
    std::string result = activity.result;
    if (!raw && utf8_length(result) > 12000) result = utf8_prefix(result, 12000) + "\n[Shown up to 12,000 characters; the session keeps the rest.]";
    auto* value = mono(result.empty() ? "No output." : result);
    value->add_css_class("code-block");
    box->append(*value);
    return box;
}

/// `live` is true when the scene belongs to the request that is still running.
void edit_lens(const Session& session, const std::vector<Activity>& activities, const Scene& scene, bool live, Gtk::Box& body, const Opener& open) {
    const std::vector<FileChange> changes = merged_changes(session);
    std::vector<std::string> paths;
    for (size_t i = scene.first; i <= scene.last(); i++) {
        if (const auto path = scenes::path_of(activities[i]); path && std::find(paths.begin(), paths.end(), *path) == paths.end()) paths.push_back(*path);
    }
    for (const std::string& path : paths) {
        const Activity* last = nullptr;
        const Activity* failure = nullptr;
        bool writing = false;
        for (size_t i = scene.first; i <= scene.last(); i++) {
            if (scenes::path_of(activities[i]) != path) continue;
            last = &activities[i];
            if (scenes::failed(activities[i])) failure = &activities[i];
            writing = writing || activities[i].state == "running";
        }
        const auto change = std::find_if(changes.begin(), changes.end(), [&](const FileChange& item) { return scenes::normalize(item.path) == path; });
        auto* top = row(6);
        auto* name = bold(path);
        name->set_ellipsize(Pango::EllipsizeMode::START);
        name->set_hexpand(true);
        name->set_tooltip_text(path);
        top->append(*name);
        // While the request runs, the kept changes are from earlier requests, so their counts would not be this edit's.
        if (change != changes.end() && !live) {
            if (change->created()) top->append(*pill("new file", "blocked"));
            if (change->deleted()) top->append(*pill("deleted", "blocked"));
            if (!change->opaque()) {
                const DiffCounts counts = counts_of(*change);
                top->append(*pill("+" + std::to_string(counts.added), "done"));
                if (counts.removed > 0) top->append(*pill("−" + std::to_string(counts.removed), "danger"));
            }
        }
        if (failure) top->append(*pill(state_words(failure->state), state_tone(failure->state)));
        else if (writing) top->append(*pill("Writing…", "ready"));
        const std::string target = change != changes.end() ? change->path : path;
        top->append(*link_button("Open file", [open, target] { open.file(target); }));
        body.append(*top);
        if (live) {
            // The request's changes are kept with the session when it ends; until then the call's own words say what changed.
            if (last && !trim(last->result).empty()) body.append(*mono(clip(last->result, 800)));
            auto* note = dim_label("The diff shows here when this request ends.");
            note->add_css_class("caption");
            body.append(*note);
        } else if (change != changes.end() && !change->opaque()) {
            body.append(*diff_block(*change));
            auto* note = dim_label(change->created() ? "The whole file, as this session wrote it." : "Everything this session has changed in the file so far, against its original.");
            note->add_css_class("caption");
            body.append(*note);
        } else if (change != changes.end()) {
            body.append(*dim_label("A binary or large file: there is no text to compare."));
        } else if (last && !trim(last->result).empty()) {
            body.append(*mono(clip(last->result, 800)));
        }
    }
    for (size_t i = scene.first; i <= scene.last(); i++) {
        if (!scenes::path_of(activities[i])) body.append(*call_card(activities[i], false));
    }
    if (!changes.empty()) {
        auto* line = row(6);
        line->append(*link_button("Review all changes", [open] { open.panel("changes"); }));
        body.append(*line);
    }
}

void command_lens(const std::vector<Activity>& activities, const Scene& scene, Gtk::Box& body) {
    const size_t shown = 6;
    if (scene.count > shown) body.append(*dim_label(std::to_string(scene.count - shown) + " earlier commands are under Step details."));
    for (size_t i = scene.first + (scene.count > shown ? scene.count - shown : 0); i <= scene.last(); i++) {
        const Activity& call = activities[i];
        const scenes::CommandSummary summary = scenes::command(call);
        auto* box = card();
        auto* line = mono("$ " + clip(scenes::command_text(call), 160));
        line->add_css_class("code-block");
        box->append(*line);
        auto* pills = row(6);
        if (summary.exit_code) pills->append(*pill("exit " + std::to_string(*summary.exit_code), *summary.exit_code == 0 ? "done" : "danger"));
        else pills->append(*pill(state_words(call.state), state_tone(call.state)));
        if (summary.passed) pills->append(*pill(std::to_string(*summary.passed) + " passed", "done"));
        if (summary.failed && *summary.failed > 0) pills->append(*pill(std::to_string(*summary.failed) + " failed", "danger"));
        if (summary.truncated) pills->append(*pill("output clipped", "recheck"));
        if (summary.exit_code && call.state == "skipped") pills->append(*pill("Repeat skipped", "recheck"));
        box->append(*pills);
        for (const std::string& failure : summary.failures) {
            auto* text = mono(failure);
            text->add_css_class("failure-line");
            box->append(*text);
        }
        const std::string output = trim(summary.output);
        if (!output.empty() && call.state != "running") {
            const size_t lines = static_cast<size_t>(std::count(output.begin(), output.end(), '\n')) + 1;
            const std::string tail = utf8_length(output) > 8000 ? "[earlier output left out · Raw shows all that was kept]\n" + utf8_suffix(output, 8000) : output;
            auto* text = mono(tail, false);
            auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
            scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::AUTOMATIC);
            scroll->set_propagate_natural_height(true);
            scroll->set_max_content_height(360);
            scroll->add_css_class("code-block");
            scroll->set_child(*text);
            auto* expander = Gtk::make_managed<Gtk::Expander>(lines == 1 ? "Output · 1 line" : "Output · " + std::to_string(lines) + " lines");
            expander->set_child(*scroll);
            // Open only when it failed and nothing above says why.
            expander->set_expanded(summary.failures.empty() && summary.exit_code && *summary.exit_code != 0);
            box->append(*expander);
        } else if (call.state == "running") {
            auto* waiting = row(8);
            auto* spinner = Gtk::make_managed<Gtk::Spinner>();
            spinner->start();
            waiting->append(*spinner);
            waiting->append(*dim_label("Running…"));
            box->append(*waiting);
        }
        body.append(*box);
    }
}

void read_lens(const std::vector<Activity>& activities, const Scene& scene, Gtk::Box& body) {
    const std::vector<Activity> calls(activities.begin() + static_cast<std::ptrdiff_t>(scene.first), activities.begin() + static_cast<std::ptrdiff_t>(scene.last() + 1));
    std::set<std::string> files;
    int searches = 0, failed = 0;
    for (const Activity& call : calls) {
        if (scenes::is_search(call.tool)) searches++;
        else if (const auto path = scenes::path_of(call)) files.insert(*path);
        if (scenes::failed(call)) failed++;
    }
    auto* pills = row(6);
    if (!files.empty()) pills->append(*pill(files.size() == 1 ? "1 file" : std::to_string(files.size()) + " files", "blocked"));
    if (searches > 0) pills->append(*pill(searches == 1 ? "1 search" : std::to_string(searches) + " searches", "blocked"));
    if (failed > 0) pills->append(*pill(std::to_string(failed) + " failed", "danger"));
    body.append(*pills);

    const std::vector<std::pair<std::string, int>> folders = scenes::folders(calls);
    if (folders.size() > 1) {
        auto* heading = bold("Where Mason looked");
        body.append(*heading);
        auto* grid = Gtk::make_managed<Gtk::Grid>();
        grid->set_column_spacing(10);
        grid->set_row_spacing(4);
        int most = 1;
        for (const auto& [folder, count] : folders) most = std::max(most, count);
        int at = 0;
        for (const auto& [folder, count] : folders) {
            if (at == 8) break;
            auto* name = mono(folder, false);
            name->set_ellipsize(Pango::EllipsizeMode::MIDDLE);
            name->set_max_width_chars(18);
            name->set_width_chars(18);
            auto* bar = Gtk::make_managed<Gtk::Box>();
            bar->add_css_class("folder-bar");
            bar->set_size_request(std::max(8, 180 * count / most), 8);
            bar->set_halign(Gtk::Align::START);
            bar->set_valign(Gtk::Align::CENTER);
            bar->set_hexpand(true);
            gtk_accessible_update_property(GTK_ACCESSIBLE(bar->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, (folder + ": " + std::to_string(count)).c_str(), -1);
            auto* number = dim_label(std::to_string(count));
            grid->attach(*name, 0, at);
            grid->attach(*bar, 1, at);
            grid->attach(*number, 2, at);
            at++;
        }
        body.append(*grid);
    }

    auto* list = column(6);
    for (const Activity& call : calls) {
        auto* line = row(10);
        auto* verb = dim_label(label_of(call.tool));
        verb->set_wrap(false);
        verb->set_width_chars(13);
        verb->set_max_width_chars(13);
        verb->set_ellipsize(Pango::EllipsizeMode::END);
        verb->set_valign(Gtk::Align::START);
        auto* what = mono(clip(call.subject.empty() ? "(the whole project)" : call.subject, 140));
        what->set_hexpand(true);
        line->append(*verb);
        line->append(*what);
        if (call.state != "completed") line->append(*pill(state_words(call.state), state_tone(call.state)));
        list->append(*line);
        if (scenes::is_search(call.tool) && call.state == "completed") {
            std::vector<std::string> hits;
            for (const std::string& hit : split(replace_all(call.result, "\r\n", "\n"), '\n')) {
                if (!trim(hit).empty()) hits.push_back(hit);
            }
            if (!hits.empty()) {
                const std::vector<std::string> first(hits.begin(), hits.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(5, hits.size())));
                auto* preview = mono(join(first, "\n") + (hits.size() > 5 ? "\n… " + std::to_string(hits.size() - 5) + " more" : ""), false);
                preview->set_ellipsize(Pango::EllipsizeMode::END);
                preview->set_margin_start(24);
                preview->add_css_class("dim-label");
                list->append(*preview);
            }
        }
    }
    body.append(*list);
}

void page_lens(const std::vector<Activity>& activities, const Scene& scene, Gtk::Box& body, const Opener& open) {
    for (size_t i = scene.first; i <= scene.last(); i++) {
        const Activity& call = activities[i];
        auto* box = card();
        auto* top = row(8);
        auto* name = bold(label_of(call.tool));
        name->set_hexpand(true);
        top->append(*name);
        top->append(*pill(state_words(call.state), state_tone(call.state)));
        if ((call.tool == "open_preview" || call.tool == "check_page") && !call.subject.empty()) {
            const std::string page = call.subject;
            top->append(*link_button("Open page", [open, page] { open.page(page); }));
        }
        box->append(*top);
        if (!call.subject.empty()) box->append(*mono(clip(call.subject, 160)));
        const std::string result = trim(call.result);
        if (!result.empty()) box->append(*mono(clip(result, 2400)));
        body.append(*box);
    }
}

void plan_lens(const std::vector<MilestoneStatus>& milestones, Gtk::Box& body, const Opener& open) {
    auto* title = Gtk::make_managed<Gtk::Label>("Plan");
    title->set_xalign(0);
    title->add_css_class("title-3");
    body.append(*title);
    for (const MilestoneStatus& status : milestones) {
        auto* box = card();
        auto* top = row(8);
        auto* mark = Gtk::make_managed<Gtk::Image>();
        mark->set_from_icon_name(status.done() ? "emblem-ok-symbolic" : "media-record-symbolic");
        if (status.done()) mark->add_css_class("success");
        auto* name = text_label(status.item.title);
        name->set_hexpand(true);
        top->append(*mark);
        top->append(*name);
        top->append(*pill(status.state, status.done() ? "done" : status.state == "Ready" ? "ready" : status.state == "Blocked" ? "blocked" : "recheck"));
        box->append(*top);
        if (!status.reason.empty()) box->append(*dim_label(status.reason));
        body.append(*box);
    }
    auto* line = row(6);
    line->append(*link_button("Open the plan", [open] { open.panel("plan"); }));
    body.append(*line);
}

void receipt_lens(const Session& session, const std::vector<Activity>& activities, Gtk::Box& body, const Opener& open) {
    const RunEnding& ending = *session.ending;
    const std::optional<EndingStyle> look = ending_style(ending.code);
    const size_t start = std::min(scenes::request_start(session), activities.size());
    const std::vector<Activity> calls(activities.begin() + static_cast<std::ptrdiff_t>(start), activities.end());

    auto* pills = row(6);
    pills->append(*pill(look ? look->glyph + " " + look->headline : "Run ended", look ? ending_tone(look->tone) : "blocked"));
    pills->append(*pill(calls.size() == 1 ? "1 step" : std::to_string(calls.size()) + " steps", "blocked"));
    body.append(*pills);
    const std::string request = session.turns.empty() ? session.title : trim(session.turns.back().request);
    auto* title = text_label(clip(request, 120));
    title->add_css_class("title-4");
    body.append(*title);

    const std::vector<FileChange> changes = merged_changes(session);
    std::vector<DiffCounts> counts;
    int added = 0, removed = 0;
    for (size_t i = 0; i < changes.size() && i < 40; i++) {
        counts.push_back(counts_of(changes[i]));
        added += counts.back().added;
        removed += counts.back().removed;
    }
    int commands = 0, failed_commands = 0;
    for (const Activity& call : calls) {
        if (scenes::kind_of(call.tool) != Kind::Command) continue;
        commands++;
        if (scenes::command_failed(call)) failed_commands++;
    }
    auto* tiles = Gtk::make_managed<Gtk::Grid>();
    tiles->set_column_homogeneous(true);
    tiles->set_row_spacing(10);
    tiles->add_css_class("viewer-card");
    const std::vector<std::pair<std::string, std::string>> stats = {
        {std::to_string(changes.size()), changes.size() == 1 ? "file changed" : "files changed"},
        {"+" + std::to_string(added) + " −" + std::to_string(removed), "lines"},
        {std::to_string(commands), commands == 1 ? "command run" : "commands run"},
        {std::to_string(failed_commands), failed_commands == 1 ? "command failed" : "commands failed"},
    };
    for (size_t i = 0; i < stats.size(); i++) {
        auto* tile = column(0);
        auto* value = Gtk::make_managed<Gtk::Label>(stats[i].first);
        value->set_xalign(0);
        value->add_css_class("stat-value");
        auto* words = dim_label(stats[i].second);
        words->add_css_class("caption");
        tile->append(*value);
        tile->append(*words);
        // Two by two, so the tiles fit the side panel.
        tiles->attach(*tile, static_cast<int>(i % 2), static_cast<int>(i / 2));
    }
    body.append(*tiles);

    if (!changes.empty()) {
        auto* list = column(4);
        for (size_t i = 0; i < changes.size() && i < 40; i++) {
            const std::string path = changes[i].path;
            auto* line = row(6);
            auto* name = mono(path, false);
            name->set_ellipsize(Pango::EllipsizeMode::START);
            name->set_hexpand(true);
            line->append(*name);
            if (changes[i].created()) line->append(*pill("new", "blocked"));
            if (changes[i].deleted()) line->append(*pill("deleted", "blocked"));
            if (!changes[i].opaque()) {
                line->append(*pill("+" + std::to_string(counts[i].added), "done"));
                if (counts[i].removed > 0) line->append(*pill("−" + std::to_string(counts[i].removed), "danger"));
            }
            if (!changes[i].deleted()) line->append(*link_button("Open", [open, path] { open.file(path); }));
            list->append(*line);
        }
        if (changes.size() > 40) list->append(*dim_label(std::to_string(changes.size() - 40) + " more in Changes"));
        body.append(*list);
    }

    std::vector<std::string> notes;
    if (ending.code == "model_loop" && session.repeat_stop) notes.push_back(session.repeat_stop->explanation());
    else if (ending.code != "done" && !session.detail.empty()) notes.push_back(session.detail);
    if (const std::string hint = ending_hint(ending.code, false); !hint.empty()) notes.push_back(hint);
    const auto skipped = std::count_if(calls.begin(), calls.end(), [](const Activity& call) { return call.state == "skipped"; });
    if (skipped > 0 && ending.code != "model_loop") notes.push_back(skipped == 1 ? "Mason skipped one repeated call." : "Mason skipped " + std::to_string(skipped) + " repeated calls.");
    for (const std::string& note : notes) body.append(*text_label(note));

    if (!changes.empty()) {
        // A flow box puts the buttons side by side when they fit and one under the other when they do not.
        auto* actions = Gtk::make_managed<Gtk::FlowBox>();
        actions->set_selection_mode(Gtk::SelectionMode::NONE);
        actions->set_column_spacing(8);
        actions->set_row_spacing(8);
        actions->set_max_children_per_line(2);
        actions->set_homogeneous(false);
        actions->add_css_class("viewer-actions");
        auto* review = Gtk::make_managed<Gtk::Button>("Review all changes");
        review->signal_clicked().connect([open] { Glib::signal_idle().connect_once([open] { open.panel("changes"); }); });
        auto* ship = Gtk::make_managed<Gtk::Button>("Commit and open a pull request…");
        ship->add_css_class("suggested-action");
        ship->set_tooltip_text("Settings has the pull request steps: review against a branch, draft from the hunks, publish");
        ship->signal_clicked().connect([open] { open.settings(); });
        review->set_halign(Gtk::Align::START);
        ship->set_halign(Gtk::Align::START);
        actions->append(*review);
        actions->append(*ship);
        body.append(*actions);
    }
}

}  // namespace

void MainWindow::show_activity(const std::string& activity_id) {
    panel_ = "activity";
    activity_scene_ = activity_id;
    if (!changes_toggle_.get_active()) changes_toggle_.set_active(true);
    changes_shown_.clear();
    refresh_changes();
}

void MainWindow::refresh_activity() {
    const Session* session = controller_.session();
    const bool running = controller_.running;
    std::vector<Activity> activities;
    if (session) {
        activities = session->activities;
        if (running) activities.insert(activities.end(), controller_.live_activities.begin(), controller_.live_activities.end());
    }
    const std::vector<MilestoneStatus> milestones = session ? controller_.milestone_status() : std::vector<MilestoneStatus>{};
    const std::vector<Scene> run = scenes::group(activities);
    const bool has_plan = !milestones.empty();
    const bool has_end = session && !running && session->ending && !activities.empty();

    std::string fingerprint = "activity|" + (session ? session->id : "") + (running ? "|run|" : "|idle|") + (activity_raw_ ? "raw|" : "") + activity_scene_ + "|" +
                              (session && session->ending ? session->ending->code : "") + "|" + std::to_string(milestones.size()) + "|";
    for (const Activity& activity : activities) fingerprint += activity.id.substr(0, 8) + activity.state + std::to_string(activity.result.size()) + ",";
    if (session) {
        for (const Turn& turn : session->turns) fingerprint += turn.id.substr(0, 6) + (turn.undone ? "u" : "-") + std::to_string(turn.changes.size());
    }
    for (const MilestoneStatus& status : milestones) fingerprint += status.state;
    if (fingerprint == changes_shown_) return;
    changes_shown_ = fingerprint;
    activity_scroll_watch_.disconnect();
    clear(changes_);
    changes_.append(*panel_switch());
    if (!session) return;

    // Which scene is shown: the live one while following, else the one the user picked if it still exists.
    const std::string live = running ? (!run.empty() ? key_of(run.back()) : has_plan ? kPlan : "")
                             : has_end ? kEnd : !run.empty() ? key_of(run.back()) : has_plan ? kPlan : "";
    std::string selected = live;
    bool following = activity_scene_.empty();
    if (!following) {
        const auto at = std::find_if(activities.begin(), activities.end(), [&](const Activity& activity) { return activity.id == activity_scene_; });
        if (activity_scene_ == kEnd && has_end) selected = kEnd;
        else if (activity_scene_ == kPlan && has_plan) selected = kPlan;
        else if (at != activities.end()) {
            const size_t index = static_cast<size_t>(at - activities.begin());
            for (const Scene& scene : run) {
                if (scene.contains(index)) selected = key_of(scene);
            }
        } else {
            activity_scene_.clear();
            following = true;
        }
    }
    if (selected == live) following = true;
    const auto chosen = std::find_if(run.begin(), run.end(), [&](const Scene& scene) { return key_of(scene) == selected; });
    const Scene* scene = chosen == run.end() ? nullptr : &*chosen;

    const auto choose = [this, live, run, activities](const std::string& key) {
        if (key == live) activity_scene_.clear();
        else if (key == kPlan || key == kEnd) activity_scene_ = key;
        else {
            activity_scene_.clear();
            for (const Scene& item : run) {
                if (key_of(item) == key) activity_scene_ = activities[item.first].id;
            }
        }
        changes_shown_.clear();
        Glib::signal_idle().connect_once([this] { refresh_changes(); });
    };
    Opener open;
    open.file = [this](const std::string& path) { show_file(path, 1); };
    open.page = [this](const std::string& page) { show_page(page); };
    open.panel = [this](const std::string& name) {
        panel_ = name;
        changes_shown_.clear();
        refresh_changes();
    };
    open.settings = [this] { adw_view_stack_set_visible_child_name(stack_, "settings"); };

    // ---- header: following state, raw switch, related pages; then where we are ----
    auto* header = row(8);
    if (following) {
        auto* state = pill(running ? "● Following" : selected == kEnd ? "Run ended" : "Latest", running ? "ready" : "blocked");
        state->set_tooltip_text(running ? "Showing each step as it happens" : "Showing the latest step");
        header->append(*state);
    } else {
        auto* jump = Gtk::make_managed<Gtk::Button>("Jump to latest");
        jump->add_css_class("pill");
        jump->add_css_class("viewer-jump");
        jump->set_tooltip_text("Show the latest step and follow the run");
        jump->signal_clicked().connect([choose, live] { choose(live); });
        header->append(*jump);
    }
    auto* gap = Gtk::make_managed<Gtk::Box>();
    gap->set_hexpand(true);
    header->append(*gap);
    auto* raw = Gtk::make_managed<Gtk::ToggleButton>("Raw");
    raw->add_css_class("flat");
    raw->set_active(activity_raw_);
    raw->set_tooltip_text("Show each call's subject and result exactly as Mason kept them");
    raw->signal_toggled().connect([this, raw] {
        activity_raw_ = raw->get_active();
        changes_shown_.clear();
        Glib::signal_idle().connect_once([this] { refresh_changes(); });
    });
    header->append(*raw);
    auto* more = Gtk::make_managed<Gtk::MenuButton>();
    more->set_icon_name("view-more-symbolic");
    more->add_css_class("flat");
    more->set_tooltip_text("More about this run");
    gtk_accessible_update_property(GTK_ACCESSIBLE(more->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "More about this run", -1);
    auto* popover = Gtk::make_managed<Gtk::Popover>();
    auto* items = column(2);
    items->set_margin(6);
    const std::vector<std::pair<std::string, std::function<void()>>> pages = {
        {"Plan", [open] { open.panel("plan"); }},
        {"Changes", [open] { open.panel("changes"); }},
        {"Model roles and background tasks", [open] { open.settings(); }},
    };
    for (const auto& [text, action] : pages) {
        auto* item = Gtk::make_managed<Gtk::Button>(text);
        item->add_css_class("flat");
        if (auto* words = dynamic_cast<Gtk::Label*>(item->get_child())) words->set_xalign(0);
        item->signal_clicked().connect([popover, action] {
            popover->popdown();
            Glib::signal_idle().connect_once(action);
        });
        items->append(*item);
    }
    popover->set_child(*items);
    more->set_popover(*popover);
    header->append(*more);
    changes_.append(*header);

    std::string where = "Nothing has run yet", steps;
    if (selected == kPlan) where = "Plan";
    else if (selected == kEnd) {
        const std::optional<EndingStyle> look = ending_style(session->ending->code);
        where = look ? look->headline : "Run ended";
    } else if (scene) {
        where = scenes::label(*scene, activities);
        steps = scene->count > 1 ? "Steps " + std::to_string(scene->first + 1) + "–" + std::to_string(scene->last() + 1) + " of " + std::to_string(activities.size())
                                 : "Step " + std::to_string(scene->first + 1) + " of " + std::to_string(activities.size());
    }
    auto* title = Gtk::make_managed<Gtk::Label>(where);
    title->set_xalign(0);
    title->set_wrap(true);
    title->set_lines(2);
    title->set_ellipsize(Pango::EllipsizeMode::END);
    title->add_css_class("viewer-where");
    changes_.append(*title);
    if (!steps.empty()) {
        auto* step_line = dim_label(steps);
        step_line->add_css_class("caption");
        changes_.append(*step_line);
    }

    // ---- the lens ----
    auto* body = column(12);
    body->set_margin_top(4);
    body->set_margin_bottom(8);
    body->set_margin_end(4);
    if (selected.empty()) {
        body->append(*dim_label("When Mason reads, edits, runs or checks something, it shows here as it happens."));
    } else if (selected == kPlan) {
        plan_lens(milestones, *body, open);
    } else if (selected == kEnd) {
        receipt_lens(*session, activities, *body, open);
    } else if (scene && activity_raw_) {
        for (size_t i = scene->first; i <= scene->last(); i++) body->append(*call_card(activities[i], true));
    } else if (scene) {
        switch (scene->kind) {
            case Kind::Edit: edit_lens(*session, activities, *scene, running && scene->last() >= session->activities.size(), *body, open); break;
            case Kind::Command: command_lens(activities, *scene, *body); break;
            case Kind::Read: read_lens(activities, *scene, *body); break;
            case Kind::Page: page_lens(activities, *scene, *body, open); break;
            case Kind::Other:
                for (size_t i = scene->first; i <= scene->last(); i++) body->append(*call_card(activities[i], false));
                break;
        }
        if (scene->kind != Kind::Other) {
            auto* details = column(8);
            for (size_t i = scene->first; i <= scene->last(); i++) details->append(*call_card(activities[i], false));
            auto* expander = Gtk::make_managed<Gtk::Expander>(scene->count == 1 ? "Step details" : "Step details (" + std::to_string(scene->count) + ")");
            expander->set_child(*details);
            body->append(*expander);
        }
    }
    auto* lens = Gtk::make_managed<Gtk::ScrolledWindow>();
    lens->set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);
    lens->set_vexpand(true);
    lens->set_min_content_height(160);
    lens->set_child(*body);
    changes_.append(*lens);
    // The same scene rebuilt (a call finished, a result grew) keeps its place; another scene starts at the top.
    const Glib::RefPtr<Gtk::Adjustment> adjustment = lens->get_vadjustment();
    if (selected == activity_scroll_scene_ && activity_scroll_ > 0) {
        const double restore = activity_scroll_;
        Glib::signal_idle().connect_once([adjustment, restore] { adjustment->set_value(restore); });
    } else {
        activity_scroll_ = 0;
    }
    activity_scroll_scene_ = selected;
    // A plain pointer: the adjustment owns this connection, so holding a reference to it here would keep it alive forever.
    Gtk::Adjustment* watched = adjustment.get();
    activity_scroll_watch_ = adjustment->signal_value_changed().connect([this, watched] { activity_scroll_ = watched->get_value(); });

    // ---- the strip: the shape of the run, one tick per scene, tinted by kind and outcome ----
    auto* ticks = row(4);
    ticks->set_margin_bottom(2);
    Gtk::Button* current = nullptr;
    const auto tick = [&](const std::string& key, const std::string& text, const std::string& tone, const std::string& tip) {
        auto* button = Gtk::make_managed<Gtk::Button>(text);
        button->add_css_class("scene-tick");
        button->add_css_class("tick-" + tone);
        const bool shown = key == selected;
        if (shown) { button->add_css_class("tick-selected"); current = button; }
        button->set_tooltip_text(tip);
        gtk_accessible_update_property(GTK_ACCESSIBLE(button->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, (tip + (shown ? ", shown" : "")).c_str(), -1);
        button->signal_clicked().connect([choose, key] { choose(key); });
        ticks->append(*button);
    };
    if (has_plan) tick(kPlan, "plan", "read", "Plan · " + std::to_string(milestones.size()) + (milestones.size() == 1 ? " step" : " steps"));
    const size_t shown_ticks = 150;
    if (run.size() > shown_ticks) ticks->append(*dim_label(std::to_string(run.size() - shown_ticks) + " earlier"));
    for (size_t s = run.size() > shown_ticks ? run.size() - shown_ticks : 0; s < run.size(); s++) {
        const Scene& item = run[s];
        bool failed = false, skipped = false;
        for (size_t i = item.first; i <= item.last(); i++) {
            failed = failed || scenes::failed(activities[i]) || (item.kind == Kind::Command && scenes::command_failed(activities[i]));
            skipped = skipped || activities[i].state == "skipped";
        }
        std::string text = "step", tone = "read";
        switch (item.kind) {
            case Kind::Read: text = item.count > 1 ? "read×" + std::to_string(item.count) : "read"; tone = "read"; break;
            case Kind::Edit: text = "edit"; tone = "edit"; break;
            case Kind::Command: text = "run"; tone = "run"; break;
            case Kind::Page: text = "page"; tone = "page"; break;
            case Kind::Other: break;
        }
        if (failed) tone = "failed";
        else if (skipped) tone = "skipped";
        tick(key_of(item), text, tone, scenes::label(item, activities) + (failed ? " · failed" : skipped ? " · repeat skipped" : ""));
    }
    if (has_end) {
        const std::optional<EndingStyle> look = ending_style(session->ending->code);
        const std::string tone = !look ? "read" : look->tone == "success" ? "run" : look->tone == "warning" ? "skipped" : look->tone == "danger" ? "failed" : look->tone == "accent" ? "edit" : "read";
        tick(kEnd, "end", tone, look ? look->headline : "Run ended");
    }
    if (ticks->get_first_child()) {
        auto* strip = Gtk::make_managed<Gtk::ScrolledWindow>();
        strip->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::NEVER);
        strip->add_css_class("scene-strip");
        strip->set_child(*ticks);
        gtk_accessible_update_property(GTK_ACCESSIBLE(strip->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Steps of this run", -1);
        changes_.append(*strip);
        // Once laid out, the shown tick is scrolled into view. The first frames may come before the strip has its size,
        // so this is checked over a few frames and then left to the user.
        if (current) {
            auto frames = std::make_shared<int>(0);
            strip->add_tick_callback([strip, ticks, current, frames](const Glib::RefPtr<Gdk::FrameClock>&) {
                graphene_rect_t bounds;
                const auto adjustment = strip->get_hadjustment();
                if (adjustment->get_page_size() > 0 && gtk_widget_compute_bounds(GTK_WIDGET(current->gobj()), GTK_WIDGET(ticks->gobj()), &bounds)) {
                    const double right = bounds.origin.x + bounds.size.width + 12;
                    if (right > adjustment->get_value() + adjustment->get_page_size()) adjustment->set_value(right - adjustment->get_page_size());
                    else if (bounds.origin.x < adjustment->get_value()) adjustment->set_value(std::max(0.0, static_cast<double>(bounds.origin.x) - 12));
                }
                return ++*frames < 8;
            });
        }
    }
}

}  // namespace mason::app
