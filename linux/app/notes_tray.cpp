// Notes: a small tab on the top edge of the request box, and the tray that slides up from behind it over the end
// of the conversation. The tray keeps the project's notes, which Mason reads with every request.
#include "window.hpp"

#include <algorithm>

namespace mason::app {

namespace {

Gtk::Label* muted(const std::string& text) {
    auto* label = Gtk::make_managed<Gtk::Label>(text);
    label->set_xalign(0);
    label->set_wrap(true);
    label->set_wrap_mode(Pango::WrapMode::WORD_CHAR);
    label->add_css_class("notes-muted");
    return label;
}

std::string full_text() { return std::to_string(project_notes::kLimit) + " of " + std::to_string(project_notes::kLimit) + ". Remove one to add another."; }

}  // namespace

void MainWindow::build_notes() {
    // ---- the tab ----
    auto* tab = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    auto* tab_label = Gtk::make_managed<Gtk::Label>("Notes");
    notes_count_.add_css_class("notes-badge");
    notes_count_.set_valign(Gtk::Align::CENTER);
    tab->append(*tab_label);
    tab->append(notes_count_);
    notes_tab_.set_child(*tab);
    notes_tab_.add_css_class("notes-tab");
    notes_tab_.set_tooltip_text("Notes Mason reads with every request (Ctrl+Shift+N)");
    notes_tab_.signal_clicked().connect([this] { open_notes(!notes_open()); });

    // ---- the tray: header ----
    notes_tray_.add_css_class("notes-tray");
    auto* header = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* title = Gtk::make_managed<Gtk::Label>("Notes");
    title->add_css_class("notes-title");
    auto* subtitle = muted("Mason reads these with every request");
    subtitle->set_hexpand(true);
    header->append(*title);
    header->append(*subtitle);
    notes_tray_.append(*header);
    notes_running_.set_text("Changes apply from the next request.");
    notes_running_.set_xalign(0);
    notes_running_.add_css_class("notes-muted");
    notes_tray_.append(notes_running_);

    // ---- the add row ----
    auto* add_row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    notes_entry_.set_hexpand(true);
    notes_entry_.set_max_length(static_cast<int>(project_notes::kTextLimit));
    notes_entry_.set_placeholder_text("Something Mason should always know about this project");
    gtk_accessible_update_property(GTK_ACCESSIBLE(notes_entry_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "New note", -1);
    notes_entry_.signal_activate().connect([this] { add_note(); });
    notes_entry_.signal_changed().connect([this] { notes_add_.set_sensitive(!trim(notes_entry_.get_text()).empty() && notes_.size() < project_notes::kLimit); });
    notes_about_label_.add_css_class("monospace");
    notes_about_label_.set_ellipsize(Pango::EllipsizeMode::START);
    notes_about_label_.set_max_width_chars(22);
    auto* drop_about = Gtk::make_managed<Gtk::Button>();
    drop_about->set_icon_name("window-close-symbolic");
    drop_about->add_css_class("flat");
    drop_about->add_css_class("notes-icon");
    drop_about->set_tooltip_text("Not about a file");
    drop_about->signal_clicked().connect([this] {
        notes_about_.clear();
        notes_about_chip_.set_visible(false);
    });
    notes_about_chip_.add_css_class("notes-about-chip");
    notes_about_chip_.set_valign(Gtk::Align::CENTER);
    notes_about_chip_.append(notes_about_label_);
    notes_about_chip_.append(*drop_about);
    notes_about_chip_.set_visible(false);
    // The file is a path in the project; the file open in the side panel is offered first.
    auto* about_box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    about_box->set_margin(10);
    auto* about_title = Gtk::make_managed<Gtk::Label>("The file this note is about");
    about_title->set_xalign(0);
    about_title->add_css_class("heading");
    notes_about_entry_.set_placeholder_text("Path in the project, like src/app.py");
    notes_about_entry_.set_width_chars(34);
    notes_about_entry_.add_css_class("monospace");
    auto* about_hint = muted("Mason stops sending the note once that file changes.");
    about_hint->set_max_width_chars(36);
    auto* use = Gtk::make_managed<Gtk::Button>("Use This File");
    use->add_css_class("suggested-action");
    use->set_halign(Gtk::Align::END);
    auto choose = [this] {
        const std::string path = trim(notes_about_entry_.get_text());
        if (path.empty()) return;
        try {
            if (!Workspace(notes_root_).exists(path)) throw Error(path + " is not a file in the project.");
        } catch (const std::exception& error) {
            notes_toast(error.what(), nullptr);
            return;
        }
        notes_about_ = path;
        notes_about_label_.set_text(path);
        notes_about_chip_.set_visible(true);
        notes_about_popover_.popdown();
        notes_entry_.grab_focus();
    };
    use->signal_clicked().connect(choose);
    notes_about_entry_.signal_activate().connect(choose);
    about_box->append(*about_title);
    about_box->append(notes_about_entry_);
    about_box->append(*about_hint);
    about_box->append(*use);
    notes_about_popover_.set_child(*about_box);
    notes_about_popover_.signal_show().connect([this] {
        notes_about_entry_.set_text(!notes_about_.empty() ? notes_about_ : file_open_);
        notes_about_entry_.grab_focus();
    });
    notes_about_button_.set_label("About a file…");
    notes_about_button_.add_css_class("notes-small");
    notes_about_button_.set_popover(notes_about_popover_);
    notes_about_button_.set_valign(Gtk::Align::CENTER);
    notes_add_.add_css_class("suggested-action");
    notes_add_.set_valign(Gtk::Align::CENTER);
    notes_add_.set_sensitive(false);
    notes_add_.signal_clicked().connect([this] { add_note(); });
    add_row->append(notes_entry_);
    add_row->append(notes_about_chip_);
    add_row->append(notes_about_button_);
    add_row->append(notes_add_);
    notes_tray_.append(*add_row);

    // ---- the empty state ----
    notes_empty_.set_margin_top(6);
    notes_empty_.set_margin_bottom(4);
    notes_empty_.append(*pose_picture(pose::planning, 76, "planning"));
    auto* empty_title = Gtk::make_managed<Gtk::Label>("Nothing to remember yet");
    empty_title->add_css_class("notes-title");
    notes_empty_.append(*empty_title);
    auto* empty_line = muted("Notes are facts Mason should know every time: how to run things, where things live, what not to touch.");
    empty_line->set_justify(Gtk::Justification::CENTER);
    empty_line->set_xalign(0.5);
    empty_line->set_max_width_chars(60);
    empty_line->set_halign(Gtk::Align::CENTER);
    notes_empty_.append(*empty_line);
    auto* starters = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    starters->set_halign(Gtk::Align::CENTER);
    for (const char* text : {"We use pnpm, not npm", "Tests live in tests/", "Keep the public API stable"}) {
        auto* chip = Gtk::make_managed<Gtk::Button>(text);
        chip->add_css_class("chip");
        chip->add_css_class("notes-starter");
        chip->signal_clicked().connect([this, text] {
            notes_entry_.set_text(text);
            notes_entry_.grab_focus();
            notes_entry_.set_position(-1);
        });
        starters->append(*chip);
    }
    notes_empty_.append(*starters);
    notes_tray_.append(notes_empty_);

    // ---- the list: about five notes show, then it scrolls ----
    notes_list_.set_valign(Gtk::Align::START);
    notes_scroll_.set_child(notes_list_);
    notes_scroll_.set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);
    notes_scroll_.set_propagate_natural_height(true);
    notes_scroll_.set_max_content_height(5 * 64);
    notes_tray_.append(notes_scroll_);

    // ---- the footer, and the confirmation that takes its place ----
    notes_sent_.set_xalign(0);
    notes_sent_.set_hexpand(true);
    notes_sent_.add_css_class("notes-muted");
    auto* clear_all = Gtk::make_managed<Gtk::Button>("Clear all");
    clear_all->add_css_class("flat");
    clear_all->add_css_class("notes-link");
    clear_all->signal_clicked().connect([this] {
        notes_confirming_ = true;
        refresh_notes();
    });
    notes_footer_.append(notes_sent_);
    notes_footer_.append(*clear_all);
    notes_tray_.append(notes_footer_);
    notes_confirm_.add_css_class("notes-confirm");
    notes_confirm_label_.set_xalign(0);
    notes_confirm_label_.set_hexpand(true);
    notes_confirm_label_.set_wrap(true);
    auto* clear = Gtk::make_managed<Gtk::Button>("Clear");
    clear->add_css_class("destructive-action");
    clear->signal_clicked().connect([this] {
        const std::vector<ProjectNote> cleared = notes_;
        const std::string root = notes_root_;
        notes_confirming_ = false;
        try {
            project_notes::clear(root, controller_.home());
        } catch (const std::exception& error) {
            notes_toast(error.what(), nullptr);
            return;
        }
        notes_toast(std::to_string(cleared.size()) + (cleared.size() == 1 ? " note cleared" : " notes cleared"), [this, cleared, root] {
            try {
                project_notes::restore(cleared, root, controller_.home());
            } catch (const std::exception& error) {
                notes_toast(error.what(), nullptr);
            }
            notes_changed();
        });
        notes_changed();
    });
    auto* keep = Gtk::make_managed<Gtk::Button>("Keep");
    keep->signal_clicked().connect([this] {
        notes_confirming_ = false;
        refresh_notes();
    });
    notes_confirm_.append(notes_confirm_label_);
    notes_confirm_.append(*clear);
    notes_confirm_.append(*keep);
    notes_confirm_.set_visible(false);
    notes_tray_.append(notes_confirm_);

    notes_revealer_.set_child(notes_tray_);
    notes_revealer_.set_transition_type(Gtk::RevealerTransitionType::SLIDE_UP);
    notes_revealer_.set_transition_duration(220);
    notes_revealer_.set_reveal_child(false);

    // The tray and the tab sit on the request box's top edge, wherever that is; the conversation under them does not move.
    conversation_overlay_.add_overlay(notes_revealer_);
    conversation_overlay_.add_overlay(notes_tab_);
    conversation_overlay_.signal_get_child_position().connect([this](Gtk::Widget* child, Gdk::Rectangle& place) {
        if (!composer_card_ || (child != &notes_revealer_ && child != &notes_tab_)) return false;
        graphene_rect_t card;
        if (!gtk_widget_compute_bounds(GTK_WIDGET(composer_card_->gobj()), GTK_WIDGET(conversation_overlay_.gobj()), &card)) return false;
        // The tray is a little narrower than the request box, so it meets the straight part of its top edge.
        const int left = static_cast<int>(card.origin.x) + 14, width = std::max(1, static_cast<int>(card.size.width) - 28), top = static_cast<int>(card.origin.y);
        int ignored = 0, tab_minimum = 0, tab_width = 0, tab_height = 0, minimum = 0, natural = 0;
        notes_tab_.measure(Gtk::Orientation::HORIZONTAL, -1, tab_minimum, tab_width, ignored, ignored);
        notes_tab_.measure(Gtk::Orientation::VERTICAL, tab_width, tab_minimum, tab_height, ignored, ignored);
        notes_revealer_.measure(Gtk::Orientation::VERTICAL, width, minimum, natural, ignored, ignored);
        const int tray = std::max(minimum, std::min(natural, top - tab_height - 8));
        if (child == &notes_revealer_) place = Gdk::Rectangle(left, top - tray, width, tray);
        else place = Gdk::Rectangle(left + 12, top - tray - tab_height, tab_width, tab_height);
        return true;
    }, false);
}

void MainWindow::open_notes(bool open) {
    if (open && !controller_.session()) return;
    if (open) {
        load_notes(true);
        refresh_notes();
    } else {
        notes_confirming_ = false;
        notes_editing_.clear();
    }
    notes_revealer_.set_reveal_child(open);
    if (open) notes_tab_.add_css_class("open");
    else notes_tab_.remove_css_class("open");
    if (open && notes_entry_.get_sensitive()) notes_entry_.grab_focus();
    else if (!open) draft_.grab_focus();
    notes_shown_.clear();
    refresh_notes();
}

void MainWindow::load_notes(bool force) {
    const Session* session = controller_.session();
    std::string root;
    std::optional<Workspace> workspace;
    if (session) {
        try {
            workspace.emplace(session->workspace);
            root = workspace->root();
        } catch (const std::exception&) {
        }
    }
    if (root != notes_root_) {
        // Another project: what was being typed or chosen for the last one does not carry over.
        notes_root_ = root;
        notes_about_.clear();
        notes_about_chip_.set_visible(false);
        notes_editing_.clear();
        notes_confirming_ = false;
        notes_entry_.set_text("");
    } else if (!force && controller_.running) {
        return;
    }
    notes_.clear();
    notes_current_.clear();
    if (!workspace || controller_.home().empty()) return;
    try {
        notes_ = project_notes::load(controller_.home(), root);
    } catch (const std::exception&) {
    }
    for (const ProjectNote& note : notes_) notes_current_.push_back(project_notes::current(note, *workspace));
}

void MainWindow::notes_changed() {
    Glib::signal_idle().connect_once([this] {
        load_notes(true);
        refresh();
    });
}

void MainWindow::refresh_notes() {
    const Session* session = controller_.session();
    notes_tab_.set_visible(session != nullptr);
    if (!session && notes_open()) open_notes(false);
    const size_t count = notes_.size();
    notes_count_.set_text(std::to_string(count));
    if (count == 0) notes_count_.add_css_class("empty");
    else notes_count_.remove_css_class("empty");
    gtk_accessible_update_property(GTK_ACCESSIBLE(notes_tab_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Notes, " + std::to_string(count)).c_str(), -1);

    std::string print = notes_root_ + "|" + notes_editing_ + "|" + (notes_confirming_ ? "c" : "-") + (controller_.running ? "r" : "-") + "|";
    for (size_t i = 0; i < notes_.size(); i++) print += notes_[i].id + notes_[i].text + notes_[i].saved + (notes_current_[i] ? "1" : "0") + "|";
    if (print == notes_shown_) return;
    notes_shown_ = print;

    const bool full = count >= project_notes::kLimit;
    notes_entry_.set_sensitive(!full);
    notes_entry_.set_placeholder_text(full ? full_text() : "Something Mason should always know about this project");
    notes_about_button_.set_sensitive(!full);
    notes_add_.set_sensitive(!full && !trim(notes_entry_.get_text()).empty());
    notes_running_.set_visible(controller_.running);
    notes_empty_.set_visible(count == 0);
    notes_scroll_.set_visible(count > 0);
    notes_footer_.set_visible(count > 0 && !notes_confirming_);
    notes_confirm_.set_visible(count > 0 && notes_confirming_);
    notes_confirm_label_.set_text("Clear all " + std::to_string(count) + (count == 1 ? " note" : " notes") + "? Mason stops reading them.");
    size_t sent = 0;
    for (size_t i = 0; i < notes_.size(); i++) {
        if (sent < project_notes::kSent && notes_current_[i]) sent++;
    }
    notes_sent_.set_text(std::to_string(sent) + " of " + std::to_string(count) + " sent · up to " + std::to_string(project_notes::kSent));
    clear(notes_list_);
    for (size_t i = 0; i < notes_.size(); i++) notes_list_.append(*note_row(notes_[i], notes_current_[i]));
}

Gtk::Widget* MainWindow::note_row(const ProjectNote& note, bool current) {
    auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    card->add_css_class("note-card");
    if (!current) card->add_css_class("stale");
    auto* body = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 3);
    body->set_hexpand(true);
    const std::string id = note.id;
    if (notes_editing_ == id) {
        // Edited in place: Enter saves, Esc puts it back as it was.
        auto* entry = Gtk::make_managed<Gtk::Entry>();
        entry->set_text(note.text);
        entry->set_max_length(static_cast<int>(std::max(project_notes::kTextLimit, utf8_length(note.text))));
        gtk_accessible_update_property(GTK_ACCESSIBLE(entry->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Note", -1);
        entry->signal_activate().connect([this, entry, id] {
            const std::string text = entry->get_text();
            try {
                project_notes::edit(id, text, Workspace(notes_root_), controller_.home());
            } catch (const std::exception& error) {
                notes_toast(error.what(), nullptr);
                return;
            }
            notes_editing_.clear();
            notes_changed();
        });
        auto keys = Gtk::EventControllerKey::create();
        keys->signal_key_pressed().connect([this](guint key, guint, Gdk::ModifierType) {
            if (key != GDK_KEY_Escape) return false;
            notes_editing_.clear();
            Glib::signal_idle().connect_once([this] { refresh_notes(); notes_entry_.grab_focus(); });
            return true;
        }, false);
        entry->add_controller(keys);
        body->append(*entry);
        Glib::signal_idle().connect_once([entry] { entry->grab_focus(); entry->set_position(-1); });
    } else {
        auto* text = Gtk::make_managed<Gtk::Label>(note.text);
        text->set_xalign(0);
        text->set_wrap(true);
        text->set_wrap_mode(Pango::WrapMode::WORD_CHAR);
        text->add_css_class("note-text");
        auto* edit = Gtk::make_managed<Gtk::Button>();
        edit->set_child(*text);
        edit->add_css_class("flat");
        edit->add_css_class("note-text-button");
        edit->set_tooltip_text("Edit this note");
        edit->signal_clicked().connect([this, id] {
            notes_editing_ = id;
            Glib::signal_idle().connect_once([this] { refresh_notes(); });
        });
        body->append(*edit);
    }
    std::string meta;
    if (!note.source.empty()) meta = "<tt>" + escape(note.source) + "</tt>" + (current ? "" : " changed · not sent");
    const std::string when = project_notes::when(note.saved);
    if (current && !when.empty()) meta += (meta.empty() ? "" : " · ") + escape(when);
    if (!meta.empty()) {
        auto* line = Gtk::make_managed<Gtk::Label>();
        line->set_markup(meta);
        line->set_xalign(0);
        line->set_ellipsize(Pango::EllipsizeMode::MIDDLE);
        line->add_css_class("note-meta");
        body->append(*line);
    }
    if (!current) {
        // Its file changed: keep it as it is against the file as it is now, or remove it.
        auto* actions = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
        auto* keep = Gtk::make_managed<Gtk::Button>("Keep anyway");
        keep->add_css_class("notes-small");
        keep->set_tooltip_text("Save it again against the file as it is now, so Mason reads it again");
        keep->signal_clicked().connect([this, id] {
            try {
                project_notes::keep(id, Workspace(notes_root_), controller_.home());
            } catch (const std::exception& error) {
                notes_toast(error.what(), nullptr);
            }
            notes_changed();
        });
        auto* remove = Gtk::make_managed<Gtk::Button>("Remove");
        remove->add_css_class("notes-small");
        actions->append(*keep);
        actions->append(*remove);
        body->append(*actions);
        remove->signal_clicked().connect([this, note] {
            try {
                project_notes::remove(note.id, notes_root_, controller_.home());
            } catch (const std::exception& error) {
                notes_toast(error.what(), nullptr);
                return;
            }
            const std::string root = notes_root_;
            notes_toast("Note removed", [this, note, root] {
                try { project_notes::restore({note}, root, controller_.home()); } catch (const std::exception&) {}
                notes_changed();
            });
            notes_changed();
        });
    }
    card->append(*body);
    auto* drop = Gtk::make_managed<Gtk::Button>();
    drop->set_icon_name("window-close-symbolic");
    drop->add_css_class("flat");
    drop->add_css_class("notes-icon");
    drop->set_valign(Gtk::Align::START);
    drop->set_tooltip_text("Remove this note");
    gtk_accessible_update_property(GTK_ACCESSIBLE(drop->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Remove this note", -1);
    drop->signal_clicked().connect([this, note] {
        try {
            project_notes::remove(note.id, notes_root_, controller_.home());
        } catch (const std::exception& error) {
            notes_toast(error.what(), nullptr);
            return;
        }
        const std::string root = notes_root_;
        notes_toast("Note removed", [this, note, root] {
            try { project_notes::restore({note}, root, controller_.home()); } catch (const std::exception&) {}
            notes_changed();
        });
        notes_changed();
    });
    card->append(*drop);
    return card;
}

void MainWindow::add_note() {
    const std::string text = trim(notes_entry_.get_text());
    if (text.empty() || notes_root_.empty()) return;
    try {
        project_notes::add(text, notes_about_, Workspace(notes_root_), controller_.home());
    } catch (const std::exception& error) {
        notes_toast(error.what(), nullptr);
        return;
    }
    notes_entry_.set_text("");
    notes_about_.clear();
    notes_about_chip_.set_visible(false);
    notes_changed();
}

void MainWindow::notes_toast(const std::string& text, std::function<void()> undo) {
    AdwToast* toast = adw_toast_new(text.c_str());
    adw_toast_set_timeout(toast, 6);
    if (undo) {
        adw_toast_set_button_label(toast, "Undo");
        auto* held = new std::function<void()>(std::move(undo));
        g_signal_connect_data(toast, "button-clicked", G_CALLBACK(+[](AdwToast*, gpointer data) { (*static_cast<std::function<void()>*>(data))(); }), held,
                              +[](gpointer data, GClosure*) { delete static_cast<std::function<void()>*>(data); }, GConnectFlags(0));
    }
    adw_toast_overlay_add_toast(toasts_, toast);
}

Gtk::Widget* MainWindow::suggestion_widget(const std::string& text) {
    auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    card->add_css_class("suggestion-card");
    card->set_margin_start(38);
    auto* heading = Gtk::make_managed<Gtk::Label>("Remember this for next time?");
    heading->set_xalign(0);
    heading->add_css_class("notes-title");
    auto* quote = Gtk::make_managed<Gtk::Label>("“" + text + "”");
    quote->set_xalign(0);
    quote->set_wrap(true);
    quote->set_wrap_mode(Pango::WrapMode::WORD_CHAR);
    quote->set_selectable(true);
    quote->set_can_focus(false);
    card->append(*heading);
    card->append(*quote);
    auto* buttons = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* remember = Gtk::make_managed<Gtk::Button>("Remember");
    remember->add_css_class("suggested-action");
    if (notes_.size() >= project_notes::kLimit) {
        remember->set_sensitive(false);
        // A tooltip on the row, since an insensitive button shows none of its own.
        buttons->set_tooltip_text(full_text());
    }
    remember->signal_clicked().connect([this] {
        Glib::signal_idle().connect_once([this] {
            if (controller_.answer_suggestion("remember")) {
                load_notes(true);
                notes_toast("Kept. Mason reads it with every request in this project.", nullptr);
                refresh();
            }
        });
    });
    auto* edit = Gtk::make_managed<Gtk::Button>("Edit");
    edit->signal_clicked().connect([this, text] {
        Glib::signal_idle().connect_once([this, text] {
            if (!controller_.answer_suggestion("edit")) return;
            open_notes(true);
            notes_entry_.set_text(text);
            notes_entry_.grab_focus();
            notes_entry_.set_position(-1);
        });
    });
    auto* dismiss = Gtk::make_managed<Gtk::Button>("Dismiss");
    dismiss->add_css_class("flat");
    dismiss->signal_clicked().connect([this] { Glib::signal_idle().connect_once([this] { controller_.answer_suggestion("dismiss"); }); });
    buttons->append(*remember);
    buttons->append(*edit);
    buttons->append(*dismiss);
    card->append(*buttons);
    return card;
}

}  // namespace mason::app
