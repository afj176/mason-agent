#include "style.hpp"

#include "util.hpp"

namespace mason::app {

namespace {

const char* const kResources = "/site/masonagent/MasonAgent/";

// Mason's palette, set through libadwaita's own named colours so every stock control follows it.
const char* const kLight = R"css(
@define-color accent_bg_color #4849A8;
@define-color accent_fg_color #FFFFFF;
@define-color accent_color #4849A8;
@define-color window_bg_color #F8F6F2;
@define-color window_fg_color #20263C;
@define-color view_bg_color #F8F6F2;
@define-color view_fg_color #20263C;
@define-color headerbar_bg_color #F8F6F2;
@define-color headerbar_fg_color #20263C;
@define-color headerbar_backdrop_color #F8F6F2;
@define-color headerbar_shade_color rgba(32, 38, 60, 0.08);
@define-color sidebar_bg_color #F1EEF6;
@define-color sidebar_fg_color #20263C;
@define-color sidebar_backdrop_color #F1EEF6;
@define-color sidebar_shade_color rgba(32, 38, 60, 0.08);
@define-color card_bg_color #FFFFFF;
@define-color card_fg_color #20263C;
@define-color popover_bg_color #FFFFFF;
@define-color popover_fg_color #20263C;
@define-color dialog_bg_color #FCFAF8;
@define-color dialog_fg_color #20263C;
@define-color mason_muted #535B72;
@define-color mason_seam #D9DBE1;
)css";

const char* const kDark = R"css(
@define-color accent_bg_color #C3C2FF;
@define-color accent_fg_color #171A2B;
@define-color accent_color #C3C2FF;
@define-color window_bg_color #171A2B;
@define-color window_fg_color #F8F6F2;
@define-color view_bg_color #171A2B;
@define-color view_fg_color #F8F6F2;
@define-color headerbar_bg_color #171A2B;
@define-color headerbar_fg_color #F8F6F2;
@define-color headerbar_backdrop_color #171A2B;
@define-color headerbar_shade_color rgba(0, 0, 0, 0.36);
@define-color sidebar_bg_color #131626;
@define-color sidebar_fg_color #F8F6F2;
@define-color sidebar_backdrop_color #131626;
@define-color sidebar_shade_color rgba(0, 0, 0, 0.36);
@define-color card_bg_color #24283E;
@define-color card_fg_color #F8F6F2;
@define-color popover_bg_color #24283E;
@define-color popover_fg_color #F8F6F2;
@define-color dialog_bg_color #1E2236;
@define-color dialog_fg_color #F8F6F2;
@define-color mason_muted #C3C9E0;
@define-color mason_seam #3B4059;
)css";

const char* const kWidgets = R"css(
.brand-title { font-weight: 800; font-size: 1.08em; letter-spacing: -0.01em; }
.speaker-name { font-weight: 700; }
.speaker-you { font-weight: 600; color: @mason_muted; }
.you-avatar { border-radius: 999px; background: alpha(@view_fg_color, 0.09); min-width: 28px; min-height: 28px; }
.turn { margin-bottom: 6px; }
.tool-card { background: alpha(@card_bg_color, 0.7); border: 1px solid @mason_seam; border-radius: 12px; padding: 4px 10px; }
.tool-card expander title { padding: 2px 0; }
.summary-line { font-size: 0.9em; color: @mason_muted; }
.changed-link { padding: 2px 8px; border-radius: 8px; font-weight: 600; }
.request-bubble { background: alpha(@accent_bg_color, 0.16); border-radius: 14px; padding: 10px 14px; }
.code-block { background: alpha(@view_fg_color, 0.07); border: 1px solid alpha(@view_fg_color, 0.06); border-radius: 10px; padding: 10px 12px; }
.approval-card { background: alpha(@warning_bg_color, 0.14); border: 1px solid alpha(@warning_bg_color, 0.6); border-radius: 16px; padding: 16px; }
.composer-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 18px; padding: 10px 12px 10px 12px; box-shadow: 0 10px 28px alpha(black, 0.16); }
.composer-card:focus-within { border-color: alpha(@accent_bg_color, 0.75); }
.composer-card textview, .composer-card textview text { background: transparent; }
.composer-card scrolledwindow { background: transparent; }
.chip, .composer-card dropdown > button { border-radius: 10px; background: alpha(@view_fg_color, 0.07); padding: 4px 10px; min-height: 30px; box-shadow: none; }
.composer-hint { font-size: 0.85em; color: @mason_muted; }
.send-round { border-radius: 999px; min-width: 40px; min-height: 40px; padding: 0; }
.status-bar { border-top: 1px solid @mason_seam; padding: 5px 16px; font-size: 0.85em; color: @mason_muted; }
.welcome-title { font-size: 2.1em; font-weight: 800; letter-spacing: -0.02em; }
.welcome-lede { font-size: 1.1em; color: @mason_muted; }
.diff-added { color: @success_color; background: alpha(@success_bg_color, 0.12); }
.diff-removed { color: @error_color; background: alpha(@error_bg_color, 0.12); }
.chip-menu > button { border-radius: 10px; background: alpha(@view_fg_color, 0.07); padding: 4px 10px; min-height: 30px; box-shadow: none; font-weight: 700; }
.comment-pin { opacity: 0.45; }
.comment-pin > button { min-height: 0; min-width: 0; padding: 0 3px; -gtk-icon-size: 11px; }
.comment-pin:hover, .comment-pin:focus-within { opacity: 1; }
.comment-chip { background: alpha(@accent_bg_color, 0.12); border-radius: 8px; padding: 2px 4px 2px 10px; }
.milestone { padding: 12px; }
.state-pill { border-radius: 999px; padding: 1px 9px; font-size: 0.82em; font-weight: 700; }
.state-done { color: @success_color; background: alpha(@success_bg_color, 0.16); }
.state-ready { color: @accent_color; background: alpha(@accent_bg_color, 0.16); }
.state-recheck { color: @warning_color; background: alpha(@warning_bg_color, 0.18); }
.state-blocked { color: @mason_muted; background: alpha(@view_fg_color, 0.08); }
.project-row { font-weight: 700; }
.session-state { font-size: 0.82em; color: @mason_muted; }
.navigation-sidebar row { border-radius: 10px; }
.navigation-sidebar row:selected { background: alpha(@accent_bg_color, 0.20); }
.sidebar-pane { background: @sidebar_bg_color; }
.sidebar-pane list { background: transparent; }
.section-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 16px; padding: 16px; }
)css";

