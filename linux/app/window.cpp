#include "window.hpp"

#include <algorithm>

namespace mason::app {

std::string escape(const std::string& text) { return Glib::Markup::escape_text(text); }

std::string inline_markup(const std::string& text) {
    std::string out;
    size_t at = 0;
    while (at < text.size()) {
        if (text[at] == '`') {
            const size_t end = text.find('`', at + 1);
            if (end != std::string::npos) {
                out += "<tt>" + escape(text.substr(at + 1, end - at - 1)) + "</tt>";
                at = end + 1;
                continue;
            }
        }
        if (text.compare(at, 2, "**") == 0) {
            const size_t end = text.find("**", at + 2);
            if (end != std::string::npos) {
                out += "<b>" + escape(text.substr(at + 2, end - at - 2)) + "</b>";
                at = end + 2;
                continue;
            }
        }
        const size_t next = text.find_first_of("`*", at + 1);
        out += escape(text.substr(at, next == std::string::npos ? std::string::npos : next - at));
        at = next == std::string::npos ? text.size() : next;
    }
    return out;
}

void clear(Gtk::Box& box) {
    while (Gtk::Widget* child = box.get_first_child()) box.remove(*child);
}

Gtk::Label* dim_label(const std::string& text) {
    auto* label = Gtk::make_managed<Gtk::Label>(text);
    label->set_xalign(0);
    label->set_wrap(true);
    label->add_css_class("dim-label");
    return label;
}

namespace {

const char* const kStyle = R"css(
.request-bubble { background: alpha(@accent_bg_color, 0.16); border-radius: 12px; padding: 10px 14px; }
.code-block { background: alpha(@view_fg_color, 0.06); border-radius: 8px; padding: 10px; }
.approval-card { background: alpha(@warning_bg_color, 0.14); border: 1px solid alpha(@warning_bg_color, 0.6); border-radius: 12px; padding: 14px; }
.composer { background: alpha(@view_fg_color, 0.05); border-radius: 10px; padding: 6px; }
.composer textview, .composer textview text { background: transparent; }
.diff-added { color: @success_color; background: alpha(@success_bg_color, 0.12); }
.diff-removed { color: @error_color; background: alpha(@error_bg_color, 0.12); }
.project-row { font-weight: 600; }
)css";

Gtk::Label* text_label(const std::string& text, bool selectable = true) {
    auto* label = Gtk::make_managed<Gtk::Label>(text);
    label->set_xalign(0);
    label->set_wrap(true);
    label->set_wrap_mode(Pango::WrapMode::WORD_CHAR);
    label->set_selectable(selectable);
    return label;
}

std::string label_of(const std::string& tool) { return replace_all(tool, "_", " "); }

/// Changed lines with two lines of context; a row with kind 0 marks a gap.
std::vector<DiffLine> visible_rows(const std::vector<DiffLine>& rows) {
    std::vector<bool> keep(rows.size(), false);
    for (size_t i = 0; i < rows.size(); i++) {
        if (rows[i].kind == ' ') continue;
        for (size_t near = i >= 2 ? i - 2 : 0; near <= std::min(rows.size() - 1, i + 2); near++) keep[near] = true;
    }
    std::vector<DiffLine> out;
    bool gap = false;
    for (size_t i = 0; i < rows.size() && out.size() < 600; i++) {
        if (!keep[i]) { gap = true; continue; }
        if (gap && !out.empty()) out.push_back({0, "", 0});
        gap = false;
        out.push_back(rows[i]);
    }
    return out;
}

}  // namespace

