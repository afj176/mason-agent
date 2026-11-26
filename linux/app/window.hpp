// The main window: a sidebar of projects and sessions, and the Code and Models pages.
#pragma once
#include <adwaita.h>
#include <gtkmm.h>

#include "controller.hpp"
#include "style.hpp"

namespace mason::app {

/// Escapes text for Pango markup.
std::string escape(const std::string& text);
/// Inline Markdown (bold and code) as Pango markup.
std::string inline_markup(const std::string& text);
/// Removes every child of a box.
void clear(Gtk::Box& box);
/// A small label styled as secondary text.
Gtk::Label* dim_label(const std::string& text);
/// A model's answer: prose with headings, bullets and inline code, and fenced code as scrollable blocks.
Gtk::Widget* answer_view(const std::string& text);

/// The Models page: running servers, the llama.cpp runtime, local model files and Hugging Face.
class ModelsPage : public Gtk::ScrolledWindow {
public:
    explicit ModelsPage(Controller& controller);
    void refresh();

private:
    Controller& controller_;
    Gtk::Box content_{Gtk::Orientation::VERTICAL, 24};
    Gtk::Entry search_;
    std::string shown_;  ///< A fingerprint of what is displayed, so unchanged state does not rebuild the page.
    Gtk::Label* status_label_ = nullptr;  ///< Owned by the page; reset whenever the page is rebuilt.
    Gtk::ProgressBar* progress_ = nullptr;
};

/// The Chat page: saved conversations, the selected one, and a message box. Chat has no tools.
class ChatPage : public Gtk::Box {
public:
    explicit ChatPage(Controller& controller);
    void refresh();

private:
    void refresh_buttons();
    void choose_files();

    Controller& controller_;
    Gtk::ListBox list_;
    Gtk::Box transcript_{Gtk::Orientation::VERTICAL, 16};
    Gtk::ScrolledWindow transcript_scroll_;
    Gtk::Box attachments_{Gtk::Orientation::HORIZONTAL, 6};
    Gtk::TextView draft_;
    Gtk::Button attach_{"Attach…"}, send_{"Send"}, stop_{"Stop"};
    Gtk::Label model_label_;
    Gtk::Label* live_label_ = nullptr;  ///< Owned by the transcript and reset when it is rebuilt.
    bool updating_ = false;
    std::string list_shown_, transcript_shown_, attachments_shown_;
};

class MainWindow {
public:
    MainWindow(Gtk::Application& application, Controller& controller);
    void present();

private:
    void refresh();
    void refresh_sidebar();
    void refresh_transcript();
    void refresh_composer();
    void refresh_changes();
    void open_project();
    Gtk::Widget* turn_widget(const Turn& turn, const std::vector<Activity>& activities, bool live);
    Gtk::Widget* activity_widget(const Activity& activity);
    Gtk::Widget* answer_widget(const std::string& text);
    Gtk::Widget* approval_widget(const ApprovalRequest& request);

    Controller& controller_;
    AdwApplicationWindow* window_ = nullptr;
    AdwToastOverlay* toasts_ = nullptr;
    AdwViewStack* stack_ = nullptr;
    GtkWidget* split_ = nullptr;
    Gtk::Label status_left_, status_right_;
    Gtk::Label composer_hint_;

    Gtk::ListBox sidebar_;
    Gtk::Box transcript_{Gtk::Orientation::VERTICAL, 18};
    Gtk::ScrolledWindow transcript_scroll_;
    Gtk::Box approval_slot_{Gtk::Orientation::VERTICAL, 0};
    Gtk::TextView draft_;
    Gtk::DropDown permission_;
    Gtk::Button send_{"Send"}, stop_{"Stop"}, queue_{"Queue"}, steer_{"Steer"}, run_queued_;
    Gtk::Label waiting_;
    Gtk::Button model_button_;
    Gtk::ToggleButton changes_toggle_;
    Gtk::Box changes_{Gtk::Orientation::VERTICAL, 12};
    Gtk::ScrolledWindow changes_scroll_;
    Gtk::Stack session_stack_;
    ModelsPage models_;
    ChatPage chat_;
    Gtk::Label* live_label_ = nullptr;  ///< Shows the model's streamed words; owned by the transcript and reset when it is rebuilt.
    bool updating_ = false;
    std::string sidebar_shown_, transcript_shown_, changes_shown_;
};

}  // namespace mason::app
