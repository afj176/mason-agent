// How the app looks: the theme family's colours over libadwaita, the pose art and app icon, and the pieces every page shares.
#pragma once
#include <adwaita.h>
#include <gtkmm.h>

#include <string>

#include "theme.hpp"

namespace mason::app {

/// Follow the system, or always light or dark. The choice is kept in Mason's data folder.
enum class Appearance { System, Light, Dark };
Appearance appearance();
void set_appearance(Appearance choice);

/// The theme family on screen (saved in appearance.json, shared with the mason command line tool).
const theme::Family& family();
/// Puts a family on screen and saves it.
void set_family(const theme::Family& chosen);
/// Emitted after the mode or the family changes, so pickers can show the new choice.
sigc::signal<void()>& appearance_changed();

/// The CSS that defines libadwaita's named colours for a family in light or dark.
std::string palette_css(const theme::Family& chosen, bool dark);

/// Installs the colours and widget styles, and keeps them in step with the family, light and dark, and high contrast.
void install_style();

/// Pose art: the bear and the level-bot, one still per state.
namespace pose {
inline const char* const open_project = "open-project";
inline const char* const listening = "listening";
inline const char* const reading = "reading-files";
inline const char* const planning = "planning";
inline const char* const editing = "editing";
inline const char* const checks = "running-checks";
inline const char* const review = "ready-to-review";
inline const char* const choose_model = "choose-model";
inline const char* const downloading = "downloading";
inline const char* const loading_model = "loading-model";
inline const char* const connected = "connected";
inline const char* const approval = "awaiting-approval";
inline const char* const failed = "check-failed";
inline const char* const restored = "restored";
inline const char* const attention = "needs-attention";
inline const char* const first_launch = "first-launch";
}  // namespace pose

/// The pose for what a running session is doing, from the tool it is using: editing, running checks, reading files, or planning.
std::string pose_for_tool(const std::string& tool);
/// A pose still drawn at a fixed height and its own width (poses are wider than tall), whatever room it is given.
class PoseView : public Gtk::Widget {
public:
    PoseView();
    void show(const std::string& name, int height, const std::string& meaning);

protected:
    Gtk::SizeRequestMode get_request_mode_vfunc() const override;
    void measure_vfunc(Gtk::Orientation orientation, int for_size, int& minimum, int& natural, int& minimum_baseline, int& natural_baseline) const override;
    void snapshot_vfunc(const Glib::RefPtr<Gtk::Snapshot>& snapshot) override;

private:
    Glib::RefPtr<Gdk::Texture> texture_;
    int width_ = 0, height_ = 0;
};
/// One pose still, `height` pixels tall at its own width. `meaning` is what assistive technology reads.
PoseView* pose_picture(const std::string& name, int height, const std::string& meaning);
/// Shows another pose in a view made by pose_picture.
void set_pose(PoseView& view, const std::string& name, int height, const std::string& meaning);
/// The app icon (the bear with the level-bot): the brand mark and the agent's speaker mark.
Gtk::Image* app_icon(int size);
/// A notice with a pose beside it: pose::attention when the environment needs the user, pose::failed when something broke.
Gtk::Widget* problem_notice(const std::string& name, const std::string& title, const std::string& detail);
/// The line that says who is speaking: "You", or the app icon and "Mason Agent".
Gtk::Widget* speaker(bool mason);
/// Indents a message under its speaker line.
Gtk::Widget* message_body(Gtk::Widget& content);

}  // namespace mason::app
