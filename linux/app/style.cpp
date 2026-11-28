#include "style.hpp"

#include <cmath>
#include <map>

#include "util.hpp"

namespace mason::app {

namespace {

const char* const kResources = "/site/masonagent/MasonAgent/";

// Status colours are shared by every family, so a failure reads the same in each theme (as on Windows).
struct Status { uint32_t success, caution, critical; };
constexpr Status kLightStatus{0x14734A, 0x8A5A00, 0xAF2323};
constexpr Status kDarkStatus{0x7EE0A6, 0xF2C66D, 0xFF9D94};

std::string rgba(uint32_t rgb, double alpha) {
    return "rgba(" + std::to_string((rgb >> 16) & 0xFF) + ", " + std::to_string((rgb >> 8) & 0xFF) + ", " + std::to_string(rgb & 0xFF) + ", " + std::to_string(alpha) + ")";
}

// In high contrast libadwaita's own colours win; only Mason's extra names are defined, from the theme's own.
const char* const kHighContrast = R"css(
@define-color mason_muted @window_fg_color;
@define-color mason_seam @borders;
@define-color mason_pane @window_bg_color;
@define-color mason_fit_good @success_color;
@define-color mason_fit_tight @warning_color;
@define-color mason_fit_over @error_color;
@define-color mason_accent_hover @accent_bg_color;
@define-color mason_accent_pressed @accent_bg_color;
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
.approval-card { background: @card_bg_color; border: 1px solid alpha(@accent_bg_color, 0.55); border-radius: 16px; padding: 16px; }
.composer-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 18px; padding: 10px 12px 10px 12px; box-shadow: 0 10px 28px alpha(black, 0.12); }
.composer-card:focus-within { border-color: alpha(@accent_bg_color, 0.75); }
.composer-card textview, .composer-card textview text { background: transparent; }
.composer-card scrolledwindow { background: transparent; }
.chip, .composer-card dropdown > button { border-radius: 10px; background: alpha(@view_fg_color, 0.07); padding: 4px 10px; min-height: 30px; box-shadow: none; }
.composer-hint { font-size: 0.85em; color: @mason_muted; }
.send-round { border-radius: 999px; min-width: 40px; min-height: 40px; padding: 0; }
.suggested-action:hover { background-color: @mason_accent_hover; }
.suggested-action:active, .suggested-action:checked { background-color: @mason_accent_pressed; }
.status-bar { border-top: 1px solid @mason_seam; padding: 5px 16px; font-size: 0.85em; color: @mason_muted; }
.state-row { padding: 2px 4px; }
.state-row label { color: @mason_muted; }
.welcome-title { font-size: 2.1em; font-weight: 800; letter-spacing: -0.02em; }
.welcome-lede { font-size: 1.1em; color: @mason_muted; }
.problem-notice { padding: 14px 18px; }
.notice-title { font-size: 1.25em; font-weight: 700; }
.diff-added { color: @success_color; background: alpha(@success_color, 0.12); }
.diff-removed { color: @error_color; background: alpha(@error_color, 0.12); }
.chip-menu > button { border-radius: 10px; background: alpha(@view_fg_color, 0.07); padding: 4px 10px; min-height: 30px; box-shadow: none; font-weight: 700; }
.comment-pin { opacity: 0.45; }
.comment-pin > button { min-height: 0; min-width: 0; padding: 0 3px; -gtk-icon-size: 11px; }
.comment-pin:hover, .comment-pin:focus-within { opacity: 1; }
.comment-chip { background: alpha(@accent_bg_color, 0.12); border-radius: 8px; padding: 2px 4px 2px 10px; }
.milestone { padding: 12px; }
.state-pill { border-radius: 999px; padding: 1px 9px; font-size: 0.82em; font-weight: 700; }
.state-done { color: @success_color; background: alpha(@success_color, 0.14); }
.state-ready { color: @accent_color; background: alpha(@accent_bg_color, 0.16); }
.state-recheck { color: @warning_color; background: alpha(@warning_color, 0.16); }
.state-blocked { color: @mason_muted; background: alpha(@view_fg_color, 0.08); }
.project-row { font-weight: 700; }
.session-state { font-size: 0.82em; color: @mason_muted; }
.navigation-sidebar row { border-radius: 10px; }
.navigation-sidebar row:selected { background: alpha(@accent_bg_color, 0.20); }
.sidebar-pane { background: @sidebar_bg_color; }
.sidebar-pane list { background: transparent; }
.section-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 16px; padding: 16px; }
.models-hero { padding: 4px 4px 8px 4px; }
.models-hero-title { font-size: 1.3em; font-weight: 700; }
/* The theme picker: one card per family, its light and dark previews under the name. */
.theme-choice { border-radius: 14px; padding: 10px; background: transparent; border: 2px solid @mason_seam; box-shadow: none; }
.theme-choice:hover { background: alpha(@view_fg_color, 0.04); }
.theme-choice:checked { border-color: @accent_bg_color; background: alpha(@accent_bg_color, 0.10); color: @window_fg_color; }
.theme-choice-name { font-weight: 700; }
/* A model's panel: fit tones are always paired with their words, never colour alone. */
.fit-good { color: @mason_fit_good; }
.fit-tight { color: @mason_fit_tight; }
.fit-over { color: @mason_fit_over; }
.fit-muted { color: @mason_muted; }
.fit-dot { min-width: 8px; min-height: 8px; border-radius: 999px; background: @mason_muted; }
.fit-dot.fit-good { background: @mason_fit_good; }
.fit-dot.fit-tight { background: @mason_fit_tight; }
.fit-dot.fit-over { background: @mason_fit_over; }
.fit-bar trough { min-height: 6px; border-radius: 3px; background: alpha(@view_fg_color, 0.08); }
.fit-bar progress { min-height: 6px; border-radius: 3px; background: @mason_muted; }
.fit-bar.fit-good progress { background: @mason_fit_good; }
.fit-bar.fit-tight progress { background: @mason_fit_tight; }
.fit-bar.fit-over progress { background: @mason_fit_over; }
.fit-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 12px; padding: 12px 14px; }
.fit-title { font-weight: 600; }
.fit-detail { font-size: 12px; color: @mason_muted; }
.fit-status { font-size: 13px; color: @mason_muted; }
.model-title { font-size: 22px; font-weight: 600; letter-spacing: -0.01em; }
.model-chip { font-size: 12px; font-weight: 600; padding: 2px 9px; border-radius: 999px; border: 1px solid @mason_seam; color: @mason_muted; }
.detail-heading { font-weight: 600; }
.help-button > button { min-width: 20px; min-height: 20px; padding: 0; border-radius: 999px; font-size: 11px; border: 1px solid @mason_seam; color: @mason_muted; background: transparent; box-shadow: none; }
.ladder-track { background: alpha(@view_fg_color, 0.04); border: 1px solid @mason_seam; border-radius: 12px; padding: 4px; }
.ladder-step { border-radius: 9px; padding: 8px 0 7px 0; background: transparent; box-shadow: none; }
.ladder-step:hover { background: alpha(@view_fg_color, 0.06); }
.ladder-step:checked { background: alpha(@view_fg_color, 0.13); color: @window_fg_color; }
.ladder-step .step-label { font-weight: 700; font-size: 15px; }
.ladder-step .step-memory { font-size: 11px; font-feature-settings: "tnum"; }
.suggest-button { font-size: 12px; font-weight: 600; padding: 3px 10px; min-height: 0; color: @accent_color; background: transparent; border: 1px solid @mason_seam; }
.start-button { min-height: 44px; font-weight: 700; }
.model-action { min-height: 40px; padding: 0 10px; border-radius: 8px; }
.model-action label { font-weight: normal; }
.model-actions { margin: -6px -10px 0 -10px; }
.model-footer { border-top: 1px solid @mason_seam; padding-top: 14px; font-size: 12px; color: @mason_muted; }
.footer-button > button { font-size: 12px; font-weight: normal; padding: 3px 10px; min-height: 0; background: transparent; border: 1px solid @mason_seam; }
/* Notes: the tab on the request box's top edge, the tray behind it, and Mason's suggestion under an answer. */
.notes-tab { background: @mason_pane; color: @window_fg_color; border: 1px solid @mason_seam; border-bottom-width: 0; border-radius: 10px 10px 0 0; padding: 3px 10px 3px 12px; min-height: 0; box-shadow: none; font-size: 0.88em; font-weight: 600; }
.notes-tab:hover { background: mix(@mason_pane, @window_fg_color, 0.06); }
.notes-tab.open { color: @accent_color; }
.notes-badge { background: @accent_bg_color; color: @accent_fg_color; border-radius: 999px; padding: 0 6px; font-size: 0.82em; font-weight: 700; }
.notes-badge.empty { background: alpha(@window_fg_color, 0.10); color: @mason_muted; }
.notes-tray { background: @mason_pane; border: 1px solid @mason_seam; border-bottom-width: 0; border-radius: 16px 16px 0 0; padding: 14px 16px 12px 16px; box-shadow: 0 -10px 26px alpha(black, 0.10); }
.notes-title { font-weight: 700; }
.notes-muted { color: @mason_muted; font-size: 0.9em; }
.notes-small { font-size: 0.88em; padding: 2px 10px; min-height: 28px; }
.notes-icon { min-width: 24px; min-height: 24px; padding: 0; }
.notes-link { color: @accent_color; font-size: 0.9em; padding: 2px 6px; min-height: 0; }
.notes-about-chip { background: alpha(@accent_bg_color, 0.14); border-radius: 8px; padding: 0 0 0 8px; font-size: 0.88em; }
.notes-starter { font-size: 0.88em; }
.notes-confirm { background: alpha(@error_color, 0.08); border: 1px solid alpha(@error_color, 0.35); border-radius: 10px; padding: 6px 6px 6px 12px; }
.note-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 10px; padding: 6px 4px 6px 4px; }
.note-text-button { padding: 2px 6px; min-height: 0; font-weight: normal; }
.note-text-button:hover { background: alpha(@accent_bg_color, 0.08); }
.note-meta { font-size: 0.82em; color: @mason_muted; margin: 0 6px; }
.note-card.stale .note-text, .note-card.stale .note-meta { opacity: 0.6; }
.note-card .notes-small { margin: 2px 0 2px 6px; }
.suggestion-card { background: @card_bg_color; border: 1px solid alpha(@accent_bg_color, 0.55); border-radius: 14px; padding: 12px 14px; }
.state-danger { color: @error_color; background: alpha(@error_color, 0.14); }
.state-row label.state-pill.state-done { color: @success_color; }
.state-row label.state-pill.state-recheck { color: @warning_color; }
.state-row label.state-pill.state-ready { color: @accent_color; }
.state-row label.state-pill.state-blocked { color: @mason_muted; }
.state-row label.state-pill.state-danger { color: @error_color; }
.session-state.warning { color: @warning_color; }
.session-state.error { color: @error_color; }
.ending-card { background: @card_bg_color; border: 1px solid @mason_seam; border-left-width: 4px; border-radius: 14px; padding: 12px 14px; }
.ending-card.tone-warning { border-left-color: @warning_color; background: alpha(@warning_color, 0.06); }
.ending-card.tone-warning .ending-glyph { color: @warning_color; }
.ending-card.tone-danger { border-left-color: @error_color; background: alpha(@error_color, 0.06); }
.ending-card.tone-danger .ending-glyph { color: @error_color; }
.ending-card .ending-title { font-weight: 700; }
.ending-card .changed-link { padding: 0 4px; min-height: 0; }
/* Moments: the side panel's header and shelf, the feed of cards and quiet lines, and an opened moment's lens. */
.surface-header { padding: 0 12px 0 14px; border-bottom: 1px solid @mason_seam; }
.surface-title { font-weight: 800; font-size: 1.08em; }
.surface-back { padding: 3px 10px; min-height: 0; border-radius: 8px; font-weight: 700; }
.surface-shelf { margin-left: 6px; }
/* Room under the chips for the overlay scrollbar, so it never covers them. */
.surface-shelf > viewport > box { padding-bottom: 9px; padding-top: 9px; }
.shelf-chip { padding: 3px 10px; min-height: 0; border-radius: 8px; font-size: 0.9em; font-weight: 600; box-shadow: none;
  background: alpha(@view_fg_color, 0.05); border: 1px solid @mason_seam; }
