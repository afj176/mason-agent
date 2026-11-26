// The Files and Website panels of the side panel.
#include "window.hpp"

#include "browser.hpp"

#include <cmath>
#include <cstring>

#include <unistd.h>
#include <cstdio>
#include <thread>

#ifdef MASON_HAVE_WEBKIT
#include <webkit/webkit.h>
#endif

namespace mason::app {

namespace {

/// True for an address on this computer, which is all the Website panel shows.
bool loopback(const std::string& address) {
    for (const char* prefix : {"http://127.0.0.1:", "http://127.0.0.1/", "http://localhost:", "http://localhost/", "http://[::1]:", "http://[::1]/"}) {
        if (address.rfind(prefix, 0) == 0) return true;
    }
    return address == "http://127.0.0.1" || address == "http://localhost" || address == "about:blank";
}

#ifdef MASON_HAVE_WEBKIT
/// A navigation to anywhere but this computer is refused: the panel is for the project, not for the web.
gboolean decide(WebKitWebView*, WebKitPolicyDecision* decision, WebKitPolicyDecisionType type, gpointer) {
    if (type != WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION && type != WEBKIT_POLICY_DECISION_TYPE_NEW_WINDOW_ACTION) return FALSE;
    WebKitNavigationAction* action = webkit_navigation_policy_decision_get_navigation_action(WEBKIT_NAVIGATION_POLICY_DECISION(decision));
    const char* uri = webkit_uri_request_get_uri(webkit_navigation_action_get_request(action));
    if (uri && loopback(uri) && type == WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION) return FALSE;
    webkit_policy_decision_ignore(decision);
    return TRUE;
}
#endif

}  // namespace

void MainWindow::refresh_files() {
    const Session* session = controller_.session();
    const std::string fingerprint = "files|" + (session ? session->id : "") + "|" + file_open_ + "|" + std::to_string(file_line_);
    if (fingerprint == changes_shown_) return;
    changes_shown_ = fingerprint;
    clear(changes_);
    changes_.append(*panel_switch());
    if (!session) return;
    std::optional<Workspace> workspace;
    try {
        workspace.emplace(session->workspace);
    } catch (const std::exception& error) {
        changes_.append(*dim_label(error.what()));
        return;
    }
    if (file_open_.empty()) {
        auto paths = std::make_shared<std::vector<std::string>>();
        bool clipped = false;
        try {
            const auto listing = workspace->files(".", 2000);
            *paths = listing.paths;
            clipped = listing.truncated;
        } catch (const std::exception& error) {
            changes_.append(*dim_label(error.what()));
            return;
        }
        auto* search = Gtk::make_managed<Gtk::SearchEntry>();
        search->set_placeholder_text("Filter files");
        auto* list = Gtk::make_managed<Gtk::ListBox>();
        list->set_selection_mode(Gtk::SelectionMode::NONE);
        list->set_activate_on_single_click(true);
        list->add_css_class("navigation-sidebar");
        for (const std::string& path : *paths) {
            auto* row = Gtk::make_managed<Gtk::Label>(path);
            row->set_xalign(0);
            row->set_ellipsize(Pango::EllipsizeMode::START);
            row->add_css_class("monospace");
            list->append(*row);
        }
        list->set_filter_func([search, paths](Gtk::ListBoxRow* row) {
            const std::string needle = to_lower(search->get_text());
            const int index = row->get_index();
            return needle.empty() || (index >= 0 && static_cast<size_t>(index) < paths->size() && to_lower((*paths)[static_cast<size_t>(index)]).find(needle) != std::string::npos);
        });
        search->signal_search_changed().connect([list] { list->invalidate_filter(); });
        list->signal_row_activated().connect([this, paths](Gtk::ListBoxRow* row) {
            const int index = row->get_index();
            if (index < 0 || static_cast<size_t>(index) >= paths->size()) return;
            file_open_ = (*paths)[static_cast<size_t>(index)];
            file_line_ = 1;
            // Rebuilding the panel removes this list, so it happens after the click is over.
            Glib::signal_idle().connect_once([this] { refresh_changes(); });
        });
        changes_.append(*search);
        if (paths->empty()) changes_.append(*dim_label("This project has no files yet."));
        changes_.append(*list);
        if (clipped) changes_.append(*dim_label("The first 2,000 files are listed."));
        return;
    }

    auto* bar = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 8);
    auto* back = Gtk::make_managed<Gtk::Button>();
    back->set_icon_name("go-previous-symbolic");
    back->set_tooltip_text("Back to the list of files");
    gtk_accessible_update_property(GTK_ACCESSIBLE(back->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Back to the list of files", -1);
    back->signal_clicked().connect([this] {
        file_open_.clear();
        Glib::signal_idle().connect_once([this] { refresh_changes(); });
    });
    auto* title = Gtk::make_managed<Gtk::Label>(file_open_);
    title->set_xalign(0);
    title->set_hexpand(true);
    title->set_ellipsize(Pango::EllipsizeMode::START);
    title->set_selectable(true);
    title->add_css_class("heading");
    bar->append(*back);
    bar->append(*title);
    changes_.append(*bar);
    std::string content;
    try {
        content = workspace->read(file_open_);
    } catch (const std::exception& error) {
        changes_.append(*dim_label(error.what()));
        return;
    }
    const std::vector<std::string> lines = split(content, '\n');
    std::string numbered;
    const size_t width = std::to_string(lines.size()).size();
    for (size_t i = 0; i < lines.size(); i++) {
        const std::string number = std::to_string(i + 1);
        numbered += std::string(width - number.size(), ' ') + number + "  " + lines[i] + (i + 1 < lines.size() ? "\n" : "");
    }
    auto* view = Gtk::make_managed<Gtk::TextView>();
    view->set_editable(false);
    view->set_cursor_visible(false);
    view->set_monospace(true);
    view->set_wrap_mode(Gtk::WrapMode::NONE);
    view->set_left_margin(8);
    view->set_top_margin(8);
    view->set_bottom_margin(8);
    view->get_buffer()->set_text(numbered);
    gtk_accessible_update_property(GTK_ACCESSIBLE(view->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Contents of " + file_open_).c_str(), -1);
    const int line = std::clamp(file_line_, 1, static_cast<int>(std::max<size_t>(1, lines.size())));
    if (file_line_ > 1) {
        // The line the model pointed at is marked and brought into view.
        auto mark = view->get_buffer()->create_tag("pointed");
        mark->property_paragraph_background() = "rgba(232, 89, 12, 0.22)";
        auto start = view->get_buffer()->get_iter_at_line(line - 1);
        auto end = start;
        end.forward_to_line_end();
        view->get_buffer()->apply_tag(mark, start, end);
    }
    auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::AUTOMATIC);
    scroll->set_min_content_height(520);
    scroll->set_vexpand(true);
    scroll->add_css_class("card");
    scroll->set_child(*view);
    changes_.append(*scroll);
    if (file_line_ > 1) {
        Glib::signal_idle().connect_once([view, line] {
            auto at = view->get_buffer()->get_iter_at_line(line - 1);
            view->scroll_to(at, 0.0, 0.0, 0.25);
        });
    }
    auto* note = dim_label(std::to_string(lines.size()) + (lines.size() == 1 ? " line" : " lines") + ". This is for reading; ask Mason to change the file.");
    note->add_css_class("caption");
    changes_.append(*note);
}

void MainWindow::show_file(const std::string& path, int line) {
    panel_ = "files";
    file_open_ = path;
    file_line_ = std::max(1, line);
    if (!changes_toggle_.get_active()) changes_toggle_.set_active(true);
    changes_shown_.clear();
    refresh_changes();
}

void MainWindow::show_page(const std::string& page) {
    panel_ = "website";
    if (!changes_toggle_.get_active()) changes_toggle_.set_active(true);
    changes_shown_.clear();
    refresh_changes();
    web_address_.set_text(page);
    open_address(page);
}

void MainWindow::open_address(const std::string& given) {
    const Session* session = controller_.session();
    const std::string text = trim(given);
    if (!session || text.empty()) return;
    std::string address;
    web_page_.clear();
    picked_.reset();
    web_verify_.set_sensitive(false);
    web_add_.set_sensitive(false);
    if (text.rfind("http://", 0) == 0 || text.rfind("https://", 0) == 0) {
        if (!loopback(text)) { web_note_.set_text("The Website panel shows this project's pages and servers on this computer (127.0.0.1 or localhost), not the web."); return; }
        address = text;
    } else {
        try {
            const Workspace workspace(session->workspace);
            const std::string kind = to_lower(fs::path(text).extension().string());
            if (!workspace.exists(text) || (kind != ".html" && kind != ".htm")) { web_note_.set_text(text + " is not an HTML page in this project."); return; }
            if (!pages_) return;
            address = pages_->address_of(text);
            web_page_ = text;
        } catch (const std::exception& error) {
            web_note_.set_text(error.what());
            return;
        }
    }
    web_note_.set_text("Showing " + (web_page_.empty() ? address : web_page_) + ".");
#ifdef MASON_HAVE_WEBKIT
    if (web_view_) webkit_web_view_load_uri(WEBKIT_WEB_VIEW(web_view_), address.c_str());
#else
    // Built without a web view: the page opens in the user's own browser.
    Gtk::UriLauncher::create(address)->launch(*Glib::wrap(GTK_WINDOW(window_), true), [](const Glib::RefPtr<Gio::AsyncResult>&) {});
#endif
}

void MainWindow::element_picked(const std::string& message) {
#ifdef MASON_HAVE_WEBKIT
    std::string page = web_page_;
    if (page.empty() && web_view_) {
        const char* uri = webkit_web_view_get_uri(WEBKIT_WEB_VIEW(web_view_));
        page = uri ? uri : "the page";
    }
    picked_ = preview::pick(message, page);
    web_pick_.set_label("Pick Element");
    if (!picked_) return;
    web_note_.set_text(preview::evidence(*picked_));
    web_add_.set_sensitive(true);
    web_verify_.set_sensitive(!web_page_.empty() && !browser::find().empty());
#else
    (void)message;
#endif
}

void MainWindow::build_website() {
    website_built_ = true;
    auto* address_row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    web_address_.set_hexpand(true);
    web_address_.set_placeholder_text("index.html, or http://127.0.0.1:3000/");
    web_address_.set_tooltip_text("A page of this project, or the address of a server on this computer");
    web_address_.signal_activate().connect([this] { open_address(web_address_.get_text()); });
    auto* open = Gtk::make_managed<Gtk::Button>("Open");
    open->signal_clicked().connect([this] { open_address(web_address_.get_text()); });
    address_row->append(web_address_);
    address_row->append(*open);
    website_.append(*address_row);

#ifdef MASON_HAVE_WEBKIT
    auto* actions = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    web_pick_.set_label("Pick Element");
    web_pick_.set_tooltip_text("Then click something in the page to tell Mason about it");
    web_pick_.signal_clicked().connect([this] {
        if (!web_view_) return;
        const bool arming = web_pick_.get_label() == "Pick Element";
        webkit_web_view_evaluate_javascript(WEBKIT_WEB_VIEW(web_view_), arming ? preview::kArm : preview::kDisarm, -1, nullptr, nullptr, nullptr, nullptr, nullptr);
        web_pick_.set_label(arming ? "Cancel Pick" : "Pick Element");
        if (arming) web_note_.set_text("Click an element in the page.");
    });
    web_verify_.set_label("Verify Click");
    web_verify_.set_tooltip_text("Click the picked element in a fresh browser, at a phone width and a desktop width");
    web_verify_.set_sensitive(false);
    web_verify_.signal_clicked().connect([this] {
        const Session* session = controller_.session();
        if (!session || !picked_ || web_page_.empty()) return;
        web_verify_.set_sensitive(false);
        web_note_.set_text("Clicking it in a fresh browser…");
        const std::string folder = session->workspace, page = web_page_, selector = picked_->selector;
        std::thread([this, folder, page, selector] {
            std::string said;
            try {
                said = preview::verify_click(Workspace(folder), page, selector);
            } catch (const std::exception& error) {
                said = error.what();
            }
            Glib::signal_idle().connect_once([this, said] {
                web_note_.set_text(said);
                web_verify_.set_sensitive(picked_.has_value() && !web_page_.empty());
            });
        }).detach();
    });
    web_add_.set_label("Add to Request");
    web_add_.set_tooltip_text("Put what is shown below into your next request");
    web_add_.set_sensitive(false);
    web_add_.signal_clicked().connect([this] {
        const std::string note = web_note_.get_text();
        if (note.empty()) return;
        const std::string existing = draft_.get_buffer()->get_text();
        draft_.get_buffer()->set_text(existing + (existing.empty() || existing.back() == '\n' ? "" : "\n\n") + note + "\n");
        draft_.grab_focus();
    });
    actions->append(web_pick_);
    actions->append(web_verify_);
    website_.append(*actions);
#endif

    auto* server = Gtk::make_managed<Gtk::Expander>("Development server");
    auto* server_row = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
    web_workflow_.set_placeholder_text("Workflow");
    web_workflow_.set_tooltip_text("A workflow in mason.workflows.json that starts the project's server");
    web_workflow_.set_hexpand(true);
    web_port_.set_placeholder_text("Port");
    web_port_.set_max_width_chars(6);
    web_port_.set_width_chars(6);
    web_server_.set_label("Start");
    web_server_.signal_clicked().connect([this] {
        const Session* session = controller_.session();
        if (!session) return;
        if (dev_server_) {
            dev_server_.reset();
            web_server_.set_label("Start");
            web_note_.set_text("Stopped the development server.");
            return;
        }
        const std::string workflow = trim(web_workflow_.get_text()), digits = trim(web_port_.get_text());
        if (workflow.empty() || digits.empty() || digits.size() > 5 || digits.find_first_not_of("0123456789") != std::string::npos) {
            web_note_.set_text("Name the workflow that starts the server, and the port it listens on.");
            return;
        }
        const int port = std::stoi(digits);
        const std::string folder = session->workspace;
        web_server_.set_sensitive(false);
        web_note_.set_text("Starting " + workflow + " with your account, and waiting for port " + digits + "…");
        std::thread([this, folder, workflow, port] {
            std::shared_ptr<preview::DevServer> started;
            std::string problem;
            try {
                started = std::make_shared<preview::DevServer>(Workspace(folder), workflow, port);
            } catch (const std::exception& error) {
                problem = error.what();
            }
            Glib::signal_idle().connect_once([this, started, problem] {
                web_server_.set_sensitive(true);
                if (!started) { web_note_.set_text(problem); return; }
                dev_server_ = started;
                web_server_.set_label("Stop");
                web_address_.set_text(started->address());
                open_address(started->address());
            });
        }).detach();
    });
    server_row->append(web_workflow_);
    server_row->append(web_port_);
    server_row->append(web_server_);
    server->set_child(*server_row);
    website_.append(*server);

    web_note_.set_xalign(0);
    web_note_.set_wrap(true);
    web_note_.set_wrap_mode(Pango::WrapMode::WORD_CHAR);
    web_note_.set_selectable(true);
    web_note_.add_css_class("caption");
    website_.append(web_note_);
#ifdef MASON_HAVE_WEBKIT
    web_add_.set_halign(Gtk::Align::START);
    website_.append(web_add_);
#endif

#ifdef MASON_HAVE_WEBKIT
    WebKitUserContentManager* manager = webkit_user_content_manager_new();
    webkit_user_content_manager_register_script_message_handler(manager, "mason", nullptr);
    g_signal_connect(manager, "script-message-received::mason", G_CALLBACK(+[](WebKitUserContentManager*, JSCValue* value, gpointer self) {
        char* text = jsc_value_to_string(value);
        static_cast<MainWindow*>(self)->element_picked(text ? text : "");
        g_free(text);
    }), this);
    WebKitUserScript* script = webkit_user_script_new(preview::kScript, WEBKIT_USER_CONTENT_INJECT_TOP_FRAME, WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START, nullptr, nullptr);
    webkit_user_content_manager_add_script(manager, script);
    webkit_user_script_unref(script);
    web_view_ = GTK_WIDGET(g_object_new(WEBKIT_TYPE_WEB_VIEW, "user-content-manager", manager, nullptr));
    g_object_unref(manager);
    g_signal_connect(web_view_, "decide-policy", G_CALLBACK(decide), nullptr);
    // A page here may load only from this computer: its own requests to other hosts are blocked too.
    {
        const fs::path folder = fs::temp_directory_path() / ("mason-web-filters-" + std::to_string(getuid()));
        WebKitUserContentFilterStore* filters = webkit_user_content_filter_store_new(folder.c_str());
        GBytes* rules = g_bytes_new_static(preview::kLoopbackOnly, std::strlen(preview::kLoopbackOnly));
        g_object_ref(manager);
        webkit_user_content_filter_store_save(filters, "mason-loopback-only", rules, nullptr, +[](GObject* source, GAsyncResult* result, gpointer data) {
            WebKitUserContentManager* content = WEBKIT_USER_CONTENT_MANAGER(data);
            if (WebKitUserContentFilter* filter = webkit_user_content_filter_store_save_finish(WEBKIT_USER_CONTENT_FILTER_STORE(source), result, nullptr)) {
                webkit_user_content_manager_add_filter(content, filter);
                webkit_user_content_filter_unref(filter);
            }
            g_object_unref(content);
            g_object_unref(source);
        }, manager);
        g_bytes_unref(rules);
    }
    gtk_widget_set_vexpand(web_view_, TRUE);
    gtk_widget_set_size_request(web_view_, -1, 520);
    auto* frame = Gtk::make_managed<Gtk::Frame>();
    gtk_frame_set_child(frame->gobj(), web_view_);
    website_.append(*frame);
#else
    website_.append(*dim_label("This build has no built-in web view, so pages open in your browser. Picking elements needs a build with WebKitGTK."));
#endif
}

void MainWindow::refresh_website() {
    const Session* session = controller_.session();
    const std::string fingerprint = "website|" + (session ? session->id : "");
    if (fingerprint == changes_shown_) return;
    changes_shown_ = fingerprint;
    clear(changes_);
    changes_.append(*panel_switch());
    if (!session) return;
    if (!website_built_) build_website();
    if (pages_root_ != session->workspace) {
        // Another project: serve its pages, and stop a server started for the previous one.
        pages_.reset();
        dev_server_.reset();
        web_server_.set_label("Start");
        pages_root_ = session->workspace;
        web_page_.clear();
        picked_.reset();
        try {
            const Workspace workspace(session->workspace);
            pages_ = std::make_unique<browser::PageServer>(workspace);
            if (workspace.exists("index.html")) {
                web_address_.set_text("index.html");
                open_address("index.html");
            } else {
                web_note_.set_text("Type the path of an HTML page in this project, or start its development server.");
            }
        } catch (const std::exception& error) {
            web_note_.set_text(error.what());
        }
    }
    changes_.append(website_);
}

Gtk::Widget* table_card(const TableData& table) {
    auto* card = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 12);
    card->add_css_class("card");
    card->set_margin_top(4);
    auto* inside = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 12);
    inside->set_margin(12);
    auto* grid = Gtk::make_managed<Gtk::Grid>();
    grid->set_column_spacing(18);
    grid->set_row_spacing(6);
    for (size_t column = 0; column < table.headers.size(); column++) {
        auto* header = Gtk::make_managed<Gtk::Label>();
        header->set_markup("<b>" + escape(table.headers[column]) + "</b>");
        header->set_xalign(0);
        grid->attach(*header, static_cast<int>(column), 0);
        for (size_t row = 0; row < table.rows.size(); row++) {
            auto* cell = Gtk::make_managed<Gtk::Label>(table.rows[row][column]);
            cell->set_xalign(0);
            cell->set_selectable(true);
            grid->attach(*cell, static_cast<int>(column), static_cast<int>(row) + 1);
        }
    }
    auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    scroll->set_policy(Gtk::PolicyType::AUTOMATIC, Gtk::PolicyType::NEVER);
    scroll->set_child(*grid);
    inside->append(*scroll);
    // Each numeric column has its own chart, so columns on different scales can both be read.
    static const double colours[4][3] = {{0.29, 0.27, 0.72}, {0.91, 0.35, 0.05}, {0.10, 0.55, 0.45}, {0.75, 0.20, 0.45}};
    for (size_t which = 0; which < table.series.size(); which++) {
        const TableSeries series = table.series[which];
        std::vector<std::string> labels;
        for (const auto& row : table.rows) labels.push_back(row.empty() ? "" : row[0]);
        auto* title = Gtk::make_managed<Gtk::Label>(series.name);
        title->set_xalign(0);
        title->add_css_class("caption-heading");
        title->add_css_class("dim-label");
        inside->append(*title);
        auto* chart = Gtk::make_managed<Gtk::DrawingArea>();
        chart->set_content_height(150);
        chart->set_content_width(420);
        chart->set_hexpand(true);
        gtk_accessible_update_property(GTK_ACCESSIBLE(chart->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, ("Chart of " + series.name).c_str(), -1);
        chart->set_draw_func([chart, series, labels, which](const Cairo::RefPtr<Cairo::Context>& context, int width, int height) {
            const Gdk::RGBA ink = chart->get_color();
            double low = 0, high = 0;
            for (double value : series.values) { low = std::min(low, value); high = std::max(high, value); }
            if (high == low) high = low + 1;
            const double left = 8, right = 8, top = 18, bottom = 22;
            const double plot_width = width - left - right, plot_height = height - top - bottom;
            const size_t groups = series.values.size();
            const double group_width = plot_width / static_cast<double>(std::max<size_t>(1, groups));
            const double bar_width = std::max(2.0, std::min(64.0, group_width * 0.6));
            const double zero = top + plot_height * (high / (high - low));
            context->select_font_face("sans-serif", Cairo::ToyFontFace::Slant::NORMAL, Cairo::ToyFontFace::Weight::NORMAL);
            context->set_font_size(11);
            for (size_t group = 0; group < groups; group++) {
                const double value = series.values[group];
                const double tall = plot_height * (std::abs(value) / (high - low));
                const double middle = left + group_width * (static_cast<double>(group) + 0.5);
                context->set_source_rgb(colours[which % 4][0], colours[which % 4][1], colours[which % 4][2]);
                context->rectangle(middle - bar_width / 2, value >= 0 ? zero - tall : zero, bar_width, tall);
                context->fill();
                // The value above each bar, and the label under it, shortened to fit.
                char number[32];
                std::snprintf(number, sizeof number, value == std::floor(value) ? "%.0f" : "%.2f", value);
                Cairo::TextExtents extents;
                context->get_text_extents(number, extents);
                context->set_source_rgba(ink.get_red(), ink.get_green(), ink.get_blue(), 0.8);
                context->move_to(middle - extents.width / 2, (value >= 0 ? zero - tall : zero + tall + 12) - 4);
                context->show_text(number);
                std::string label = group < labels.size() ? labels[group] : "";
                if (utf8_length(label) > 12) label = utf8_prefix(label, 11) + "…";
                context->get_text_extents(label, extents);
                context->move_to(middle - extents.width / 2, height - 6);
                context->show_text(label);
            }
            context->set_source_rgba(ink.get_red(), ink.get_green(), ink.get_blue(), 0.35);
            context->set_line_width(1);
            context->move_to(left, zero + 0.5);
            context->line_to(width - right, zero + 0.5);
            context->stroke();
        });
        inside->append(*chart);
    }
    card->append(*inside);
    return card;
}

}  // namespace mason::app
