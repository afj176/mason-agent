#include "desktop.hpp"

#include <algorithm>
#include <cmath>
#include <deque>
#include <map>
#include <unistd.h>

#ifdef MASON_HAVE_ATSPI
#include <atspi/atspi.h>
#endif
#if defined(MASON_HAVE_ATSPI) && defined(MASON_HAVE_X11)
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#endif

#include "planning.hpp"

namespace mason {

std::string DesktopFrame::hash() const { return sha256_hex(png); }

PixelDecision ModelVision::see(const std::string& task, const DesktopFrame& frame, const CancelToken& cancel) {
    static const char* const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string encoded;
    encoded.reserve(frame.png.size() * 4 / 3 + 4);
    for (size_t i = 0; i < frame.png.size(); i += 3) {
        const unsigned a = static_cast<unsigned char>(frame.png[i]);
        const unsigned b = i + 1 < frame.png.size() ? static_cast<unsigned char>(frame.png[i + 1]) : 0;
        const unsigned c = i + 2 < frame.png.size() ? static_cast<unsigned char>(frame.png[i + 2]) : 0;
        encoded += alphabet[a >> 2];
        encoded += alphabet[((a & 3) << 4) | (b >> 4)];
        encoded += i + 1 < frame.png.size() ? alphabet[((b & 15) << 2) | (c >> 6)] : '=';
        encoded += i + 2 < frame.png.size() ? alphabet[c & 63] : '=';
    }
    const json content = json::array({{{"type", "text"}, {"text", desktop::vision_prompt(task, frame)}}, {{"type", "image_url"}, {"image_url", {{"url", "data:image/png;base64," + encoded}}}}});
    const json body = {{"model", endpoint_.model}, {"stream", false}, {"temperature", 0}, {"max_tokens", 400}, {"messages", json::array({{{"role", "user"}, {"content", content}}})}};
    std::vector<std::string> headers = {"Content-Type: application/json"};
    if (!endpoint_.key.empty()) headers.push_back("Authorization: Bearer " + endpoint_.key);
    const HttpResponse reply = http_send("POST", validated_endpoint(endpoint_.url) + "v1/chat/completions", headers, body.dump(), 300, 8 * 1024 * 1024, nullptr, cancel);
    if (reply.status != 200) throw Error("The model could not look at the picture (HTTP " + std::to_string(reply.status) + "). It needs a model that takes images.");
    const json answer = json::parse(reply.body, nullptr, false);
    if (!answer.is_object() || !answer.contains("choices") || !answer["choices"].is_array() || answer["choices"].empty() || !answer["choices"][0].is_object() ||
        !answer["choices"][0].contains("message") || !answer["choices"][0]["message"].is_object() || !answer["choices"][0]["message"].contains("content") ||
        !answer["choices"][0]["message"]["content"].is_string()) {
        throw Error("The model did not answer about the picture.");
    }
    return desktop::parse_decision(answer["choices"][0]["message"]["content"].get<std::string>(), frame);
}

namespace desktop {

std::string vision_prompt(const std::string& task, const DesktopFrame& frame) {
    return "This is a picture of an application window, " + std::to_string(frame.width) + " by " + std::to_string(frame.height) + " pixels, with (0, 0) at its top left corner.\nTask: " + task +
           "\nReply with only a JSON object: {\"observation\": what you see that matters for the task, \"action\": \"none\", \"click\" or \"drag\", \"x\": pixel, \"y\": pixel, \"endX\": pixel, \"endY\": pixel}.\n"
           "Use \"none\" when the task only asks what is shown, or when nothing in the picture does what is asked. Propose at most one click or drag; endX and endY are only for a drag.";
}

PixelDecision parse_decision(const std::string& text, const DesktopFrame& frame) {
    const size_t start = text.find('{'), end = text.rfind('}');
    const json object = start == std::string::npos || end == std::string::npos || start > end ? json() : json::parse(text.substr(start, end - start + 1), nullptr, false);
    if (!object.is_object()) throw Error("The vision model did not answer in the form asked for.");
    const auto whole = [&](const char* key) { return object.contains(key) && object[key].is_number() ? static_cast<int>(std::lround(object[key].get<double>())) : 0; };
    PixelDecision decision;
    decision.action = to_lower(object.value("action", "none"));
    decision.x = whole("x"); decision.y = whole("y"); decision.end_x = whole("endX"); decision.end_y = whole("endY");
    decision.observation = utf8_prefix(object.contains("observation") && object["observation"].is_string() ? object["observation"].get<std::string>() : "", 1200);
    if (decision.action != "none" && decision.action != "click" && decision.action != "drag") throw Error("The vision model proposed something other than a click or a drag.");
    const auto inside = [&](int x, int y) { return x >= 0 && x < frame.width && y >= 0 && y < frame.height; };
    if (decision.action != "none" && (!inside(decision.x, decision.y) || (decision.action == "drag" && !inside(decision.end_x, decision.end_y)))) throw Error("The vision model pointed outside the window's picture.");
    return decision;
}

std::string encode_png(int width, int height, const std::string& rgb) {
    static uint32_t table[256];
    static bool ready = false;
    if (!ready) {
        for (uint32_t n = 0; n < 256; n++) {
            uint32_t c = n;
            for (int k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        ready = true;
    }
    const auto big = [](uint32_t value) { return std::string{static_cast<char>(value >> 24), static_cast<char>(value >> 16), static_cast<char>(value >> 8), static_cast<char>(value)}; };
    const auto chunk = [&](const char* type, const std::string& data) {
        uint32_t crc = 0xFFFFFFFFu;
        for (const char c : std::string(type) + data) crc = table[(crc ^ static_cast<unsigned char>(c)) & 0xFF] ^ (crc >> 8);
        return big(static_cast<uint32_t>(data.size())) + type + data + big(crc ^ 0xFFFFFFFFu);
    };
    // Each row starts with a filter byte of 0 (none).
    std::string raw;
    raw.reserve(static_cast<size_t>(height) * (static_cast<size_t>(width) * 3 + 1));
    for (int row = 0; row < height; row++) {
        raw += '\0';
        raw.append(rgb, static_cast<size_t>(row) * static_cast<size_t>(width) * 3, static_cast<size_t>(width) * 3);
    }
    // zlib with stored (uncompressed) blocks of up to 65535 bytes, then the Adler-32 of the data.
    std::string packed = "\x78\x01";
    for (size_t at = 0; at < raw.size() || at == 0; at += 65535) {
        const size_t size = std::min<size_t>(65535, raw.size() - at);
        packed += static_cast<char>(at + size >= raw.size() ? 1 : 0);
        packed += static_cast<char>(size & 0xFF);
        packed += static_cast<char>(size >> 8);
        packed += static_cast<char>(~size & 0xFF);
        packed += static_cast<char>((~size >> 8) & 0xFF);
        packed.append(raw, at, size);
        if (raw.empty()) break;
    }
    uint32_t a = 1, b = 0;
    for (const char c : raw) { a = (a + static_cast<unsigned char>(c)) % 65521; b = (b + a) % 65521; }
    packed += big((b << 16) | a);
    std::string header = big(static_cast<uint32_t>(width)) + big(static_cast<uint32_t>(height));
    header += std::string("\x08\x02\x00\x00\x00", 5);  // 8 bits a channel, RGB, no interlace
    return std::string("\x89PNG\r\n\x1a\n", 8) + chunk("IHDR", header) + chunk("IDAT", packed) + chunk("IEND", "");
}

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

    std::optional<DesktopFrame> frame(const DesktopWindow& window) override {
#ifdef MASON_HAVE_X11
        const char* session = std::getenv("XDG_SESSION_TYPE");
        if (session && std::string(session) == "wayland") throw Error("Taking a picture of another app's window needs an X11 session; this is a Wayland session. desktop_observe still works here.");
        const Extents place = extents(window);
        Display* display = XOpenDisplay(nullptr);
        if (!display) throw Error("The display could not be opened to take a picture of the window.");
        const Window root = DefaultRootWindow(display);
        XWindowAttributes screen{};
        XGetWindowAttributes(display, root, &screen);
        // Only the part of the window that is on the screen can be read.
        const int left = std::max(0, place.x), top = std::max(0, place.y);
        const int width = std::min(place.x + place.width, screen.width) - left, height = std::min(place.y + place.height, screen.height) - top;
        XImage* image = width > 0 && height > 0 ? XGetImage(display, root, left, top, static_cast<unsigned>(width), static_cast<unsigned>(height), AllPlanes, ZPixmap) : nullptr;
        if (!image) {
            XCloseDisplay(display);
            throw Error("The window is not on the screen, so no picture of it could be taken.");
        }
        std::string rgb;
        rgb.reserve(static_cast<size_t>(width) * static_cast<size_t>(height) * 3);
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                const unsigned long pixel = XGetPixel(image, x, y);
                rgb += static_cast<char>((pixel & image->red_mask) >> 16);
                rgb += static_cast<char>((pixel & image->green_mask) >> 8);
                rgb += static_cast<char>(pixel & image->blue_mask);
            }
        }
        XDestroyImage(image);
        XCloseDisplay(display);
        origin_x_ = left;
        origin_y_ = top;
        return DesktopFrame{width, height, encode_png(width, height, rgb)};
#else
        (void)window;
        return std::nullopt;
#endif
    }