MainWindow::MainWindow(Gtk::Application& application, Controller& controller) : controller_(controller), models_(controller), chat_(controller) {
    auto provider = Gtk::CssProvider::create();
    provider->load_from_data(kStyle);
    Gtk::StyleContext::add_provider_for_display(Gdk::Display::get_default(), provider, GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);

    window_ = ADW_APPLICATION_WINDOW(adw_application_window_new(application.gobj()));
    gtk_window_set_title(GTK_WINDOW(window_), "Mason Agent");
    gtk_window_set_default_size(GTK_WINDOW(window_), 1180, 760);
    gtk_window_set_icon_name(GTK_WINDOW(window_), "site.masonagent.MasonAgent");

    // ---- sidebar ----
    sidebar_.set_selection_mode(Gtk::SelectionMode::SINGLE);
    sidebar_.add_css_class("navigation-sidebar");
    sidebar_.signal_row_selected().connect([this](Gtk::ListBoxRow* row) {
        if (updating_ || !row) return;
        const std::string id = row->get_name();
        if (!id.empty() && id[0] != '/') controller_.select(id);
    });
    auto* sidebar_scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    sidebar_scroll->set_child(sidebar_);
    sidebar_scroll->set_vexpand(true);
    auto* open_button = Gtk::make_managed<Gtk::Button>();
    open_button->set_icon_name("folder-new-symbolic");
    open_button->set_tooltip_text("Open a project folder");
    open_button->signal_clicked().connect([this] { open_project(); });
    GtkWidget* sidebar_header = adw_header_bar_new();
    adw_header_bar_pack_start(ADW_HEADER_BAR(sidebar_header), GTK_WIDGET(open_button->gobj()));
    GtkWidget* sidebar_view = adw_toolbar_view_new();
    adw_toolbar_view_add_top_bar(ADW_TOOLBAR_VIEW(sidebar_view), sidebar_header);
    adw_toolbar_view_set_content(ADW_TOOLBAR_VIEW(sidebar_view), GTK_WIDGET(sidebar_scroll->gobj()));
    AdwNavigationPage* sidebar_page = adw_navigation_page_new(sidebar_view, "Mason Agent");

    // ---- session page ----
    transcript_.set_margin(20);
    transcript_.set_valign(Gtk::Align::START);
    GtkWidget* clamp = adw_clamp_new();
    adw_clamp_set_maximum_size(ADW_CLAMP(clamp), 860);
    adw_clamp_set_child(ADW_CLAMP(clamp), GTK_WIDGET(transcript_.gobj()));
    gtk_scrolled_window_set_child(transcript_scroll_.gobj(), clamp);
    transcript_scroll_.set_vexpand(true);
    transcript_scroll_.set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);

    draft_.set_wrap_mode(Gtk::WrapMode::WORD_CHAR);
    draft_.set_accepts_tab(false);
    draft_.set_top_margin(6);
    draft_.set_bottom_margin(6);
    draft_.set_left_margin(8);
    draft_.set_right_margin(8);
    draft_.set_tooltip_text("Ask about this project or describe a change. Ctrl+Enter sends.");
    gtk_accessible_update_property(GTK_ACCESSIBLE(draft_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Request", -1);
    auto keys = Gtk::EventControllerKey::create();
    keys->signal_key_pressed().connect([this](guint key, guint, Gdk::ModifierType state) {
        if ((key == GDK_KEY_Return || key == GDK_KEY_KP_Enter) && (state & Gdk::ModifierType::CONTROL_MASK) == Gdk::ModifierType::CONTROL_MASK) {
            if (controller_.running) queue_.activate();
            else send_.activate();
            return true;
        }
        return false;
    }, false);
    draft_.add_controller(keys);
    draft_.get_buffer()->signal_changed().connect([this] { refresh_composer(); });
    auto* draft_scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    draft_scroll->set_child(draft_);
    draft_scroll->set_min_content_height(54);
    draft_scroll->set_max_content_height(150);
    draft_scroll->set_propagate_natural_height(true);
    draft_scroll->add_css_class("composer");

    permission_.set_model(Gtk::StringList::create({"Read-only", "Review edits", "Workspace edits", "Auto (sandboxed)"}));
    permission_.set_tooltip_text("What Mason may do without asking. Auto runs commands in a sandbox limited to this project, with network access off.");
    permission_.property_selected().signal_changed().connect([this] {
        if (updating_) return;
        const Permission choices[] = {Permission::ReadOnly, Permission::ReviewEdits, Permission::WorkspaceEdits, Permission::Auto};
        controller_.set_permission(choices[std::min<guint>(permission_.get_selected(), 3)]);
    });
    send_.add_css_class("suggested-action");
    send_.signal_clicked().connect([this] {
        const std::string text = draft_.get_buffer()->get_text();
        if (trim(text).empty() || controller_.running) return;
        draft_.get_buffer()->set_text("");
        controller_.send(text);
    });
    stop_.add_css_class("destructive-action");
    stop_.signal_clicked().connect([this] { controller_.stop(); });
    // While a request runs, the request box sends follow-ups: queued for afterwards, or steering it now.
    auto follow_up = [this](bool steer) {
        const std::string text = draft_.get_buffer()->get_text();
        if (trim(text).empty() || !controller_.running) return;
        draft_.get_buffer()->set_text("");
        controller_.follow_up(text, steer);
    };
    queue_.set_tooltip_text("Handle this message after the current request completes (Ctrl+Enter)");
    queue_.signal_clicked().connect([follow_up] { follow_up(false); });
    steer_.set_tooltip_text("Interrupt what Mason is doing and handle this message next");
    steer_.signal_clicked().connect([follow_up] { follow_up(true); });
    run_queued_.set_tooltip_text("Run the messages that were waiting when the last request stopped");
    run_queued_.signal_clicked().connect([this] { controller_.run_queued(); });
    waiting_.add_css_class("dim-label");
    waiting_.add_css_class("caption");
    auto* controls = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* spacer = Gtk::make_managed<Gtk::Box>();
    spacer->set_hexpand(true);
    controls->append(permission_);
    controls->append(*spacer);
    controls->append(waiting_);
    controls->append(steer_);
    controls->append(queue_);
    controls->append(run_queued_);
    controls->append(stop_);
    controls->append(send_);
    auto* composer = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    composer->set_margin(14);
    composer->append(*draft_scroll);
    composer->append(*controls);

    auto* conversation = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    conversation->append(transcript_scroll_);
    conversation->append(approval_slot_);
    conversation->append(*composer);
    conversation->set_size_request(420, -1);

    changes_.set_margin(12);
    changes_scroll_.set_child(changes_);
    changes_scroll_.set_size_request(340, -1);
    changes_scroll_.set_visible(false);
    auto* paned = Gtk::make_managed<Gtk::Paned>(Gtk::Orientation::HORIZONTAL);
    paned->set_start_child(*conversation);
    paned->set_end_child(changes_scroll_);
    paned->set_resize_start_child(true);
    paned->set_shrink_start_child(false);
    paned->set_shrink_end_child(false);

    GtkWidget* empty = adw_status_page_new();
    adw_status_page_set_icon_name(ADW_STATUS_PAGE(empty), "site.masonagent.MasonAgent");
    adw_status_page_set_title(ADW_STATUS_PAGE(empty), "Make the next move clear");
    adw_status_page_set_description(ADW_STATUS_PAGE(empty), "Open a project folder and ask Mason to read, explain or change it. Sessions start by asking before each edit and command.");
    auto* open_first = Gtk::make_managed<Gtk::Button>("Open Project…");
    open_first->add_css_class("suggested-action");
    open_first->add_css_class("pill");
    open_first->set_halign(Gtk::Align::CENTER);
    open_first->signal_clicked().connect([this] { open_project(); });
    adw_status_page_set_child(ADW_STATUS_PAGE(empty), GTK_WIDGET(open_first->gobj()));
    session_stack_.add(*paned, "session");
    gtk_stack_add_named(session_stack_.gobj(), empty, "empty");

    // ---- content: Code and Models ----
    stack_ = ADW_VIEW_STACK(adw_view_stack_new());
    adw_view_stack_add_titled_with_icon(stack_, GTK_WIDGET(session_stack_.gobj()), "code", "Code", "utilities-terminal-symbolic");
    adw_view_stack_add_titled_with_icon(stack_, GTK_WIDGET(chat_.gobj()), "chat", "Chat", "user-available-symbolic");
    adw_view_stack_add_titled_with_icon(stack_, GTK_WIDGET(models_.gobj()), "models", "Models", "applications-science-symbolic");
    GtkWidget* switcher = adw_view_switcher_new();
    adw_view_switcher_set_stack(ADW_VIEW_SWITCHER(switcher), stack_);
    adw_view_switcher_set_policy(ADW_VIEW_SWITCHER(switcher), ADW_VIEW_SWITCHER_POLICY_WIDE);

    changes_toggle_.set_icon_name("view-dual-symbolic");
    changes_toggle_.set_tooltip_text("Show the files this session changed");
    changes_toggle_.signal_toggled().connect([this] {
        changes_scroll_.set_visible(changes_toggle_.get_active());
        refresh_changes();
    });
    model_button_.add_css_class("flat");
    model_button_.signal_clicked().connect([this] { adw_view_stack_set_visible_child_name(stack_, "models"); });

    GtkWidget* header = adw_header_bar_new();
    adw_header_bar_set_title_widget(ADW_HEADER_BAR(header), switcher);
    adw_header_bar_pack_end(ADW_HEADER_BAR(header), GTK_WIDGET(changes_toggle_.gobj()));
    adw_header_bar_pack_end(ADW_HEADER_BAR(header), GTK_WIDGET(model_button_.gobj()));
    GtkWidget* content_view = adw_toolbar_view_new();
    adw_toolbar_view_add_top_bar(ADW_TOOLBAR_VIEW(content_view), header);
    adw_toolbar_view_set_content(ADW_TOOLBAR_VIEW(content_view), GTK_WIDGET(stack_));
    content_page_ = adw_navigation_page_new(content_view, "Mason Agent");

    GtkWidget* split = adw_navigation_split_view_new();
    adw_navigation_split_view_set_sidebar(ADW_NAVIGATION_SPLIT_VIEW(split), sidebar_page);
    adw_navigation_split_view_set_content(ADW_NAVIGATION_SPLIT_VIEW(split), content_page_);
    adw_navigation_split_view_set_min_sidebar_width(ADW_NAVIGATION_SPLIT_VIEW(split), 220);
    adw_navigation_split_view_set_max_sidebar_width(ADW_NAVIGATION_SPLIT_VIEW(split), 320);

    toasts_ = ADW_TOAST_OVERLAY(adw_toast_overlay_new());
    adw_toast_overlay_set_child(toasts_, split);
    adw_application_window_set_content(window_, GTK_WIDGET(toasts_));

    controller_.changed.connect([this] { refresh(); });
    // System notifications are for when the window is not the one in use.
    controller_.notification.connect([this](const std::string& title, const std::string& body) {
        if (gtk_window_is_active(GTK_WINDOW(window_))) return;
        GApplication* application = g_application_get_default();
        if (!application) return;
        GNotification* notification = g_notification_new(title.c_str());
        const std::string shown = utf8_length(body) > 180 ? utf8_prefix(body, 180) + "…" : body;
        g_notification_set_body(notification, shown.c_str());
        g_application_send_notification(application, nullptr, notification);
        g_object_unref(notification);
    });
    controller_.notice.connect([this](const std::string& message) {
        AdwToast* toast = adw_toast_new(message.c_str());
        adw_toast_set_timeout(toast, 6);
        adw_toast_overlay_add_toast(toasts_, toast);
    });
    refresh();
}

void MainWindow::present() { gtk_window_present(GTK_WINDOW(window_)); }

void MainWindow::open_project() {
    auto dialog = Gtk::FileDialog::create();
    dialog->set_title("Open Project");
    dialog->set_accept_label("Open Project");
    dialog->select_folder(*Glib::wrap(GTK_WINDOW(window_)), [this, dialog](const Glib::RefPtr<Gio::AsyncResult>& result) {
        try {
            if (const auto folder = dialog->select_folder_finish(result)) {
                controller_.new_session(folder->get_path());
                adw_view_stack_set_visible_child_name(stack_, "code");
            }
        } catch (const Glib::Error&) {
            // The dialog was dismissed.
        }
    });
}

void MainWindow::refresh() {
    updating_ = true;
    refresh_sidebar();
    refresh_transcript();
    refresh_composer();
    refresh_changes();
    models_.refresh();
    chat_.refresh();
    const Session* session = controller_.session();
    session_stack_.set_visible_child(session ? "session" : "empty");
    adw_navigation_page_set_title(content_page_, session ? session->title.c_str() : "Mason Agent");
    model_button_.set_label(controller_.endpoint ? controller_.endpoint_name : "No model running");
    model_button_.set_tooltip_text(controller_.endpoint ? "Running at " + controller_.endpoint->url : "No model is running. Open Models to start one.");
    updating_ = false;
}

void MainWindow::refresh_sidebar() {
    std::string fingerprint = controller_.selection + "|";
    for (const Session& item : controller_.sessions()) fingerprint += item.id + item.title + item.state + "|";
    if (fingerprint == sidebar_shown_) return;
    sidebar_shown_ = fingerprint;
    while (Gtk::Widget* child = sidebar_.get_first_child()) sidebar_.remove(*child);
    for (const std::string& project : controller_.projects()) {
        auto* header = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
        auto* name = Gtk::make_managed<Gtk::Label>(fs::path(project).filename().string());
        name->set_xalign(0);
        name->set_hexpand(true);
        name->set_ellipsize(Pango::EllipsizeMode::END);
        name->add_css_class("project-row");
        auto* add = Gtk::make_managed<Gtk::Button>();
        add->set_icon_name("list-add-symbolic");
        add->add_css_class("flat");
        add->set_tooltip_text("New session in " + fs::path(project).filename().string());
        add->signal_clicked().connect([this, project] { controller_.new_session(project); });
        auto* folder_icon = Gtk::make_managed<Gtk::Image>();
        folder_icon->set_from_icon_name("folder-symbolic");
        header->append(*folder_icon);
        header->append(*name);
        header->append(*add);
        auto* header_row = Gtk::make_managed<Gtk::ListBoxRow>();
        header_row->set_child(*header);
        header_row->set_selectable(false);
        header_row->set_activatable(false);
        header_row->set_name(project);
        header_row->set_tooltip_text(project);
        sidebar_.append(*header_row);
        for (const Session& item : controller_.sessions()) {
            if (item.workspace != project) continue;
            auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 2);
            box->set_margin_start(22);
            auto* title = Gtk::make_managed<Gtk::Label>(item.title);
            title->set_xalign(0);
            title->set_ellipsize(Pango::EllipsizeMode::END);
            auto* state = dim_label(item.state);
            state->add_css_class("caption");
            state->set_wrap(false);
            box->append(*title);
            box->append(*state);
            auto* row = Gtk::make_managed<Gtk::ListBoxRow>();
            row->set_child(*box);
            row->set_name(item.id);
            // Right-click or the keyboard menu key deletes a session.
            auto menu = Gio::Menu::create();
            menu->append("Delete Session", "row.delete");
            auto actions = Gio::SimpleActionGroup::create();
            const std::string id = item.id;
            actions->add_action("delete", [this, id] { controller_.remove_session(id); });
            row->insert_action_group("row", actions);
            auto* popover = Gtk::make_managed<Gtk::PopoverMenu>(menu);
            popover->set_parent(*row);
            popover->set_has_arrow(false);
            auto click = Gtk::GestureClick::create();
            click->set_button(GDK_BUTTON_SECONDARY);
            click->signal_pressed().connect([popover](int, double x, double y) {
                popover->set_pointing_to(Gdk::Rectangle(static_cast<int>(x), static_cast<int>(y), 1, 1));
                popover->popup();
            });
            row->add_controller(click);
            row->signal_destroy().connect([popover] { popover->unparent(); });
            sidebar_.append(*row);
            if (item.id == controller_.selection) sidebar_.select_row(*row);
        }
    }
}

