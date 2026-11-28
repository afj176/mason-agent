#include "harness.hpp"
#include "window.hpp"

#include "background.hpp"
#include "delivery.hpp"

#include <array>

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

Gtk::Label* text_label(const std::string& text, bool selectable) {
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

namespace {

/// The endings that get a card at the end of the transcript: the ones the user should understand before going on.
bool has_end_card(const std::string& code) {
    return code == "model_loop" || code == "model_stuck" || code == "step_limit" || code == "time_limit" || code == "model_unavailable" || code == "blocked";
}

/// At the end of a request that a loop, a limit, the model server or the project's checks ended: a calm card in the
/// ending's tone (warning, or danger only when the model server is gone), never a failure for what the model did.
Gtk::Widget* ending_card(const Session& session, const EndingStyle& look, const std::function<void()>& review) {
    const RunEnding& ending = *session.ending;
    auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 12);
    card->add_css_class("ending-card");
    card->add_css_class(look.tone == "danger" ? "tone-danger" : "tone-warning");
    card->set_margin_start(38);
    auto* glyph = Gtk::make_managed<Gtk::Image>();
    glyph->set_from_icon_name(ending.code == "model_loop" ? "view-refresh-symbolic"
                              : ending.code == "step_limit" || ending.code == "time_limit" ? "media-playback-pause-symbolic"
                              : ending.code == "model_unavailable" ? "network-offline-symbolic" : "action-unavailable-symbolic");
    glyph->set_pixel_size(20);
    glyph->set_valign(Gtk::Align::START);
    glyph->add_css_class("ending-glyph");
    auto* text = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 4);
    text->set_hexpand(true);
    auto* title = Gtk::make_managed<Gtk::Label>(look.headline);
    title->set_xalign(0);
    title->add_css_class("ending-title");
    text->append(*title);
    const std::string body = ending.code == "model_loop" && session.repeat_stop ? session.repeat_stop->explanation() : session.detail;
    if (!body.empty()) text->append(*text_label(body));
    if (const std::string hint = ending_hint(ending.code, false); !hint.empty()) {
        auto* line = dim_label(hint);
        line->add_css_class("caption");
        text->append(*line);
    }
    if (ending.kept > 0) {
        auto* kept = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
        auto* count = dim_label(std::to_string(ending.kept) + (ending.kept == 1 ? " file kept ·" : " files kept ·"));
        count->add_css_class("caption");
        count->set_hexpand(false);
        auto* open = Gtk::make_managed<Gtk::Button>("Review changes");
        open->add_css_class("flat");
        open->add_css_class("changed-link");
        open->add_css_class("caption");
        open->signal_clicked().connect(review);
        kept->append(*count);
        kept->append(*open);
        text->append(*kept);
    }
    card->append(*glyph);
    card->append(*text);
    return card;
}

}  // namespace

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
    brand->append(*app_icon(26));
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
        // /note or /notes alone opens the Notes tray, and /notes clear asks there first.
        const std::string typed = to_lower(trim(text));
        if (typed == "/note" || typed == "/notes" || typed == "/notes clear") {
            draft_.get_buffer()->set_text("");
            open_notes(true);
            if (typed == "/notes clear" && !notes_.empty()) {
                notes_confirming_ = true;
                refresh_notes();
            }
            return;
        }
        draft_.get_buffer()->set_text("");
        if (notes_open()) open_notes(false);
        controller_.send(text);
    });
    stop_.add_css_class("destructive-action");
    stop_.signal_clicked().connect([this] { controller_.stop(); });
    // While a request runs, the request box sends follow-ups: queued for afterwards, or steering it now.
    auto follow_up = [this](bool steer) {
        const std::string text = draft_.get_buffer()->get_text();
        if (trim(text).empty() || !controller_.running) return;
        draft_.get_buffer()->set_text("");
        if (notes_open()) open_notes(false);
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
    // Room for the Notes tab, which sits on the request box's top edge.
    composer->set_margin_top(30);
    composer_card_ = composer_card;

    // The session's state: a pose for what Mason is doing or what it needs, beside the state in words.
    state_row_.add_css_class("state-row");
    state_row_.set_margin_start(24);
    state_row_.set_margin_end(24);
    state_pose_ = pose_picture(pose::planning, 46, "planning");
    state_label_.set_xalign(0);
    state_label_.set_hexpand(true);
    state_label_.set_ellipsize(Pango::EllipsizeMode::END);
    ending_pill_.add_css_class("state-pill");
    ending_pill_.set_valign(Gtk::Align::CENTER);
    ending_pill_.set_visible(false);
    state_row_.append(*state_pose_);
    state_row_.append(ending_pill_);
    state_row_.append(state_label_);
    state_row_.set_visible(false);
    auto* conversation = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    conversation->append(transcript_scroll_);
    conversation->append(state_row_);
    conversation->append(approval_slot_);
    conversation->append(*composer);
    conversation->set_size_request(420, -1);

    changes_.set_margin(12);
    changes_.set_margin_top(4);
    changes_scroll_.set_child(changes_);
    changes_scroll_.set_vexpand(true);
    surface_.add_css_class("surface-header");
    side_.append(surface_);
    side_.append(changes_scroll_);
    side_.set_size_request(380, -1);
    side_.set_visible(false);
    conversation_overlay_.set_child(*conversation);
    build_notes();
    auto* paned = Gtk::make_managed<Gtk::Paned>(Gtk::Orientation::HORIZONTAL);
    paned->set_start_child(conversation_overlay_);
    paned->set_end_child(side_);
    paned->set_resize_start_child(true);
    paned->set_shrink_start_child(false);
    paned->set_shrink_end_child(false);

    auto* welcome = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 14);
    welcome->set_valign(Gtk::Align::CENTER);
    welcome->set_halign(Gtk::Align::CENTER);
    welcome->set_margin(32);
    // The first launch shows the bear and the level-bot arriving; once projects exist, the open-project pose.
    welcome_pose_ = pose_picture(pose::first_launch, 148, "welcome");
    welcome->append(*welcome_pose_);
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
    changes_toggle_.set_tooltip_text("Show Moments: what Mason did, and the files, changes and pages it made (Ctrl+Alt+V)");
    changes_toggle_.signal_toggled().connect([this] {
        side_.set_visible(changes_toggle_.get_active());
        refresh_changes();
    });
    model_button_.signal_clicked().connect([this] { adw_view_stack_set_visible_child_name(stack_, "models"); });
    models_.open_chat.connect([this] { adw_view_stack_set_visible_child_name(stack_, "chat"); });

    // Appearance: follow the system, or always light or dark, and the theme family. Settings has the same choices with previews.
    auto appearance_menu = Gio::Menu::create();
    auto modes = Gio::Menu::create();
    modes->append("Follow System", "look.appearance::system");
    modes->append("Light", "look.appearance::light");
    modes->append("Dark", "look.appearance::dark");
    appearance_menu->append_section(modes);
    auto families = Gio::Menu::create();
    for (const theme::Family& item : theme::families()) families->append(item.name, "look.family::" + item.slug());
    appearance_menu->append_section("Theme", families);
    auto look = Gio::SimpleActionGroup::create();
    auto mode_name = [] { const Appearance now = appearance(); return now == Appearance::Light ? "light" : now == Appearance::Dark ? "dark" : "system"; };
    look->add_action_radio_string("appearance", [](const Glib::ustring& choice) {
        set_appearance(choice == "light" ? Appearance::Light : choice == "dark" ? Appearance::Dark : Appearance::System);
    }, mode_name());
    look->add_action_radio_string("family", [](const Glib::ustring& choice) { set_family(theme::find(choice)); }, family().slug());
    // Either place can change the look; both show the current choice.
    appearance_changed().connect([look, mode_name] {
        if (auto action = std::dynamic_pointer_cast<Gio::SimpleAction>(look->lookup_action("appearance"))) action->set_state(Glib::Variant<Glib::ustring>::create(mode_name()));
        if (auto action = std::dynamic_pointer_cast<Gio::SimpleAction>(look->lookup_action("family"))) action->set_state(Glib::Variant<Glib::ustring>::create(family().slug()));
    });
    auto* appearance_button = Gtk::make_managed<Gtk::MenuButton>();
    appearance_button->set_icon_name("weather-clear-night-symbolic");
    appearance_button->set_tooltip_text("Appearance");
    appearance_button->set_menu_model(appearance_menu);
    appearance_button->insert_action_group("look", look);

    // The session bar's chips: the project's published pull request, and its background tasks while any is going.
    // Each opens its section of Settings.
    pr_chip_.set_label("Pull request");
    pr_chip_.add_css_class("session-chip");
    pr_chip_.set_tooltip_text("This project's pull request and its checks, in Settings");
    pr_chip_.set_visible(false);
    pr_chip_.signal_clicked().connect([this] {
        adw_view_stack_set_visible_child_name(stack_, "settings");
        settings_.show_section("pull-request");
    });
    tasks_chip_.add_css_class("session-chip");
    tasks_chip_.add_css_class("session-chip-live");
    tasks_chip_.set_tooltip_text("Background tasks running for this project, in Settings");
    tasks_chip_.set_visible(false);
    tasks_chip_.signal_clicked().connect([this] {
        adw_view_stack_set_visible_child_name(stack_, "settings");
        settings_.show_section("background");
    });
    chips_timer_ = Glib::signal_timeout().connect_seconds([this] { refresh_chips(true); return true; }, 4);

    GtkWidget* header = adw_header_bar_new();
    adw_header_bar_set_title_widget(ADW_HEADER_BAR(header), switcher);
    adw_header_bar_pack_start(ADW_HEADER_BAR(header), GTK_WIDGET(pr_chip_.gobj()));
    adw_header_bar_pack_start(ADW_HEADER_BAR(header), GTK_WIDGET(tasks_chip_.gobj()));
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
    // Chats that mason chat made or continued are read again when Chat is opened and when the window is focused.
    g_signal_connect(stack_, "notify::visible-child-name", G_CALLBACK(+[](GObject* stack, GParamSpec*, gpointer self) {
        const char* page = adw_view_stack_get_visible_child_name(ADW_VIEW_STACK(stack));
        if (page && std::string(page) == "chat") static_cast<Controller*>(self)->reload_chats();
    }), &controller_);
    g_signal_connect(window_, "notify::is-active", G_CALLBACK(+[](GObject* window, GParamSpec*, gpointer self) {
        if (gtk_window_is_active(GTK_WINDOW(window))) static_cast<Controller*>(self)->reload_chats();
    }), &controller_);

    toasts_ = ADW_TOAST_OVERLAY(adw_toast_overlay_new());
    adw_toast_overlay_set_child(toasts_, split_);
    adw_application_window_set_content(window_, GTK_WIDGET(toasts_));

    controller_.changed.connect([this] { refresh(); });
    // The model may show the user a file at a line, or one of the project's pages.
    controller_.present.connect([this](const std::string& kind, const std::string& path, int line) {
        if (kind == "page") show_page(path);
        else show_file(path, line);
    });
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
    controller_.draft_suggested.connect([this](const std::string& text) { draft_.get_buffer()->set_text(text); draft_.grab_focus(); });
    // Ctrl+K opens the search box from anywhere in the window.
    auto shortcuts = Gtk::EventControllerKey::create();
    shortcuts->set_propagation_phase(Gtk::PropagationPhase::CAPTURE);
    shortcuts->signal_key_pressed().connect([this](guint key, guint, Gdk::ModifierType state) {
        if ((key == GDK_KEY_k || key == GDK_KEY_K) && (state & Gdk::ModifierType::CONTROL_MASK) == Gdk::ModifierType::CONTROL_MASK) {
            show_palette();
            return true;
        }
        // Ctrl+Shift+N opens and closes the Notes tray on the Code page; Esc closes it.
        const bool on_code = adw_view_stack_get_visible_child(stack_) == GTK_WIDGET(session_stack_.gobj()) && controller_.session();
        const Gdk::ModifierType chord = Gdk::ModifierType::CONTROL_MASK | Gdk::ModifierType::SHIFT_MASK;
        if ((key == GDK_KEY_n || key == GDK_KEY_N) && (state & chord) == chord && on_code) {
            open_notes(!notes_open());
            return true;
        }
        // Ctrl+Shift+M compacts the current session's conversation, as /compact does.
        if ((key == GDK_KEY_m || key == GDK_KEY_M) && (state & chord) == chord && on_code) {
            controller_.compact();
            return true;
        }
        if (key == GDK_KEY_Escape && notes_open() && notes_editing_.empty() && !notes_about_popover_.get_visible()) {
            open_notes(false);
            return true;
        }
        // Ctrl+Alt+V shows and hides the side panel (Moments), as it shows and hides the viewer on Windows.
        const Gdk::ModifierType viewer = Gdk::ModifierType::CONTROL_MASK | Gdk::ModifierType::ALT_MASK;
        if ((key == GDK_KEY_v || key == GDK_KEY_V) && (state & viewer) == viewer && on_code) {
            changes_toggle_.set_active(!changes_toggle_.get_active());
            return true;
        }
        return false;
    }, false);
    Glib::wrap(GTK_WINDOW(window_), true)->add_controller(shortcuts);
    controller_.notice.connect([this](const std::string& message) {
        // Several lines do not fit a toast: a command's answer is shown in a dialog, where it can be read and copied.
        const size_t first = message.find('\n');
        if (first != std::string::npos) {
            auto dialog = Gtk::AlertDialog::create(message.substr(0, first));
            dialog->set_detail(message.substr(first + 1));
            dialog->set_modal(true);
            dialog->show(*Glib::wrap(GTK_WINDOW(window_), true));
            return;
        }
        AdwToast* toast = adw_toast_new(message.c_str());
        adw_toast_set_timeout(toast, 6);
        adw_toast_overlay_add_toast(toasts_, toast);
    });
    refresh();
}