Glib::RefPtr<Gtk::CssProvider> palette() {
    static Glib::RefPtr<Gtk::CssProvider> provider = Gtk::CssProvider::create();
    return provider;
}

fs::path choice_file() { return mason::data_home() / "appearance"; }

void apply_palette() {
    palette()->load_from_data(adw_style_manager_get_dark(adw_style_manager_get_default()) ? kDark : kLight);
}

void apply_choice(Appearance choice) {
    adw_style_manager_set_color_scheme(adw_style_manager_get_default(),
                                       choice == Appearance::Light ? ADW_COLOR_SCHEME_FORCE_LIGHT : choice == Appearance::Dark ? ADW_COLOR_SCHEME_FORCE_DARK : ADW_COLOR_SCHEME_DEFAULT);
}

}  // namespace

Appearance appearance() {
    try {
        const std::string saved = mason::trim(mason::read_file(choice_file()));
        if (saved == "light") return Appearance::Light;
        if (saved == "dark") return Appearance::Dark;
    } catch (const std::exception&) {
        // Nothing chosen yet.
    }
    return Appearance::System;
}

void set_appearance(Appearance choice) {
    try {
        fs::create_directories(mason::data_home());
        mason::write_file_atomic(choice_file(), choice == Appearance::Light ? "light" : choice == Appearance::Dark ? "dark" : "system");
    } catch (const std::exception&) {
        // The choice still applies to this run.
    }
    apply_choice(choice);
}

void install_style() {
    const auto display = Gdk::Display::get_default();
    auto widgets = Gtk::CssProvider::create();
    widgets->load_from_data(kWidgets);
    // The palette goes above the theme's own colour definitions; the widget styles go above that.
    Gtk::StyleContext::add_provider_for_display(display, palette(), GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
    Gtk::StyleContext::add_provider_for_display(display, widgets, GTK_STYLE_PROVIDER_PRIORITY_APPLICATION + 1);
    apply_choice(appearance());
    apply_palette();
    g_signal_connect(adw_style_manager_get_default(), "notify::dark", G_CALLBACK(+[](GObject*, GParamSpec*, gpointer) { apply_palette(); }), nullptr);
}

Gtk::Image* mascot(int size) {
    auto* image = Gtk::make_managed<Gtk::Image>();
    image->set_from_resource(std::string(kResources) + "mascot.png");
    image->set_pixel_size(size);
    gtk_accessible_update_property(GTK_ACCESSIBLE(image->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Mason Agent", -1);
    return image;
}

Gtk::Picture* welcome_art(int size) {
    auto* picture = Gtk::make_managed<Gtk::Picture>();
    picture->set_resource(std::string(kResources) + "welcome.png");
    picture->set_can_shrink(true);
    picture->set_content_fit(Gtk::ContentFit::CONTAIN);
    picture->set_size_request(size, size);
    picture->set_halign(Gtk::Align::CENTER);
    picture->set_alternative_text("Mortar, the Mason Agent mascot, holding a wrench");
    return picture;
}

Gtk::Widget* speaker(bool mason) {
    auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
    if (mason) {
        row->append(*mascot(28));
        auto* name = Gtk::make_managed<Gtk::Label>("Mason Agent");
        name->add_css_class("speaker-name");
        row->append(*name);
    } else {
        auto* icon = Gtk::make_managed<Gtk::Image>();
        icon->set_from_icon_name("avatar-default-symbolic");
        icon->add_css_class("you-avatar");
        icon->set_size_request(28, 28);
        row->append(*icon);
        auto* name = Gtk::make_managed<Gtk::Label>("You");
        name->add_css_class("speaker-you");
        row->append(*name);
    }
    return row;
}

Gtk::Widget* message_body(Gtk::Widget& content) {
    auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    box->set_margin_start(38);
    box->append(content);
    return box;
}

}  // namespace mason::app
