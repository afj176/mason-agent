// Mason Agent for Linux: a GTK 4 and libadwaita app over the masoncore engine.
#include <adwaita.h>
#include <gtkmm.h>

#include "controller.hpp"
#include "window.hpp"

int main(int argc, char** argv) {
    auto application = Gtk::Application::create("site.masonagent.MasonAgent");
    std::unique_ptr<mason::app::Controller> controller;
    std::unique_ptr<mason::app::MainWindow> window;

    application->signal_startup().connect([&] {
        adw_init();
        application->set_accel_for_action("app.new-session", "<Control>n");
        application->set_accel_for_action("app.quit", "<Control>q");
    });
    application->signal_activate().connect([&] {
        if (!window) {
            controller = std::make_unique<mason::app::Controller>();
            window = std::make_unique<mason::app::MainWindow>(*application, *controller);
            application->add_action("new-session", [&] { controller->new_session(); });
            application->add_action("quit", [&] { application->quit(); });
        }
        window->present();
    });
    const int status = application->run(argc, argv);
    // The window goes first; the controller then stops any run and the model it started.
    window.reset();
    controller.reset();
    return status;
}