void MainWindow::present() { gtk_window_present(GTK_WINDOW(window_)); }

void MainWindow::refresh_chips(bool force) {
    const Session* session = controller_.session();
    const std::string project = session ? session->workspace : "";
    // The draft and the tasks are files on disk: read when the project changes and every few seconds, not on every update.
    if (!force && project == chips_project_) return;
    chips_project_ = project;
    std::optional<std::string> pull_request;
    int active = 0;
    if (!project.empty() && !controller_.home().empty()) {
        try {
            const Workspace workspace(project);
            pull_request = delivery::pull_request(delivery::current(workspace, controller_.home()));
            active = background::active_count(background::all(&workspace, controller_.home()));
        } catch (const std::exception&) {
        }
    }
    pr_chip_.set_visible(pull_request.has_value());
    if (pull_request) pr_chip_.set_tooltip_text("Pull request " + *pull_request + " and its checks, in Settings");
    tasks_chip_.set_visible(active > 0);
    tasks_chip_.set_label(background::chip_label(active));
}

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
    load_notes(false);
    refresh_notes();
    refresh_transcript();
    refresh_composer();
    if (const Session* now = controller_.session(); (now ? now->id : "") != panel_session_) {
        // Another session opens the side panel on Moments, its feed.
        panel_session_ = now ? now->id : "";
        panel_ = "moments";
        activity_scene_.clear();
        changes_shown_.clear();
    }
    refresh_changes();
    refresh_chips(false);
    models_.refresh();
    chat_.refresh();
    settings_.refresh();
    const Session* session = controller_.session();
    session_stack_.set_visible_child(session ? "session" : "empty");
    // Before any project, the first-launch pose; once there are projects to go back to, the open-project pose.
    const std::string welcome = controller_.sessions().empty() ? pose::first_launch : pose::open_project;
    if (welcome_pose_ && welcome != welcome_shown_) {
        welcome_shown_ = welcome;
        set_pose(*welcome_pose_, welcome, 148, welcome == pose::first_launch ? "welcome" : "open a project");
    }
    refresh_state();
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

