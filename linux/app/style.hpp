// How the app looks: Mason's colours over libadwaita, the mascot, and the pieces every page shares.
#pragma once
#include <adwaita.h>
#include <gtkmm.h>

#include <string>

namespace mason::app {

/// Follow the system, or always light or dark. The choice is kept in Mason's data folder.
enum class Appearance { System, Light, Dark };
Appearance appearance();
void set_appearance(Appearance choice);

/// Installs Mason's colours and widget styles, and keeps them in step with light and dark.
void install_style();

/// The mascot, for the brand mark and beside Mason's answers.
Gtk::Image* mascot(int size);
/// The larger art for the welcome page.
Gtk::Picture* welcome_art(int size);
/// The line that says who is speaking: "You", or the mascot and "Mason Agent".
Gtk::Widget* speaker(bool mason);
/// Indents a message under its speaker line.
Gtk::Widget* message_body(Gtk::Widget& content);

}  // namespace mason::app