Gtk::Widget* MainWindow::activity_widget(const Activity& activity) {
    auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    if (activity.state == "running") {
        auto* spinner = Gtk::make_managed<Gtk::Spinner>();
        spinner->start();
        row->append(*spinner);
    } else {
        // Set by icon name: Gtk::Image's string constructor takes a file path.
        auto* icon = Gtk::make_managed<Gtk::Image>();
        icon->set_from_icon_name(activity.state == "completed" ? "emblem-ok-symbolic" : activity.state == "denied" ? "action-unavailable-symbolic" : "dialog-error-symbolic");
        icon->add_css_class(activity.state == "completed" ? "success" : activity.state == "denied" ? "warning" : "error");
        row->append(*icon);
    }
    auto* name = Gtk::make_managed<Gtk::Label>();
    name->set_markup("<b>" + escape(label_of(activity.tool)) + "</b>");
    auto* subject = dim_label(replace_all(activity.subject, "\n", " ") + (activity.state == "denied" ? "  · declined" : ""));
    subject->set_wrap(false);
    subject->set_ellipsize(Pango::EllipsizeMode::MIDDLE);
    subject->set_hexpand(true);
    row->append(*name);
    row->append(*subject);
    if (activity.state == "running") return row;
    auto* expander = Gtk::make_managed<Gtk::Expander>();
    expander->set_label_widget(*row);
    auto* result = text_label(activity.result.empty() ? "No output." : utf8_prefix(activity.result, 6000));
    result->add_css_class("monospace");
    result->add_css_class("code-block");
    result->add_css_class("caption");
    expander->set_child(*result);
    return expander;
}