.shelf-chip:hover { background: alpha(@view_fg_color, 0.10); }
.shelf-chip.shelf-made { font-weight: 500; }
.shelf-chip.shelf-selected { background: @card_bg_color; border: 2px solid @accent_bg_color; padding: 2px 9px; }
.viewer-where { font-weight: 700; font-size: 1.05em; }
.viewer-jump { padding: 2px 12px; min-height: 0; font-size: 0.88em; font-weight: 700; }
.viewer-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 12px; padding: 10px 12px; }
.viewer-card .code-block { padding: 6px 8px; }
.viewer-card .changed-link { min-height: 0; }
.moment-card { background: @card_bg_color; border: 1px solid @mason_seam; border-radius: 12px; padding: 12px 14px; }
.moment-card.moment-live { border: 2px solid @accent_bg_color; padding: 11px 13px; }
.moment-card .changed-link { min-height: 0; padding: 2px 6px; }
.end-moment { padding: 14px 16px; }
.end-title { font-size: 1.05em; }
.moment-title { font-size: 1.0em; }
.moment-badge { min-width: 26px; min-height: 26px; border-radius: 7px; font-weight: 700; font-size: 0.95em; }
.key-badge { min-width: 28px; min-height: 22px; border-radius: 5px; font-size: 1em; }
.badge-good { color: @success_color; background: alpha(@success_color, 0.16); }
.badge-accent { color: @accent_color; background: alpha(@accent_bg_color, 0.18); }
.badge-bad { color: @error_color; background: alpha(@error_color, 0.16); }
.badge-search { color: mix(@purple_3, @window_fg_color, 0.25); background: alpha(@purple_3, 0.18); }
.badge-neutral { color: @mason_muted; background: alpha(@view_fg_color, 0.08); }
.moment-peek { opacity: 0.75; }
.moment-added { color: @success_color; background: alpha(@success_color, 0.14); border-radius: 4px; padding: 2px 8px; }
.moment-action { padding: 3px 12px; min-height: 0; }
.pass-bar trough { min-height: 6px; border-radius: 3px; background: @error_color; }
.pass-bar progress { min-height: 6px; border-radius: 3px; background: @success_color; }
.quiet-moment { padding: 5px 14px; border-radius: 10px; font-weight: normal; }
.quiet-moment label { font-size: 0.95em; }
.moment-dot { min-width: 6px; min-height: 6px; border-radius: 3px; background: alpha(@view_fg_color, 0.35); }
.moment-dot.moment-dot-failed { background: @error_color; }
.asked-moment { font-size: 0.92em; padding: 8px 14px 2px 14px; }
.moments-empty-title { font-weight: 800; font-size: 1.35em; }
.failure-line { border-left: 3px solid @error_color; padding-left: 8px; color: @error_color; }
.folder-bar { background: @accent_bg_color; border-radius: 4px; min-height: 8px; }
.stat-value { font-size: 1.5em; font-weight: 800; font-feature-settings: "tnum"; }
.viewer-card grid > box { padding: 2px 4px; }
.viewer-actions > flowboxchild { padding: 0; }
/* The session bar's chips: the published pull request, and background tasks while any is going. */
.session-chip { padding: 2px 10px; min-height: 0; border-radius: 999px; font-size: 0.88em; font-weight: 700; box-shadow: none;
  background: alpha(@view_fg_color, 0.07); }
