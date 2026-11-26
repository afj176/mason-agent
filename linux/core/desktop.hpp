// Operating a window of another app on this computer, through the accessibility interface the app itself offers:
// the names of its controls, pressing one, and setting the value of one. The user attaches one window to a session;
// Mason reads only that window, and asks before every action.
#pragma once
#include "session.hpp"

namespace mason {

struct DesktopControl {
    std::string id;  ///< Names the control in the most recent observation of the window, such as "c12".
    std::string role, name, value;
    bool can_press = false, can_set = false;
};

class DesktopAutomation {
public:
    virtual ~DesktopAutomation() = default;
    /// Why windows cannot be operated right now (no accessibility service), or empty.
    virtual std::string unavailable() = 0;
    /// The windows of other apps that are open now.
    virtual std::vector<DesktopWindow> windows() = 0;
    /// The controls of the window, in the order the app presents them. Ids are valid until the next observation.
    virtual std::vector<DesktopControl> observe(const DesktopWindow& window) = 0;
    /// Presses a control ("invoke") or sets its value ("set"). The id comes from the latest observation.
    virtual void act(const DesktopWindow& window, const std::string& control, const std::string& operation, const std::string& value) = 0;
};

namespace desktop {
constexpr size_t kControlLimit = 200;
/// The accessibility interface of this computer (AT-SPI), or null when this build has none.
std::shared_ptr<DesktopAutomation> system();
std::string label(const DesktopWindow& window);
/// The observation as the model reads it.
std::string report(const DesktopWindow& window, const std::vector<DesktopControl>& controls);
/// The typed form: what followed "/window". Lists windows, attaches one by number, or detaches.
std::string command(const std::string& rest, Session& session, const std::shared_ptr<DesktopAutomation>& automation = system());
}  // namespace desktop

}  // namespace mason
