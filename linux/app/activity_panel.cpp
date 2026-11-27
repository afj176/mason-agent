// Moments: the side panel's feed of what the run did, newest first. A step that made something (a file, a change, a
// command's report, a page check) is a card with Open; reading and other steps are quiet one-line moments, and each
// request begins with a "You asked" line. Opening a change, a report or a set of reads shows it here with its lens:
// a diff for edits, a summary-first terminal for commands, a map for reads, the page for checks, and a receipt for
// the end of a run. Every fact the inline rows show (state, subject, result, skipped repeats) is under Step details or Raw.
// The panel's header (refresh_surface) carries "Moments" or the way back to it, and the shelf.
#include "window.hpp"

#include "scenes.hpp"

#include <algorithm>
#include <array>
#include <map>
#include <set>

namespace mason::app {

namespace {

using scenes::Kind;
using scenes::Scene;

const char* const kEnd = "end";

/// What a moment can open: a file in the Files panel, another panel, a project page, a Settings section, or another
/// moment ("end", "<call id>" or "<call id>|<path>").
struct Opener {
    std::function<void(const std::string&)> file, panel, page, settings, moment;
};

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

std::vector<FileChange> merged_changes(const Session& session) { return scenes::kept_changes(session); }

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

/// `live` is true when the scene belongs to the request that is still running. `only` limits it to one file.
void edit_lens(const Session& session, const std::vector<Activity>& activities, const Scene& scene, bool live, Gtk::Box& body, const Opener& open, const std::string& only = "") {
    const std::vector<FileChange> changes = merged_changes(session);
    std::vector<std::string> paths;
    for (size_t i = scene.first; i <= scene.last(); i++) {
        const auto path = scenes::path_of(activities[i]);
        if (path && (only.empty() || *path == only) && std::find(paths.begin(), paths.end(), *path) == paths.end()) paths.push_back(*path);
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
    for (size_t i = scene.first; i <= scene.last() && only.empty(); i++) {
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
        auto* ship = Gtk::make_managed<Gtk::Button>("Commit & open pull request…");
        ship->add_css_class("suggested-action");
        ship->set_tooltip_text("Settings has the pull request steps: review against a branch, draft from the hunks, publish");
        ship->signal_clicked().connect([open] { Glib::signal_idle().connect_once([open] { open.settings("pull-request"); }); });
        review->set_halign(Gtk::Align::START);
        ship->set_halign(Gtk::Align::START);
        actions->append(*review);
        actions->append(*ship);
        body.append(*actions);
    }
}


// ---- the feed ----

/// The square glyph at the start of a card, tinted by what the moment was: good, accent, bad, search or neutral.
Gtk::Label* badge(const std::string& glyph, const std::string& tone) {
    auto* label = Gtk::make_managed<Gtk::Label>(glyph);
    label->add_css_class("moment-badge");
    label->add_css_class("badge-" + tone);
    label->set_valign(Gtk::Align::CENTER);
    label->set_halign(Gtk::Align::CENTER);
    return label;
}

/// A card's top line: the badge, the title, and any pills after it.
Gtk::Box* moment_title(const std::string& glyph, const std::string& tone, const std::string& title, const std::vector<Gtk::Widget*>& extras) {
    auto* line = row(10);
    line->append(*badge(glyph, tone));
    auto* name = bold(title);
    name->add_css_class("moment-title");
    name->set_ellipsize(Pango::EllipsizeMode::END);
    name->set_tooltip_text(title);
    name->set_valign(Gtk::Align::CENTER);
    line->append(*name);
    for (Gtk::Widget* extra : extras) {
        if (!extra) continue;
        extra->set_valign(Gtk::Align::CENTER);
        line->append(*extra);
    }
    return line;
}

Gtk::Box* moment_card(bool newest) {
    auto* box = column(8);
    box->add_css_class("moment-card");
    if (newest) box->add_css_class("moment-live");
    return box;
}

/// A button that acts after the click is over, since acting rebuilds the panel. Suggested when `primary`.
Gtk::Button* action_button(const std::string& text, bool primary, const std::function<void()>& action) {
    auto* button = Gtk::make_managed<Gtk::Button>(text);
    button->add_css_class("moment-action");
    if (primary) button->add_css_class("suggested-action");
    button->signal_clicked().connect([action] { Glib::signal_idle().connect_once(action); });
    return button;
}

std::string file_name(const std::string& path) {
    const size_t slash = path.find_last_of('/');
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

std::string capitalized(std::string text) {
    if (!text.empty() && text[0] >= 'a' && text[0] <= 'z') text[0] = static_cast<char>(text[0] - 'a' + 'A');
    return text;
}

/// One file an edit scene touched. `live` is true while its request runs: its change is kept only when the request ends.
Gtk::Widget* edit_moment(const std::vector<FileChange>& changes, const Activity& call, const std::string& path, bool live, bool newest, const Opener& open) {
    const auto found = live ? changes.end() : std::find_if(changes.begin(), changes.end(), [&](const FileChange& item) { return scenes::normalize(item.path) == path; });
    const FileChange* change = found == changes.end() ? nullptr : &*found;
    const std::string name = file_name(path);
    const std::string moment = call.id + "|" + path;
    auto* box = moment_card(newest);
    auto* actions = row(8);
    if (!change) {
        const bool failed = scenes::failed(call);
        const bool writing = call.state == "running";
        box->append(*moment_title(failed ? "✕" : "✎", failed ? "bad" : "accent", (failed ? "Couldn't change " : writing ? "Changing " : "Edited ") + name,
                                  {failed ? pill(state_words(call.state), state_tone(call.state)) : writing ? pill("Writing…", "ready") : nullptr}));
        if (!failed && !writing && live) {
            auto* note = dim_label("The change shows here when this request ends.");
            note->add_css_class("caption");
            box->append(*note);
        }
        actions->append(*action_button("Open", newest, [open, moment] { open.moment(moment); }));
    } else if (change->created()) {
        const std::string text = change->opaque() ? "" : replace_all(change->after, "\r\n", "\n");
        const size_t lines = static_cast<size_t>(std::count(text.begin(), text.end(), '\n')) + (text.empty() || text.back() == '\n' ? 0 : 1);
        auto* count = dim_label(change->opaque() ? "binary or large" : lines == 1 ? "1 line" : std::to_string(lines) + " lines");
        count->add_css_class("caption");
        box->append(*moment_title("+", "good", "Created " + name, {count}));
        std::vector<std::string> head;
        for (const std::string& line : split(text, '\n')) {
            if (head.size() == 3) break;
            if (!trim(line).empty()) head.push_back(clip(replace_all(line, "\t", "    "), 120));
        }
        if (!head.empty()) {
            auto* peek = mono(join(head, "\n"), false);
            peek->set_ellipsize(Pango::EllipsizeMode::END);
            peek->add_css_class("moment-peek");
            box->append(*peek);
        }
        const std::string target = change->path;
        actions->append(*action_button("Open", newest, [open, target] { open.file(target); }));
        actions->append(*link_button("Diff", [open, moment] { open.moment(moment); }));
    } else {
        std::vector<Gtk::Widget*> extras;
        if (change->deleted()) extras.push_back(pill("deleted", "blocked"));
        else if (!change->opaque()) {
            const DiffCounts counts = counts_of(*change);
            extras.push_back(pill("+" + std::to_string(counts.added), "done"));
            if (counts.removed > 0) extras.push_back(pill("−" + std::to_string(counts.removed), "danger"));
        }
        box->append(*moment_title("✎", "accent", (change->deleted() ? "Deleted " : "Changed ") + name, extras));
        if (!change->opaque() && !change->deleted()) {
            for (const DiffLine& line : diff_lines(change->before, change->after)) {
                if (line.kind != '+' || trim(line.text).empty()) continue;
                auto* added = mono(clip(trim(line.text), 160), false);
                added->set_ellipsize(Pango::EllipsizeMode::END);
                added->add_css_class("moment-added");
                added->set_halign(Gtk::Align::START);
                box->append(*added);
                break;
            }
        }
        actions->append(*action_button("Open", newest, [open, moment] { open.moment(moment); }));
    }
    box->append(*actions);
    gtk_accessible_update_property(GTK_ACCESSIBLE(box->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ((change && change->created() ? "Created " : "Changed ") + path).c_str(), -1);
    return box;
}

/// A command scene as one card: how it went, the test counts as pills and a bar, and the first failing lines.
Gtk::Widget* command_moment(const std::vector<Activity>& activities, const Scene& scene, bool newest, const Opener& open) {
    const Activity& call = activities[scene.last()];
    const scenes::CommandSummary summary = scenes::command(call);
    const bool failed = scenes::failed(call) || (summary.exit_code && *summary.exit_code != 0) || (summary.failed && *summary.failed > 0);
    const bool active = call.state == "running";
    const std::string title = scenes::label(scene, activities);
    std::vector<Gtk::Widget*> extras;
    if (summary.passed) extras.push_back(pill(std::to_string(*summary.passed) + " passed", "done"));
    if (summary.failed && *summary.failed > 0) extras.push_back(pill(std::to_string(*summary.failed) + " failed", "danger"));
    if (!summary.passed && !summary.failed && summary.exit_code) extras.push_back(pill("exit " + std::to_string(*summary.exit_code), *summary.exit_code == 0 ? "done" : "danger"));
    if (call.state == "skipped") extras.push_back(pill("Repeat skipped", "recheck"));
    if (call.state == "denied") extras.push_back(pill("Declined", "danger"));
    auto* box = moment_card(newest);
    box->append(*moment_title(active ? "…" : failed ? "✕" : "✓", active ? "accent" : failed ? "bad" : "good", title, extras));
    const int good = summary.passed.value_or(0), bad = summary.failed.value_or(0);
    if (summary.passed && good + bad > 0) {
        // Green for what passed over red for what failed.
        auto* bar = Gtk::make_managed<Gtk::ProgressBar>();
        bar->add_css_class("pass-bar");
        bar->set_fraction(static_cast<double>(good) / (good + bad));
        gtk_accessible_update_property(GTK_ACCESSIBLE(bar->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, (std::to_string(good) + " passed, " + std::to_string(bad) + " failed").c_str(), -1);
        box->append(*bar);
    }
    for (size_t i = 0; i < summary.failures.size() && i < 3; i++) {
        auto* line = mono(summary.failures[i], false);
        line->set_ellipsize(Pango::EllipsizeMode::END);
        line->add_css_class("failure-line");
        box->append(*line);
    }
    if (active) {
        auto* waiting = row(8);
        auto* spinner = Gtk::make_managed<Gtk::Spinner>();
        spinner->start();
        waiting->append(*spinner);
        waiting->append(*dim_label("Running…"));
        box->append(*waiting);
    }
    const std::string id = call.id;
    auto* actions = row(8);
    actions->append(*link_button(failed || summary.passed ? "Open the report" : "Open the output", [open, id] { open.moment(id); }));
    box->append(*actions);
    gtk_accessible_update_property(GTK_ACCESSIBLE(box->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, (title + (failed ? ", failed" : "")).c_str(), -1);
    return box;
}

/// A page check: what was checked, what it found, and the page. Linux keeps no screenshot with a check, so there is none here.
Gtk::Widget* page_moment(const std::vector<Activity>& activities, const Scene& scene, bool newest, const Opener& open) {
    std::string page, said;
    for (size_t i = scene.first; i <= scene.last(); i++) {
        const Activity& call = activities[i];
        if ((call.tool == "open_preview" || call.tool == "check_page") && !trim(call.subject).empty()) page = trim(call.subject);
        if (!trim(call.result).empty()) said = trim(call.result);
    }
    const bool failed = scenes::failed(activities[scene.last()]);
    auto* box = moment_card(newest);
    box->append(*moment_title("◫", failed ? "bad" : "search", scenes::label(scene, activities), {failed ? pill(state_words(activities[scene.last()].state), "danger") : nullptr}));
    if (!page.empty()) {
        auto* where = mono(page, false);
        where->set_ellipsize(Pango::EllipsizeMode::MIDDLE);
        box->append(*where);
    }
    if (!said.empty()) {
        auto* found = dim_label(clip(replace_all(said, "\n", " "), 220));
        found->set_wrap(true);
        found->set_lines(3);
        found->set_ellipsize(Pango::EllipsizeMode::END);
        found->set_xalign(0);
        box->append(*found);
    }
    auto* actions = row(8);
    if (!page.empty()) actions->append(*action_button("Open page", newest, [open, page] { open.page(page); }));
    const std::string id = activities[scene.last()].id;
    actions->append(*link_button("Details", [open, id] { open.moment(id); }));
    box->append(*actions);
    return box;
}

/// Reading, searching and other steps: one quiet line that opens the set.
Gtk::Widget* quiet_moment(const std::vector<Activity>& activities, const Scene& scene, const Opener& open) {
    const std::vector<Activity> calls(activities.begin() + static_cast<std::ptrdiff_t>(scene.first), activities.begin() + static_cast<std::ptrdiff_t>(scene.last() + 1));
    const std::string label = capitalized(scenes::label(scene, activities));
    std::string detail;
    if (scene.kind == Kind::Read) {
        std::vector<std::string> names;
        for (const auto& [folder, count] : scenes::folders(calls)) {
            if (names.size() == 3) break;
            names.push_back(folder);
        }
        detail = join(names, ", ");
    }
    const bool failed = std::any_of(calls.begin(), calls.end(), [](const Activity& call) { return scenes::failed(call); });
    auto* line = row(10);
    auto* dot = Gtk::make_managed<Gtk::Box>();
    dot->add_css_class("moment-dot");
    if (failed) dot->add_css_class("moment-dot-failed");
    dot->set_valign(Gtk::Align::CENTER);
    line->append(*dot);
    // The label keeps its words; the folders after it give way when the panel is narrow.
    auto* name = bold(clip(label, 40));
    line->append(*name);
    if (!detail.empty()) {
        auto* more = dim_label("· " + detail);
        more->set_ellipsize(Pango::EllipsizeMode::END);
        more->set_hexpand(true);
        more->set_xalign(0);
        line->append(*more);
    }
    auto* button = Gtk::make_managed<Gtk::Button>();
    button->add_css_class("flat");
    button->add_css_class("quiet-moment");
    button->set_child(*line);
    const std::string id = activities[scene.last()].id;
    button->signal_clicked().connect([open, id] { Glib::signal_idle().connect_once([open, id] { open.moment(id); }); });
    const std::string spoken = label + (detail.empty() ? "" : ", " + detail);
    button->set_tooltip_text(spoken);
    gtk_accessible_update_property(GTK_ACCESSIBLE(button->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, spoken.c_str(), -1);
    return button;
}

Gtk::Widget* asked_moment(const std::string& text) {
    auto* words = dim_label("You asked: " + clip(replace_all(text, "\n", " "), 160));
    words->set_wrap(true);
    words->set_lines(2);
    words->set_ellipsize(Pango::EllipsizeMode::END);
    words->set_xalign(0);
    words->add_css_class("asked-moment");
    return words;
}

/// The cards a scene shows in the feed: one per file for edits, one for a command or page scene, else a quiet line.
void scene_moments(const Session& session, const std::vector<FileChange>& changes, const std::vector<Activity>& activities, const Scene& scene, bool running, bool newest,
                   Gtk::Box& feed, const Opener& open) {
    switch (scene.kind) {
        case Kind::Edit: {
            // Newest first, each file once, as its last call in the scene left it.
            const bool live = running && scene.last() >= session.activities.size();
            std::vector<std::string> seen;
            for (size_t i = scene.last() + 1; i-- > scene.first;) {
                const auto path = scenes::path_of(activities[i]);
                if (!path || std::find(seen.begin(), seen.end(), *path) != seen.end()) continue;
                seen.push_back(*path);
                feed.append(*edit_moment(changes, activities[i], *path, live, newest, open));
            }
            if (seen.empty()) feed.append(*quiet_moment(activities, scene, open));
            break;
        }
        case Kind::Command: feed.append(*command_moment(activities, scene, newest, open)); break;
        case Kind::Page: feed.append(*page_moment(activities, scene, newest, open)); break;
        default: feed.append(*quiet_moment(activities, scene, open)); break;
    }
}

/// The card on top of the feed when a run has ended: how it ended, what was asked, and what it changed.
Gtk::Widget* end_moment(const Session& session, const std::vector<Activity>& activities, bool published, const Opener& open) {
    const RunEnding& ending = *session.ending;
    const std::optional<EndingStyle> look = ending_style(ending.code);
    const size_t start = std::min(scenes::request_start(session), activities.size());
    const std::vector<FileChange> changes = merged_changes(session);
    int added = 0, removed = 0, commands = 0;
    for (size_t i = 0; i < changes.size() && i < 40; i++) {
        const DiffCounts counts = counts_of(changes[i]);
        added += counts.added;
        removed += counts.removed;
    }
    for (size_t i = start; i < activities.size(); i++) {
        if (scenes::kind_of(activities[i].tool) == Kind::Command) commands++;
    }
    auto* box = column(10);
    box->add_css_class("moment-card");
    box->add_css_class("end-moment");
    auto* top = row(10);
    top->append(*pill(look ? look->glyph + " " + look->headline : "Run ended", look ? ending_tone(look->tone) : "blocked"));
    const std::string asked = session.turns.empty() ? "" : trim(session.turns.back().request);
    auto* title = bold(clip(replace_all(asked.empty() ? (look ? look->headline : "Run ended") : asked, "\n", " "), 90));
    title->add_css_class("end-title");
    title->set_ellipsize(Pango::EllipsizeMode::END);
    title->set_hexpand(true);
    title->set_valign(Gtk::Align::CENTER);
    top->append(*title);
    box->append(*top);
    const std::string facts = scenes::end_facts(changes.size(), added, removed, commands, activities.size() - start);
    box->append(*dim_label(facts));
    // A flow box puts the buttons side by side when they fit and one under the other when they do not.
    auto* actions = Gtk::make_managed<Gtk::FlowBox>();
    actions->set_selection_mode(Gtk::SelectionMode::NONE);
    actions->set_column_spacing(8);
    actions->set_row_spacing(8);
    actions->set_max_children_per_line(3);
    actions->set_homogeneous(false);
    actions->add_css_class("viewer-actions");
    auto add = [actions](Gtk::Widget* widget) {
        widget->set_halign(Gtk::Align::START);
        actions->append(*widget);
    };
    if (!changes.empty()) {
        add(action_button("Review all changes", false, [open] { open.panel("changes"); }));
        add(action_button(published ? "Pull request & CI" : "Commit & open pull request…", true, [open] { open.settings("pull-request"); }));
    }
    add(link_button("Summary", [open] { open.moment(kEnd); }));
    box->append(*actions);
    gtk_accessible_update_property(GTK_ACCESSIBLE(box->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ((look ? look->headline : "Run ended") + ". " + facts).c_str(), -1);
    return box;
}

/// Moments before any step: a pose, what the feed will show, and the kinds of moment.
Gtk::Widget* empty_moments(bool running) {
    auto* page = column(14);
    page->add_css_class("moments-empty");
    page->set_valign(Gtk::Align::CENTER);
    page->set_halign(Gtk::Align::CENTER);
    page->set_vexpand(true);
    page->set_margin_top(24);
    page->append(*pose_picture(running ? pose::planning : pose::reading, 132, running ? "planning the first step" : "waiting to read the project"));
    auto* title = Gtk::make_managed<Gtk::Label>(running ? "Working out the first step" : "Moments show up here");
    title->add_css_class("moments-empty-title");
    title->set_wrap(true);
    title->set_justify(Gtk::Justification::CENTER);
    page->append(*title);
    auto* lead = dim_label(running ? "The first thing Mason reads, changes or runs appears here as a moment."
                                   : "Each thing Mason does becomes a moment, newest first. Open one to see the file, change, test report or page it made.");
    lead->set_wrap(true);
    lead->set_justify(Gtk::Justification::CENTER);
    lead->set_xalign(0.5);
    lead->set_max_width_chars(46);
    page->append(*lead);
    auto* key = Gtk::make_managed<Gtk::Grid>();
    key->set_column_spacing(12);
    key->set_row_spacing(8);
    key->set_margin_top(10);
    key->set_halign(Gtk::Align::CENTER);
    const std::vector<std::array<std::string, 3>> rows = {
        {"+", "good", "A new file · opens the file"},
        {"✎", "accent", "A change · opens the lines that changed"},
        {"✓", "good", "Tests and commands · opens the report, failures first"},
        {"◫", "search", "A page Mason checked · opens the page"},
        {"•", "neutral", "Reading and searching · a quiet line"},
    };
    for (size_t i = 0; i < rows.size(); i++) {
        auto* tick = badge(rows[i][0], rows[i][1]);
        tick->add_css_class("key-badge");
        auto* meaning = dim_label(rows[i][2]);
        meaning->set_xalign(0);
        meaning->set_wrap(true);
        key->attach(*tick, 0, static_cast<int>(i));
        key->attach(*meaning, 1, static_cast<int>(i));
    }
    gtk_accessible_update_property(GTK_ACCESSIBLE(key->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "The kinds of moment", -1);
    page->append(*key);
    return page;
}

}  // namespace

void MainWindow::show_activity(const std::string& activity_id) { show_moments(activity_id); }

void MainWindow::show_moments(const std::string& location) {
    panel_ = "moments";
    activity_scene_ = location;
    changes_shown_.clear();
    if (!changes_toggle_.get_active()) changes_toggle_.set_active(true);
    else refresh_changes();
}

void MainWindow::refresh_surface() {
    const Session* session = controller_.session();
    const bool running = controller_.running;
    const bool home = panel_ == "moments" && activity_scene_.empty();
    // What the latest run made, each with the id of the call that made it.
    std::vector<std::pair<scenes::Made, std::string>> made;
    if (session) {
        std::vector<Activity> activities = session->activities;
        if (running) activities.insert(activities.end(), controller_.live_activities.begin(), controller_.live_activities.end());
        for (const scenes::Made& item : scenes::made_so_far(activities, scenes::request_start(*session), scenes::kept_changes(*session))) made.emplace_back(item, activities[item.activity].id);
    }
    std::string print = panel_ + (home ? "|home|" : "|back|") + (running ? "run|" : "idle|") + (session ? session->id : "");
    for (const auto& [item, call] : made) print += "|" + item.label + item.target + call + (item.created ? "+" : "");
    if (print == surface_shown_) return;
    surface_shown_ = print;
    clear(surface_);

    // Left: Moments itself, or the way back to it from anything else.
    if (home) {
        auto* title = Gtk::make_managed<Gtk::Label>("Moments");
        title->add_css_class("surface-title");
        surface_.append(*title);
        if (running) {
            auto* live = pill("● Live", "ready");
            live->set_tooltip_text("Showing each step as it happens");
            surface_.append(*live);
        }
    } else {
        auto* back = Gtk::make_managed<Gtk::Button>("← Moments");
        back->add_css_class("surface-back");
        back->set_valign(Gtk::Align::CENTER);
        back->set_tooltip_text("Back to Moments");
        gtk_accessible_update_property(GTK_ACCESSIBLE(back->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Back to Moments", -1);
        back->signal_clicked().connect([this] { Glib::signal_idle().connect_once([this] { show_moments(""); }); });
        surface_.append(*back);
    }

    // Middle: the shelf. The other panels, the shown one outlined, then what the latest run made.
    auto* shelf = row(6);
    const auto chip = [shelf](const std::string& label, bool selected, const std::string& tip, const std::function<void()>& show, bool made_here) {
        auto* button = Gtk::make_managed<Gtk::Button>();
        // Clipped here rather than ellipsized, so a chip keeps its width and the shelf scrolls instead of squeezing it.
        button->set_child(*Gtk::make_managed<Gtk::Label>(clip(label, 24)));
        button->add_css_class("shelf-chip");
        if (made_here) button->add_css_class("shelf-made");
        if (selected) button->add_css_class("shelf-selected");
        button->set_valign(Gtk::Align::CENTER);
        button->set_tooltip_text(tip);
        gtk_accessible_update_property(GTK_ACCESSIBLE(button->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ((selected ? "Showing " : "Show ") + label).c_str(), -1);
        button->signal_clicked().connect([show] { Glib::signal_idle().connect_once(show); });
        shelf->append(*button);
    };
    const std::vector<std::array<std::string, 3>> panels = {
        {"changes", "Changes", "The files this session changed"},
        {"plan", "Plan", "Milestones: the steps of the work, the files each covers and how each is checked"},
        {"files", "Files", "The project's files, to read"},
        {"website", "Website", "The project's pages: look at one, and pick an element to tell Mason about"},
    };
    for (const auto& panel : panels) {
        const std::string id = panel[0];
        chip(panel[1], panel_ == id, panel[2], [this, id] {
            panel_ = id;
            changes_shown_.clear();
            refresh_changes();
        }, false);
    }
    for (const auto& [item, call] : made) {
        const std::string target = item.target;
        if (item.page) chip(item.label, false, "Open " + target, [this, target] { show_page(target); }, true);
        else if (item.created) chip(item.label, false, "Open " + target + ", which this run created", [this, target] { show_file(target, 1); }, true);
        else {
            const std::string location = call + "|" + scenes::normalize(target);
            chip(item.label, false, "Show what this run changed in " + target, [this, location] { show_moments(location); }, true);
        }
    }
    auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::NEVER);
    scroll->set_propagate_natural_height(true);
    scroll->set_hexpand(true);
    scroll->set_child(*shelf);
    scroll->add_css_class("surface-shelf");
    gtk_accessible_update_property(GTK_ACCESSIBLE(scroll->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Panels, and what this run made", -1);
    surface_.append(*scroll);
}

void MainWindow::refresh_activity() {
    const Session* session = controller_.session();
    const bool running = controller_.running;
    std::vector<Activity> activities;
    if (session) {
        activities = session->activities;
        if (running) activities.insert(activities.end(), controller_.live_activities.begin(), controller_.live_activities.end());
    }
    const bool published = pr_chip_.get_visible();
    std::string fingerprint = "moments|" + (session ? session->id : "") + (running ? "|run|" : "|idle|") + (activity_raw_ ? "raw|" : "") + (published ? "pr|" : "") + activity_scene_ + "|" +
                              (session && session->ending ? session->ending->code : "") + "|";
    for (const Activity& activity : activities) fingerprint += activity.id.substr(0, 8) + activity.state + std::to_string(activity.result.size()) + ",";
    if (session) {
        for (const Turn& turn : session->turns) fingerprint += turn.id.substr(0, 6) + (turn.undone ? "u" : "-") + std::to_string(turn.changes.size()) + std::to_string(turn.request.size());
    }
    if (fingerprint == changes_shown_) return;
    changes_shown_ = fingerprint;
    activity_scroll_watch_.disconnect();
    clear(changes_);
    if (!session) return;
    if (activities.empty()) {
        changes_.append(*empty_moments(running));
        return;
    }

    // Each request starts its own scenes, so its "You asked" line sits between its steps and the previous request's.
    const std::vector<scenes::Asked> asked = scenes::requests(*session, activities.size());
    std::vector<size_t> starts;
    for (const scenes::Asked& item : asked) starts.push_back(item.position);
    const std::vector<Scene> run = scenes::group(activities, starts);
    Opener open;
    open.file = [this](const std::string& path) { show_file(path, 1); };
    open.page = [this](const std::string& page) { show_page(page); };
    open.panel = [this](const std::string& name) {
        panel_ = name;
        changes_shown_.clear();
        refresh_changes();
    };
    open.settings = [this](const std::string& section) {
        adw_view_stack_set_visible_child_name(stack_, "settings");
        settings_.show_section(section);
    };
    open.moment = [this](const std::string& location) { show_moments(location); };

    // What is open, if anything: "end", "<call id>" or "<call id>|<path>" for one file of an edit.
    const bool has_end = !running && session->ending.has_value();
    const Scene* opened = nullptr;
    std::string opened_path;
    bool opened_end = false;
    if (activity_scene_ == kEnd && has_end) opened_end = true;
    else if (!activity_scene_.empty()) {
        const size_t bar = activity_scene_.find('|');
        const std::string id = activity_scene_.substr(0, bar);
        if (bar != std::string::npos) opened_path = activity_scene_.substr(bar + 1);
        const auto at = std::find_if(activities.begin(), activities.end(), [&](const Activity& activity) { return activity.id == id; });
        if (at != activities.end()) {
            const size_t index = static_cast<size_t>(at - activities.begin());
            for (const Scene& scene : run) {
                if (scene.contains(index)) opened = &scene;
            }
        }
    }
    if (!opened && !opened_end && !activity_scene_.empty()) {
        // What was open is gone (a new session, or an undo): back to the feed, header included.
        activity_scene_.clear();
        Glib::signal_idle().connect_once([this] { refresh_surface(); });
    }

    auto* body = column(12);
    body->set_margin_bottom(8);
    body->set_margin_end(4);
    if (opened || opened_end) {
        // An opened moment: its title, how much has happened since, and Raw.
        size_t newer = 0;
        if (opened) {
            for (const Scene& scene : run) {
                if (scene.first > opened->last()) newer++;
            }
        }
        std::string title_text = opened_end ? (ending_style(session->ending->code) ? ending_style(session->ending->code)->headline : "Run ended")
                                 : !opened_path.empty() ? file_name(opened_path) : scenes::label(*opened, activities);
        auto* top = row(8);
        auto* title = bold(title_text);
        title->add_css_class("viewer-where");
        title->set_ellipsize(Pango::EllipsizeMode::END);
        title->set_hexpand(true);
        title->set_tooltip_text(opened_path.empty() ? title_text : opened_path);
        top->append(*title);
        if (newer > 0) {
            auto* fresh = Gtk::make_managed<Gtk::Button>(newer == 1 ? "1 new moment" : std::to_string(newer) + " new moments");
            fresh->add_css_class("pill");
            fresh->add_css_class("viewer-jump");
            fresh->set_tooltip_text("Back to Moments, where the newest is on top");
            fresh->signal_clicked().connect([this] { Glib::signal_idle().connect_once([this] { show_moments(""); }); });
            top->append(*fresh);
        } else if (running) {
            top->append(*pill("● Live", "ready"));
        }
        if (!opened_end) {
            auto* raw = Gtk::make_managed<Gtk::ToggleButton>("Raw");
            raw->add_css_class("flat");
            raw->set_active(activity_raw_);
            raw->set_tooltip_text("Show each call's subject and result exactly as Mason kept them");
            raw->signal_toggled().connect([this, raw] {
                activity_raw_ = raw->get_active();
                changes_shown_.clear();
                Glib::signal_idle().connect_once([this] { refresh_changes(); });
            });
            top->append(*raw);
        }
        body->append(*top);
        if (opened_end) {
            receipt_lens(*session, activities, *body, open);
        } else if (activity_raw_) {
            for (size_t i = opened->first; i <= opened->last(); i++) body->append(*call_card(activities[i], true));
        } else {
            const Scene& scene = *opened;
            switch (scene.kind) {
                case Kind::Edit: edit_lens(*session, activities, scene, running && scene.last() >= session->activities.size(), *body, open, opened_path); break;
                case Kind::Command: command_lens(activities, scene, *body); break;
                case Kind::Read: read_lens(activities, scene, *body); break;
                case Kind::Page: page_lens(activities, scene, *body, open); break;
                case Kind::Other:
                    for (size_t i = scene.first; i <= scene.last(); i++) body->append(*call_card(activities[i], false));
                    break;
            }
            if (scene.kind != Kind::Other) {
                auto* details = column(8);
                for (size_t i = scene.first; i <= scene.last(); i++) details->append(*call_card(activities[i], false));
                auto* expander = Gtk::make_managed<Gtk::Expander>(scene.count == 1 ? "Step details" : "Step details (" + std::to_string(scene.count) + ")");
                expander->set_child(*details);
                body->append(*expander);
            }
        }
    } else {
        // The feed, newest first, with the end of the run on top once it has ended.
        body->set_spacing(10);
        if (has_end) body->append(*end_moment(*session, activities, published, open));
        const std::vector<FileChange> changes = merged_changes(*session);
        size_t shown = 0;
        for (const scenes::FeedEntry& entry : scenes::feed(run, asked)) {
            if (++shown > 120) {
                body->append(*dim_label("Earlier moments are left out. Each request's steps are in the conversation."));
                break;
            }
            if (entry.asked) body->append(*asked_moment(asked[entry.index].text));
            else scene_moments(*session, changes, activities, run[entry.index], running, running && entry.index + 1 == run.size(), *body, open);
        }
    }
    changes_.append(*body);

    // The same view rebuilt (a call finished, a result grew) keeps its place; another view starts at the top.
    const Glib::RefPtr<Gtk::Adjustment> adjustment = changes_scroll_.get_vadjustment();
    if (activity_scene_ == activity_scroll_scene_ && activity_scroll_ > 0) {
        const double restore = activity_scroll_;
        Glib::signal_idle().connect_once([adjustment, restore] { adjustment->set_value(restore); });
    } else {
        activity_scroll_ = 0;
        Glib::signal_idle().connect_once([adjustment] { adjustment->set_value(0); });
    }
    activity_scroll_scene_ = activity_scene_;
    // A plain pointer: the adjustment owns this connection, so holding a reference to it here would keep it alive forever.
    Gtk::Adjustment* watched = adjustment.get();
    activity_scroll_watch_ = adjustment->signal_value_changed().connect([this, watched] { activity_scroll_ = watched->get_value(); });
}

}  // namespace mason::app