.session-chip.session-chip-live { color: @accent_color; background: alpha(@accent_bg_color, 0.16); }
)css";

Glib::RefPtr<Gtk::CssProvider> palette() {
    static Glib::RefPtr<Gtk::CssProvider> provider = Gtk::CssProvider::create();
    return provider;
}

const theme::Family*& current_family() {
    static const theme::Family* chosen = nullptr;
    if (!chosen) chosen = &theme::saved(mason::data_home());
    return chosen;
}

void apply_palette() {
    AdwStyleManager* manager = adw_style_manager_get_default();
    if (adw_style_manager_get_high_contrast(manager)) palette()->load_from_data(kHighContrast);
    else palette()->load_from_data(palette_css(family(), adw_style_manager_get_dark(manager)));
}

void apply_choice(Appearance choice) {
    adw_style_manager_set_color_scheme(adw_style_manager_get_default(),
                                       choice == Appearance::Light ? ADW_COLOR_SCHEME_FORCE_LIGHT : choice == Appearance::Dark ? ADW_COLOR_SCHEME_FORCE_DARK : ADW_COLOR_SCHEME_DEFAULT);
}

/// Pose stills at one height, kept so a status row can switch poses without reading the file again.
Glib::RefPtr<Gdk::Texture> pose_texture(const std::string& name, int height) {
    static std::map<std::pair<std::string, int>, Glib::RefPtr<Gdk::Texture>> cache;
    const auto key = std::make_pair(name, height);
    if (const auto found = cache.find(key); found != cache.end()) return found->second;
    Glib::RefPtr<Gdk::Texture> texture;
    try {
        texture = Gdk::Texture::create_for_pixbuf(Gdk::Pixbuf::create_from_resource(std::string(kResources) + "poses/" + name + ".png", -1, height, true));
    } catch (const Glib::Error&) {
        // A missing still leaves an empty picture; the words beside it still say the state.
    }
    cache[key] = texture;
    return texture;
}

}  // namespace

