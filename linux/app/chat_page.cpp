// The Chat page: saved conversations on the left, the selected one and a message box on the right.
#include "window.hpp"

#include <algorithm>
#include <tuple>

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

    attach_.set_tooltip_text("Attach text files, pictures, PDFs or Word documents to this message (up to 5)");
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
    // Options for this chat: how much the model thinks, how long an answer may be, and a reference it always has.
    options_.set_label("Options");
    options_.add_css_class("chip");
    options_.set_tooltip_text("This chat's reasoning level, answer length and pinned reference");
    auto* panel = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    panel->set_margin(12);
    panel->set_size_request(380, -1);
    auto* reasoning_label = Gtk::make_managed<Gtk::Label>("Reasoning");
    reasoning_label->set_xalign(0);
    options_reasoning_.set_model(Gtk::StringList::create({"Server's default", "Off", "Low", "Medium", "High"}));
    gtk_accessible_update_property(GTK_ACCESSIBLE(options_reasoning_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Reasoning", -1);
    auto* limit_label = Gtk::make_managed<Gtk::Label>("Most tokens in one answer");
    limit_label->set_xalign(0);
    options_limit_.set_placeholder_text("Left to the server");
    auto* reference_label = Gtk::make_managed<Gtk::Label>("Reference: text the model is given with every message");
    reference_label->set_xalign(0);
    reference_label->set_wrap(true);
    options_reference_.set_wrap_mode(Gtk::WrapMode::WORD_CHAR);
    gtk_accessible_update_property(GTK_ACCESSIBLE(options_reference_.gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Reference", -1);
    auto* reference_scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    reference_scroll->set_min_content_height(140);
    reference_scroll->add_css_class("card");
    reference_scroll->set_child(options_reference_);
    auto* apply = Gtk::make_managed<Gtk::Button>("Apply");
    apply->add_css_class("suggested-action");
    apply->set_halign(Gtk::Align::END);
    apply->signal_clicked().connect([this] {
        static const char* levels[] = {"default", "off", "low", "medium", "high"};
        const std::string digits = trim(options_limit_.get_text());
        if (!digits.empty() && (digits.size() > 7 || digits.find_first_not_of("0123456789") != std::string::npos)) {
            controller_.notice.emit("An answer's limit is a number of tokens, or empty.");
            return;
        }
        options_popover_.popdown();
        options_shown_.clear();
        controller_.configure_chat(levels[std::min<guint>(4, options_reasoning_.get_selected())], digits.empty() ? 0 : std::stoi(digits), options_reference_.get_buffer()->get_text());
    });
    for (Gtk::Widget* widget : std::vector<Gtk::Widget*>{reasoning_label, &options_reasoning_, limit_label, &options_limit_, reference_label, reference_scroll, apply}) panel->append(*widget);
    options_popover_.set_child(*panel);
    options_.set_popover(options_popover_);
    buttons->append(options_);
    // Tools for this chat: the web, skills and MCP servers, each on or off, saved with the conversation.
    tools_.set_label("Tools");
    tools_.add_css_class("chip");
    tools_.set_tooltip_text("What this chat may use: the web, skills and your MCP servers");
    auto* tools_panel = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 10);
    tools_panel->set_margin(12);
    tools_panel->set_size_request(340, -1);
    auto* tools_title = Gtk::make_managed<Gtk::Label>("Tools for this chat");
    tools_title->set_xalign(0);
    tools_title->add_css_class("heading");
    tools_panel->append(*tools_title);
    const std::tuple<Gtk::Switch*, const char*, const char*, const char*> switches[] = {
        {&tools_web_, "web", "Web", "Read web pages and search. Every new host asks first."},
        {&tools_skills_, "skills", "Skills", "Instructions from your bundles and Mason's skills folder."},
        {&tools_mcp_, "mcp", "MCP", "Your MCP servers' allowed tools. Every call asks first."},
    };
    for (const auto& [toggle, key, title, detail] : switches) {
        auto* line = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 12);
        auto* words = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 2);
        auto* name = Gtk::make_managed<Gtk::Label>(title);
        name->set_xalign(0);
        auto* about = dim_label(detail);
        about->add_css_class("caption");
        about->set_wrap(true);
        about->set_max_width_chars(36);
        words->append(*name);
        words->append(*about);
        words->set_hexpand(true);
        toggle->set_valign(Gtk::Align::CENTER);
        gtk_accessible_update_property(GTK_ACCESSIBLE(toggle->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, (std::string(title) + " in this chat").c_str(), -1);
        const std::string group = key;
        Gtk::Switch* control = toggle;
        toggle->property_active().signal_changed().connect([this, control, group] {
            if (tools_updating_) return;
            controller_.set_chat_tool(group, control->get_active());
        });
        line->append(*words);
        line->append(*toggle);
        tools_panel->append(*line);
    }
    tools_note_.set_xalign(0);
    tools_note_.set_wrap(true);
    tools_note_.set_max_width_chars(40);
    tools_note_.add_css_class("dim-label");
    tools_note_.add_css_class("caption");
    tools_panel->append(tools_note_);
    tools_popover_.set_child(*tools_panel);
    tools_.set_popover(tools_popover_);
    buttons->append(tools_);
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
    options_.set_sensitive(has_chat && !running);
    if (const ChatConversation* current = controller_.chat()) {
        // The popover shows the selected chat's own settings; it is filled when the chat or its settings change, not while typing.
        const std::string print = current->id + "|" + current->reasoning + "|" + std::to_string(current->max_tokens) + "|" + std::to_string(current->reference.size());
        if (print != options_shown_) {
            options_shown_ = print;
            const std::string& level = current->reasoning;
            options_reasoning_.set_selected(level == "off" ? 1 : level == "low" ? 2 : level == "medium" ? 3 : level == "high" ? 4 : 0);
            options_limit_.set_text(current->max_tokens > 0 ? std::to_string(current->max_tokens) : "");
            options_reference_.get_buffer()->set_text(current->reference);
            const bool set = !level.empty() || current->max_tokens > 0 || !current->reference.empty();
            options_.set_label(set ? "Options •" : "Options");
        }
    }
    draft_.set_sensitive(has_chat);
    model_label_.set_text(controller_.endpoint ? controller_.endpoint_name : "No model is running");

    // The Tools control shows the selected chat's switches; a model that cannot call tools gets none.
    const bool can_call = controller_.chat_calls_tools();
    const ChatConversation* current = controller_.chat();
    const bool online = !controller_.home().empty() && network_settings::web(controller_.home());
    const std::string tools_print = (current ? current->id + (current->web ? "w" : "-") + (current->skills ? "s" : "-") + (current->mcp ? "m" : "-") : std::string("none")) + (can_call ? "c" : "-") + (online ? "o" : "-");
    if (tools_print != tools_shown_) {
        tools_shown_ = tools_print;
        tools_updating_ = true;
        tools_web_.set_active(current && current->web);
        tools_skills_.set_active(current && current->skills);
        tools_mcp_.set_active(current && current->mcp);
        tools_updating_ = false;
        int on = current ? (current->web && online ? 1 : 0) + (current->skills ? 1 : 0) + (current->mcp ? 1 : 0) : 0;
        tools_.set_label(can_call && current ? "Tools · " + std::to_string(on) : "Tools");
        tools_note_.set_text(!can_call ? chat::kNoTools : online ? "Chat never reads your files or runs commands." : "The internet is off on this computer, so the web tools are not offered. Change it in Settings, under Internet.");
        tools_.set_tooltip_text(can_call ? "What this chat may use: the web, skills and your MCP servers" : chat::kNoTools);
    }
    tools_web_.set_sensitive(can_call && online && !running);
    tools_skills_.set_sensitive(can_call && !running);
    tools_mcp_.set_sensitive(can_call && !running);
    tools_.set_sensitive(has_chat && !running && can_call);
}

Gtk::Widget* ChatPage::step_row(const Activity& step) {
    auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    if (step.state == "running") {
        auto* spinner = Gtk::make_managed<Gtk::Spinner>();
        spinner->start();
        row->append(*spinner);
    } else {
        auto* icon = Gtk::make_managed<Gtk::Image>();
        icon->set_from_icon_name(step.state == "completed" ? "emblem-ok-symbolic" : step.state == "denied" ? "action-unavailable-symbolic" : "dialog-error-symbolic");
        icon->add_css_class(step.state == "completed" ? "success" : step.state == "denied" ? "warning" : "error");
        row->append(*icon);
    }
    auto* name = Gtk::make_managed<Gtk::Label>();
    name->set_markup("<b>" + escape(label_of(step.tool)) + "</b>");
    auto* subject = dim_label(replace_all(step.subject, "\n", " "));
    subject->set_wrap(false);
    subject->set_ellipsize(Pango::EllipsizeMode::MIDDLE);
    subject->set_hexpand(true);
    row->append(*name);
    row->append(*subject);
    if (step.state == "running" || step.result.empty()) return row;
    auto* expander = Gtk::make_managed<Gtk::Expander>();
    expander->set_label_widget(*row);
    auto* result = text_label(utf8_prefix(step.result, 2000));
    result->add_css_class("monospace");
    result->add_css_class("code-block");
    result->add_css_class("caption");
    expander->set_child(*result);
    return expander;
}

Gtk::Widget* ChatPage::approval_card(const ApprovalRequest& request) {
    auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 10);
    card->add_css_class("approval-card");
    auto* top = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 14);
    top->append(*pose_picture(pose::approval, 56, "waiting for your decision"));
    auto* heading = Gtk::make_managed<Gtk::Label>(request.title);
    heading->set_xalign(0);
    heading->set_wrap(true);
    heading->set_hexpand(true);
    heading->add_css_class("heading");
    top->append(*heading);
    card->append(*top);
    if (!request.detail.empty()) {
        auto* detail = text_label(utf8_prefix(request.detail, 1200));
        detail->add_css_class("dim-label");
        if (request.tool != "network") detail->add_css_class("monospace");
        card->append(*detail);
    }
    auto* buttons = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* once = Gtk::make_managed<Gtk::Button>("Once");
    once->add_css_class("suggested-action");
    once->signal_clicked().connect([this] { controller_.answer_chat(ChatAnswer::Once); });
    auto* always = Gtk::make_managed<Gtk::Button>("Always");
    always->set_tooltip_text(request.tool == "network" ? "Keep this host for every chat on this computer" : "Keep this call allowed in Chat");
    always->signal_clicked().connect([this] { controller_.answer_chat(ChatAnswer::Always); });
    auto* spacer = Gtk::make_managed<Gtk::Box>();
    spacer->set_hexpand(true);
    auto* no = Gtk::make_managed<Gtk::Button>("No");
    no->signal_clicked().connect([this] { controller_.answer_chat(ChatAnswer::No); });
    buttons->append(*once);
    buttons->append(*always);
    buttons->append(*spacer);
    buttons->append(*no);
    card->append(*buttons);
    return card;
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
    for (const Activity& step : controller_.chat_steps) print += "|" + step.id + step.state;
    if (controller_.chat_approval) print += "|ask:" + controller_.chat_approval->title;
    if (print != transcript_shown_) {
        transcript_shown_ = print;
        live_label_ = nullptr;
        clear(transcript_);
        if ((!chat || chat->messages.size() <= 1) && !controller_.chat_running) {
            auto* hint = dim_label(chat ? "Ask anything. Chat cannot read your project, edit files or run commands; use Code for that. With Tools it can read the web, use skills and call your MCP servers, asking first."
                                        : "Talk a problem through with the running model. Chats are saved on this computer. Choose New Chat to begin.");
            hint->set_wrap(true);
            hint->set_justify(Gtk::Justification::CENTER);
            hint->set_halign(Gtk::Align::CENTER);
            auto* face = pose_picture(pose::listening, 120, "listening");
            face->set_margin_top(60);
            transcript_.append(*face);
            transcript_.append(*hint);
        }
        if (chat) {
            for (const ChatMessage& message : chat->messages) {
                if (message.role == "user") {
                    transcript_.append(*speaker(false));
                    transcript_.append(*message_body(*wrapped(shown(message.content))));
                } else if (message.role == "assistant") {
                    if (!message.tool_calls.empty()) {
                        // Tool calls show as compact steps: the tool, the host or query, and how it went.
                        auto* steps = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 4);
                        if (!trim(message.content).empty()) steps->append(*dim_label(utf8_prefix(trim(message.content), 600)));
                        for (const ToolCall& call : message.tool_calls) {
                            const auto found = std::find_if(chat->steps.begin(), chat->steps.end(), [&](const Activity& step) { return step.id == call.id; });
                            Activity shown_step;
                            if (found != chat->steps.end()) shown_step = *found;
                            else { shown_step.tool = call.name; shown_step.state = "failed"; shown_step.subject = "not run"; }
                            steps->append(*step_row(shown_step));
                        }
                        transcript_.append(*message_body(*steps));
                        continue;
                    }
                    transcript_.append(*speaker(true));
                    transcript_.append(*message_body(*answer_view(tables::prose(message.content))));
                    // A table in the answer is laid out as a grid, with a chart of each numeric column.
                    for (const TableData& table : tables::find(message.content)) transcript_.append(*message_body(*table_card(table)));
                }
            }
        }
        if (controller_.chat_running && !trim(controller_.chat_sending).empty()) {
            // The message being answered, until the saved conversation holds it.
            transcript_.append(*speaker(false));
            transcript_.append(*message_body(*wrapped(controller_.chat_sending)));
        }
        if (controller_.chat_running && !controller_.chat_steps.empty()) {
            // This answer's steps so far; the saved conversation shows the finished ones once it is done.
            auto* steps = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 4);
            for (const Activity& step : controller_.chat_steps) {
                const bool saved = chat && std::any_of(chat->steps.begin(), chat->steps.end(), [&](const Activity& item) { return item.id == step.id; });
                if (!saved) steps->append(*step_row(step));
            }
            transcript_.append(*message_body(*steps));
        }
        if (controller_.chat_approval) transcript_.append(*message_body(*approval_card(*controller_.chat_approval)));
        if (controller_.chat_running) {
            // The answer being written: the planning pose beside the streamed words.
            auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
            auto* face = pose_picture(pose::planning, 34, "working on the answer");
            face->set_valign(Gtk::Align::START);
            auto* spinner = Gtk::make_managed<Gtk::Spinner>();
            spinner->start();
            spinner->set_valign(Gtk::Align::CENTER);
            live_label_ = wrapped("Thinking");
            live_label_->add_css_class("dim-label");
            live_label_->set_hexpand(true);
            row->append(*face);
            row->append(*spinner);
            row->append(*live_label_);
            transcript_.append(*row);
        } else if (chat && !chat->messages.empty() && chat->messages.back().role == "user") {
            // The question was saved but no answer came back.
            auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
            row->append(*pose_picture(pose::failed, 40, "no answer"));
            auto* note = wrapped(controller_.chat_problem.empty() ? "No answer arrived. Send the message again to retry." : "No answer arrived: " + controller_.chat_problem);
            note->add_css_class("error");
            note->set_hexpand(true);
            row->append(*note);
            transcript_.append(*speaker(true));
            transcript_.append(*message_body(*row));
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