Gtk::Widget* MainWindow::answer_widget(const std::string& text) { return answer_view(text); }

Gtk::Widget* answer_view(const std::string& text) {
    auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    std::string prose, code;
    bool in_code = false;
    auto flush_prose = [&] {
        if (trim(prose).empty()) { prose.clear(); return; }
        std::string markup;
        for (const std::string& line : split(trim(prose), '\n')) {
            const std::string trimmed = trim(line);
            if (starts_with(trimmed, "#")) markup += "<b>" + escape(trim(trimmed.substr(trimmed.find_first_not_of('#')))) + "</b>\n";
            else if (starts_with(trimmed, "- ") || starts_with(trimmed, "* ")) markup += "• " + inline_markup(trimmed.substr(2)) + "\n";
            else markup += inline_markup(line) + "\n";
        }
        auto* label = text_label("");
        label->set_markup(trim(markup));
        box->append(*label);
        prose.clear();
    };
    for (const std::string& line : split(replace_all(text, "\r\n", "\n"), '\n')) {
        if (starts_with(trim(line), "```")) {
            if (in_code) {
                auto* block = text_label(code.empty() ? "" : code.substr(0, code.size() - 1));
                block->set_wrap(false);
                block->add_css_class("monospace");
                auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
                scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::NEVER);
                scroll->set_child(*block);
                scroll->add_css_class("code-block");
                box->append(*scroll);
                code.clear();
            } else {
                flush_prose();
            }
            in_code = !in_code;
        } else if (in_code) {
            code += line + "\n";
        } else {
            prose += line + "\n";
        }
    }
    if (in_code) prose += code;
    flush_prose();
    return box;
}

