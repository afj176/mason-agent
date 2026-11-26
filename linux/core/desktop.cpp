#include "desktop.hpp"

#include <algorithm>
#include <deque>
#include <map>
#include <unistd.h>

#ifdef MASON_HAVE_ATSPI
#include <atspi/atspi.h>
#endif

namespace mason {

namespace desktop {

std::string label(const DesktopWindow& window) { return window.title.empty() || window.title == window.app ? window.app : window.app + " — " + window.title; }

std::string report(const DesktopWindow& window, const std::vector<DesktopControl>& controls) {
    std::string text = "Window: " + label(window) + "\nControls (id · kind · name · what it accepts):";
    for (const DesktopControl& control : controls) {
        text += "\n  " + control.id + " · " + control.role + (control.name.empty() ? "" : " · " + control.name);
        if (!control.value.empty()) text += " = " + utf8_prefix(control.value, 120);
        std::vector<std::string> accepts;
        if (control.can_press) accepts.push_back("invoke");
        if (control.can_set) accepts.push_back("set");
        if (!accepts.empty()) text += "  [" + join(accepts, ", ") + "]";
    }
    if (controls.empty()) text += "\n  (the window exposes no controls)";
    if (controls.size() >= kControlLimit) text += "\n  (only the first " + std::to_string(kControlLimit) + " are listed)";
    return text;
}

std::string command(const std::string& rest, Session& session, const std::shared_ptr<DesktopAutomation>& automation) {
    const std::string word = to_lower(trim(rest));
    if (word == "off" || word == "none" || word == "detach") {
        session.desktop.reset();
        return "No window is attached to this session.";
    }
    if (!automation) throw Error("This build cannot operate windows on this computer.");
    if (const std::string problem = automation->unavailable(); !problem.empty()) throw Error(problem);
    const std::vector<DesktopWindow> open = automation->windows();
    if (word.empty()) {
        std::string text;
        for (size_t i = 0; i < open.size(); i++) text += std::to_string(i + 1) + ". " + label(open[i]) + "\n";
        if (open.empty()) text += "No windows of other apps are open.\n";
        return text + (session.desktop ? "Attached: " + label(*session.desktop) + ". /window off detaches it."
                                       : "Attach one with /window NUMBER. Mason then reads that window's controls, and asks before pressing or changing anything.");
    }
    const bool digits = std::all_of(word.begin(), word.end(), [](unsigned char c) { return std::isdigit(c); });
    const size_t number = digits && word.size() < 6 ? static_cast<size_t>(std::stoi(word)) : 0;
    if (number < 1 || number > open.size()) throw Error("Give the number of a window. /window lists them.");
    session.desktop = open[number - 1];
    return "Attached " + label(open[number - 1]) + ". Mason can read its controls, and asks before pressing or changing anything.";
}

#ifdef MASON_HAVE_ATSPI

namespace {

/// Takes ownership of a string the accessibility library allocated.
std::string take(gchar* text) {
    std::string copy = text ? text : "";
    g_free(text);
    return copy;
}

/// Linux: AT-SPI, which GTK, Qt and most desktop toolkits speak. It works on X11 and on Wayland.
class AtspiDesktop : public DesktopAutomation {
public:
    ~AtspiDesktop() override { forget(); }

    std::string unavailable() override {
        if (!started_) {
            atspi_init();
            started_ = true;
        }
        AtspiAccessible* root = atspi_get_desktop(0);
        GError* error = nullptr;
        const gint count = root ? atspi_accessible_get_child_count(root, &error) : -1;
        if (error) g_error_free(error);
        if (root) g_object_unref(root);
        return count >= 0 ? "" : "The accessibility service (AT-SPI) is not answering, so Mason cannot read other apps' windows. It is part of GNOME, KDE and most desktops; on a minimal system install at-spi2-core.";
    }

