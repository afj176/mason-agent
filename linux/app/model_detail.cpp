// One local model's panel: the context ladder with the memory each step needs, how that fits the memory free
// now, starting it on the GPU or CPU, and what else can be done with the file.
#include <algorithm>
#include <cmath>
#include <regex>

#include "window.hpp"

namespace mason::app {

namespace {

const char* const kToneClasses[] = {"fit-good", "fit-tight", "fit-over", "fit-muted"};

void set_tone(Gtk::Widget& widget, Tone tone) {
    for (const char* name : kToneClasses) widget.remove_css_class(name);
    widget.add_css_class(kToneClasses[static_cast<int>(tone)]);
}

Gtk::Widget* dot() {
    auto* widget = Gtk::make_managed<Gtk::Box>();
    widget->add_css_class("fit-dot");
    widget->set_valign(Gtk::Align::CENTER);
    widget->set_halign(Gtk::Align::CENTER);
    widget->set_size_request(8, 8);
    return widget;
}

Gtk::Label* text(const std::string& value, const char* css = nullptr) {
    auto* label = Gtk::make_managed<Gtk::Label>(value);
    label->set_xalign(0);
    if (css) label->add_css_class(css);
    return label;
}

void accessible_label(Gtk::Widget& widget, const std::string& value) {
    gtk_accessible_update_property(GTK_ACCESSIBLE(widget.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, value.c_str(), -1);
}

Gtk::Widget* spacer() {
    auto* widget = Gtk::make_managed<Gtk::Box>();
    widget->set_hexpand(true);
    return widget;
}

/// The llama.cpp build in a runtime path Mason installed (…/llama.cpp/b12716/…), or empty.
std::string llama_build(const std::string& runtime) {
    static const std::regex tag("b[0-9]+");
    for (const fs::path& part : fs::path(runtime)) {
        if (std::regex_match(part.string(), tag)) return part.string();
    }
    return "";
}

/// A full-width row in the action list: an icon, a label and optional muted text at the end.
Gtk::Button* action(Gtk::Box& list, const char* icon, const std::string& label, Gtk::Label** trailing = nullptr, const std::string& trailing_text = "") {
    auto* button = Gtk::make_managed<Gtk::Button>();
    button->add_css_class("flat");
    button->add_css_class("model-action");
    auto* line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 12);
    auto* image = Gtk::make_managed<Gtk::Image>();
    image->set_from_icon_name(icon);
    image->add_css_class("fit-muted");
    line->append(*image);
    line->append(*text(label));
    line->append(*spacer());
    auto* end = text(trailing_text, "fit-muted");
    end->add_css_class("caption");
    line->append(*end);
    if (trailing) *trailing = end;
    button->set_child(*line);
    accessible_label(*button, label);
    list.append(*button);
    return button;
}

GtkWindow* window_of(Gtk::Widget& widget) {
    GtkRoot* root = gtk_widget_get_root(GTK_WIDGET(widget.gobj()));
    return root && GTK_IS_WINDOW(root) ? GTK_WINDOW(root) : nullptr;
}

}  // namespace

MemoryProfile ModelsPage::detail_profile() const {
    if (!detail_model_) return {};
    const auto found = controller_.profiles.find(detail_model_->path);
    return found != controller_.profiles.end() ? found->second : read_profile(*detail_model_);
}

bool ModelsPage::detail_running() const {
    return detail_model_ && controller_.server() && controller_.server()->model.path == detail_model_->path && controller_.server()->running();
}

void ModelsPage::open_detail(const LocalModel& model) {
    detail_model_ = model;
    detail_chosen_ = false;
    detail_shown_.clear();
    detail_ = Detail{};
    auto* placeholder = Gtk::make_managed<Gtk::Box>();
    detail_page_ = adw_navigation_page_new(GTK_WIDGET(placeholder->gobj()), model.name.c_str());
    adw_navigation_page_set_tag(detail_page_, "model");
    adw_navigation_view_push(navigation_, detail_page_);
    refresh();
}

void ModelsPage::build_detail() {
    if (!detail_model_ || !detail_page_) return;
    Controller& c = controller_;
    const LocalModel model = *detail_model_;
    const MemoryProfile profile = detail_profile();
    detail_ = Detail{};

    auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 20);
    box->set_margin(24);