Gtk::Widget* MainWindow::turn_widget(const Turn& turn, const std::vector<Activity>& activities, bool live) {
    auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 10);
    auto* request = text_label(turn.request);
    request->add_css_class("request-bubble");
    request->set_halign(Gtk::Align::END);
    box->append(*request);
    for (const Activity& activity : activities) box->append(*activity_widget(activity));
    if (live) {
        auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
        auto* spinner = Gtk::make_managed<Gtk::Spinner>();
        spinner->start();
        live_label_ = dim_label("");
        live_label_->set_lines(3);
        live_label_->set_ellipsize(Pango::EllipsizeMode::START);
        row->append(*spinner);
        row->append(*live_label_);
        box->append(*row);
    } else if (!turn.answer.empty()) {
        box->append(*answer_widget(turn.answer));
    }
    if (!turn.changes.empty() && !live) {
        auto* chips = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
        for (size_t i = 0; i < turn.changes.size() && i < 6; i++) {
            const FileChange& changed = turn.changes[i];
            const DiffCounts counts = diff_counts(changed.before, changed.deleted() ? "" : changed.after);
            auto* chip = Gtk::make_managed<Gtk::Label>(changed.opaque() ? changed.path + "  binary" : changed.path + "  +" + std::to_string(counts.added) + " −" + std::to_string(counts.removed));
            chip->add_css_class("caption");
            chip->add_css_class("dim-label");
            chips->append(*chip);
        }
        if (turn.undone) {
            auto* undone = Gtk::make_managed<Gtk::Label>("Undone");
            undone->add_css_class("caption");
            undone->add_css_class("warning");
            chips->append(*undone);
        }
        box->append(*chips);
    }
    return box;
}