void MainWindow::refresh_state() {
    const Session* session = controller_.session();
    std::string mood, meaning;
    const bool restored = std::chrono::steady_clock::now() - controller_.restored_at < std::chrono::seconds(30);
    if (session && !session->turns.empty()) {
        if (controller_.approval) {
            // The approval card below carries the awaiting-approval pose, so the row stays out of the way.
        } else if (controller_.running) {
            // The latest tool call that is still running says what Mason is doing; between calls it is planning.
            std::string tool;
            for (auto it = controller_.live_activities.rbegin(); it != controller_.live_activities.rend(); ++it) {
                if (it->state == "running") { tool = it->tool; break; }
            }
            mood = pose_for_tool(tool);
            meaning = mood == pose::reading ? "reading files" : mood == pose::editing ? "editing" : mood == pose::checks ? "running checks" : "planning";
        } else if (restored) {
            mood = pose::restored;
            meaning = "files restored";
        } else if (session->state == "Awaiting review") {
            mood = pose::approval;
            meaning = "waiting for your decision";
        } else if (const auto look = session->ending ? ending_style(session->ending->code) : std::nullopt; look && session->ending->code != "done") {
            // How the request ended picks the pose: a question waits for the answer, a failure shows the failed pose, and
            // anything the model or a limit caused stays calm.
            const std::string& code = session->ending->code;
            if (code == "question") { mood = pose::listening; meaning = "waiting for your answer"; }
            else if (code == "unverified" || code == "model_unavailable" || code == "failed") { mood = pose::failed; meaning = look->headline; }
            else { mood = pose::connected; meaning = look->headline; }
        } else if (session->state == "Needs your answer") {
            mood = pose::listening;
            meaning = "waiting for your answer";
        } else if (session->state == "Completed") {
            // Ready to review when the session left changes in place; connected and calm when it only answered.
            const bool changes = std::any_of(session->turns.begin(), session->turns.end(), [](const Turn& turn) { return !turn.undone && !turn.changes.empty(); });
            mood = changes ? pose::review : pose::connected;
            meaning = changes ? "ready to review" : "finished";
        } else if (session->state == "Failed" || session->state == "Blocked" || session->state == "Needs verification") {
            mood = pose::failed;
            meaning = "a check failed";
        }
    }
    std::string words = controller_.running ? (controller_.live_status.empty() ? "Working" : controller_.live_status) : session ? session->state : "";
    // Once a request has ended, a pill says how, in the ending's tone, and the text beside it says what happened.
    const std::optional<EndingStyle> look = !controller_.running && !controller_.approval && session && session->ending ? ending_style(session->ending->code) : std::nullopt;
    ending_pill_.set_visible(look.has_value());
    if (look) {
        static const char* const tones[] = {"state-done", "state-recheck", "state-ready", "state-blocked", "state-danger"};
        for (const char* tone : tones) ending_pill_.remove_css_class(tone);
        ending_pill_.add_css_class(look->tone == "success" ? "state-done" : look->tone == "warning" ? "state-recheck" : look->tone == "accent" ? "state-ready" : look->tone == "danger" ? "state-danger" : "state-blocked");
        ending_pill_.set_text(look->glyph + " " + look->headline);
        words = session->detail;
    }
    words = utf8_prefix(replace_all(words, "\n", " "), 160);
    state_row_.set_visible(!mood.empty());
    state_label_.set_text(words);
    state_label_.set_tooltip_text(look ? session->detail : "");
    if (!mood.empty() && mood != state_shown_) {
        state_shown_ = mood;
        set_pose(*state_pose_, mood, 46, meaning);
    }
    // The restored pose lasts thirty seconds; look again then.
    if (restored && !restored_timer_.connected()) {
        restored_timer_ = Glib::signal_timeout().connect_seconds([this] { refresh_state(); return false; }, 31);
    }
}

