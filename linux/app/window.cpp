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

Gtk::Label* text_label(const std::string& text, bool selectable = true) {
    auto* label = Gtk::make_managed<Gtk::Label>(text);
    label->set_xalign(0);
    label->set_wrap(true);
    label->set_wrap_mode(Pango::WrapMode::WORD_CHAR);
    label->set_selectable(selectable);
    // Text can be selected with the pointer, but moving the keyboard through the window does not select whole messages.
    label->set_can_focus(false);
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

MainWindow::MainWindow(Gtk::Application& application, Controller& controller) : controller_(controller), models_(controller), chat_(controller), settings_(controller) {
    install_style();

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
    auto* brand = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    brand->append(*mascot(28));
    auto* brand_name = Gtk::make_managed<Gtk::Label>("Mason Agent");
    brand_name->add_css_class("brand-title");
    brand->append(*brand_name);
    GtkWidget* sidebar_header = adw_header_bar_new();
    adw_header_bar_set_title_widget(ADW_HEADER_BAR(sidebar_header), GTK_WIDGET(brand->gobj()));
    adw_header_bar_set_show_end_title_buttons(ADW_HEADER_BAR(sidebar_header), FALSE);
    adw_header_bar_pack_end(ADW_HEADER_BAR(sidebar_header), GTK_WIDGET(open_button->gobj()));
    GtkWidget* sidebar_view = adw_toolbar_view_new();
    adw_toolbar_view_add_top_bar(ADW_TOOLBAR_VIEW(sidebar_view), sidebar_header);
    adw_toolbar_view_set_content(ADW_TOOLBAR_VIEW(sidebar_view), GTK_WIDGET(sidebar_scroll->gobj()));

    // ---- session page ----
    transcript_.set_margin(24);
    transcript_.set_spacing(22);
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
    draft_.set_tooltip_text("");

    permission_.set_model(Gtk::StringList::create({"Read-only", "Review edits", "Workspace edits", "Auto (sandboxed)"}));
    permission_.set_tooltip_text("What Mason may do without asking. Auto runs commands in a sandbox limited to this project, with network access off.");
    permission_.property_selected().signal_changed().connect([this] {
        if (updating_) return;
        const Permission choices[] = {Permission::ReadOnly, Permission::ReviewEdits, Permission::WorkspaceEdits, Permission::Auto};
        controller_.set_permission(choices[std::min<guint>(permission_.get_selected(), 3)]);
    });
    send_.set_label("");
    send_.set_icon_name("go-up-symbolic");
    send_.set_tooltip_text("Send (Ctrl+Enter)");
    gtk_accessible_update_property(GTK_ACCESSIBLE(send_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Send", -1);
    send_.add_css_class("suggested-action");
    send_.add_css_class("send-round");
    send_.signal_clicked().connect([this] {
        const std::string text = draft_.get_buffer()->get_text();
        const Session* current = controller_.session();
        if ((trim(text).empty() && (!current || current->comments.empty())) || controller_.running) return;
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
    model_button_.add_css_class("chip");
    composer_hint_.add_css_class("composer-hint");
    composer_hint_.set_text("Ctrl+Enter to send");
    controls->append(permission_);
    controls->append(model_button_);
    window_button_.set_label("Window");
    window_button_.add_css_class("chip-menu");
    window_button_.set_tooltip_text("Attach a window of another app so Mason can read its controls and, with your approval each time, operate it");
    window_list_.set_margin(8);
    window_popover_.set_child(window_list_);
    window_button_.set_popover(window_popover_);
    // The list is read when the menu opens, so it shows the windows that are open now.
    window_popover_.signal_show().connect([this] { fill_window_list(); });
    controls->append(window_button_);
    controls->append(*spacer);
    controls->append(composer_hint_);
    controls->append(waiting_);
    controls->append(steer_);
    controls->append(queue_);
    controls->append(run_queued_);
    controls->append(stop_);
    controls->append(send_);
    auto* composer_card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    composer_card->add_css_class("composer-card");
    // A hint in the empty request box, in front of the text and out of the way of the pointer.
    auto* placeholder = Gtk::make_managed<Gtk::Label>("Describe a change, or ask about the project…");
    placeholder->add_css_class("composer-hint");
    placeholder->set_halign(Gtk::Align::START);
    placeholder->set_valign(Gtk::Align::START);
    placeholder->set_margin_start(9);
    placeholder->set_margin_top(7);
    placeholder->set_can_target(false);
    draft_.get_buffer()->signal_changed().connect([this, placeholder] { placeholder->set_visible(draft_.get_buffer()->get_char_count() == 0); });
    auto* draft_overlay = Gtk::make_managed<Gtk::Overlay>();
    draft_overlay->set_child(*draft_scroll);
    draft_overlay->add_overlay(*placeholder);
    comments_.set_visible(false);
    composer_card->append(comments_);
    composer_card->append(*draft_overlay);
    composer_card->append(*controls);
    GtkWidget* composer_clamp = adw_clamp_new();
    adw_clamp_set_maximum_size(ADW_CLAMP(composer_clamp), 900);
    adw_clamp_set_child(ADW_CLAMP(composer_clamp), GTK_WIDGET(composer_card->gobj()));
    auto* composer = Glib::wrap(composer_clamp);
    composer->set_margin(16);
    composer->set_margin_top(6);

    auto* conversation = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    conversation->append(transcript_scroll_);
    conversation->append(approval_slot_);
    conversation->append(*composer);
    conversation->set_size_request(420, -1);

    changes_.set_margin(12);
    changes_scroll_.set_child(changes_);
    changes_scroll_.set_size_request(384, -1);
    changes_scroll_.set_visible(false);
    auto* paned = Gtk::make_managed<Gtk::Paned>(Gtk::Orientation::HORIZONTAL);
    paned->set_start_child(*conversation);
    paned->set_end_child(changes_scroll_);
    paned->set_resize_start_child(true);
    paned->set_shrink_start_child(false);
    paned->set_shrink_end_child(false);

    auto* welcome = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 14);
    welcome->set_valign(Gtk::Align::CENTER);
    welcome->set_halign(Gtk::Align::CENTER);
    welcome->set_margin(32);
    welcome->append(*welcome_art(230));
    auto* welcome_title = Gtk::make_managed<Gtk::Label>("Make the next move clear");
    welcome_title->add_css_class("welcome-title");
    auto* welcome_lede = Gtk::make_managed<Gtk::Label>("Open a project folder and ask Mason to read, explain or change it.\nIt runs on your own local model, and asks before each edit and command.");
    welcome_lede->add_css_class("welcome-lede");
    welcome_lede->set_justify(Gtk::Justification::CENTER);
    welcome_lede->set_wrap(true);
    auto* open_first = Gtk::make_managed<Gtk::Button>("Open Project…");
    open_first->add_css_class("suggested-action");
    open_first->add_css_class("pill");
    open_first->set_halign(Gtk::Align::CENTER);
    open_first->set_margin_top(8);
    open_first->signal_clicked().connect([this] { open_project(); });
    welcome->append(*welcome_title);
    welcome->append(*welcome_lede);
    welcome->append(*open_first);
    GtkWidget* empty = GTK_WIDGET(welcome->gobj());
    session_stack_.add(*paned, "session");
    gtk_stack_add_named(session_stack_.gobj(), empty, "empty");

    // ---- content: Code and Models ----
    stack_ = ADW_VIEW_STACK(adw_view_stack_new());
    adw_view_stack_add_titled_with_icon(stack_, GTK_WIDGET(session_stack_.gobj()), "code", "Code", "utilities-terminal-symbolic");
    adw_view_stack_add_titled_with_icon(stack_, GTK_WIDGET(chat_.gobj()), "chat", "Chat", "user-available-symbolic");
    adw_view_stack_add_titled_with_icon(stack_, GTK_WIDGET(models_.gobj()), "models", "Models", "applications-science-symbolic");
    adw_view_stack_add_titled_with_icon(stack_, GTK_WIDGET(settings_.gobj()), "settings", "Settings", "emblem-system-symbolic");
    GtkWidget* switcher = adw_view_switcher_new();
    adw_view_switcher_set_stack(ADW_VIEW_SWITCHER(switcher), stack_);
    adw_view_switcher_set_policy(ADW_VIEW_SWITCHER(switcher), ADW_VIEW_SWITCHER_POLICY_WIDE);

    changes_toggle_.set_icon_name("view-dual-symbolic");
    changes_toggle_.set_tooltip_text("Show the files this session changed");
    changes_toggle_.signal_toggled().connect([this] {
        changes_scroll_.set_visible(changes_toggle_.get_active());
        refresh_changes();
    });
    model_button_.signal_clicked().connect([this] { adw_view_stack_set_visible_child_name(stack_, "models"); });

    // Appearance: follow the system, or always light or dark.
    auto appearance_menu = Gio::Menu::create();
    appearance_menu->append("Follow System", "look.appearance::system");
    appearance_menu->append("Light", "look.appearance::light");
    appearance_menu->append("Dark", "look.appearance::dark");
    auto look = Gio::SimpleActionGroup::create();
    const Appearance current = appearance();
    look->add_action_radio_string("appearance", [look](const Glib::ustring& choice) {
        set_appearance(choice == "light" ? Appearance::Light : choice == "dark" ? Appearance::Dark : Appearance::System);
        if (auto action = std::dynamic_pointer_cast<Gio::SimpleAction>(look->lookup_action("appearance"))) action->set_state(Glib::Variant<Glib::ustring>::create(choice));
    }, current == Appearance::Light ? "light" : current == Appearance::Dark ? "dark" : "system");
    auto* appearance_button = Gtk::make_managed<Gtk::MenuButton>();
    appearance_button->set_icon_name("weather-clear-night-symbolic");
    appearance_button->set_tooltip_text("Appearance");
    appearance_button->set_menu_model(appearance_menu);
    appearance_button->insert_action_group("look", look);

    GtkWidget* header = adw_header_bar_new();
    adw_header_bar_set_title_widget(ADW_HEADER_BAR(header), switcher);
    adw_header_bar_pack_end(ADW_HEADER_BAR(header), GTK_WIDGET(appearance_button->gobj()));
    adw_header_bar_pack_end(ADW_HEADER_BAR(header), GTK_WIDGET(changes_toggle_.gobj()));

    // A quiet line at the bottom: what Mason is doing, and where the session lives.
    auto* status = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 12);
    status->add_css_class("status-bar");
    status_left_.set_xalign(0);
    status_left_.set_hexpand(true);
    status_left_.set_ellipsize(Pango::EllipsizeMode::END);
    status_right_.set_xalign(1);
    status_right_.set_ellipsize(Pango::EllipsizeMode::START);
    status->append(status_left_);
    status->append(status_right_);

    GtkWidget* content_view = adw_toolbar_view_new();
    adw_toolbar_view_add_top_bar(ADW_TOOLBAR_VIEW(content_view), header);
    adw_toolbar_view_set_content(ADW_TOOLBAR_VIEW(content_view), GTK_WIDGET(stack_));
    adw_toolbar_view_add_bottom_bar(ADW_TOOLBAR_VIEW(content_view), GTK_WIDGET(status->gobj()));

    split_ = adw_overlay_split_view_new();
    adw_overlay_split_view_set_sidebar(ADW_OVERLAY_SPLIT_VIEW(split_), sidebar_view);
    adw_overlay_split_view_set_content(ADW_OVERLAY_SPLIT_VIEW(split_), content_view);
    adw_overlay_split_view_set_min_sidebar_width(ADW_OVERLAY_SPLIT_VIEW(split_), 230);
    adw_overlay_split_view_set_max_sidebar_width(ADW_OVERLAY_SPLIT_VIEW(split_), 320);
    // Projects and sessions belong to Code. Chat has its own list, and Models needs the room.
    g_signal_connect(stack_, "notify::visible-child-name", G_CALLBACK(+[](GObject* stack, GParamSpec*, gpointer split) {
        const char* page = adw_view_stack_get_visible_child_name(ADW_VIEW_STACK(stack));
        adw_overlay_split_view_set_show_sidebar(ADW_OVERLAY_SPLIT_VIEW(split), page && std::string(page) == "code");
    }), split_);

    toasts_ = ADW_TOAST_OVERLAY(adw_toast_overlay_new());
    adw_toast_overlay_set_child(toasts_, split_);
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
    settings_.refresh();
    const Session* session = controller_.session();
    session_stack_.set_visible_child(session ? "session" : "empty");
    gtk_window_set_title(GTK_WINDOW(window_), session ? (session->title + " · Mason Agent").c_str() : "Mason Agent");
    model_button_.set_label(controller_.endpoint ? controller_.endpoint_name : "No model running");
    std::string doing = "Ready";
    if (controller_.running) doing = controller_.live_status.empty() ? "Working" : controller_.live_status;
    else if (session && session->state != "Ready") doing = session->state + (session->detail.empty() ? "" : " · " + session->detail);
    status_left_.set_text(utf8_prefix(replace_all(doing, "\n", " "), 160));
    status_right_.set_text(session ? fs::path(session->workspace).filename().string() + " · " + permission_label(session->ask_only ? Permission::ReadOnly : session->permission) + " · Session saved on this computer" : "Everything stays on this computer");
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
            auto* text = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 1);
            auto* title = Gtk::make_managed<Gtk::Label>(item.title);
            title->set_xalign(0);
            title->set_ellipsize(Pango::EllipsizeMode::END);
            auto* state = Gtk::make_managed<Gtk::Label>(item.state);
            state->set_xalign(0);
            state->add_css_class("session-state");
            text->append(*title);
            text->append(*state);
            text->set_hexpand(true);
            auto* bubble = Gtk::make_managed<Gtk::Image>();
            bubble->set_from_icon_name(item.state == "Running" || item.state == "Awaiting review" ? "content-loading-symbolic" : "user-available-symbolic");
            bubble->add_css_class("dim-label");
            auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
            box->set_margin_start(14);
            box->set_margin_top(3);
            box->set_margin_bottom(3);
            box->append(*bubble);
            box->append(*text);
            auto* row = Gtk::make_managed<Gtk::ListBoxRow>();
            row->set_child(*box);
            row->set_name(item.id);
            // Right-click or the keyboard menu key deletes a session.
            auto menu = Gio::Menu::create();
            menu->append("Export Session…", "row.export");
            menu->append("Export a Sanitized Copy…", "row.sanitized");
            menu->append("Import a Session Into This Project…", "row.import");
            menu->append("Delete Session", "row.delete");
            auto actions = Gio::SimpleActionGroup::create();
            const std::string id = item.id, named = item.title, folder = item.workspace;
            actions->add_action("delete", [this, id] { controller_.remove_session(id); });
            actions->add_action("export", [this, id, named] { choose_export(id, named, false); });
            actions->add_action("sanitized", [this, id, named] { choose_export(id, named, true); });
            actions->add_action("import", [this, folder] { choose_import(folder); });
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
    box->add_css_class("turn");
    box->append(*speaker(false));
    auto* request = message_body(*text_label(turn.request));
    box->append(*request);
    if (!live) {
        // Right-click a request to start again from before it.
        auto menu = Gio::Menu::create();
        menu->append("Fork From Before This Request", "turn.fork");
        menu->append("Fork and Put the Files Back as They Were", "turn.restore");
        auto actions = Gio::SimpleActionGroup::create();
        const std::string id = turn.id;
        actions->add_action("fork", [this, id] { Glib::signal_idle().connect_once([this, id] { controller_.fork_session(id, false); }); });
        actions->add_action("restore", [this, id] { Glib::signal_idle().connect_once([this, id] { controller_.fork_session(id, true); }); });
        request->insert_action_group("turn", actions);
        auto* popover = Gtk::make_managed<Gtk::PopoverMenu>(menu);
        popover->set_parent(*request);
        popover->set_has_arrow(false);
        auto click = Gtk::GestureClick::create();
        click->set_button(GDK_BUTTON_SECONDARY);
        click->signal_pressed().connect([popover](int, double x, double y) {
            popover->set_pointing_to(Gdk::Rectangle(static_cast<int>(x), static_cast<int>(y), 1, 1));
            popover->popup();
        });
        request->add_controller(click);
        request->signal_destroy().connect([popover] { popover->unparent(); });
    }

    auto* reply = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 10);
    if (!activities.empty()) {
        auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 2);
        card->add_css_class("tool-card");
        for (const Activity& activity : activities) card->append(*activity_widget(activity));
        reply->append(*card);
    }
    if (live) {
        auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
        auto* spinner = Gtk::make_managed<Gtk::Spinner>();
        spinner->start();
        live_label_ = dim_label("");
        live_label_->set_lines(3);
        live_label_->set_ellipsize(Pango::EllipsizeMode::START);
        row->append(*spinner);
        row->append(*live_label_);
        reply->append(*row);
    } else if (!turn.answer.empty()) {
        reply->append(*answer_widget(turn.answer));
    }
    if (!live && turn.checks) {
        // What Mason found when it ran the checks it worked out from the request.
        const DerivedReceipt& receipt = *turn.checks;
        const bool ran = std::any_of(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state != "notRun"; });
        const bool good = derived::passed(receipt);
        auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
        auto* icon = Gtk::make_managed<Gtk::Image>();
        icon->set_from_icon_name(!ran ? "dialog-information-symbolic" : good ? "emblem-ok-symbolic" : "dialog-error-symbolic");
        auto* label = Gtk::make_managed<Gtk::Label>(derived::summary(receipt));
        label->set_xalign(0);
        label->set_wrap(true);
        for (Gtk::Widget* part : {static_cast<Gtk::Widget*>(icon), static_cast<Gtk::Widget*>(label)}) part->add_css_class(!ran ? "warning" : good ? "success" : "error");
        row->append(*icon);
        row->append(*label);
        row->set_tooltip_text(derived::report(receipt));
        reply->append(*row);
    }
    if (!live && turn.verification) {
        // What Mason found when it ran the project's verify workflow itself.
        const Verification& receipt = *turn.verification;
        const bool good = receipt.verdict == "passed" || receipt.verdict == "fixed";
        const bool quiet = receipt.verdict == "notRun" || receipt.verdict == "failingBefore";
        auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
        auto* icon = Gtk::make_managed<Gtk::Image>();
        icon->set_from_icon_name(good ? "emblem-ok-symbolic" : quiet ? "dialog-information-symbolic" : "dialog-error-symbolic");
        auto* label = Gtk::make_managed<Gtk::Label>("Verified by Mason: " + receipt.summary() + (receipt.repairs > 0 ? " · after " + std::to_string(receipt.repairs) + (receipt.repairs == 1 ? " repair" : " repairs") : ""));
        label->set_xalign(0);
        for (Gtk::Widget* part : {static_cast<Gtk::Widget*>(icon), static_cast<Gtk::Widget*>(label)}) part->add_css_class(good ? "success" : quiet ? "warning" : "error");
        row->append(*icon);
        row->append(*label);
        row->set_tooltip_text(receipt.command + (receipt.output.empty() ? "" : "\n\n" + utf8_suffix(receipt.output, 600)));
        reply->append(*row);
    }
    if (!live && (!activities.empty() || !turn.changes.empty())) {
        // What the request amounted to, in one line, and the way to its changes.
        int reads = 0, edits = 0, commands = 0, other = 0;
        for (const Activity& activity : activities) {
            if (activity.state != "completed") continue;
            if (activity.tool == "read_file" || activity.tool == "read_lines" || activity.tool == "list_files" || activity.tool == "search_files") reads++;
            else if (activity.tool == "edit_file" || activity.tool == "write_file" || activity.tool == "apply_edits") edits++;
            else if (activity.tool == "run_shell" || activity.tool == "run_workflow" || activity.tool == "start_job") commands++;
            else other++;
        }
        std::vector<std::string> parts;
        auto count = [&](int number, const char* one, const char* many) { if (number > 0) parts.push_back(std::to_string(number) + " " + (number == 1 ? one : many)); };
        count(reads, "read", "reads");
        count(edits, "edit", "edits");
        count(commands, "command", "commands");
        count(other, "other step", "other steps");
        auto* line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
        auto* summary = Gtk::make_managed<Gtk::Label>(parts.empty() ? "No steps completed" : join(parts, " · "));
        summary->add_css_class("summary-line");
        line->append(*summary);
        if (!turn.changes.empty()) {
            auto* changed = Gtk::make_managed<Gtk::Button>(std::to_string(turn.changes.size()) + (turn.changes.size() == 1 ? " changed file" : " changed files"));
            changed->add_css_class("flat");
            changed->add_css_class("changed-link");
            changed->set_tooltip_text("Show what changed");
            changed->signal_clicked().connect([this] { changes_toggle_.set_active(true); });
            line->append(*changed);
        }
        if (turn.undone) {
            auto* undone = Gtk::make_managed<Gtk::Label>("Undone");
            undone->add_css_class("caption");
            undone->add_css_class("warning");
            line->append(*undone);
        }
        reply->append(*line);
    }
    if (reply->get_first_child()) {
        box->append(*speaker(true));
        box->append(*message_body(*reply));
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
    card->set_margin_bottom(4);
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
            // The keyboard belongs in the request box, not on the first line of the conversation.
            if (!controller_.approval && adw_view_stack_get_visible_child(stack_) == GTK_WIDGET(session_stack_.gobj())) draft_.grab_focus();
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
    // Comments pinned to diff lines, each with a way to drop it.
    std::string print;
    if (session) {
        for (const ReviewComment& item : session->comments) print += item.id + "|";
    }
    if (print != comments_shown_) {
        comments_shown_ = print;
        clear(comments_);
        comments_.set_visible(session && !session->comments.empty());
        if (session) {
            for (const ReviewComment& item : session->comments) {
                auto* chip = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
                chip->add_css_class("comment-chip");
                auto* where = Gtk::make_managed<Gtk::Label>();
                where->set_markup("<b>" + escape(review::location(item)) + "</b>  " + escape(item.note));
                where->set_xalign(0);
                where->set_hexpand(true);
                where->set_ellipsize(Pango::EllipsizeMode::END);
                auto* drop = Gtk::make_managed<Gtk::Button>();
                drop->set_icon_name("window-close-symbolic");
                drop->add_css_class("flat");
                drop->set_tooltip_text("Remove this comment");
                gtk_accessible_update_property(GTK_ACCESSIBLE(drop->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Remove the comment on " + review::location(item)).c_str(), -1);
                drop->signal_clicked().connect([this, id = item.id] { controller_.remove_comment(id); });
                chip->append(*where);
                chip->append(*drop);
                comments_.append(*chip);
            }
        }
    }
    const bool has_comments = session && !session->comments.empty();
    // A fork leaves its request here to be edited and sent again.
    if (!controller_.forked_request.empty()) {
        draft_.get_buffer()->set_text(controller_.forked_request);
        controller_.forked_request.clear();
    }
    window_button_.set_label(session && session->desktop ? session->desktop->app : "Window");
    window_button_.set_sensitive(!controller_.running);
    stop_.set_visible(controller_.running);
    send_.set_visible(!controller_.running);
    send_.set_sensitive(has_text || has_comments);
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

/// Two linked buttons at the top of the side panel: the session's changes, or its plan.
Gtk::Widget* MainWindow::panel_switch() {
    auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 0);
    box->add_css_class("linked");
    box->set_hexpand(true);
    auto* changes = Gtk::make_managed<Gtk::ToggleButton>("Changes");
    auto* plan = Gtk::make_managed<Gtk::ToggleButton>("Plan");
    plan->set_group(*changes);
    (plan_shown_ ? plan : changes)->set_active(true);
    plan->set_tooltip_text("Milestones: the steps of the work, the files each covers and how each is checked");
    plan->signal_toggled().connect([this, plan] {
        if (plan->get_active() == plan_shown_) return;
        plan_shown_ = plan->get_active();
        changes_shown_.clear();
        // Rebuilding the panel removes this button, so it happens after the click is over.
        Glib::signal_idle().connect_once([this] { refresh_changes(); });
    });
    box->append(*changes);
    box->append(*plan);
    return box;
}

void MainWindow::choose_export(const std::string& id, const std::string& title, bool sanitize) {
    auto dialog = Gtk::FileDialog::create();
    dialog->set_title(sanitize ? "Export a Sanitized Copy" : "Export Session");
    dialog->set_initial_name(replace_all(utf8_prefix(title, 40), "/", "-") + (sanitize ? " (sanitized)" : "") + ".json");
    dialog->save(*Glib::wrap(GTK_WINDOW(window_)), [this, dialog, id, sanitize](const Glib::RefPtr<Gio::AsyncResult>& result) {
        try {
            if (const auto file = dialog->save_finish(result)) controller_.export_session(id, file->get_path(), sanitize);
        } catch (const Glib::Error&) {
            // Cancelled.
        }
    });
}

void MainWindow::choose_import(const std::string& workspace) {
    auto dialog = Gtk::FileDialog::create();
    dialog->set_title("Import a Session");
    dialog->set_accept_label("Import");
    dialog->open(*Glib::wrap(GTK_WINDOW(window_)), [this, dialog, workspace](const Glib::RefPtr<Gio::AsyncResult>& result) {
        try {
            if (const auto file = dialog->open_finish(result)) controller_.import_session(file->get_path(), workspace);
        } catch (const Glib::Error&) {
            // Cancelled.
        }
    });
}

void MainWindow::fill_window_list() {
    clear(window_list_);
    const Session* session = controller_.session();
    auto row = [this](const std::string& label, std::function<void()> action) {
        auto* button = Gtk::make_managed<Gtk::Button>(label);
        button->add_css_class("flat");
        if (auto* text = dynamic_cast<Gtk::Label*>(button->get_child())) { text->set_xalign(0); text->set_ellipsize(Pango::EllipsizeMode::END); text->set_max_width_chars(48); }
        button->signal_clicked().connect([this, action] { window_popover_.popdown(); action(); });
        window_list_.append(*button);
    };
    if (session && session->desktop) row("Detach " + desktop::label(*session->desktop), [this] { controller_.attach_window(std::nullopt); });
    const std::vector<DesktopWindow> open = controller_.open_windows();
    if (open.empty()) window_list_.append(*dim_label("No windows of other apps are open."));
    for (const DesktopWindow& window : open) row(desktop::label(window), [this, window] { controller_.attach_window(window); });
}

void MainWindow::refresh_plan() {
    const Session* session = controller_.session();
    const std::vector<MilestoneStatus> rows = controller_.milestone_status();
    std::string fingerprint = "plan|" + std::string(controller_.running ? "run|" : "idle|") + controller_.milestone_busy + "|" + observing_ + "|" + (adding_milestone_ ? "adding" : "") + "|" + (session ? session->id : "");
    for (const MilestoneStatus& row : rows) fingerprint += row.item.id + row.state + row.reason + "|";
    if (fingerprint == changes_shown_) return;
    changes_shown_ = fingerprint;
    clear(changes_);
    auto* bar = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    bar->append(*panel_switch());
    changes_.append(*bar);
    if (!session) return;
    if (rows.empty() && !adding_milestone_) {
        auto* empty = dim_label("No milestones yet. Break the work into steps. Each names the files it covers and how it is checked: a project workflow, or what you saw yourself. Mason is told the plan with every request.");
        empty->set_wrap(true);
        changes_.append(*empty);
    }
    for (size_t index = 0; index < rows.size(); index++) {
        const MilestoneStatus& row = rows[index];
        const std::string id = row.item.id;
        auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 6);
        card->add_css_class("card");
        card->add_css_class("milestone");
        auto* top = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
        auto* title = Gtk::make_managed<Gtk::Label>();
        title->set_markup("<b>" + escape(std::to_string(index + 1) + ". " + row.item.title) + "</b>");
        title->set_xalign(0);
        title->set_hexpand(true);
        title->set_wrap(true);
        auto* state = Gtk::make_managed<Gtk::Label>(row.state);
        state->add_css_class("state-pill");
        state->add_css_class(row.done() ? "state-done" : row.state == "Ready" ? "state-ready" : row.state == "Blocked" ? "state-blocked" : "state-recheck");
        state->set_valign(Gtk::Align::START);
        top->append(*title);
        top->append(*state);
        card->append(*top);
        if (!row.item.decision.empty()) card->append(*text_label(row.item.decision));
        auto* files = dim_label(join(row.item.files, ", "));
        files->add_css_class("monospace");
        files->add_css_class("caption");
        files->set_wrap(true);
        card->append(*files);
        auto* reason = dim_label((row.item.workflow.empty() ? "" : "Workflow " + row.item.workflow + " · ") + split(row.reason, '\n').front());
        reason->add_css_class("caption");
        reason->set_wrap(true);
        card->append(*reason);
        if (observing_ == id) {
            auto* entry = Gtk::make_managed<Gtk::Entry>();
            entry->set_placeholder_text("What did you observe?");
            auto* buttons = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
            auto* record = Gtk::make_managed<Gtk::Button>("Record");
            record->add_css_class("suggested-action");
            auto* cancel = Gtk::make_managed<Gtk::Button>("Cancel");
            auto submit = [this, entry, id] {
                const std::string seen = trim(entry->get_text());
                if (seen.empty()) return;
                observing_.clear();
                Glib::signal_idle().connect_once([this, id, seen] { controller_.check_milestone(id, seen); });
            };
            entry->signal_activate().connect(submit);
            record->signal_clicked().connect(submit);
            cancel->signal_clicked().connect([this] { observing_.clear(); Glib::signal_idle().connect_once([this] { refresh_changes(); }); });
            buttons->append(*record);
            buttons->append(*cancel);
            card->append(*entry);
            card->append(*buttons);
        } else {
            auto* buttons = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
            const bool manual = row.item.workflow.empty();
            auto* check = Gtk::make_managed<Gtk::Button>(controller_.milestone_busy == id ? "Checking…" : manual ? "Record What You Saw…" : "Run Check");
            check->set_sensitive(row.state != "Blocked" && controller_.milestone_busy.empty() && !controller_.running);
            check->set_tooltip_text(manual ? "Record what you observed as this milestone's evidence" : "Run the workflow " + row.item.workflow + ". Mason runs it in Auto, or when you chose Always Allow for it.");
            check->signal_clicked().connect([this, id, manual] {
                if (manual) { observing_ = id; Glib::signal_idle().connect_once([this] { refresh_changes(); }); }
                else controller_.check_milestone(id, "");
            });
            auto* spacer = Gtk::make_managed<Gtk::Box>();
            spacer->set_hexpand(true);
            auto* remove = Gtk::make_managed<Gtk::Button>();
            remove->set_icon_name("user-trash-symbolic");
            remove->add_css_class("flat");
            remove->set_tooltip_text("Remove this milestone");
            gtk_accessible_update_property(GTK_ACCESSIBLE(remove->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Remove " + row.item.title).c_str(), -1);
            remove->signal_clicked().connect([this, id] { Glib::signal_idle().connect_once([this, id] { controller_.remove_milestone(id); }); });
            buttons->append(*check);
            buttons->append(*spacer);
            buttons->append(*remove);
            card->append(*buttons);
        }
        changes_.append(*card);
    }
    if (!adding_milestone_) {
        auto* add = Gtk::make_managed<Gtk::Button>("Add Milestone");
        add->set_halign(Gtk::Align::START);
        add->signal_clicked().connect([this] { adding_milestone_ = true; Glib::signal_idle().connect_once([this] { refresh_changes(); }); });
        changes_.append(*add);
        return;
    }
    auto* form = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    form->add_css_class("card");
    form->add_css_class("milestone");
    auto* heading = Gtk::make_managed<Gtk::Label>("New milestone");
    heading->add_css_class("heading");
    heading->set_xalign(0);
    auto entry = [](const char* placeholder) {
        auto* made = Gtk::make_managed<Gtk::Entry>();
        made->set_placeholder_text(placeholder);
        gtk_accessible_update_property(GTK_ACCESSIBLE(made->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, placeholder, -1);
        return made;
    };
    auto* title = entry("Title, for example: Totals include tax");
    auto* files = entry("Files it covers, separated by commas");
    auto* decision = entry("Decision or note (optional)");
    std::vector<Glib::ustring> choices = {"Checked by what I observe"};
    const std::vector<std::string> names = controller_.workflow_names();
    for (const std::string& name : names) choices.push_back("Checked by the workflow " + name);
    auto* checked_by = Gtk::make_managed<Gtk::DropDown>(choices);
    gtk_accessible_update_property(GTK_ACCESSIBLE(checked_by->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "How it is checked", -1);
    form->append(*heading);
    form->append(*title);
    form->append(*files);
    form->append(*decision);
    form->append(*checked_by);
    auto after = std::make_shared<std::vector<std::pair<std::string, Gtk::CheckButton*>>>();
    if (!rows.empty()) {
        form->append(*dim_label("Comes after"));
        for (const MilestoneStatus& row : rows) {
            auto* box = Gtk::make_managed<Gtk::CheckButton>(row.item.title);
            after->push_back({row.item.id, box});
            form->append(*box);
        }
    }
    auto* buttons = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* add = Gtk::make_managed<Gtk::Button>("Add");
    add->add_css_class("suggested-action");
    auto* cancel = Gtk::make_managed<Gtk::Button>("Cancel");
    add->signal_clicked().connect([this, title, files, decision, checked_by, names, after] {
        Milestone item;
        item.title = title->get_text();
        item.decision = decision->get_text();
        for (const std::string& path : split(files->get_text(), ',', false)) {
            if (!trim(path).empty()) item.files.push_back(trim(path));
        }
        const guint chosen = checked_by->get_selected();
        if (chosen >= 1 && chosen - 1 < names.size()) item.workflow = names[chosen - 1];
        for (const auto& [id, box] : *after) {
            if (box->get_active()) item.depends_on.push_back(id);
        }
        Glib::signal_idle().connect_once([this, item] {
            if (controller_.put_milestone(item)) { adding_milestone_ = false; changes_shown_.clear(); refresh_changes(); }
        });
    });
    cancel->signal_clicked().connect([this] { adding_milestone_ = false; Glib::signal_idle().connect_once([this] { refresh_changes(); }); });
    buttons->append(*add);
    buttons->append(*cancel);
    form->append(*buttons);
    changes_.append(*form);
}

void MainWindow::refresh_changes() {
    if (!changes_toggle_.get_active()) return;
    const Session* session = controller_.session();
    std::vector<FileChange> merged;
    if (plan_shown_) { refresh_plan(); return; }
    std::string fingerprint = std::string(controller_.running ? "run|" : "idle|") + (controller_.reviewing ? "reviewing|" : "");
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
    auto* heading = panel_switch();
    // Icons keep the bar inside a narrow panel; each has a tooltip and a name for screen readers.
    auto* undo = Gtk::make_managed<Gtk::Button>();
    undo->set_icon_name("edit-undo-symbolic");
    undo->set_tooltip_text("Undo: put back everything the latest request changed");
    gtk_accessible_update_property(GTK_ACCESSIBLE(undo->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Undo", -1);
    undo->set_sensitive(!controller_.running);
    undo->signal_clicked().connect([this] { controller_.undo(); });
    auto* redo = Gtk::make_managed<Gtk::Button>();
    redo->set_icon_name("edit-redo-symbolic");
    redo->set_tooltip_text("Redo: reapply what was undone");
    gtk_accessible_update_property(GTK_ACCESSIBLE(redo->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Redo", -1);
    redo->set_sensitive(!controller_.running);
    redo->signal_clicked().connect([this] { controller_.undo(true); });
    auto* review = Gtk::make_managed<Gtk::Button>(controller_.reviewing ? "Reviewing…" : "Review");
    review->set_tooltip_text("Have the model read these changes and pin what it finds as comments");
    review->set_sensitive(!controller_.running && !controller_.reviewing && !merged.empty());
    review->signal_clicked().connect([this] { controller_.review_changes(); });
    bar->append(*heading);
    bar->append(*review);
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
            line->set_hexpand(true);
            line->add_css_class("monospace");
            line->add_css_class("caption");
            auto* whole = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 2);
            if (row.kind == '+') whole->add_css_class("diff-added");
            else if (row.kind == '-') whole->add_css_class("diff-removed");
            else if (row.kind == 0) line->add_css_class("dim-label");
            if (row.kind != 0) {
                // A small button beside each line opens a place to write a comment about it.
                const std::string place = change.path + " · " + (row.kind == '-' ? "original" : "updated") + " line " + std::to_string(row.number);
                auto* pin = Gtk::make_managed<Gtk::MenuButton>();
                pin->set_icon_name("list-add-symbolic");
                pin->add_css_class("flat");
                pin->add_css_class("comment-pin");
                pin->set_tooltip_text("Comment on this line");
                gtk_accessible_update_property(GTK_ACCESSIBLE(pin->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Comment on " + place).c_str(), -1);
                auto* popover = Gtk::make_managed<Gtk::Popover>();
                auto* form = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
                form->set_margin(10);
                auto* title_label = Gtk::make_managed<Gtk::Label>("Comment on " + place);
                title_label->add_css_class("heading");
                title_label->set_xalign(0);
                auto* entry = Gtk::make_managed<Gtk::Entry>();
                entry->set_placeholder_text("What should change here?");
                entry->set_width_chars(40);
                auto* add = Gtk::make_managed<Gtk::Button>("Add Comment");
                add->add_css_class("suggested-action");
                add->set_halign(Gtk::Align::END);
                auto submit = [this, popover, entry, path = change.path, row] {
                    if (trim(entry->get_text()).empty()) return;
                    const std::string note = entry->get_text();
                    popover->popdown();
                    controller_.add_comment(path, row, note);
                };
                entry->signal_activate().connect(submit);
                add->signal_clicked().connect(submit);
                form->append(*title_label);
                form->append(*entry);
                form->append(*dim_label("Sent with your next request."));
                form->append(*add);
                popover->set_child(*form);
                pin->set_popover(*popover);
                whole->append(*pin);
            } else {
                auto* gap = Gtk::make_managed<Gtk::Box>();
                gap->set_size_request(24, -1);
                whole->append(*gap);
            }
            whole->append(*line);
            lines->append(*whole);
        }
        auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
        scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::NEVER);
        scroll->set_child(*lines);
        changes_.append(*scroll);
    }
    auto* note = dim_label("Use the + beside a line to pin a comment to it; comments go with your next request. Undo puts back everything the latest request changed, including files its commands created, changed or deleted.");
    note->add_css_class("caption");
    changes_.append(*note);
}

}  // namespace mason::app