    std::vector<DesktopWindow> windows() override {
        std::vector<DesktopWindow> found;
        each_window([&](AtspiAccessible*, const DesktopWindow& window) {
            found.push_back(window);
            return false;
        });
        std::sort(found.begin(), found.end(), [](const DesktopWindow& a, const DesktopWindow& b) { return to_lower(label(a)) < to_lower(label(b)); });
        return found;
    }

    std::vector<DesktopControl> observe(const DesktopWindow& window) override {
        AtspiAccessible* root = find(window);
        forget();
        std::vector<DesktopControl> controls;
        // Breadth first, so a deep tree still shows the controls nearest the top of the window.
        std::deque<std::pair<AtspiAccessible*, int>> queue = {{root, 0}};
        size_t visited = 0;
        while (!queue.empty()) {
            const auto [element, depth] = queue.front();
            queue.pop_front();
            if (controls.size() < kControlLimit && visited++ < 4000) inspect(element, depth, controls, queue);
            g_object_unref(element);
        }
        return controls;
    }

    void act(const DesktopWindow& window, const std::string& control, const std::string& operation, const std::string& value) override {
        g_object_unref(find(window));
        const auto found = elements_.find(control);
        if (found == elements_.end()) throw Error("Observe the window first, then use the id of a control from that observation.");
        GError* error = nullptr;
        gboolean done = FALSE;
        if (operation == "invoke") {
            if (AtspiAction* action = atspi_accessible_get_action_iface(found->second)) {
                done = atspi_action_do_action(action, 0, &error);
                g_object_unref(action);
            }
        } else if (AtspiEditableText* editable = atspi_accessible_get_editable_text_iface(found->second)) {
            done = atspi_editable_text_set_text_contents(editable, value.c_str(), &error);
            g_object_unref(editable);
        }
        const std::string problem = error ? error->message : "";
        if (error) g_error_free(error);
        if (!done) throw Error("The app did not accept that" + (problem.empty() ? std::string() : " (" + problem + ")") + ". Observe the window again; it may have changed.");
    }

private:
    void forget() {
        for (auto& [id, element] : elements_) g_object_unref(element);
        elements_.clear();
    }

    /// Calls `visit` for every window of every other app until it returns true. The window passed is only valid during the call.
    void each_window(const std::function<bool(AtspiAccessible*, const DesktopWindow&)>& visit) {
        if (const std::string problem = unavailable(); !problem.empty()) throw Error(problem);
        AtspiAccessible* root = atspi_get_desktop(0);
        const gint apps = atspi_accessible_get_child_count(root, nullptr);
        bool stop = false;
        for (gint i = 0; i < apps && !stop; i++) {
            AtspiAccessible* app = atspi_accessible_get_child_at_index(root, i, nullptr);
            if (!app) continue;
            const int pid = static_cast<int>(atspi_accessible_get_process_id(app, nullptr));
            if (pid != getpid() && pid > 0) {
                const std::string name = take(atspi_accessible_get_name(app, nullptr));
                const gint count = atspi_accessible_get_child_count(app, nullptr);
                for (gint j = 0; j < count && !stop; j++) {
                    AtspiAccessible* window = atspi_accessible_get_child_at_index(app, j, nullptr);
                    if (!window) continue;
                    const AtspiRole role = atspi_accessible_get_role(window, nullptr);
                    if (role == ATSPI_ROLE_FRAME || role == ATSPI_ROLE_WINDOW || role == ATSPI_ROLE_DIALOG) {
                        stop = visit(window, DesktopWindow{pid, name.empty() ? "App" : name, take(atspi_accessible_get_name(window, nullptr))});
                    }
                    g_object_unref(window);
                }
            }
            g_object_unref(app);
        }
        g_object_unref(root);
    }