void MainWindow::refresh_sidebar() {
    std::string fingerprint = controller_.selection + "|";
    for (const Session& item : controller_.sessions()) fingerprint += item.id + item.title + item.state + (item.ending ? item.ending->code : "") + "|";
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
            // An ended request says how it ended: a danger ending in the error colour, one that needs the user in warning.
            const std::optional<EndingStyle> look = item.ending && item.state != "Running" ? ending_style(item.ending->code) : std::nullopt;
            auto* state = Gtk::make_managed<Gtk::Label>(look ? look->headline : item.state);
            state->set_xalign(0);
            state->add_css_class("session-state");
            if (look && look->tone == "danger") state->add_css_class("error");
            else if (look && (look->tone == "warning" || look->tone == "accent")) state->add_css_class("warning");
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
            // Compact works on the session it is chosen from, as /compact does on the current one.
            auto compact_item = Gio::MenuItem::create("Compact Conversation", "row.compact");
            compact_item->set_attribute_value("accel", Glib::Variant<Glib::ustring>::create("<Control><Shift>m"));
            menu->append_item(compact_item);
            menu->append("Export Session…", "row.export");
            menu->append("Export a Sanitized Copy…", "row.sanitized");
            menu->append("Import a Session Into This Project…", "row.import");
            menu->append("Delete Session", "row.delete");
            auto actions = Gio::SimpleActionGroup::create();
            const std::string id = item.id, named = item.title, folder = item.workspace;
            actions->add_action("delete", [this, id] { controller_.remove_session(id); });
            // After the menu closes: selecting the session rebuilds this list, row and menu included.
            actions->add_action("compact", [this, id] { Glib::signal_idle().connect_once([this, id] { controller_.select(id); controller_.compact(); }); });
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
        // A repeat Mason skipped is not a failure: it shows the repeat glyph, not the error mark.
        const bool skipped = activity.state == "skipped";
        icon->set_from_icon_name(activity.state == "completed" ? "emblem-ok-symbolic" : activity.state == "denied" ? "action-unavailable-symbolic" : skipped ? "view-refresh-symbolic" : "dialog-error-symbolic");
        icon->add_css_class(activity.state == "completed" ? "success" : activity.state == "denied" || skipped ? "warning" : "error");
        if (skipped) icon->set_tooltip_text("Skipped: the model asked for this exact call three times in a row");
        row->append(*icon);
    }
    auto* name = Gtk::make_managed<Gtk::Label>();
    name->set_markup("<b>" + escape(label_of(activity.tool)) + "</b>");
    auto* subject = dim_label(replace_all(activity.subject, "\n", " ") + (activity.state == "denied" ? "  · declined" : activity.state == "skipped" ? "  · Skipped · repeated" : ""));
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
        // The box may already have let go of the menu (and its wrapper) by now, so only a menu still attached is released.
        g_signal_connect(request->gobj(), "destroy", G_CALLBACK(+[](GtkWidget* owner, gpointer) {
            for (GtkWidget* child = gtk_widget_get_first_child(owner); child;) {
                GtkWidget* next = gtk_widget_get_next_sibling(child);
                if (GTK_IS_POPOVER(child)) gtk_widget_unparent(child);
                child = next;
            }
        }), nullptr);
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
        auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
        auto* label = Gtk::make_managed<Gtk::Label>(derived::summary(receipt));
        label->set_xalign(0);
        label->set_wrap(true);
        label->add_css_class(!ran ? "warning" : good ? "success" : "error");
        if (ran) {
            // Checks that passed show the running-checks pose; a failed check, the check-failed pose.
            row->append(*pose_picture(good ? pose::checks : pose::failed, 48, good ? "checks passed" : "a check failed"));
        } else {
            auto* icon = Gtk::make_managed<Gtk::Image>();
            icon->set_from_icon_name("dialog-information-symbolic");
            icon->add_css_class("warning");
            row->append(*icon);
        }
        row->append(*label);
        row->set_tooltip_text(derived::report(receipt));
        reply->append(*row);
    }
    if (!live && turn.verification) {
        // What Mason found when it ran the project's verify workflow itself.
        const Verification& receipt = *turn.verification;
        const bool good = receipt.verdict == "passed" || receipt.verdict == "fixed";
        const bool quiet = receipt.verdict == "notRun" || receipt.verdict == "failingBefore";
        auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
        auto* label = Gtk::make_managed<Gtk::Label>("Verified by Mason: " + receipt.summary() + (receipt.repairs > 0 ? " · after " + std::to_string(receipt.repairs) + (receipt.repairs == 1 ? " repair" : " repairs") : ""));
        label->set_xalign(0);
        label->set_wrap(true);
        label->add_css_class(good ? "success" : quiet ? "warning" : "error");
        if (quiet) {
            auto* icon = Gtk::make_managed<Gtk::Image>();
            icon->set_from_icon_name("dialog-information-symbolic");
            icon->add_css_class("warning");
            row->append(*icon);
        } else {
            row->append(*pose_picture(good ? pose::checks : pose::failed, 48, good ? "checks passed" : "a check failed"));
        }
        row->append(*label);
        row->set_tooltip_text(receipt.command + (receipt.output.empty() ? "" : "\n\n" + utf8_suffix(receipt.output, 600)));
        reply->append(*row);
    }
    if (!live && (!activities.empty() || !turn.changes.empty())) {
        // What the request amounted to, in one line, and the way to its changes.
        int reads = 0, edits = 0, commands = 0, other = 0, repeats = 0;
        for (const Activity& activity : activities) {
            if (activity.state == "skipped") repeats++;
            if (activity.state != "completed") continue;
            if (activity.tool == "read_file" || activity.tool == "read_lines" || activity.tool == "list_files" || activity.tool == "search_files" || activity.tool == "search_next") reads++;
            else if (activity.tool == "edit_file" || activity.tool == "write_file" || activity.tool == "apply_edits") edits++;
            else if (activity.tool == "run_shell" || activity.tool == "run_command" || activity.tool == "run_workflow" || activity.tool == "start_job" || activity.tool == "start_terminal") commands++;
            else other++;
        }
        std::vector<std::string> parts;
        auto count = [&](int number, const char* one, const char* many) { if (number > 0) parts.push_back(std::to_string(number) + " " + (number == 1 ? one : many)); };
        count(reads, "read", "reads");
        count(edits, "edit", "edits");
        count(commands, "command", "commands");
        count(other, "other step", "other steps");
        count(repeats, "repeat skipped", "repeats skipped");
        auto* line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
        auto* summary = Gtk::make_managed<Gtk::Label>(parts.empty() ? "No steps completed" : join(parts, " · "));
        summary->add_css_class("summary-line");
        line->append(*summary);
        if (!activities.empty()) {
            // Opens Moments on this request's first scene.
            auto* view = Gtk::make_managed<Gtk::Button>("View in Moments");
            view->add_css_class("flat");
            view->add_css_class("changed-link");
            view->set_tooltip_text("Show what Mason did, step by step, in Moments");
            const std::string first = activities.front().id;
            view->signal_clicked().connect([this, first] { Glib::signal_idle().connect_once([this, first] { show_activity(first); }); });
            line->append(*view);
        }
        if (!turn.changes.empty()) {
            auto* changed = Gtk::make_managed<Gtk::Button>(std::to_string(turn.changes.size()) + (turn.changes.size() == 1 ? " changed file" : " changed files"));
            changed->add_css_class("flat");
            changed->add_css_class("changed-link");
            changed->set_tooltip_text("Show what changed");
            changed->signal_clicked().connect([this] {
                panel_ = "changes";
                changes_shown_.clear();
                if (changes_toggle_.get_active()) Glib::signal_idle().connect_once([this] { refresh_changes(); });
                else changes_toggle_.set_active(true);
            });
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
    heading->set_hexpand(true);
    heading->add_css_class("heading");
    auto* top = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 14);
    top->append(*pose_picture(pose::approval, 70, "waiting for your decision"));
    top->append(*heading);
    card->append(*top);
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
        fingerprint = session->id + "|" + std::to_string(session->turns.size()) + "|" + session->state + "|" + session->detail + "|" + std::to_string(session->activities.size()) + "|" + (controller_.running ? "run" : "idle") +
                      (session->ending ? "|end:" + session->ending->code + ":" + std::to_string(session->ending->kept) : "");
        for (const Turn& turn : session->turns) fingerprint += turn.undone ? "u" : "-";
        for (const Activity& activity : controller_.live_activities) fingerprint += activity.id + activity.state;
        if (controller_.approval) fingerprint += "|ask:" + controller_.approval->title;
        if (!session->suggested_note.empty()) fingerprint += "|note:" + session->suggested_note + (notes_.size() >= project_notes::kLimit ? "full" : "");
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
                // A new session: the bear and the level-bot listen for the first request.
                auto* face = pose_picture(pose::listening, 120, "listening");
                face->set_margin_top(60);
                transcript_.append(*face);
                auto* hint = dim_label("Ask a question about this project, or describe a change.");
                hint->set_halign(Gtk::Align::CENTER);
                transcript_.append(*hint);
            }
            const std::optional<EndingStyle> look = session->ending ? ending_style(session->ending->code) : std::nullopt;
            if (!controller_.running && look && has_end_card(session->ending->code) && !session->turns.empty()) {
                transcript_.append(*ending_card(*session, *look, [this] { changes_toggle_.set_active(true); }));
            } else if (!controller_.running && !session->detail.empty()) {
                auto* detail = dim_label(session->detail);
                if (session->state == "Failed") { detail->remove_css_class("dim-label"); detail->add_css_class("error"); }
                transcript_.append(*detail);
            }
            // A note Mason suggested with this answer, until the user keeps, edits or dismisses it.
            if (!controller_.running && !session->suggested_note.empty() && !session->turns.empty()) transcript_.append(*suggestion_widget(session->suggested_note));
            if (controller_.approval) approval_slot_.append(*approval_widget(*controller_.approval));
        }
        // After the new widgets are laid out, show the newest part of the conversation.
        Glib::signal_idle().connect_once([this] {
            auto adjustment = transcript_scroll_.get_vadjustment();
            adjustment->set_value(adjustment->get_upper());
            // The keyboard belongs in the request box, not on the first line of the conversation.
            if (!controller_.approval && !notes_open() && adw_view_stack_get_visible_child(stack_) == GTK_WIDGET(session_stack_.gobj())) draft_.grab_focus();
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
    // While a request runs, Ctrl+Enter queues and Steer, Queue and Stop take the room: the hint and the Window menu
    // (which cannot change mid-request) give way, so the request box keeps its width and the side panel stays on screen.
    composer_hint_.set_visible(!controller_.running);
    window_button_.set_visible(!controller_.running);
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
    refresh_surface();
    if (panel_ != "moments") {
        // Moments keeps its place only while it stays shown.
        activity_scroll_watch_.disconnect();
        activity_scroll_scene_ = "-";
    }
    const Session* session = controller_.session();
    std::vector<FileChange> merged;
    if (panel_ == "moments") { refresh_activity(); return; }
    if (panel_ == "plan") { refresh_plan(); return; }
    if (panel_ == "files") { refresh_files(); return; }
    if (panel_ == "website") { refresh_website(); return; }
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
    auto* heading = Gtk::make_managed<Gtk::Label>("Changes");
    heading->add_css_class("viewer-where");
    heading->set_xalign(0);
    heading->set_hexpand(true);
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

// ---- the search box over everything ----

void MainWindow::show_palette() {
    std::vector<PaletteItem> items;
    for (const Session& item : controller_.sessions()) items.push_back({"session", item.title, "Session in " + fs::path(item.workspace).filename().string(), item.id});
    for (const std::string& folder : controller_.projects()) items.push_back({"project", fs::path(folder).filename().string(), "Project at " + folder, folder});
    for (const auto& [name, detail] : palette::commands()) items.push_back({"command", name, detail, name});
    if (const Session* current = controller_.session()) {
        items.push_back({"action", "Compact Conversation", "Replace this session's conversation with a summary to free context (Ctrl+Shift+M)", "compact"});
        for (const Definition& command : customization::commands(controller_.home(), current->workspace)) {
            items.push_back({"command", "/" + command.name, command.description.empty() ? "Your command" : command.description, "/" + command.name});
        }
        try {
            for (const std::string& path : Workspace(current->workspace).files(".", 3000).paths) items.push_back({"file", fs::path(path).filename().string(), path, path});
        } catch (const std::exception&) {
        }
    }
    for (const ChatConversation& chat : controller_.chats()) items.push_back({"chat", chat.title, "Chat", chat.id});

    auto* dialog = new Gtk::Window();
    dialog->set_transient_for(*Glib::wrap(GTK_WINDOW(window_), true));
    dialog->set_modal(true);
    dialog->set_title("Search Mason");
    dialog->set_default_size(640, 440);
    auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    auto* entry = Gtk::make_managed<Gtk::SearchEntry>();
    entry->set_margin(12);
    gtk_search_entry_set_placeholder_text(entry->gobj(), "Search sessions, projects, files, commands and chats");
    auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    scroll->set_vexpand(true);
    scroll->set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);
    auto* list = Gtk::make_managed<Gtk::ListBox>();
    list->set_selection_mode(Gtk::SelectionMode::SINGLE);
    list->add_css_class("navigation-sidebar");
    scroll->set_child(*list);
    box->append(*entry);
    box->append(*scroll);
    dialog->set_child(*box);

    const auto shown = std::make_shared<std::vector<PaletteItem>>();
    const auto fill = [items, shown, list, entry] {
        while (Gtk::ListBoxRow* row = list->get_row_at_index(0)) list->remove(*row);
        *shown = palette::matches(items, entry->get_text());
        static const std::map<std::string, std::string> icons = {{"session", "utilities-terminal-symbolic"}, {"project", "folder-symbolic"}, {"file", "text-x-generic-symbolic"},
                                                                 {"command", "system-run-symbolic"}, {"action", "system-run-symbolic"}, {"chat", "user-available-symbolic"}};
        for (const PaletteItem& item : *shown) {
            auto* line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
            line->set_margin(6);
            auto* icon = Gtk::make_managed<Gtk::Image>();
            icon->set_from_icon_name(icons.at(item.kind));
            auto* words = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
            auto* title = Gtk::make_managed<Gtk::Label>(item.title);
            title->set_xalign(0);
            title->set_ellipsize(Pango::EllipsizeMode::END);
            auto* detail = Gtk::make_managed<Gtk::Label>(item.detail);
            detail->set_xalign(0);
            detail->set_ellipsize(Pango::EllipsizeMode::MIDDLE);
            detail->add_css_class("dim-label");
            detail->add_css_class("caption");
            words->append(*title);
            words->append(*detail);
            line->append(*icon);
            line->append(*words);
            list->append(*line);
        }
        if (Gtk::ListBoxRow* first = list->get_row_at_index(0)) list->select_row(*first);
    };
    const auto choose = [this, dialog, shown, list] {
        const Gtk::ListBoxRow* row = list->get_selected_row();
        if (!row || row->get_index() < 0 || static_cast<size_t>(row->get_index()) >= shown->size()) return;
        const PaletteItem item = (*shown)[static_cast<size_t>(row->get_index())];
        dialog->close();
        open_palette_item(item);
    };
    entry->signal_search_changed().connect(fill);
    entry->signal_activate().connect(choose);
    entry->signal_stop_search().connect([dialog] { dialog->close(); });
    list->signal_row_activated().connect([choose](Gtk::ListBoxRow*) { choose(); });
    // The arrow keys move through the list while the typing stays in the box.
    auto keys = Gtk::EventControllerKey::create();
    keys->set_propagation_phase(Gtk::PropagationPhase::CAPTURE);
    keys->signal_key_pressed().connect([list, dialog](guint key, guint, Gdk::ModifierType) {
        if (key == GDK_KEY_Escape) { dialog->close(); return true; }
        if (key != GDK_KEY_Down && key != GDK_KEY_Up) return false;
        const Gtk::ListBoxRow* at = list->get_selected_row();
        const int next = (at ? at->get_index() : -1) + (key == GDK_KEY_Down ? 1 : -1);
        if (Gtk::ListBoxRow* row = list->get_row_at_index(std::max(0, next))) {
            list->select_row(*row);
            row->grab_focus();
        }
        return true;
    }, false);
    entry->add_controller(keys);
    // The window is gone for good once it is closed.
    dialog->signal_hide().connect([dialog] { Glib::signal_idle().connect_once([dialog] { delete dialog; }); });
    fill();
    dialog->present();
    entry->grab_focus();
}

void MainWindow::open_palette_item(const PaletteItem& item) {
    if (item.kind == "chat") {
        controller_.select_chat(item.value);
        adw_view_stack_set_visible_child_name(stack_, "chat");
        return;
    }
    adw_view_stack_set_visible_child_name(stack_, "code");
    if (item.kind == "session") controller_.select(item.value);
    else if (item.kind == "project") {
        // The project's latest session, or a new one when it has none.
        std::string latest;
        for (const Session& each : controller_.sessions()) {
            if (each.workspace == item.value) { latest = each.id; break; }
        }
        if (latest.empty()) controller_.new_session(item.value);
        else controller_.select(latest);
    } else if (item.kind == "file") show_file(item.value, 1);
    else if (item.kind == "command") {
        draft_.get_buffer()->set_text(item.value + " ");
        draft_.grab_focus();
    } else if (item.kind == "action" && item.value == "compact") controller_.compact();
}

}  // namespace mason
