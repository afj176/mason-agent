// The Chat page: saved conversations on the left, the selected one and a message box on the right.
#include "window.hpp"

namespace mason::app {

namespace {

Gtk::Label* wrapped(const std::string& text) {
    auto* label = Gtk::make_managed<Gtk::Label>(text);
    label->set_xalign(0);
    label->set_wrap(true);
    label->set_wrap_mode(Pango::WrapMode::WORD_CHAR);
    label->set_selectable(true);
    label->set_can_focus(false);
    return label;
}

/// A message with attached files shows the file names, not their whole content.
std::string shown(const std::string& content) {
    const std::string marker = "Attached file ";
    std::string text;
    size_t from = 0;
    while (true) {
        size_t at = content.find(marker, from);
        // Only a marker at the start of a paragraph is an attachment.
        while (at != std::string::npos && at != 0 && content.compare(at - 2 < content.size() ? at - 2 : 0, 2, "\n\n") != 0) at = content.find(marker, at + 1);
        if (at == std::string::npos) {
            if (from == 0) text = content;
            break;
        }
        if (from == 0) text = trim(content.substr(0, at));
        const size_t colon = content.find(':', at);
        if (colon == std::string::npos) break;
        text += (text.empty() ? "" : "\n") + std::string("📎 ") + content.substr(at + marker.size(), colon - at - marker.size());
        from = colon;
    }
    return text;
}

}  // namespace

ChatPage::ChatPage(Controller& controller) : Gtk::Box(Gtk::Orientation::HORIZONTAL, 0), controller_(controller) {
    // ---- the list of chats ----
    auto* side = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    side->set_size_request(250, -1);
    side->add_css_class("sidebar-pane");
    list_.set_selection_mode(Gtk::SelectionMode::SINGLE);
    list_.add_css_class("navigation-sidebar");
    list_.signal_row_selected().connect([this](Gtk::ListBoxRow* row) {
        if (updating_ || !row) return;
        controller_.select_chat(row->get_name());
    });
    auto* list_scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    list_scroll->set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);
    list_scroll->set_child(list_);
    list_scroll->set_vexpand(true);
    auto* add = Gtk::make_managed<Gtk::Button>("New Chat");
    add->set_margin(8);
    add->signal_clicked().connect([this] { controller_.new_chat(); draft_.grab_focus(); });
    side->append(*list_scroll);
    side->append(*add);