std::string palette_css(const theme::Family& chosen, bool dark) {
    using theme::hex;
    using theme::mix;
    const theme::Tokens& t = chosen.tokens(dark);
    const Status status = dark ? kDarkStatus : kLightStatus;
    const uint32_t white = 0xFFFFFF, black = 0x000000;
    const uint32_t muted = mix(t.text, t.background, dark ? 0.22 : 0.28);
    // Hover is darker in light; in dark it moves toward the background.
    const uint32_t hover = dark ? mix(t.accent, t.background, 0.10) : mix(t.accent, black, 0.10);
    const uint32_t pressed = dark ? mix(t.accent, t.background, 0.20) : mix(t.accent, black, 0.20);
    const uint32_t raised = dark ? t.card : mix(t.background, white, 0.5);
    const std::string shade = dark ? "rgba(0, 0, 0, 0.36)" : rgba(t.text, 0.08);
    std::string css;
    auto define = [&css](const std::string& name, const std::string& value) { css += "@define-color " + name + " " + value + ";\n"; };
    define("accent_bg_color", hex(t.accent));
    define("accent_fg_color", hex(t.on_accent));
    define("accent_color", hex(t.accent));
    define("window_bg_color", hex(t.background));
    define("window_fg_color", hex(t.text));
    define("view_bg_color", hex(t.background));
    define("view_fg_color", hex(t.text));
    define("headerbar_bg_color", hex(t.background));
    define("headerbar_fg_color", hex(t.text));
    define("headerbar_border_color", hex(t.text));
    define("headerbar_backdrop_color", hex(t.background));
    define("headerbar_shade_color", shade);
    define("headerbar_darker_shade_color", shade);
    define("sidebar_bg_color", hex(t.pane));
    define("sidebar_fg_color", hex(t.text));
    define("sidebar_backdrop_color", hex(t.pane));
    define("sidebar_shade_color", shade);
    define("secondary_sidebar_bg_color", hex(t.pane));
    define("secondary_sidebar_fg_color", hex(t.text));
    define("secondary_sidebar_backdrop_color", hex(t.pane));
    define("secondary_sidebar_shade_color", shade);
    define("card_bg_color", hex(t.card));
    define("card_fg_color", hex(t.text));
    define("card_shade_color", shade);
    define("popover_bg_color", hex(raised));
    define("popover_fg_color", hex(t.text));
    define("popover_shade_color", shade);
    define("dialog_bg_color", hex(dark ? t.pane : t.background));
    define("dialog_fg_color", hex(t.text));
    define("thumbnail_bg_color", hex(t.card));
    define("thumbnail_fg_color", hex(t.text));
    define("shade_color", shade);
    define("success_color", hex(status.success));
    define("warning_color", hex(status.caution));
    define("error_color", hex(status.critical));
    define("destructive_color", hex(status.critical));
    define("mason_muted", hex(muted));
    define("mason_seam", hex(t.line));
    define("mason_pane", hex(t.pane));
    define("mason_fit_good", hex(status.success));
    define("mason_fit_tight", hex(status.caution));
    define("mason_fit_over", hex(status.critical));
    define("mason_accent_hover", hex(hover));
    define("mason_accent_pressed", hex(pressed));
    return css;
}

