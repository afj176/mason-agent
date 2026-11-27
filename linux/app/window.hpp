// The main window: a sidebar of projects and sessions, and the Code and Models pages.
#pragma once
#include <adwaita.h>
#include <gtkmm.h>

#include "gpu.hpp"
#include "model_memory.hpp"
#include "palette.hpp"
#include "controller.hpp"
#include "browser.hpp"
#include "chat.hpp"
#include "derived.hpp"
#include "preview.hpp"
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
/// A table from an answer: its cells in a grid, and its numeric columns as bars against the first column.
Gtk::Widget* table_card(const TableData& table);

/// The Models page: running servers, the llama.cpp runtime, local model files and Hugging Face. Choosing a local
/// model pushes its panel: the context ladder with its memory fit, starting, and what else can be done with it.
class ModelsPage : public Gtk::Box {
public:
    explicit ModelsPage(Controller& controller);
    void refresh();
    /// Emitted when the user asks to talk to the running model in Chat.
    sigc::signal<void()> open_chat;

private:
    Controller& controller_;
    AdwNavigationView* navigation_ = nullptr;
    Gtk::ScrolledWindow library_;
    Gtk::Box content_{Gtk::Orientation::VERTICAL, 24};
    Gtk::Entry search_;
    std::string shown_;  ///< A fingerprint of what is displayed, so unchanged state does not rebuild the page.
    Gtk::Label* status_label_ = nullptr;  ///< Owned by the page; reset whenever the page is rebuilt.
    Gtk::ProgressBar* progress_ = nullptr;
    /// The graphics and memory readings: taken off the main thread every two seconds while the page is on screen.
    GpuSample gpu_;
    MemoryReading memory_;
    bool gpu_reading_ = false;
    std::vector<AdwActionRow*> gpu_rows_;  ///< Owned by the page; reset whenever the page is rebuilt.
    std::vector<std::pair<AdwActionRow*, LocalModel>> local_rows_;
    AdwPreferencesGroup* local_group_ = nullptr;
    std::shared_ptr<bool> alive_ = std::make_shared<bool>(true);
    sigc::connection gpu_timer_;
    void read_gpu();
    /// The memory note under each local model, updated in place with each reading.
    void update_local_rows();
    std::string local_subtitle(const LocalModel& model) const;

    // ---- one model's panel (model_detail.cpp) ----
    struct Detail {
        Gtk::Widget* status_dot = nullptr;
        Gtk::Label* status = nullptr;
        std::vector<std::pair<Gtk::ToggleButton*, int>> steps;
        std::vector<Gtk::Label*> step_memory;
        Gtk::Widget* fit_dot = nullptr;
        Gtk::Label *fit_title = nullptr, *device = nullptr, *fit_detail = nullptr, *explanation = nullptr;
        Gtk::ProgressBar* bar = nullptr;
        Gtk::Box* suggestion_row = nullptr;
        Gtk::Label* suggestion = nullptr;
        Gtk::ToggleButton *gpu = nullptr, *cpu = nullptr;
        Gtk::Button *start = nullptr, *use = nullptr, *chat = nullptr, *trash = nullptr;
        Gtk::Label *start_label = nullptr, *use_note = nullptr;
    };
    Detail detail_;
    AdwNavigationPage* detail_page_ = nullptr;
    std::optional<LocalModel> detail_model_;
    std::string detail_shown_;
    int detail_context_ = 0;
    bool detail_cpu_ = false;
    bool detail_chosen_ = false;  ///< The user picked a step or device; until then the panel follows the suggestion.
    bool detail_updating_ = false;
    void open_detail(const LocalModel& model);
    void build_detail();
    void update_detail();
    MemoryProfile detail_profile() const;
    bool detail_running() const;
    void confirm_trash();

public:
    ~ModelsPage() override;
};

