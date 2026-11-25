#include "window.hpp"

namespace mason::app {

namespace {

/// A titled group of rows with a one-line explanation.
AdwPreferencesGroup* group(Gtk::Box& parent, const std::string& title, const std::string& description) {
    GtkWidget* widget = adw_preferences_group_new();
    adw_preferences_group_set_title(ADW_PREFERENCES_GROUP(widget), title.c_str());
    adw_preferences_group_set_description(ADW_PREFERENCES_GROUP(widget), description.c_str());
    gtk_box_append(parent.gobj(), widget);
    return ADW_PREFERENCES_GROUP(widget);
}

/// A row with a title, a subtitle and buttons at its end.
AdwActionRow* row(AdwPreferencesGroup* into, const std::string& title, const std::string& subtitle) {
    GtkWidget* widget = adw_action_row_new();
    adw_preferences_row_set_title(ADW_PREFERENCES_ROW(widget), Glib::Markup::escape_text(title).c_str());
    adw_action_row_set_subtitle(ADW_ACTION_ROW(widget), Glib::Markup::escape_text(subtitle).c_str());
    adw_action_row_set_subtitle_lines(ADW_ACTION_ROW(widget), 3);
    adw_preferences_group_add(into, widget);
    return ADW_ACTION_ROW(widget);
}

Gtk::Button* button(AdwActionRow* on, const std::string& label, bool enabled, std::function<void()> action, bool suggested = false) {
    auto* widget = Gtk::make_managed<Gtk::Button>(label);
    widget->set_valign(Gtk::Align::CENTER);
    widget->set_sensitive(enabled);
    if (suggested) widget->add_css_class("suggested-action");
    widget->signal_clicked().connect(std::move(action));
    adw_action_row_add_suffix(on, GTK_WIDGET(widget->gobj()));
    return widget;
}

void suffix_text(AdwActionRow* on, const std::string& text, const char* css_class = "dim-label") {
    auto* label = Gtk::make_managed<Gtk::Label>(text);
    label->add_css_class(css_class);
    label->set_valign(Gtk::Align::CENTER);
    adw_action_row_add_suffix(on, GTK_WIDGET(label->gobj()));
}

}  // namespace

ModelsPage::ModelsPage(Controller& controller) : controller_(controller) {
    content_.set_margin(24);
    GtkWidget* clamp = adw_clamp_new();
    adw_clamp_set_maximum_size(ADW_CLAMP(clamp), 860);
    adw_clamp_set_child(ADW_CLAMP(clamp), GTK_WIDGET(content_.gobj()));
    gtk_scrolled_window_set_child(gobj(), clamp);
    set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);
    search_.set_placeholder_text("Search Hugging Face, for example qwen coder");
    search_.set_hexpand(true);
    search_.signal_activate().connect([this] { controller_.search(search_.get_text()); });
}

