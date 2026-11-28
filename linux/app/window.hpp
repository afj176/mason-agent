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
#include "notes.hpp"
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
/// Text that wraps and, unless told otherwise, can be selected with the pointer.
Gtk::Label* text_label(const std::string& text, bool selectable = true);
/// A tool's name as a person reads it: "read file".
std::string label_of(const std::string& tool);
/// Changed lines with two lines of context; a row with kind 0 marks a gap.
std::vector<DiffLine> visible_rows(const std::vector<DiffLine>& rows);
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
    /// Rebuilds the page and scrolls to one of its sections: "pull-request" or "background".
    void show_section(const std::string& section);

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
    /// The sections the session bar's chips open. Owned by the page; reset whenever it is rebuilt.
    GtkWidget* delivery_group_ = nullptr;
    GtkWidget* task_group_ = nullptr;
public:
    /// The Tools groups the user opened, kept across rebuilds of the page.
    std::set<std::string> open_tool_groups_;

private:
    /// Opens a file in the system's default app, or its folder in the file manager.
    void open_file(const std::string& path, bool folder);
    /// Shows a file's text, read-only.
    void show_text(const std::string& title, const std::string& path, const std::string& text);
    /// Asks for a name and description, makes .mason/skills/NAME/SKILL.md and opens it.
    void new_skill(const std::string& workspace);
};

/// The Chat page: saved conversations, the selected one, and a message box. Chat is not tied to a project: its tools
/// are the web, skills and MCP servers, switched per conversation with the Tools control.
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
    /// Web, Skills and MCP for the selected chat.
    Gtk::MenuButton tools_;
    Gtk::Popover tools_popover_;
    Gtk::Switch tools_web_, tools_skills_, tools_mcp_;
    Gtk::Label tools_note_;
    std::string tools_shown_;
    bool tools_updating_ = false;
    /// A compact line for one tool step: its mark, the tool and what it was about.
    Gtk::Widget* step_row(const Activity& step);
    /// The card that asks about a host or an MCP call, with Once, Always and No.
    Gtk::Widget* approval_card(const ApprovalRequest& request);
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
    /// The side panel shows Moments (the run as a feed), the changes, the plan, the project's files or its pages.
    std::string panel_ = "moments";
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
    /// Moments (activity_panel.cpp): the run as a feed, newest first, with a card for each thing a step made and a
    /// quiet line for the rest. Opening a moment shows its change, report or reads in the panel.
    void refresh_activity();
    /// Opens Moments on the scene that holds this call, or on the feed when the id is empty.
    void show_activity(const std::string& activity_id);
    /// Shows Moments: the feed when `location` is empty, else "end" or "<call id>" or "<call id>|<path>" opened.
    void show_moments(const std::string& location);
    std::string activity_scene_;  ///< What Moments has open: "" for the feed, "end", a call's id, or "<call id>|<path>".
    bool activity_raw_ = false;   ///< Show each call's subject and result exactly as kept.
    double activity_scroll_ = 0;  ///< Where Moments was scrolled, kept while the same view is rebuilt.
    std::string activity_scroll_scene_;
    sigc::connection activity_scroll_watch_;
    /// The side panel: its header over whichever panel is shown. The header stays put while the panel scrolls.
    Gtk::Box side_{Gtk::Orientation::VERTICAL, 0};
    /// The header: "Moments" (and Live while a request runs) or "← Moments", then a shelf of chips for the other
    /// panels and for what the latest run made. Rebuilt only when what it shows changes.
    Gtk::Box surface_{Gtk::Orientation::HORIZONTAL, 8};
    std::string surface_shown_;
    std::string panel_session_;  ///< The session the side panel was last shown for.
    void refresh_surface();
    /// The session bar's chips: the published pull request, and background tasks still going. Read every few seconds.
    Gtk::Button pr_chip_, tasks_chip_;
    std::string chips_project_;
    sigc::connection chips_timer_;
    void refresh_chips(bool force);
    std::string observing_;  ///< The milestone whose observation is being written.
    void refresh_plan();
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
    /// How the latest request ended: the ending's glyph and headline in its tone, in front of the status text.
    Gtk::Label ending_pill_;
    std::string state_shown_;
    sigc::connection restored_timer_;
    void refresh_state();

    // ---- Notes (notes_tray.cpp): a tab on the request box, and the tray that slides up from behind it ----
    Gtk::Overlay conversation_overlay_;
    Gtk::Box* composer_card_ = nullptr;
    Gtk::Button notes_tab_;
    Gtk::Label notes_count_;
    Gtk::Revealer notes_revealer_;
    Gtk::Box notes_tray_{Gtk::Orientation::VERTICAL, 10};
    Gtk::Label notes_running_;
    Gtk::Entry notes_entry_, notes_about_entry_;
    Gtk::MenuButton notes_about_button_;
    Gtk::Popover notes_about_popover_;
    Gtk::Box notes_about_chip_{Gtk::Orientation::HORIZONTAL, 2};
    Gtk::Label notes_about_label_;
    Gtk::Button notes_add_{"Add"};
    Gtk::Box notes_empty_{Gtk::Orientation::VERTICAL, 8};
    Gtk::ScrolledWindow notes_scroll_;
    Gtk::Box notes_list_{Gtk::Orientation::VERTICAL, 6};
    Gtk::Box notes_footer_{Gtk::Orientation::HORIZONTAL, 8};
    Gtk::Label notes_sent_;
    Gtk::Box notes_confirm_{Gtk::Orientation::HORIZONTAL, 8};
    Gtk::Label notes_confirm_label_;
    std::vector<ProjectNote> notes_;
    std::vector<bool> notes_current_;
    std::string notes_root_;     ///< The project the notes are for.
    std::string notes_shown_;    ///< A fingerprint of the tray as shown.
    std::string notes_about_;    ///< The file the next note is about, or empty.
    std::string notes_editing_;  ///< The note being edited in place, or empty.
    bool notes_confirming_ = false;
    void build_notes();
    bool notes_open() const { return notes_revealer_.get_reveal_child(); }
    void open_notes(bool open);
    /// Reads the project's notes again. While a request runs they are only read when asked, not on every update.
    void load_notes(bool force);
    void refresh_notes();
    /// After a change to the notes: read them again and show them, once the click that made it is over.
    void notes_changed();
    void add_note();
    /// A toast, with an Undo button when `undo` is given.
    void notes_toast(const std::string& text, std::function<void()> undo);
    Gtk::Widget* note_row(const ProjectNote& note, bool current);
    /// The card at the end of an answer that offers the note Mason suggested.
    Gtk::Widget* suggestion_widget(const std::string& text);
    bool updating_ = false;
    std::string sidebar_shown_, transcript_shown_, changes_shown_;
};

}  // namespace mason::app