/// The Settings page: saved approvals, agents and commands, MCP servers, and schedules.
class SettingsPage : public Gtk::ScrolledWindow {
public:
    explicit SettingsPage(Controller& controller);
    void refresh();

private:
    Controller& controller_;
    Gtk::Box page_{Gtk::Orientation::VERTICAL, 24};
    Gtk::Box content_{Gtk::Orientation::VERTICAL, 24};
    std::string shown_;  ///< A fingerprint of what is displayed, so unchanged state does not rebuild the page.
    /// Appearance: light, dark or the system's, and the theme family with a light and dark preview of each.
    void build_appearance();
    void show_appearance();
    GtkWidget* mode_row_ = nullptr;
    std::vector<std::pair<Gtk::ToggleButton*, const theme::Family*>> family_buttons_;
    bool showing_appearance_ = false;
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
    /// Reasoning level, output limit and pinned reference for the selected chat.
    Gtk::MenuButton options_;
    Gtk::Popover options_popover_;
    Gtk::DropDown options_reasoning_;
    Gtk::Entry options_limit_;
    Gtk::TextView options_reference_;
    std::string options_shown_;
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
    /// The search box over sessions, projects, files, commands and chats (Ctrl+K), and going to what was chosen.
    void show_palette();
    void open_palette_item(const PaletteItem& item);
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
    /// The notes waiting to go with the next request, above the request box.
    Gtk::Box comments_{Gtk::Orientation::VERTICAL, 4};
    /// Attaches a window of another app to the session.
    Gtk::MenuButton window_button_;
    Gtk::Popover window_popover_;
    Gtk::Box window_list_{Gtk::Orientation::VERTICAL, 4};
    void fill_window_list();
    /// Asks where to save a session, or which exported session to add to a project.
    void choose_export(const std::string& id, const std::string& title, bool sanitize);
    void choose_import(const std::string& workspace);
    std::string comments_shown_;
    /// The side panel shows the changes, the plan, the project's files or its pages.
    std::string panel_ = "changes";
    bool adding_milestone_ = false;
    /// The Files panel: the list, or one file being read.
    std::string file_open_;
    int file_line_ = 1;
    void refresh_files();
    void show_file(const std::string& path, int line);
    /// The Website panel. Its widgets are kept while the panel is hidden, so the page stays loaded.
    Gtk::Box website_{Gtk::Orientation::VERTICAL, 8};
    bool website_built_ = false;
    Gtk::Entry web_address_, web_workflow_, web_port_;
    Gtk::Button web_pick_, web_verify_, web_add_, web_server_;
    Gtk::Label web_note_;
    GtkWidget* web_view_ = nullptr;
    std::unique_ptr<browser::PageServer> pages_;
    std::string pages_root_;  ///< The project the page server is for.
    std::string web_page_;    ///< The project page shown, or empty for a server's address.
    std::shared_ptr<preview::DevServer> dev_server_;
    std::optional<PreviewPick> picked_;
    void build_website();
    void refresh_website();
    void show_page(const std::string& page);
    void open_address(const std::string& address);
    void element_picked(const std::string& message);
    std::string observing_;  ///< The milestone whose observation is being written.
    void refresh_plan();
    Gtk::Widget* panel_switch();
    Gtk::ScrolledWindow changes_scroll_;
    Gtk::Stack session_stack_;
    ModelsPage models_;
    ChatPage chat_;
    SettingsPage settings_;
    Gtk::Label* live_label_ = nullptr;  ///< Shows the model's streamed words; owned by the transcript and reset when it is rebuilt.
    /// The welcome page's pose: first launch until a project exists, then the open-project pose.
    PoseView* welcome_pose_ = nullptr;
    std::string welcome_shown_;
    /// The session's state row above the request box: a pose for the state, and the state in words.
    Gtk::Box state_row_{Gtk::Orientation::HORIZONTAL, 10};
    PoseView* state_pose_ = nullptr;
    Gtk::Label state_label_;
    std::string state_shown_;
    sigc::connection restored_timer_;
    void refresh_state();
    bool updating_ = false;
    std::string sidebar_shown_, transcript_shown_, changes_shown_;
};

}  // namespace mason::app