    // 1. Back to the library.
    auto* back = Gtk::make_managed<Gtk::Button>();
    back->add_css_class("flat");
    back->set_halign(Gtk::Align::START);
    auto* back_line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    auto* back_icon = Gtk::make_managed<Gtk::Image>();
    back_icon->set_from_icon_name("go-previous-symbolic");
    back_line->append(*back_icon);
    back_line->append(*text("Library"));
    back->set_child(*back_line);
    back->signal_clicked().connect([this] { adw_navigation_view_pop(navigation_); });
    box->append(*back);

    // 2–4. Title, chips and status.
    auto* heading = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 10);
    const std::string repository = model.repository();
    const std::string org = repository.empty() ? "" : repository.substr(0, repository.find('/') + 1);
    std::string name = model.name;
    if (!org.empty() && starts_with(name, org)) name = name.substr(org.size());
    auto* title = text("", "model-title");
    title->set_markup((org.empty() ? "" : "<span alpha=\"64%\">" + escape(org) + "</span>") + escape(name));
    title->set_wrap(true);
    title->set_wrap_mode(Pango::WrapMode::WORD_CHAR);
    title->set_selectable(true);
    accessible_label(*title, org + name);
    heading->append(*title);
    auto* chips = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    std::vector<std::string> chip_texts;
    if (const std::string quant = quant_in_name(fs::path(model.path).filename().string()); !quant.empty()) chip_texts.push_back(quant);
    chip_texts.push_back(model.transformers ? "Transformers" : "GGUF");
    chip_texts.push_back(byte_text(model.bytes));
    for (const std::string& chip : chip_texts) chips->append(*text(chip, "model-chip"));
    heading->append(*chips);
    auto* status_line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    detail_.status_dot = dot();
    detail_.status = text("", "fit-status");
    detail_.status->set_wrap(true);
    status_line->append(*detail_.status_dot);
    status_line->append(*detail_.status);
    heading->append(*status_line);
    box->append(*heading);

    // 5. Context length · memory use, with how the estimate works, and the model's maximum.
    auto* fit_section = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 10);
    auto* header = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    header->append(*text("Context length", "detail-heading"));
    header->append(*text("· memory use", "fit-muted"));
    auto* help = Gtk::make_managed<Gtk::MenuButton>();
    help->set_child(*Gtk::make_managed<Gtk::Label>("?"));  // a child of its own shows no dropdown arrow
    help->add_css_class("help-button");
    help->set_valign(Gtk::Align::CENTER);
    help->set_tooltip_text("How the memory estimate works");
    accessible_label(*help, "How the memory estimate works");
    auto* help_popover = Gtk::make_managed<Gtk::Popover>();
    detail_.explanation = text(fit_explanation(profile));
    detail_.explanation->set_wrap(true);
    detail_.explanation->set_max_width_chars(48);
    detail_.explanation->set_margin(8);
    help_popover->set_child(*detail_.explanation);
    help->set_popover(*help_popover);
    header->append(*help);
    header->append(*spacer());
    header->append(*text("max " + group_digits(profile.context_cap()), "fit-muted"));
    fit_section->append(*header);

    // 6. The ladder: one radio group of steps, each with the memory it needs.
    auto* track = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 2);
    track->set_homogeneous(true);
    track->add_css_class("ladder-track");
    accessible_label(*track, "Context length");
    Gtk::ToggleButton* first = nullptr;
    for (int step : ladder_steps(profile)) {
        auto* cell = Gtk::make_managed<Gtk::ToggleButton>();
        cell->add_css_class("ladder-step");
        if (first) cell->set_group(*first);
        else first = cell;
        auto* stack = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 3);
        auto* label = Gtk::make_managed<Gtk::Label>(context_label(step));
        label->add_css_class("step-label");
        auto* memory = Gtk::make_managed<Gtk::Label>("");
        memory->add_css_class("step-memory");
        stack->append(*label);
        stack->append(*memory);
        cell->set_child(*stack);
        cell->signal_toggled().connect([this, cell, step] {
            if (detail_updating_ || !cell->get_active()) return;
            detail_context_ = step;
            detail_chosen_ = true;
            update_detail();
        });
        track->append(*cell);
        detail_.steps.push_back({cell, step});
        detail_.step_memory.push_back(memory);
    }
    // Arrow keys move along the steps, as in any segmented control.
    auto keys = Gtk::EventControllerKey::create();
    keys->signal_key_pressed().connect([this](guint key, guint, Gdk::ModifierType) {
        if (key != GDK_KEY_Left && key != GDK_KEY_Right) return false;
        auto& steps = detail_.steps;
        auto at = std::find_if(steps.begin(), steps.end(), [](const auto& s) { return s.first->get_active(); });
        if (at == steps.end()) return false;
        if (key == GDK_KEY_Left && at != steps.begin()) --at;
        else if (key == GDK_KEY_Right && at + 1 != steps.end()) ++at;
        at->first->set_active(true);
        at->first->grab_focus();
        return true;
    }, false);
    track->add_controller(keys);
    fit_section->append(*track);

    // 7. The fit card.
    auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 6);
    card->add_css_class("fit-card");
    auto* card_top = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    detail_.fit_dot = dot();
    detail_.fit_title = text("", "fit-title");
    detail_.device = text("", "fit-muted");
    detail_.device->add_css_class("caption");
    detail_.device->set_ellipsize(Pango::EllipsizeMode::END);
    card_top->append(*detail_.fit_dot);
    card_top->append(*detail_.fit_title);
    card_top->append(*spacer());
    card_top->append(*detail_.device);
    card->append(*card_top);
    detail_.bar = Gtk::make_managed<Gtk::ProgressBar>();
    detail_.bar->add_css_class("fit-bar");
    accessible_label(*detail_.bar, "Estimated share of free memory");
    card->append(*detail_.bar);
    detail_.fit_detail = text("", "fit-detail");
    detail_.fit_detail->set_wrap(true);
    card->append(*detail_.fit_detail);
    fit_section->append(*card);

    // 8. The suggestion.
    detail_.suggestion_row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    detail_.suggestion = text("", "fit-detail");
    detail_.suggestion->set_wrap(true);
    detail_.suggestion->set_hexpand(true);
    auto* use_suggested = Gtk::make_managed<Gtk::Button>("Use suggested");
    use_suggested->add_css_class("suggest-button");
    use_suggested->set_valign(Gtk::Align::CENTER);
    use_suggested->signal_clicked().connect([this] {
        const Recommendation suggestion = recommend(detail_profile(), memory_);
        detail_context_ = suggestion.context;
        detail_cpu_ = !suggestion.gpu;
        detail_chosen_ = true;
        update_detail();
    });
    detail_.suggestion_row->append(*detail_.suggestion);
    detail_.suggestion_row->append(*use_suggested);
    fit_section->append(*detail_.suggestion_row);
    box->append(*fit_section);

    // 9. GPU or CPU, and start.
    auto* start_row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
    auto* device = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 0);
    device->add_css_class("linked");
    accessible_label(*device, "Compute");
    detail_.gpu = Gtk::make_managed<Gtk::ToggleButton>("GPU");
    detail_.cpu = Gtk::make_managed<Gtk::ToggleButton>("CPU");
    detail_.cpu->set_group(*detail_.gpu);
    for (Gtk::ToggleButton* toggle : {detail_.gpu, detail_.cpu}) {
        toggle->signal_toggled().connect([this, toggle] {
            if (detail_updating_ || !toggle->get_active()) return;
            detail_cpu_ = toggle == detail_.cpu;
            detail_chosen_ = true;
            update_detail();
        });
        device->append(*toggle);
    }
    start_row->append(*device);
    detail_.start = Gtk::make_managed<Gtk::Button>();
    detail_.start->add_css_class("suggested-action");
    detail_.start->add_css_class("start-button");
    detail_.start->set_hexpand(true);
    auto* start_line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    start_line->set_halign(Gtk::Align::CENTER);
    auto* play = Gtk::make_managed<Gtk::Image>();
    play->set_from_icon_name("media-playback-start-symbolic");
    detail_.start_label = Gtk::make_managed<Gtk::Label>("");
    start_line->append(*play);
    start_line->append(*detail_.start_label);
    detail_.start->set_child(*start_line);
    detail_.start->signal_clicked().connect([this] {
        if (!detail_model_) return;
        if (controller_.runtime_path.empty() && !detail_model_->transformers) {
            controller_.install_runtime();
            return;
        }
        const auto selected = std::find_if(detail_.steps.begin(), detail_.steps.end(), [](const auto& s) { return s.first->get_active(); });
        controller_.start_model(*detail_model_, selected != detail_.steps.end() ? selected->second : detail_context_, detail_cpu_);
    });
    start_row->append(*detail_.start);
    box->append(*start_row);

    // 10. What else can be done with it.
    box->append(*Gtk::make_managed<Gtk::Separator>(Gtk::Orientation::HORIZONTAL));
    auto* actions = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    actions->add_css_class("model-actions");
    detail_.use = action(*actions, "utilities-terminal-symbolic", "Use for coding", &detail_.use_note);
    detail_.use->signal_clicked().connect([this] {
        if (const ModelServer* server = controller_.server(); server && detail_running()) controller_.use(server->endpoint, server->model.name);
    });
    detail_.chat = action(*actions, "user-available-symbolic", "Open in chat");
    detail_.chat->signal_clicked().connect([this] {
        if (const ModelServer* server = controller_.server(); server && detail_running()) {
            controller_.use(server->endpoint, server->model.name);
            open_chat.emit();
        }
    });
    action(*actions, "edit-copy-symbolic", "Copy model ID")->signal_clicked().connect([this] {
        if (!detail_model_) return;
        // The name the runtime answers to while it runs; the file's own name otherwise.
        const std::string id = detail_running() ? controller_.server()->endpoint.model : detail_model_->name;
        get_clipboard()->set_text(id);
        controller_.notice.emit("Copied " + id);
    });
    action(*actions, "folder-open-symbolic", "Show in folder")->signal_clicked().connect([this] {
        if (!detail_model_) return;
        GFile* file = g_file_new_for_path(detail_model_->path.c_str());
        GtkFileLauncher* launcher = gtk_file_launcher_new(file);
        g_object_unref(file);
        gtk_file_launcher_open_containing_folder(launcher, window_of(*this), nullptr, +[](GObject* source, GAsyncResult* result, gpointer) {
            GError* error = nullptr;
            gtk_file_launcher_open_containing_folder_finish(GTK_FILE_LAUNCHER(source), result, &error);
            if (error) g_error_free(error);
            g_object_unref(source);
        }, nullptr);
    });
    if (!repository.empty()) {
        action(*actions, "web-browser-symbolic", "View on Hugging Face")->signal_clicked().connect([this, repository] {
            GtkUriLauncher* launcher = gtk_uri_launcher_new(("https://huggingface.co/" + repository).c_str());
            gtk_uri_launcher_launch(launcher, window_of(*this), nullptr, +[](GObject* source, GAsyncResult* result, gpointer) {
                GError* error = nullptr;
                gtk_uri_launcher_launch_finish(GTK_URI_LAUNCHER(source), result, &error);
                if (error) g_error_free(error);
                g_object_unref(source);
            }, nullptr);
        });
    }
    detail_.trash = action(*actions, "user-trash-symbolic", "Delete", nullptr, byte_text(model.bytes));
    detail_.trash->signal_clicked().connect([this] { confirm_trash(); });
    box->append(*actions);

    // 11. Footer: architecture and runtime.
    auto* footer = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    footer->add_css_class("model-footer");
    footer->set_vexpand(true);
    footer->set_valign(Gtk::Align::END);
    const std::string architecture = profile.architecture.empty() ? (model.transformers ? "Transformers" : "GGUF") : profile.architecture;
    const std::string build = llama_build(c.runtime_path);
    const std::string runtime = model.transformers ? "Transformers through Python" : c.runtime_path.empty() ? "llama.cpp not installed" : "llama.cpp" + (build.empty() ? "" : " " + build);
    auto* facts = text(architecture + " · " + runtime);
    facts->set_ellipsize(Pango::EllipsizeMode::END);
    facts->set_hexpand(true);
    footer->append(*facts);
    auto* runtime_button = Gtk::make_managed<Gtk::MenuButton>();
    runtime_button->set_child(*Gtk::make_managed<Gtk::Label>("Runtime"));
    runtime_button->add_css_class("footer-button");
    auto* runtime_popover = Gtk::make_managed<Gtk::Popover>();
    auto* runtime_box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    runtime_box->set_margin(8);
    auto* runtime_text = text(c.runtime_path.empty() ? "llama.cpp is not installed. Mason installs the official build for this computer, verified against its published SHA-256." : "llama.cpp · " + c.runtime_path);
    runtime_text->set_wrap(true);
    runtime_text->set_max_width_chars(48);
    runtime_box->append(*runtime_text);
    auto* install = Gtk::make_managed<Gtk::Button>(c.runtime_path.empty() ? "Install llama.cpp" : "Update llama.cpp");
    install->set_sensitive(c.model_busy.empty() && !c.download);
    install->signal_clicked().connect([this, runtime_popover] {
        runtime_popover->popdown();
        controller_.install_runtime();
    });
    runtime_box->append(*install);
    runtime_popover->set_child(*runtime_box);
    runtime_button->set_popover(*runtime_popover);
    footer->append(*runtime_button);
    box->append(*footer);

    GtkWidget* clamp = adw_clamp_new();
    adw_clamp_set_maximum_size(ADW_CLAMP(clamp), 640);
    adw_clamp_set_child(ADW_CLAMP(clamp), GTK_WIDGET(box->gobj()));
    GtkWidget* scroll = gtk_scrolled_window_new();
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), clamp);
    adw_navigation_page_set_child(detail_page_, scroll);
}