void ModelsPage::refresh() {
    Controller& c = controller_;
    const bool busy = !c.model_busy.empty() || c.download.has_value();
    // Rebuilding on every progress tick would reset scrolling and focus, so the page is rebuilt only when what it lists changes.
    std::string fingerprint = std::to_string(c.servers.size()) + "|" + std::to_string(c.local_models.size()) + "|" + c.runtime_path + "|" + (busy ? "busy" : "idle") + "|" +
                              (c.endpoint ? c.endpoint->url + c.endpoint->model : "") + "|" + (c.server() ? c.server()->model.path : "") + "|" + std::to_string(c.search_results.size()) + "|" +
                              (c.repository ? c.repository->id : "") + "|" + std::to_string(c.suggestions.size()) + "|" + (c.download ? "dl" : "");
    for (const RunningServer& item : c.servers) fingerprint += item.endpoint.url + item.endpoint.model;
    if (fingerprint == shown_) {
        // Only the progress text and bar change while work runs.
        if (status_label_) status_label_->set_text(c.download ? "Downloading " + c.download->title + " · " + c.download->detail : c.model_busy);
        if (progress_ && c.download) progress_->set_fraction(c.download->fraction);
        return;
    }
    shown_ = fingerprint;
    status_label_ = nullptr;
    progress_ = nullptr;
    if (search_.get_parent()) search_.unparent();
    clear(content_);

    if (busy) {
        auto* banner = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
        banner->add_css_class("card");
        auto* line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
        line->set_margin(12);
        auto* spinner = Gtk::make_managed<Gtk::Spinner>();
        spinner->start();
        status_label_ = Gtk::make_managed<Gtk::Label>(c.download ? "Downloading " + c.download->title + " · " + c.download->detail : c.model_busy);
        status_label_->set_xalign(0);
        status_label_->set_hexpand(true);
        status_label_->set_ellipsize(Pango::EllipsizeMode::END);
        auto* cancel = Gtk::make_managed<Gtk::Button>("Cancel");
        cancel->signal_clicked().connect([this] { controller_.cancel_model_work(); });
        line->append(*spinner);
        line->append(*status_label_);
        line->append(*cancel);
        banner->append(*line);
        if (c.download) {
            progress_ = Gtk::make_managed<Gtk::ProgressBar>();
            progress_->set_fraction(c.download->fraction);
            progress_->set_margin_start(12);
            progress_->set_margin_end(12);
            progress_->set_margin_bottom(12);
            banner->append(*progress_);
        }
        content_.append(*banner);
    }

    // ---- running now ----
    AdwPreferencesGroup* running = group(content_, "Running now", "Mason uses any OpenAI-compatible server on this computer: its own, llama.cpp, LM Studio or Ollama.");
    if (c.servers.empty()) row(running, "Nothing is answering", "Start a model below, or start your own server and refresh.");
    for (const RunningServer& item : c.servers) {
        const bool mine = c.server() && c.server()->endpoint == item.endpoint;
        AdwActionRow* line = row(running, mine ? c.server()->model.name : item.endpoint.model, item.name + " · " + item.endpoint.url);
        if (c.endpoint && *c.endpoint == item.endpoint) suffix_text(line, "In use", "success");
        else button(line, "Use", true, [this, item] { controller_.use(item.endpoint, item.endpoint.model); });
        if (mine) button(line, "Stop", !busy, [this] { controller_.stop_server(); });
    }
    auto* refresh_button = Gtk::make_managed<Gtk::Button>("Refresh");
    refresh_button->set_valign(Gtk::Align::CENTER);
    refresh_button->set_sensitive(!busy);
    refresh_button->signal_clicked().connect([this] { controller_.refresh_models(); });
    adw_preferences_group_set_header_suffix(running, GTK_WIDGET(refresh_button->gobj()));

    // ---- runtime ----
    AdwPreferencesGroup* runtime = group(content_, "Runtime", "llama.cpp runs GGUF models. Mason can install the official build for this computer, verified against its published SHA-256.");
    AdwActionRow* runtime_row = row(runtime, c.runtime_path.empty() ? "llama.cpp is not installed" : "llama.cpp is installed", c.runtime_path.empty() ? "Needed to start a model from Mason." : c.runtime_path);
    button(runtime_row, c.runtime_path.empty() ? "Install llama.cpp" : "Update", !busy, [this] { controller_.install_runtime(); }, c.runtime_path.empty());

    // ---- local models ----
    AdwPreferencesGroup* local = group(content_, "On this computer", "Every model file found is listed with what the agent can do with it. " + byte_text(memory_budget_bytes()) + " of " + memory_budget_source() + " is available for a model.");
    if (c.local_models.empty()) row(local, "No model files were found", "Search below, or let Mason suggest one that fits.");
    for (const LocalModel& item : c.local_models) {
        const bool too_large = item.startable() && !memory_fits(item.bytes);
        AdwActionRow* line = row(local, item.name, item.verdict() + "\n" + byte_text(item.bytes) + " · " + item.source + (too_large ? " · larger than the memory available" : ""));
        if (c.server() && c.server()->model.path == item.path) button(line, "Stop", !busy, [this] { controller_.stop_server(); });
        else if (item.startable()) button(line, "Start", !busy, [this, item] { controller_.start_model(item); });
    }

    // ---- find a model ----
    AdwPreferencesGroup* find = group(content_, "Find a model", "Search Hugging Face. Nothing is hidden: each result says what it is for and whether Mason can run it.");
    auto* search_row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* search_button = Gtk::make_managed<Gtk::Button>("Search");
    search_button->set_sensitive(!busy);
    search_button->signal_clicked().connect([this] { controller_.search(search_.get_text()); });
    auto* suggest_button = Gtk::make_managed<Gtk::Button>("Suggest for This Computer");
    suggest_button->set_sensitive(!busy);
    suggest_button->set_tooltip_text("Find coding models whose best build fits this computer's memory");
    suggest_button->signal_clicked().connect([this] { controller_.suggest(); });
    search_row->append(search_);
    search_row->append(*search_button);
    search_row->append(*suggest_button);
    search_row->set_margin_bottom(12);
    adw_preferences_group_add(find, GTK_WIDGET(search_row->gobj()));

    for (const HubSuggestion& pick : c.suggestions) {
        AdwActionRow* line = row(find, pick.repository.id + " · " + pick.build.file,
                                 pick.build.quant() + " is the highest-quality build that fits · " + byte_text(pick.build.bytes) + " · license " + (pick.repository.license.empty() ? "not stated" : pick.repository.license));
        button(line, "Download and Start", !busy, [this, pick] { controller_.fetch(pick.build, pick.repository); }, true);
    }
    if (c.repository) {
        const HubRepository repository = *c.repository;
        const std::optional<HubBuild> best = repository.suggested();
        AdwActionRow* heading = row(find, repository.id, "license " + (repository.license.empty() ? "not stated" : repository.license) + (repository.gated ? " · access terms apply" : ""));
        button(heading, "Back to Results", true, [this] { controller_.repository.reset(); controller_.changed.emit(); });
        if (repository.builds.empty()) row(find, "No single-file GGUF builds", "This repository has nothing Mason can download and run directly.");
        for (const HubBuild& build : repository.builds) {
            const std::string fit = build.is_projector() ? "vision companion file" : memory_fits(build.bytes) ? "fits · about " + byte_text(memory_needed_bytes(build.bytes)) : "needs about " + byte_text(memory_needed_bytes(build.bytes));
            AdwActionRow* line = row(find, build.file, byte_text(build.bytes) + " · " + fit + (best && best->file == build.file ? " · suggested" : ""));
            button(line, "Download", !busy, [this, build, repository] { controller_.fetch(build, repository); });
        }
    } else {
        size_t shown = 0;
        for (const HubModel& result : c.search_results) {
            if (++shown > 40) break;
            AdwActionRow* line = row(find, result.id, result.verdict() + "\n" + std::to_string(result.downloads) + " downloads" + (result.gated ? " · access terms" : ""));
            Gtk::Button* builds = button(line, "Builds", !busy && result.is_gguf(), [this, result] { controller_.inspect(result.id); });
            builds->set_tooltip_text(result.is_gguf() ? "See this repository's builds and which fit" : "Mason runs GGUF builds; look for one of this model");
        }
    }
}

}  // namespace mason::app