Appearance appearance() {
    const std::string saved = theme::saved_mode(mason::data_home());
    return saved == "light" ? Appearance::Light : saved == "dark" ? Appearance::Dark : Appearance::System;
}

sigc::signal<void()>& appearance_changed() {
    static sigc::signal<void()> signal;
    return signal;
}

void set_appearance(Appearance choice) {
    try {
        theme::save_mode(mason::data_home(), choice == Appearance::Light ? "light" : choice == Appearance::Dark ? "dark" : "system");
    } catch (const std::exception&) {
        // The choice still applies to this run.
    }
    apply_choice(choice);
    appearance_changed().emit();
}

const theme::Family& family() { return *current_family(); }

void set_family(const theme::Family& chosen) {
    current_family() = &chosen;
    try {
        theme::save(mason::data_home(), chosen);
    } catch (const std::exception&) {
        // The choice still applies to this run.
    }
    apply_palette();
    appearance_changed().emit();
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
    AdwStyleManager* manager = adw_style_manager_get_default();
    g_signal_connect(manager, "notify::dark", G_CALLBACK(+[](GObject*, GParamSpec*, gpointer) { apply_palette(); }), nullptr);
    g_signal_connect(manager, "notify::high-contrast", G_CALLBACK(+[](GObject*, GParamSpec*, gpointer) { apply_palette(); }), nullptr);
}