void ModelsPage::update_detail() {
    if (!detail_model_ || !detail_.start) return;
    Controller& c = controller_;
    const LocalModel& model = *detail_model_;
    const MemoryProfile profile = detail_profile();
    const bool running = detail_running();
    const bool starting = c.starting_path == model.path && !c.model_busy.empty();
    const bool busy = !c.model_busy.empty() || c.download.has_value();
    const Recommendation suggestion = recommend(profile, memory_);
    detail_updating_ = true;

    if (running && !detail_chosen_) {
        detail_context_ = c.server()->context;
        detail_cpu_ = c.server()->cpu_only;
    } else if (!detail_chosen_) {
        detail_context_ = suggestion.context;
        detail_cpu_ = !suggestion.gpu;
    }
    // The step nearest the chosen context is the selected one.
    size_t selected = 0;
    for (size_t i = 0; i < detail_.steps.size(); i++) {
        if (std::abs(detail_.steps[i].second - detail_context_) < std::abs(detail_.steps[selected].second - detail_context_)) selected = i;
    }
    const bool reading = memory_.usable();
    for (size_t i = 0; i < detail_.steps.size(); i++) {
        const auto& [cell, tokens] = detail_.steps[i];
        const FitAssessment fit = assess(profile, memory_, tokens);
        std::string memory = "— GiB", range = "memory not estimated";
        if (reading && fit.estimated) {
            memory = gib_text(fit.low_gib) + " GiB";
            range = gib_text(fit.low_gib) + "–" + gib_text(fit.high_gib) + " GiB";
        } else if (reading && fit.weight_gib > 0) {
            memory = range = gib_text(fit.weight_gib) + "+ GiB";
        }
        detail_.step_memory[i]->set_text(memory);
        set_tone(*detail_.step_memory[i], detail_cpu_ ? Tone::Muted : tone_of(fit.fit));
        accessible_label(*cell, group_digits(tokens) + " context tokens, " + range + ", " + fit.label());
        cell->set_active(i == selected);
    }
    const int tokens = detail_.steps.empty() ? detail_context_ : detail_.steps[selected].second;
    const FitAssessment fit = assess(profile, memory_, tokens);

    // The fit card.
    detail_.device->set_text(memory_.label());
    if (running) {
        set_tone(*detail_.fit_dot, Tone::Good);
        detail_.fit_title->set_text("Running · memory is already in use");
        detail_.fit_detail->set_text("The free memory reading already includes this model. Stop it before estimating a new load.");
        detail_.bar->set_fraction(0);
        set_tone(*detail_.bar, Tone::Muted);
    } else if (detail_cpu_) {
        set_tone(*detail_.fit_dot, Tone::Muted);
        detail_.fit_title->set_text("CPU · system RAM");
        detail_.fit_detail->set_text(fit_detail(fit, profile, true));
        detail_.bar->set_fraction(0);
        set_tone(*detail_.bar, Tone::Muted);
    } else {
        const Tone tone = tone_of(fit.fit);
        set_tone(*detail_.fit_dot, tone);
        detail_.fit_title->set_text(fit.label());
        detail_.fit_detail->set_text(fit_detail(fit, profile, false));
        const double need = fit.estimated ? fit.high_gib : fit.weight_gib;
        detail_.bar->set_fraction(fit.free_gib > 0 ? std::min(1.0, need / fit.free_gib) : 0);
        set_tone(*detail_.bar, tone);
    }
    detail_.suggestion->set_text(suggestion.text);
    detail_.suggestion_row->set_visible(!running);
    detail_.gpu->set_active(!detail_cpu_);
    detail_.cpu->set_active(detail_cpu_);

    // Start, or set up the runtime first.
    const bool needs_runtime = c.runtime_path.empty() && !model.transformers;
    detail_.start_label->set_text(needs_runtime ? "Set up runtime" : "Start & connect · " + context_label(tokens));
    detail_.start->set_sensitive(needs_runtime ? !busy : model.startable() && !running && !starting && !busy);

    // Status.
    if (running) {
        set_tone(*detail_.status_dot, Tone::Good);
        detail_.status->set_text("Running · " + c.server()->endpoint.url);
    } else if (starting) {
        set_tone(*detail_.status_dot, Tone::Tight);
        detail_.status->set_text("Starting · " + c.starting_stage);
    } else if (!model.startable()) {
        set_tone(*detail_.status_dot, Tone::Muted);
        detail_.status->set_text(model.verdict());
    } else if (needs_runtime) {
        set_tone(*detail_.status_dot, Tone::Muted);
        detail_.status->set_text("Needs llama.cpp · " + model.role_label());
    } else {
        set_tone(*detail_.status_dot, Tone::Good);
        detail_.status->set_text("Ready to start · " + model.role_label());
    }

    const bool in_use = running && c.endpoint && *c.endpoint == c.server()->endpoint;
    detail_.use->set_sensitive(running);
    detail_.chat->set_sensitive(running);
    detail_.use_note->set_text(!running ? "after start" : in_use ? "in use" : "");
    detail_.trash->set_sensitive(!running && !starting && !busy);
    detail_.explanation->set_text(fit_explanation(profile));
    detail_updating_ = false;
}