Gtk::Widget* MainWindow::approval_widget(const ApprovalRequest& request) {
    const std::string tool = label_of(request.tool);
    auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 10);
    card->add_css_class("approval-card");
    card->set_margin_start(16);
    card->set_margin_end(16);
    card->set_margin_top(8);
    auto* heading = Gtk::make_managed<Gtk::Label>("Review " + tool);
    heading->set_xalign(0);
    heading->add_css_class("heading");
    card->append(*heading);
    auto* title = text_label(utf8_prefix(request.title, 1200));
    title->add_css_class("monospace");
    card->append(*title);
    if (!request.detail.empty()) {
        auto* lines = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
        const std::vector<std::string> detail = split(request.detail, '\n');
        for (size_t i = 0; i < detail.size() && i < 200; i++) {
            auto* line = text_label(detail[i].empty() ? " " : detail[i]);
            line->add_css_class("monospace");
            line->add_css_class("caption");
            line->add_css_class(starts_with(detail[i], "+") ? "diff-added" : starts_with(detail[i], "-") ? "diff-removed" : "dim-label");
            lines->append(*line);
        }
        auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
        scroll->set_child(*lines);
        scroll->set_max_content_height(180);
        scroll->set_propagate_natural_height(true);
        card->append(*scroll);
    }
    auto* buttons = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* allow = Gtk::make_managed<Gtk::Button>("Allow Once");
    allow->add_css_class("suggested-action");
    allow->signal_clicked().connect([this] { controller_.answer(true); });
    auto* always = Gtk::make_managed<Gtk::MenuButton>();
    always->set_label("Always Allow…");
    auto* rule = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    rule->set_margin(12);
    auto* rule_title = Gtk::make_managed<Gtk::Label>("Always allow " + tool + " when it starts with");
    rule_title->set_xalign(0);
    auto* prefix = Gtk::make_managed<Gtk::Entry>();
    prefix->set_text(ApprovalRules::suggestion(request.tool, request.subject));
    prefix->set_width_chars(36);
    prefix->add_css_class("monospace");
    auto* note = dim_label("Saved for this project on this computer. Rules only skip this question; they never widen the sandbox.");
    note->set_max_width_chars(40);
    note->add_css_class("caption");
    auto* save = Gtk::make_managed<Gtk::Button>("Save Rule and Allow");
    save->add_css_class("suggested-action");
    save->signal_clicked().connect([this, prefix] {
        const std::string text = trim(prefix->get_text());
        if (!text.empty()) controller_.answer(true, text);
    });
    rule->append(*rule_title);
    rule->append(*prefix);
    rule->append(*note);
    rule->append(*save);
    auto* popover = Gtk::make_managed<Gtk::Popover>();
    popover->set_child(*rule);
    always->set_popover(*popover);
    auto* spacer = Gtk::make_managed<Gtk::Box>();
    spacer->set_hexpand(true);
    auto* decline = Gtk::make_managed<Gtk::Button>("Decline");
    decline->signal_clicked().connect([this] { controller_.answer(false); });
    buttons->append(*allow);
    buttons->append(*always);
    buttons->append(*spacer);
    buttons->append(*decline);
    card->append(*buttons);
    return card;
}