std::string pose_for_tool(const std::string& tool) {
    if (tool.empty()) return pose::planning;
    const std::string name = mason::to_lower(tool);
    auto has = [&name](std::initializer_list<const char*> words) {
        for (const char* word : words) {
            if (name.find(word) != std::string::npos) return true;
        }
        return false;
    };
    if (has({"edit", "replace", "create", "write", "delete", "rename", "move", "patch"})) return pose::editing;
    if (has({"run_", "shell", "check", "review", "test", "browser", "build", "verify", "workflow", "preview"})) return pose::checks;
    if (has({"read", "open", "list", "search", "find", "grep", "symbol", "definition", "reference", "documentation", "skill"})) return pose::reading;
    return pose::planning;
}

PoseView::PoseView() {
    set_halign(Gtk::Align::CENTER);
    set_valign(Gtk::Align::CENTER);
}

void PoseView::show(const std::string& name, int height, const std::string& meaning) {
    // Read at twice the height so the still stays sharp on a high-density screen.
    texture_ = pose_texture(name, height * 2);
    height_ = height;
    width_ = texture_ && texture_->get_height() > 0 ? static_cast<int>(std::lround(static_cast<double>(texture_->get_width()) * height / texture_->get_height())) : height;
    gtk_accessible_update_property(GTK_ACCESSIBLE(gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Mason Agent: " + meaning).c_str(), -1);
    queue_resize();
    queue_draw();
}

Gtk::SizeRequestMode PoseView::get_request_mode_vfunc() const { return Gtk::SizeRequestMode::CONSTANT_SIZE; }

void PoseView::measure_vfunc(Gtk::Orientation orientation, int, int& minimum, int& natural, int& minimum_baseline, int& natural_baseline) const {
    minimum = natural = orientation == Gtk::Orientation::HORIZONTAL ? width_ : height_;
    minimum_baseline = natural_baseline = -1;
}

void PoseView::snapshot_vfunc(const Glib::RefPtr<Gtk::Snapshot>& snapshot) {
    if (!texture_) return;
    // Centred in whatever room the widget was given; drawn at its own size.
    const graphene_rect_t area = GRAPHENE_RECT_INIT(static_cast<float>((get_width() - width_) / 2), static_cast<float>((get_height() - height_) / 2), static_cast<float>(width_), static_cast<float>(height_));
    gtk_snapshot_append_texture(snapshot->gobj(), texture_->gobj(), &area);
}

void set_pose(PoseView& view, const std::string& name, int height, const std::string& meaning) { view.show(name, height, meaning); }

PoseView* pose_picture(const std::string& name, int height, const std::string& meaning) {
    auto* view = Gtk::make_managed<PoseView>();
    view->show(name, height, meaning);
    return view;
}

Gtk::Image* app_icon(int size) {
    auto* image = Gtk::make_managed<Gtk::Image>();
    image->set_from_resource(std::string(kResources) + "icon.png");
    image->set_pixel_size(size);
    gtk_accessible_update_property(GTK_ACCESSIBLE(image->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Mason Agent", -1);
    return image;
}

Gtk::Widget* problem_notice(const std::string& name, const std::string& title, const std::string& detail) {
    const bool attention = name == pose::attention;
    auto* box = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 20);
    box->add_css_class("problem-notice");
    box->append(*pose_picture(name, attention ? 80 : 66, attention ? "needs your attention" : "something went wrong"));
    auto* text = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 4);
    text->set_valign(Gtk::Align::CENTER);
    text->set_hexpand(true);
    auto* heading = Gtk::make_managed<Gtk::Label>(title);
    heading->set_xalign(0);
    heading->set_wrap(true);
    heading->add_css_class("notice-title");
    text->append(*heading);
    if (!detail.empty()) {
        auto* body = Gtk::make_managed<Gtk::Label>(detail);
        body->set_xalign(0);
        body->set_wrap(true);
        body->add_css_class("dim-label");
        text->append(*body);
    }
    box->append(*text);
    return box;
}

Gtk::Widget* speaker(bool mason) {
    auto* row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 10);
    if (mason) {
        row->append(*app_icon(28));
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