    // ---- the conversation ----
    transcript_.set_margin(24);
    transcript_scroll_.set_child(transcript_);
    transcript_scroll_.set_vexpand(true);
    transcript_scroll_.set_hexpand(true);
    transcript_scroll_.set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);

    draft_.set_wrap_mode(Gtk::WrapMode::WORD_CHAR);
    draft_.set_accepts_tab(false);
    draft_.set_top_margin(6);
    draft_.set_bottom_margin(6);
    draft_.set_left_margin(8);
    draft_.set_right_margin(8);
    draft_.set_tooltip_text("Message the model. Ctrl+Enter sends.");
    gtk_accessible_update_property(GTK_ACCESSIBLE(draft_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Message", -1);
    auto keys = Gtk::EventControllerKey::create();
    keys->signal_key_pressed().connect([this](guint key, guint, Gdk::ModifierType state) {
        if ((key == GDK_KEY_Return || key == GDK_KEY_KP_Enter) && (state & Gdk::ModifierType::CONTROL_MASK) == Gdk::ModifierType::CONTROL_MASK) {
            send_.activate();
            return true;
        }
        return false;
    }, false);
    draft_.add_controller(keys);
    draft_.get_buffer()->signal_changed().connect([this] { refresh_buttons(); });
    auto* draft_scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    draft_scroll->set_child(draft_);
    draft_scroll->set_min_content_height(54);
    draft_scroll->set_max_content_height(150);
    draft_scroll->set_propagate_natural_height(true);

    attach_.set_tooltip_text("Attach text files to this message (up to 5, 256 KiB each)");
    attach_.signal_clicked().connect([this] { choose_files(); });
    send_.set_label("");
    send_.set_icon_name("go-up-symbolic");
    send_.set_tooltip_text("Send (Ctrl+Enter)");
    gtk_accessible_update_property(GTK_ACCESSIBLE(send_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Send", -1);
    send_.add_css_class("suggested-action");
    send_.add_css_class("send-round");
    attach_.add_css_class("chip");
    send_.signal_clicked().connect([this] {
        const std::string text = draft_.get_buffer()->get_text();
        if (trim(text).empty() && controller_.chat_attachments.empty()) return;
        draft_.get_buffer()->set_text("");
        controller_.send_chat(text);
    });
    stop_.signal_clicked().connect([this] { controller_.stop_chat(); });
    model_label_.add_css_class("dim-label");
    model_label_.add_css_class("caption");
    model_label_.set_hexpand(true);
    model_label_.set_xalign(0);
    auto* buttons = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    buttons->append(attach_);
    buttons->append(model_label_);
    buttons->append(stop_);
    buttons->append(send_);

    auto* composer = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    composer->add_css_class("composer-card");
    composer->set_margin(16);
    composer->set_margin_top(6);
    composer->append(attachments_);
    composer->append(*draft_scroll);
    composer->append(*buttons);

    auto* main = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    main->append(transcript_scroll_);
    main->append(*composer);
    append(*side);
    append(*Gtk::make_managed<Gtk::Separator>(Gtk::Orientation::VERTICAL));
    append(*main);
    refresh();
}

void ChatPage::choose_files() {
    auto dialog = Gtk::FileDialog::create();
    dialog->set_title("Attach text files");
    Gtk::Window* parent = dynamic_cast<Gtk::Window*>(get_root());
    auto done = [this, dialog](const Glib::RefPtr<Gio::AsyncResult>& result) {
        try {
            for (const Glib::RefPtr<Gio::File>& file : dialog->open_multiple_finish(result)) controller_.attach_to_chat(file->get_path());
        } catch (const Glib::Error&) {
            // The dialog was closed without a choice.
        }
    };
    if (parent) dialog->open_multiple(*parent, done);
    else dialog->open_multiple(done);
}

void ChatPage::refresh_buttons() {
    const bool running = controller_.chat_running;
    const bool has_chat = controller_.chat() != nullptr;
    send_.set_visible(!running);
    stop_.set_visible(running);
    send_.set_sensitive(has_chat && (!trim(draft_.get_buffer()->get_text()).empty() || !controller_.chat_attachments.empty()));
    attach_.set_sensitive(has_chat && !running && controller_.chat_attachments.size() < ChatAttachment::kMaxPerMessage);
    draft_.set_sensitive(has_chat);
    model_label_.set_text(controller_.endpoint ? controller_.endpoint_name : "No model is running");
}

void ChatPage::refresh() {
    // ---- list ----
    std::string list_print = controller_.chat_selection;
    for (const ChatConversation& chat : controller_.chats()) list_print += "|" + chat.id + chat.title;
    if (list_print != list_shown_) {
        list_shown_ = list_print;
        updating_ = true;
        while (Gtk::Widget* child = list_.get_first_child()) list_.remove(*child);
        for (const ChatConversation& chat : controller_.chats()) {
            auto* title = Gtk::make_managed<Gtk::Label>(chat.title);
            title->set_xalign(0);
            title->set_ellipsize(Pango::EllipsizeMode::END);
            title->set_hexpand(true);
            auto* remove = Gtk::make_managed<Gtk::Button>();
            remove->set_icon_name("user-trash-symbolic");
            remove->add_css_class("flat");
            remove->set_tooltip_text("Delete this chat");
            gtk_accessible_update_property(GTK_ACCESSIBLE(remove->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Delete chat " + chat.title).c_str(), -1);
            const std::string id = chat.id;
            remove->signal_clicked().connect([this, id] { controller_.remove_chat(id); });
            auto* line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
            line->append(*title);
            line->append(*remove);
            auto* row = Gtk::make_managed<Gtk::ListBoxRow>();
            row->set_child(*line);
            row->set_name(chat.id);
            list_.append(*row);
            if (chat.id == controller_.chat_selection) list_.select_row(*row);
        }
        updating_ = false;
    }

    // ---- attachments waiting to be sent ----
    std::string attachment_print;
    for (const ChatAttachment& file : controller_.chat_attachments) attachment_print += file.name + "|";
    if (attachment_print != attachments_shown_) {
        attachments_shown_ = attachment_print;
        clear(attachments_);
        for (const ChatAttachment& file : controller_.chat_attachments) {
            auto* chip = Gtk::make_managed<Gtk::Button>("📎 " + file.name + "  ✕");
            chip->add_css_class("flat");
            chip->add_css_class("caption");
            chip->set_tooltip_text("Remove " + file.name);
            const std::string name = file.name;
            chip->signal_clicked().connect([this, name] { controller_.detach_from_chat(name); });
            attachments_.append(*chip);
        }
        attachments_.set_visible(!controller_.chat_attachments.empty());
    }

    // ---- conversation ----
    const ChatConversation* chat = controller_.chat();
    std::string print = chat ? chat->id + std::to_string(chat->messages.size()) + (controller_.chat_running ? "r" : "-") : "none";
    if (print != transcript_shown_) {
        transcript_shown_ = print;
        live_label_ = nullptr;
        clear(transcript_);
        if (!chat || chat->messages.size() <= 1) {
            auto* hint = dim_label(chat ? "Ask anything. Chat answers from the conversation alone: it cannot read your project, edit files or run commands. Use Code for that."
                                        : "Talk a problem through with the running model. Chats are saved on this computer. Choose New Chat to begin.");
            hint->set_wrap(true);
            hint->set_justify(Gtk::Justification::CENTER);
            hint->set_halign(Gtk::Align::CENTER);
            auto* face = mascot(72);
            face->set_halign(Gtk::Align::CENTER);
            face->set_margin_top(70);
            transcript_.append(*face);
            transcript_.append(*hint);
        }
        if (chat) {
            for (const ChatMessage& message : chat->messages) {
                if (message.role == "user") {
                    transcript_.append(*speaker(false));
                    transcript_.append(*message_body(*wrapped(shown(message.content))));
                } else if (message.role == "assistant") {
                    transcript_.append(*speaker(true));
                    transcript_.append(*message_body(*answer_view(message.content)));
                }
            }
        }
        if (controller_.chat_running) {
            auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
            auto* spinner = Gtk::make_managed<Gtk::Spinner>();
            spinner->start();
            live_label_ = wrapped("Thinking");
            live_label_->add_css_class("dim-label");
            row->append(*spinner);
            row->append(*live_label_);
            transcript_.append(*row);
        }
        // Show the newest message once the new rows have a size.
        Glib::signal_idle().connect_once([this] {
            auto adjustment = transcript_scroll_.get_vadjustment();
            adjustment->set_value(adjustment->get_upper());
        });
    }
    if (live_label_ && !controller_.chat_live.empty()) live_label_->set_text(utf8_suffix(controller_.chat_live, 600));
    refresh_buttons();
}

}  // namespace mason::app