void MainWindow::refresh_transcript() {
    const Session* session = controller_.session();
    std::string fingerprint;
    if (session) {
        fingerprint = session->id + "|" + std::to_string(session->turns.size()) + "|" + session->state + "|" + session->detail + "|" + std::to_string(session->activities.size()) + "|" + (controller_.running ? "run" : "idle");
        for (const Turn& turn : session->turns) fingerprint += turn.undone ? "u" : "-";
        for (const Activity& activity : controller_.live_activities) fingerprint += activity.id + activity.state;
        if (controller_.approval) fingerprint += "|ask:" + controller_.approval->title;
    }
    if (fingerprint != transcript_shown_) {
        transcript_shown_ = fingerprint;
        live_label_ = nullptr;
        clear(transcript_);
        clear(approval_slot_);
        if (session) {
            for (size_t i = 0; i < session->turns.size(); i++) {
                const bool last = i + 1 == session->turns.size();
                const bool live = controller_.running && last;
                std::vector<Activity> activities;
                if (live) activities = controller_.live_activities;
                else {
                    const size_t start = session->turns[i].first_activity;
                    const size_t end = last ? session->activities.size() : session->turns[i + 1].first_activity;
                    if (start <= end && end <= session->activities.size()) activities.assign(session->activities.begin() + static_cast<std::ptrdiff_t>(start), session->activities.begin() + static_cast<std::ptrdiff_t>(end));
                }
                transcript_.append(*turn_widget(session->turns[i], activities, live));
            }
            if (session->turns.empty()) {
                auto* hint = dim_label("Ask a question about this project, or describe a change.");
                hint->set_halign(Gtk::Align::CENTER);
                hint->set_margin_top(80);
                transcript_.append(*hint);
            }
            if (!controller_.running && !session->detail.empty()) {
                auto* detail = dim_label(session->detail);
                if (session->state == "Failed") { detail->remove_css_class("dim-label"); detail->add_css_class("error"); }
                transcript_.append(*detail);
            }
            if (controller_.approval) approval_slot_.append(*approval_widget(*controller_.approval));
        }
        // After the new widgets are laid out, show the newest part of the conversation.
        Glib::signal_idle().connect_once([this] {
            auto adjustment = transcript_scroll_.get_vadjustment();
            adjustment->set_value(adjustment->get_upper());
        });
    }
    if (live_label_) {
        const std::string live = controller_.live_text.empty() ? controller_.live_status : utf8_suffix(replace_all(controller_.live_text, "\n", " "), 400);
        live_label_->set_text(live);
    }
}