void ModelsPage::confirm_trash() {
    if (!detail_model_) return;
    const LocalModel model = *detail_model_;
    const std::string heading = "Move " + model.name + " to the Trash?";
    const std::string body = byte_text(model.bytes) + " will be freed. Mason stops listing it.";
    struct Request {
        ModelsPage* page;
        LocalModel model;
        std::shared_ptr<bool> alive;
    };
    auto* request = new Request{this, model, alive_};
    auto respond = +[](GObject*, const char* response, gpointer data) {
        auto* request = static_cast<Request*>(data);
        if (!*request->alive || std::string(response) != "trash") return;
        ModelsPage* page = request->page;
        if (page->detail_page_) adw_navigation_view_pop(page->navigation_);
        page->controller_.trash_model(request->model);
    };
    auto release = +[](gpointer data, GClosure*) { delete static_cast<Request*>(data); };
#if ADW_CHECK_VERSION(1, 5, 0)
    AdwDialog* dialog = adw_alert_dialog_new(heading.c_str(), body.c_str());
    adw_alert_dialog_add_responses(ADW_ALERT_DIALOG(dialog), "trash", "Move to Trash", "cancel", "Cancel", nullptr);
    adw_alert_dialog_set_response_appearance(ADW_ALERT_DIALOG(dialog), "trash", ADW_RESPONSE_DESTRUCTIVE);
    adw_alert_dialog_set_default_response(ADW_ALERT_DIALOG(dialog), "cancel");
    adw_alert_dialog_set_close_response(ADW_ALERT_DIALOG(dialog), "cancel");
    g_signal_connect_data(dialog, "response", G_CALLBACK(respond), request, release, GConnectFlags(0));
    adw_dialog_present(dialog, GTK_WIDGET(gobj()));
#else
    GtkWidget* dialog = adw_message_dialog_new(window_of(*this), heading.c_str(), body.c_str());
    adw_message_dialog_add_responses(ADW_MESSAGE_DIALOG(dialog), "trash", "Move to Trash", "cancel", "Cancel", nullptr);
    adw_message_dialog_set_response_appearance(ADW_MESSAGE_DIALOG(dialog), "trash", ADW_RESPONSE_DESTRUCTIVE);
    adw_message_dialog_set_default_response(ADW_MESSAGE_DIALOG(dialog), "cancel");
    adw_message_dialog_set_close_response(ADW_MESSAGE_DIALOG(dialog), "cancel");
    g_signal_connect_data(dialog, "response", G_CALLBACK(respond), request, release, GConnectFlags(0));
    gtk_window_present(GTK_WINDOW(dialog));
#endif
}

}  // namespace mason::app