    void pointer(const DesktopWindow& window, const std::string& action, int x, int y, int end_x, int end_y) override {
        g_object_unref(find(window));
        const auto send = [&](int px, int py, const char* what) {
            GError* error = nullptr;
            const gboolean done = atspi_generate_mouse_event(origin_x_ + px, origin_y_ + py, what, &error);
            const std::string problem = error ? error->message : "";
            if (error) g_error_free(error);
            if (!done) throw Error("The pointer could not be moved" + (problem.empty() ? std::string(".") : ": " + problem));
        };
        if (action == "drag") {
            send(x, y, "b1p");
            for (int step = 1; step <= 8; step++) {
                send(x + (end_x - x) * step / 8, y + (end_y - y) * step / 8, "abs");
                g_usleep(20000);
            }
            send(end_x, end_y, "b1r");
        } else {
            send(x, y, "b1c");
        }
    }

private:
    struct Extents { int x = 0, y = 0, width = 0, height = 0; };
    /// Where the window is on the screen.
    Extents extents(const DesktopWindow& window) {
        AtspiAccessible* element = find(window);
        Extents place;
        if (AtspiComponent* component = atspi_accessible_get_component_iface(element)) {
            if (AtspiRect* rect = atspi_component_get_extents(component, ATSPI_COORD_TYPE_SCREEN, nullptr)) {
                place = {rect->x, rect->y, rect->width, rect->height};
                g_free(rect);
            }
            g_object_unref(component);
        }
        g_object_unref(element);
        if (place.width <= 0 || place.height <= 0) throw Error("The window does not say where it is on the screen.");
        return place;
    }
    /// The screen position of the top left of the latest picture.
    int origin_x_ = 0, origin_y_ = 0;

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