void MainWindow::refresh_composer() {
    const Session* session = controller_.session();
    const bool was_updating = updating_;
    updating_ = true;
    if (session) permission_.set_selected(session->ask_only ? 0 : static_cast<guint>(session->permission));
    updating_ = was_updating;
    permission_.set_sensitive(!controller_.running);
    const bool has_text = !trim(draft_.get_buffer()->get_text()).empty();
    const bool can_follow_up = has_text && !controller_.approval;
    stop_.set_visible(controller_.running);
    send_.set_visible(!controller_.running);
    send_.set_sensitive(has_text);
    queue_.set_visible(controller_.running);
    steer_.set_visible(controller_.running);
    queue_.set_sensitive(can_follow_up);
    steer_.set_sensitive(can_follow_up);
    waiting_.set_visible(controller_.running && controller_.waiting_follow_ups > 0);
    waiting_.set_text(std::to_string(controller_.waiting_follow_ups) + " waiting");
    const size_t queued = session ? session->pending_input.size() : 0;
    run_queued_.set_visible(!controller_.running && queued > 0);
    run_queued_.set_label("Run " + std::to_string(queued) + " Queued");
}

void MainWindow::refresh_changes() {
    if (!changes_toggle_.get_active()) return;
    const Session* session = controller_.session();
    std::vector<FileChange> merged;
    std::string fingerprint = controller_.running ? "run|" : "idle|";
    if (session) {
        for (const Turn& turn : session->turns) {
            fingerprint += turn.id + (turn.undone ? "u" : "-") + std::to_string(turn.changes.size());
            if (turn.undone) continue;
            for (const FileChange& change : turn.changes) {
                auto existing = std::find_if(merged.begin(), merged.end(), [&](const FileChange& item) { return item.path == change.path; });
                if (existing != merged.end()) { existing->after = change.after; existing->after_blob = change.after_blob; existing->after_missing = change.after_missing; }
                else merged.push_back(change);
            }
        }
    }
    if (fingerprint == changes_shown_) return;
    changes_shown_ = fingerprint;
    clear(changes_);
    auto* bar = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* heading = Gtk::make_managed<Gtk::Label>("Changes");
    heading->add_css_class("heading");
    heading->set_hexpand(true);
    heading->set_xalign(0);
    auto* undo = Gtk::make_managed<Gtk::Button>("Undo");
    undo->set_tooltip_text("Put back the files the latest request edited");
    undo->set_sensitive(!controller_.running);
    undo->signal_clicked().connect([this] { controller_.undo(); });
    auto* redo = Gtk::make_managed<Gtk::Button>("Redo");
    redo->set_sensitive(!controller_.running);
    redo->signal_clicked().connect([this] { controller_.undo(true); });
    bar->append(*heading);
    bar->append(*undo);
    bar->append(*redo);
    changes_.append(*bar);
    if (merged.empty()) {
        changes_.append(*dim_label("Files this session edits appear here."));
        return;
    }
    for (const FileChange& change : merged) {
        const std::string after = change.deleted() ? "" : change.after;
        const DiffCounts counts = diff_counts(change.before, after);
        const std::string kind = std::string(change.deleted() ? "  deleted" : change.created() ? "  new file" : "") + (change.by_command ? "  by a command" : "");
        auto* title = Gtk::make_managed<Gtk::Label>();
        title->set_xalign(0);
        title->set_markup("<b>" + escape(change.path) + "</b>" + (change.opaque() ? "  binary or large file" : "  +" + std::to_string(counts.added) + " −" + std::to_string(counts.removed)) + kind);
        changes_.append(*title);
        if (change.opaque()) continue;  // no text to compare
        auto* lines = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
        lines->add_css_class("code-block");
        for (const DiffLine& row : visible_rows(diff_lines(change.before, after))) {
            char number[16];
            std::snprintf(number, sizeof number, "%5d ", row.number);
            auto* line = Gtk::make_managed<Gtk::Label>(row.kind == 0 ? "      ⋯" : std::string(number) + row.kind + " " + replace_all(row.text, "\t", "    "));
            line->set_xalign(0);
            line->set_selectable(true);
            line->add_css_class("monospace");
            line->add_css_class("caption");
            if (row.kind == '+') line->add_css_class("diff-added");
            else if (row.kind == '-') line->add_css_class("diff-removed");
            else if (row.kind == 0) line->add_css_class("dim-label");
            lines->append(*line);
        }
        auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
        scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::NEVER);
        scroll->set_child(*lines);
        changes_.append(*scroll);
    }
    auto* note = dim_label("Undo puts back everything the latest request changed, including files its commands created, changed or deleted.");
    note->add_css_class("caption");
    changes_.append(*note);
}

}  // namespace mason::app
