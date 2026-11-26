// Operating a window of another app on this computer, through the accessibility interface the app itself offers:
// the names of its controls, pressing one, and setting the value of one. The user attaches one window to a session;
// Mason reads only that window, and asks before every action.
#pragma once
#include "model.hpp"
#include "session.hpp"

namespace mason {

struct DesktopControl {
    std::string id;  ///< Names the control in the most recent observation of the window, such as "c12".
    std::string role, name, value;
    bool can_press = false, can_set = false;
};

/// A picture of a window: a PNG and its size in pixels.
struct DesktopFrame {
    int width = 0, height = 0;
    std::string png;
    /// Names this exact picture; an action is only carried out while the window still looks the same.
    std::string hash() const;
};

/// What a vision model made of a picture of the window: what it sees, and at most one click or drag it proposes.
struct PixelDecision {
    std::string action = "none";  ///< none, click or drag
    int x = 0, y = 0, end_x = 0, end_y = 0;
    std::string observation;
};

class DesktopVision {
public:
    virtual ~DesktopVision() = default;
    virtual PixelDecision see(const std::string& task, const DesktopFrame& frame, const CancelToken& cancel) = 0;
};

/// The running model, asked about a picture. It must be a model that takes images (with llama.cpp, one started with its projector).
class ModelVision : public DesktopVision {
public:
    explicit ModelVision(ModelEndpoint endpoint) : endpoint_(std::move(endpoint)) {}
    PixelDecision see(const std::string& task, const DesktopFrame& frame, const CancelToken& cancel) override;

private:
    ModelEndpoint endpoint_;
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
    /// A picture of the window as it is now, or nothing where this computer cannot take one.
    virtual std::optional<DesktopFrame> frame(const DesktopWindow&) { return std::nullopt; }
    /// Clicks at a point of the window's picture, or drags from it to a second point. Coordinates are the picture's pixels.
    virtual void pointer(const DesktopWindow&, const std::string&, int, int, int, int) { throw Error("This computer cannot click in a window by position."); }
};

namespace desktop {
constexpr size_t kControlLimit = 200;
/// The accessibility interface of this computer (AT-SPI), or null when this build has none.
std::shared_ptr<DesktopAutomation> system();
std::string label(const DesktopWindow& window);
/// What the vision model is asked.
std::string vision_prompt(const std::string& task, const DesktopFrame& frame);
/// Reads the model's reply. A point outside the picture is refused rather than clamped.
PixelDecision parse_decision(const std::string& text, const DesktopFrame& frame);
/// A PNG of 8-bit RGB pixels, stored without compression.
std::string encode_png(int width, int height, const std::string& rgb);
/// The observation as the model reads it.
std::string report(const DesktopWindow& window, const std::vector<DesktopControl>& controls);
/// The typed form: what followed "/window". Lists windows, attaches one by number, or detaches.
std::string command(const std::string& rest, Session& session, const std::shared_ptr<DesktopAutomation>& automation = system());
}  // namespace desktop

}  // namespace mason