    /// The attached window, with a reference the caller releases.
    AtspiAccessible* find(const DesktopWindow& wanted) {
        AtspiAccessible* match = nullptr;
        AtspiAccessible* fallback = nullptr;
        each_window([&](AtspiAccessible* window, const DesktopWindow& described) {
            if (described.pid != wanted.pid) return false;
            if (described.title == wanted.title) {
                match = static_cast<AtspiAccessible*>(g_object_ref(window));
                return true;
            }
            if (!fallback) fallback = static_cast<AtspiAccessible*>(g_object_ref(window));
            return false;
        });
        if (match && fallback) g_object_unref(fallback);
        if (!match) match = fallback;
        if (!match) throw Error(wanted.app + " has no open window. Attach a window again.");
        return match;
    }

    void inspect(AtspiAccessible* element, int depth, std::vector<DesktopControl>& controls, std::deque<std::pair<AtspiAccessible*, int>>& queue) {
        AtspiStateSet* states = atspi_accessible_get_state_set(element);
        const bool showing = !states || atspi_state_set_contains(states, ATSPI_STATE_SHOWING) || depth == 0;
        const bool editable = states && atspi_state_set_contains(states, ATSPI_STATE_EDITABLE);
        if (states) g_object_unref(states);
        if (!showing) return;
        const AtspiRole role = atspi_accessible_get_role(element, nullptr);
        DesktopControl control;
        control.role = take(atspi_accessible_get_role_name(element, nullptr));
        control.name = take(atspi_accessible_get_name(element, nullptr));
        if (AtspiAction* action = atspi_accessible_get_action_iface(element)) {
            // Containers often offer an action too; only things a person would press are listed as pressable.
            static const AtspiRole pressable[] = {ATSPI_ROLE_PUSH_BUTTON, ATSPI_ROLE_TOGGLE_BUTTON, ATSPI_ROLE_CHECK_BOX, ATSPI_ROLE_RADIO_BUTTON, ATSPI_ROLE_MENU_ITEM, ATSPI_ROLE_CHECK_MENU_ITEM,
                                                  ATSPI_ROLE_RADIO_MENU_ITEM, ATSPI_ROLE_LINK, ATSPI_ROLE_PAGE_TAB, ATSPI_ROLE_COMBO_BOX, ATSPI_ROLE_LIST_ITEM, ATSPI_ROLE_MENU};
            control.can_press = atspi_action_get_n_actions(action, nullptr) > 0 && std::find(std::begin(pressable), std::end(pressable), role) != std::end(pressable);
            g_object_unref(action);
        }
        if (AtspiEditableText* edit = atspi_accessible_get_editable_text_iface(element)) {
            control.can_set = editable;
            g_object_unref(edit);
        }
        const bool label_text = role == ATSPI_ROLE_LABEL || role == ATSPI_ROLE_STATIC || role == ATSPI_ROLE_HEADING || role == ATSPI_ROLE_STATUS_BAR;
        if (control.can_set || label_text) {
            if (AtspiText* text = atspi_accessible_get_text_iface(element)) {
                control.value = take(atspi_text_get_text(text, 0, 2000, nullptr));
                g_object_unref(text);
            }
            if (label_text && control.value == control.name) control.value.clear();
        }
        if (control.can_press || control.can_set || (label_text && !(control.name.empty() && control.value.empty()))) {
            control.id = "c" + std::to_string(controls.size() + 1);
            elements_[control.id] = static_cast<AtspiAccessible*>(g_object_ref(element));
            controls.push_back(control);
        }
        if (depth >= 32) return;
        const gint count = std::min<gint>(atspi_accessible_get_child_count(element, nullptr), 400);
        for (gint i = 0; i < count; i++) {
            if (AtspiAccessible* child = atspi_accessible_get_child_at_index(element, i, nullptr)) queue.push_back({child, depth + 1});
        }
    }

    bool started_ = false;
    std::map<std::string, AtspiAccessible*> elements_;
};

}  // namespace

std::shared_ptr<DesktopAutomation> system() {
    static const std::shared_ptr<DesktopAutomation> shared = std::make_shared<AtspiDesktop>();
    return shared;
}

#else

std::shared_ptr<DesktopAutomation> system() { return nullptr; }

#endif

}  // namespace desktop
}  // namespace mason
