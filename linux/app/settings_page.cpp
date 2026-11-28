// The Settings page: saved approvals, agents and commands, MCP servers, and schedules, in one place.
#include <cmath>
#include "window.hpp"

#include "background.hpp"
#include "bundles.hpp"
#include "catalogue.hpp"
#include "generation.hpp"
#include "language.hpp"
#include "delivery.hpp"
#include "experiments.hpp"
#include "network.hpp"
#include "policy.hpp"
#include "roles.hpp"

namespace mason::app {

namespace {

AdwPreferencesGroup* group(Gtk::Box& parent, const std::string& title, const std::string& description) {
    GtkWidget* widget = adw_preferences_group_new();
    adw_preferences_group_set_title(ADW_PREFERENCES_GROUP(widget), title.c_str());
    adw_preferences_group_set_description(ADW_PREFERENCES_GROUP(widget), Glib::Markup::escape_text(description).c_str());
    gtk_box_append(parent.gobj(), widget);
    return ADW_PREFERENCES_GROUP(widget);
}

AdwActionRow* row(AdwPreferencesGroup* into, const std::string& title, const std::string& subtitle) {
    GtkWidget* widget = adw_action_row_new();
    adw_preferences_row_set_title(ADW_PREFERENCES_ROW(widget), Glib::Markup::escape_text(title).c_str());
    adw_action_row_set_subtitle(ADW_ACTION_ROW(widget), Glib::Markup::escape_text(subtitle).c_str());
    adw_action_row_set_subtitle_lines(ADW_ACTION_ROW(widget), 3);
    adw_preferences_group_add(into, widget);
    return ADW_ACTION_ROW(widget);
}

Gtk::Button* button(AdwActionRow* on, const std::string& label, std::function<void()> action, bool enabled = true) {
    auto* widget = Gtk::make_managed<Gtk::Button>(label);
    widget->set_valign(Gtk::Align::CENTER);
    widget->set_sensitive(enabled);
    widget->signal_clicked().connect(std::move(action));
    adw_action_row_add_suffix(on, GTK_WIDGET(widget->gobj()));
    return widget;
}

Gtk::Switch* toggle(AdwActionRow* on, bool active, const std::string& label, std::function<void(bool)> changed) {
    auto* widget = Gtk::make_managed<Gtk::Switch>();
    widget->set_active(active);
    widget->set_valign(Gtk::Align::CENTER);
    gtk_accessible_update_property(GTK_ACCESSIBLE(widget->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, label.c_str(), -1);
    widget->property_active().signal_changed().connect([widget, changed] { changed(widget->get_active()); });
    adw_action_row_add_suffix(on, GTK_WIDGET(widget->gobj()));
    return widget;
}

/// A row to type into, with a button that hands over the text.
Gtk::Entry* entry_row(AdwPreferencesGroup* into, const std::string& title, const std::string& placeholder, const std::string& action, std::function<void(const std::string&)> submit) {
    AdwActionRow* line = row(into, title, "");
    auto* entry = Gtk::make_managed<Gtk::Entry>();
    entry->set_placeholder_text(placeholder);
    entry->set_hexpand(true);
    entry->set_valign(Gtk::Align::CENTER);
    entry->set_width_chars(44);
    gtk_accessible_update_property(GTK_ACCESSIBLE(entry->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, title.c_str(), -1);
    auto send = [entry, submit] {
        const std::string text = trim(entry->get_text());
        if (text.empty()) return;
        entry->set_text("");
        submit(text);
    };
    entry->signal_activate().connect(send);
    adw_action_row_add_suffix(line, GTK_WIDGET(entry->gobj()));
    button(line, action, send);
    return entry;
}

}  // namespace

SettingsPage::SettingsPage(Controller& controller) : controller_(controller) {
    page_.set_margin(24);
    build_appearance();
    page_.append(content_);
    GtkWidget* clamp = adw_clamp_new();
    adw_clamp_set_maximum_size(ADW_CLAMP(clamp), 860);
    adw_clamp_set_child(ADW_CLAMP(clamp), GTK_WIDGET(page_.gobj()));
    gtk_scrolled_window_set_child(gobj(), clamp);
    set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);
}

namespace {

/// A small picture of one mode of a family: the window, its side pane, a card, a line of text and an accent button.
Gtk::DrawingArea* theme_preview(const theme::Family& family, bool dark) {
    auto* area = Gtk::make_managed<Gtk::DrawingArea>();
    area->set_content_width(76);
    area->set_content_height(50);
    const theme::Tokens tokens = family.tokens(dark);
    area->set_draw_func([tokens](const Cairo::RefPtr<Cairo::Context>& cr, int width, int height) {
        auto colour = [&cr](uint32_t rgb) { cr->set_source_rgb(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0); };
        auto rounded = [&cr](double x, double y, double w, double h, double r) {
            cr->begin_new_sub_path();
            cr->arc(x + w - r, y + r, r, -M_PI / 2, 0);
            cr->arc(x + w - r, y + h - r, r, 0, M_PI / 2);
            cr->arc(x + r, y + h - r, r, M_PI / 2, M_PI);
            cr->arc(x + r, y + r, r, M_PI, 3 * M_PI / 2);
            cr->close_path();
        };
        rounded(0.5, 0.5, width - 1, height - 1, 7);
        colour(tokens.background);
        cr->fill_preserve();
        colour(tokens.line);
        cr->set_line_width(1);
        cr->stroke();
        cr->save();
        rounded(0.5, 0.5, width - 1, height - 1, 7);
        cr->clip();
        colour(tokens.pane);
        cr->rectangle(0, 0, width * 0.26, height);
        cr->fill();
        cr->restore();
        const double left = width * 0.26 + 6;
        rounded(left, 7, width - left - 6, height - 14, 4);
        colour(tokens.card);
        cr->fill_preserve();
        colour(tokens.line);
        cr->stroke();
        colour(tokens.text);
        cr->rectangle(left + 5, 13, (width - left - 16) * 0.8, 3);
        cr->fill();
        cr->rectangle(left + 5, 19, (width - left - 16) * 0.5, 3);
        cr->fill();
        rounded(left + 5, height - 20, 22, 8, 4);
        colour(tokens.accent);
        cr->fill();
    });
    return area;
}

}  // namespace

void SettingsPage::build_appearance() {
    GtkWidget* group = adw_preferences_group_new();
    adw_preferences_group_set_title(ADW_PREFERENCES_GROUP(group), "Appearance");
    adw_preferences_group_set_description(ADW_PREFERENCES_GROUP(group), "Light, dark, or the system's choice, and a theme family. The mason command uses the same family for its accent.");

    mode_row_ = adw_combo_row_new();
    adw_preferences_row_set_title(ADW_PREFERENCES_ROW(mode_row_), "Style");
    GtkStringList* modes = gtk_string_list_new(nullptr);
    for (const char* name : {"Follow System", "Light", "Dark"}) gtk_string_list_append(modes, name);
    adw_combo_row_set_model(ADW_COMBO_ROW(mode_row_), G_LIST_MODEL(modes));
    g_object_unref(modes);
    g_signal_connect(mode_row_, "notify::selected", G_CALLBACK(+[](GObject* row, GParamSpec*, gpointer self) {
        auto* page = static_cast<SettingsPage*>(self);
        if (page->showing_appearance_) return;
        const guint chosen = adw_combo_row_get_selected(ADW_COMBO_ROW(row));
        set_appearance(chosen == 1 ? Appearance::Light : chosen == 2 ? Appearance::Dark : Appearance::System);
    }), this);
    adw_preferences_group_add(ADW_PREFERENCES_GROUP(group), mode_row_);

    // One choice per family: the name, then its light and dark previews.
    auto* choices = Gtk::make_managed<Gtk::FlowBox>();
    choices->set_selection_mode(Gtk::SelectionMode::NONE);
    choices->set_homogeneous(true);
    choices->set_min_children_per_line(2);
    choices->set_max_children_per_line(5);
    choices->set_column_spacing(10);
    choices->set_row_spacing(10);
    choices->set_margin_top(12);
    gtk_accessible_update_property(GTK_ACCESSIBLE(choices->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Theme family", -1);
    Gtk::ToggleButton* first = nullptr;
    for (const theme::Family& item : theme::families()) {
        auto* button = Gtk::make_managed<Gtk::ToggleButton>();
        button->add_css_class("theme-choice");
        if (first) button->set_group(*first);
        else first = button;
        auto* inside = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
        auto* name = Gtk::make_managed<Gtk::Label>(item.name);
        name->add_css_class("theme-choice-name");
        name->set_xalign(0);
        auto* previews = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::HORIZONTAL, 6);
        previews->append(*theme_preview(item, false));
        previews->append(*theme_preview(item, true));
        inside->append(*name);
        inside->append(*previews);
        button->set_child(*inside);
        button->set_tooltip_text(item.name + (&item == &theme::default_family() ? " (default): light and dark" : ": light and dark"));
        gtk_accessible_update_property(GTK_ACCESSIBLE(button->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, (item.name + " theme").c_str(), -1);
        const theme::Family* chosen = &item;
        button->signal_toggled().connect([this, button, chosen] {
            if (showing_appearance_ || !button->get_active()) return;
            set_family(*chosen);
        });
        choices->append(*button);
        family_buttons_.push_back({button, chosen});
    }
    adw_preferences_group_add(ADW_PREFERENCES_GROUP(group), GTK_WIDGET(choices->gobj()));
    gtk_box_append(page_.gobj(), group);
    show_appearance();
    // The header's Appearance menu changes the same choices.
    appearance_changed().connect([this] { show_appearance(); });
}

void SettingsPage::show_appearance() {
    showing_appearance_ = true;
    const Appearance mode = appearance();
    adw_combo_row_set_selected(ADW_COMBO_ROW(mode_row_), mode == Appearance::Light ? 1 : mode == Appearance::Dark ? 2 : 0);
    for (const auto& [button, item] : family_buttons_) button->set_active(item == &family());
    showing_appearance_ = false;
}

void SettingsPage::refresh() {
    Controller& c = controller_;
    const fs::path home = c.home();
    if (home.empty()) return;
    const Session* session = c.session();
    const std::string workspace = session ? session->workspace : "";

    std::vector<McpServer> servers;
    std::string problem;
    try {
        servers = mcp::all(home);
    } catch (const std::exception& error) {
        problem = error.what();
    }
    const std::vector<Schedule> all_schedules = schedules::load(home);
    const ApprovalRules rules(home, workspace);
    const std::vector<Definition> agents = workspace.empty() ? std::vector<Definition>{} : customization::agents(home, workspace);
    const std::vector<Definition> commands = workspace.empty() ? std::vector<Definition>{} : customization::commands(home, workspace);

    const std::optional<ProjectPolicy> policy = workspace.empty() ? std::nullopt : policies::load(home, workspace, true);
    const ModelRoles chosen_roles = roles::load(home);
    const std::string network_mode = network_settings::mode(home);
    const std::set<std::string> project_hosts = workspace.empty() ? std::set<std::string>{} : network_settings::always_allowed(home, workspace);
    const std::set<std::string> chat_hosts = network_settings::chat_hosts(home);
    std::vector<Skill> skill_list;
    if (!workspace.empty()) {
        try {
            skill_list = skills::all(Workspace(workspace), home);
        } catch (const std::exception&) {
        }
    }
    const std::vector<InstalledBundle> installed = bundles::list(home);
    std::vector<Experiment> trials;
    std::optional<DeliveryDraft> draft;
    std::vector<std::pair<BackgroundTask, TaskStatus>> tasks;
    if (!workspace.empty()) {
        try {
            const Workspace project(workspace);
            trials = experiments::list(project, home);
            draft = delivery::current(project, home);
            tasks = background::all(&project, home);
            if (tasks.size() > 12) tasks.resize(12);
        } catch (const std::exception&) {
        }
    }
    // The page is rebuilt only when what it lists changes, so typing in an entry is not interrupted.
    const std::optional<EmbeddingSettings> embeddings = semantic::load(home);
    std::string print = workspace + "|" + (session ? session->agent : "") + "|" + problem + "|" + c.mcp_busy;
    print += embeddings ? embeddings->url + embeddings->model + embeddings->rerank_url + embeddings->rerank_model + "|" : "none|";
    for (const auto& rule : rules.rules()) print += rule.tool + rule.prefix + "|";
    print += policy ? policy->name + (policy->hooks ? "h" : "") + join(policy->disabled_tools, ",") + (policy->workflows ? join(*policy->workflows, ",") : "any") + std::to_string(policy->expires) + "|" : "nopolicy|";
    print += (chosen_roles.reviewer ? chosen_roles.reviewer->url + chosen_roles.reviewer->model : "") + "|" + (chosen_roles.visual ? chosen_roles.visual->url + chosen_roles.visual->model : "") + "|" + chosen_roles.trace + std::to_string(chosen_roles.limit) + "|" + network_mode + "|";
    for (const InstalledBundle& bundle : installed) print += bundle.name + bundle.version + "|";
    print += policy ? join(policy->disabled_skills, ",") + "|" : "|";
    for (const std::string& host : project_hosts) print += "h:" + host + "|";
    for (const std::string& host : chat_hosts) print += "c:" + host + "|";
    for (const Skill& skill : skill_list) print += skill.name + skill.source + skill.state() + skill.description + "|";
    for (const Experiment& trial : trials) print += trial.name + "|";
    print += draft ? draft->id + draft->url + "|" : "nodraft|";
    for (const auto& [task, status] : tasks) print += task.id + status.state + (status.approval ? status.approval->id : "") + "|";
    for (const Definition& item : agents) print += item.name + "|";
    for (const Definition& item : commands) print += item.name + "|";
    for (const McpServer& server : servers) print += server.name + server.transport() + (server.enabled ? "1" : "0") + join(server.tools, ",") + "|";
    for (const auto& [name, offered] : c.mcp_offered) print += name + std::to_string(offered.size()) + "|";
    for (const Schedule& schedule : all_schedules) print += schedule.id + (schedule.enabled ? "1" : "0") + schedule.last_outcome + std::to_string(schedule.last_run) + "|";
    if (print == shown_) return;
    shown_ = print;
    delivery_group_ = task_group_ = nullptr;
    // A switch flipped on the page rebuilds it; the page stays where it was scrolled.
    const double scrolled = get_vadjustment()->get_value();
    clear(content_);
    Glib::signal_idle().connect_once([this, scrolled] { get_vadjustment()->set_value(scrolled); });

    auto report = [&c](const std::function<void()>& work) {
        try {
            work();
        } catch (const std::exception& error) {
            c.notice.emit(error.what());
        }
        c.changed.emit();
    };
    const std::string project = workspace.empty() ? "" : fs::path(workspace).filename().string();

    // ---- saved approvals ----
    AdwPreferencesGroup* approvals = group(content_, "Saved approvals", workspace.empty() ? "Open a project to see the rules saved for it."
        : "Things Mason may do in " + project + " without asking. Rules only skip the question; they never widen the sandbox.");
    if (!workspace.empty() && rules.rules().empty()) row(approvals, "No saved rules", "Choose Always Allow… on an approval to save one for this project.");
    for (size_t index = 0; index < rules.rules().size(); index++) {
        const auto& rule = rules.rules()[index];
        AdwActionRow* line = row(approvals, "Allow " + replace_all(rule.tool, "_", " "), "when it starts with  " + rule.prefix);
        button(line, "Revoke", [report, home, workspace, index] {
            report([&] {
                ApprovalRules current(home, workspace);
                if (index < current.rules().size()) current.remove(index);
            });
        });
    }

    // ---- agents and commands ----
    AdwPreferencesGroup* agent_group = group(content_, "Agents", "Standing instructions for a session, as Markdown files in .mason/agents (also .agents/agents and .claude/agents) in the project.");
    if (agents.empty()) row(agent_group, "No custom agents", workspace.empty() ? "Open a project first." : "Add a Markdown file to .mason/agents in " + project + ".");
    for (const Definition& agent : agents) {
        const bool current = session && to_lower(session->agent) == to_lower(agent.name);
        AdwActionRow* line = row(agent_group, agent.name, agent.description + "  ·  " + agent.source);
        const std::string name = agent.name;
        button(line, current ? "In Use" : "Use", [&c, name] { c.choose_agent(name); }, !current && !c.running);
    }
    if (session && !session->agent.empty()) {
        AdwActionRow* line = row(agent_group, "Default agent", "Go back to Mason without extra instructions.");
        button(line, "Use Default", [&c] { c.choose_agent("none"); }, !c.running);
    }
    AdwPreferencesGroup* command_group = group(content_, "Slash commands", "Requests you run by name, as Markdown files in .mason/commands (also .claude/commands and .opencode/command). Type the name in the request box.");
    if (commands.empty()) row(command_group, "No custom commands", workspace.empty() ? "Open a project first." : "Add a Markdown file to .mason/commands in " + project + ".");
    for (const Definition& command : commands) row(command_group, "/" + command.name, command.description + "  ·  " + command.source);

    // ---- skills ----
    AdwPreferencesGroup* skill_group = group(content_, "Skills", "Instructions for particular kinds of work, as SKILL.md files in .mason/skills (also .claude/skills and .agents/skills) and in your bundles. Mason reads one when the work fits. A skill switched off is left out for this project.");
    if (workspace.empty()) row(skill_group, "No project open", "Open a project to see its skills.");
    else if (skill_list.empty()) row(skill_group, "No skills yet", "Choose New Skill… to make one in .mason/skills.");
    for (const Skill& skill : skill_list) {
        AdwActionRow* line = row(skill_group, skill.name, skill.source + "  ·  " + skill.description);
        const std::string path = skill.file.string(), name = skill.name, title = skill.name;
        button(line, "Open", [this, path] { open_file(path, false); });
        button(line, "Show in Folder", [this, path] { open_file(path, true); });
        button(line, "View", [this, path, title] {
            std::string text;
            try {
                text = read_file(path);
            } catch (const std::exception& error) {
                text = error.what();
            }
            show_text(title + " · SKILL.md", path, text);
        });
        if (skill.shadowed()) {
            auto* hidden = dim_label("Hidden by " + skill.shadowed_by);
            hidden->set_valign(Gtk::Align::CENTER);
            adw_action_row_add_suffix(line, GTK_WIDGET(hidden->gobj()));
        } else {
            toggle(line, skill.enabled, "Use the skill " + name, [report, home, workspace, name](bool on) { report([&] { policies::set_skill(name, on, workspace, home); }); });
        }
    }
    if (!workspace.empty()) {
        AdwActionRow* new_row = row(skill_group, "New skill", "Makes .mason/skills/NAME/SKILL.md from a short template and opens it.");
        button(new_row, "New Skill…", [this, workspace] { new_skill(workspace); });
    }

    // ---- tools ----
    AdwPreferencesGroup* tools_group = group(content_, "Tools", workspace.empty() ? "Every tool Mason can be offered. Open a project to switch tools off for it."
        : "Every tool Mason can be offered in " + project + ". A tool switched off is not offered at all. Kept in the project's policy, outside the project.");
    const std::vector<std::string> tools_off = policy ? policy->disabled_tools : std::vector<std::string>{};
    for (const auto& [group_key, group_title] : tool_catalogue::groups()) {
        int total = 0, on = 0;
        for (const CatalogueTool& tool : tool_catalogue::all()) {
            if (tool.group != group_key) continue;
            total++;
            if (tool.always_on || std::find(tools_off.begin(), tools_off.end(), tool.name) == tools_off.end()) on++;
        }
        GtkWidget* expander = adw_expander_row_new();
        adw_preferences_row_set_title(ADW_PREFERENCES_ROW(expander), group_title.c_str());
        adw_expander_row_set_subtitle(ADW_EXPANDER_ROW(expander), (std::to_string(on) + " of " + std::to_string(total) + " on").c_str());
        // An open group stays open when the page is rebuilt.
        adw_expander_row_set_expanded(ADW_EXPANDER_ROW(expander), open_tool_groups_.count(group_key) > 0);
        auto* key = new std::string(group_key);
        using Opened = std::pair<SettingsPage*, std::string*>;
        g_signal_connect_data(expander, "notify::expanded", G_CALLBACK(+[](GObject* row, GParamSpec*, gpointer data) {
            auto* page = static_cast<Opened*>(data);
            if (adw_expander_row_get_expanded(ADW_EXPANDER_ROW(row))) page->first->open_tool_groups_.insert(*page->second);
            else page->first->open_tool_groups_.erase(*page->second);
        }), new Opened(this, key), +[](gpointer data, GClosure*) {
            auto* pair = static_cast<Opened*>(data);
            delete pair->second;
            delete pair;
        }, GConnectFlags(0));
        for (const CatalogueTool& tool : tool_catalogue::all()) {
            if (tool.group != group_key) continue;
            GtkWidget* tool_row = adw_action_row_new();
            adw_preferences_row_set_title(ADW_PREFERENCES_ROW(tool_row), tool.name.c_str());
            std::string about = tool.line;
            if (tool.changes) about += "  ·  Changes files or runs commands";
            if (tool.internet) about += "  ·  Uses the internet";
            adw_action_row_set_subtitle(ADW_ACTION_ROW(tool_row), Glib::Markup::escape_text(about).c_str());
            if (tool.always_on) {
                auto* always = dim_label("Always on");
                always->set_valign(Gtk::Align::CENTER);
                adw_action_row_add_suffix(ADW_ACTION_ROW(tool_row), GTK_WIDGET(always->gobj()));
            } else {
                const std::string name = tool.name;
                const bool active = std::find(tools_off.begin(), tools_off.end(), tool.name) == tools_off.end();
                Gtk::Switch* control = toggle(ADW_ACTION_ROW(tool_row), active, "Offer " + name, [report, home, workspace, name](bool enabled) { report([&] { policies::set_tool(name, enabled, workspace, home); }); });
                control->set_sensitive(!workspace.empty());
            }
            adw_expander_row_add_row(ADW_EXPANDER_ROW(expander), tool_row);
        }
        adw_preferences_group_add(tools_group, expander);
    }

    // ---- MCP servers ----
    AdwPreferencesGroup* mcp_group = group(content_, "MCP servers", "Servers on this computer whose tools Mason may call. Every call shows its arguments and asks first. Registered for all projects, in Mason's data folder.");
    if (!problem.empty()) row(mcp_group, "The MCP configuration could not be read", problem);
    for (const McpServer& server : servers) {
        const std::string name = server.name;
        AdwActionRow* line = row(mcp_group, server.name, server.transport() + "\n" + (server.tools.empty() ? "No tools allowed yet" : "May call: " + join(server.tools, ", ")));
        button(line, c.mcp_busy == name ? "Asking…" : "Discover Tools", [&c, name] { c.discover_mcp(name); }, c.mcp_busy.empty());
        button(line, "Remove", [report, home, name] {
            report([&] {
                std::vector<McpServer> all = mcp::all(home);
                all.erase(std::remove_if(all.begin(), all.end(), [&](const McpServer& item) { return item.name == name; }), all.end());
                mcp::save(all, home);
            });
        });
        toggle(line, server.enabled, "Use " + name, [report, home, name](bool on) {
            report([&] {
                std::vector<McpServer> all = mcp::all(home);
                for (McpServer& item : all) {
                    if (item.name == name) item.enabled = on;
                }
                mcp::save(all, home);
            });
        });
        const auto offered = c.mcp_offered.find(name);
        if (offered == c.mcp_offered.end()) continue;
        for (const McpTool& tool : offered->second) {
            AdwActionRow* tool_row = row(mcp_group, "      " + tool.name, "      " + tool.description);
            const std::string tool_name = tool.name;
            toggle(tool_row, server.allows_tool(tool.name), "Allow " + tool.name, [report, home, name, tool_name](bool on) {
                report([&] {
                    std::vector<McpServer> all = mcp::all(home);
                    for (McpServer& item : all) {
                        if (item.name != name) continue;
                        item.tools.erase(std::remove(item.tools.begin(), item.tools.end(), tool_name), item.tools.end());
                        if (on) item.tools.push_back(tool_name);
                    }
                    mcp::save(all, home);
                });
            });
        }
    }
    entry_row(mcp_group, "Add a server", "NAME  http://127.0.0.1:PORT/mcp   or   NAME  /full/path/to/program  arguments", "Add", [report, home, &c](const std::string& text) {
        std::string added;
        report([&] {
            const std::vector<std::string> words = split(text, ' ', false);
            if (words.size() < 2) throw Error("Give a name, then the server's http address on this computer or the full path of a program.");
            std::vector<McpServer> all = mcp::all(home);
            McpServer server;
            server.name = words[0];
            if (words[1].front() == '/') {
                server.executable = words[1];
                server.arguments.assign(words.begin() + 2, words.end());
            } else {
                server.endpoint = words[1];
            }
            mcp::validate(server);
            all.push_back(server);
            mcp::save(all, home);
            added = server.name;
        });
        if (!added.empty()) c.discover_mcp(added);
    });

    // ---- internet ----
    AdwPreferencesGroup* internet_group = group(content_, "Internet", "Web pages are read over HTTPS from public hosts only. Every new host asks first.");
    {
        // Three choices: the default (web tools ask, commands have no network), both ask, or off for both.
        GtkWidget* choice = adw_combo_row_new();
        adw_preferences_row_set_title(ADW_PREFERENCES_ROW(choice), "Internet access");
        adw_action_row_set_subtitle(ADW_ACTION_ROW(choice), "Default: web_fetch and web_search ask before each host, in projects and in Chat; sandboxed commands in Auto have no network. "
                                                            "Ask for both: commands ask too. Off: neither. A session can choose its own with /network.");
        adw_action_row_set_subtitle_lines(ADW_ACTION_ROW(choice), 4);
        GtkStringList* modes = gtk_string_list_new(nullptr);
        for (const char* name : {"Default", "Ask for both", "Off"}) gtk_string_list_append(modes, name);
        adw_combo_row_set_model(ADW_COMBO_ROW(choice), G_LIST_MODEL(modes));
        g_object_unref(modes);
        adw_combo_row_set_selected(ADW_COMBO_ROW(choice), network_mode == "ask" ? 1 : network_mode == "off" ? 2 : 0);
        auto* chosen = new std::function<void(guint)>([report, home](guint selected) {
            report([&] { network_settings::command(selected == 1 ? "ask" : selected == 2 ? "off" : "default", home); });
        });
        g_signal_connect_data(choice, "notify::selected", G_CALLBACK(+[](GObject* row, GParamSpec*, gpointer data) {
            const guint selected = adw_combo_row_get_selected(ADW_COMBO_ROW(row));
            // Run once the combo's own handling is over, as the page rebuilds itself.
            auto* call = new std::function<void()>([action = *static_cast<std::function<void(guint)>*>(data), selected] { action(selected); });
            g_idle_add_once(+[](gpointer pending) {
                auto* run = static_cast<std::function<void()>*>(pending);
                (*run)();
                delete run;
            }, call);
        }), chosen, +[](gpointer data, GClosure*) { delete static_cast<std::function<void(guint)>*>(data); }, GConnectFlags(0));
        adw_preferences_group_add(internet_group, choice);
    }
    if (!workspace.empty()) {
        if (project_hosts.empty()) row(internet_group, "No hosts always allowed in " + project, "Choose Always Allow when Mason asks to read a host or a command asks to reach one.");
        for (const std::string& host : project_hosts) {
            AdwActionRow* line = row(internet_group, host, "Always allowed in " + project);
            button(line, "Remove", [report, home, workspace, host] { report([&] { network_settings::forget(home, workspace, host); }); });
        }
    }
    for (const std::string& host : chat_hosts) {
        AdwActionRow* line = row(internet_group, host, "Always allowed in Chat, on this computer");
        button(line, "Remove", [report, home, host] { report([&] { network_settings::forget_in_chat(home, host); }); });
    }

    // ---- code search by meaning ----
    AdwPreferencesGroup* search_group = group(content_, "Code search by meaning", "With an embeddings server chosen, Mason can find code from a description such as “where are invoice totals calculated”. Any server on this computer that answers /v1/embeddings works, for example llama-server --embedding with an embeddings model.");
    auto typed = [&c, home](const std::string& arguments) {
        try {
            c.notice.emit(split(semantic::command(arguments, home), '\n').front());
        } catch (const std::exception& error) {
            c.notice.emit(error.what());
        }
        c.changed.emit();
    };
    if (embeddings) {
        AdwActionRow* line = row(search_group, embeddings->model, embeddings->url + (embeddings->rerank_url.empty() ? "" : "\nReranked by " + embeddings->rerank_model + " at " + embeddings->rerank_url));
        button(line, "Test", [typed] { typed("test"); });
        button(line, "Turn Off", [typed] { typed("off"); });
        if (!embeddings->rerank_url.empty()) button(line, "No Reranker", [typed] { typed("rerank off"); });
        entry_row(search_group, "Reranker (optional)", "http://127.0.0.1:8082/  MODEL", "Use", [typed](const std::string& text) { typed("rerank " + text); });
    } else {
        row(search_group, "Off", "No embeddings server is chosen, so Mason searches by exact text only.");
    }
    entry_row(search_group, embeddings ? "Change the server" : "Choose a server", "http://127.0.0.1:8081/  MODEL", "Use", [typed](const std::string& text) { typed(text); });

    // ---- schedules ----
    AdwPreferencesGroup* schedule_group = group(content_, "Schedules", "Requests Mason runs by itself while this app is open (or when mason --run-due is run). A scheduled run is unattended: nothing is approved for it, so it is read-only unless it is marked Auto.");
    if (all_schedules.empty()) row(schedule_group, "No schedules", "Add one below. It will work in the current project with the model that is running now.");
    for (const Schedule& schedule : all_schedules) {
        const std::string name = schedule.name;
        std::string detail = schedule.summary() + "  ·  " + (schedule.automatic ? "Auto" : "read-only") + "  ·  " + fs::path(schedule.workspace).filename().string() + "\n" + utf8_prefix(schedule.prompt, 120);
        if (!schedule.last_outcome.empty()) detail += "\nLast run: " + schedule.last_outcome;
        AdwActionRow* line = row(schedule_group, schedule.name, detail);
        button(line, "Run Now", [&c, name] { c.schedule_command("run " + name); });
        button(line, "Remove", [&c, name] { c.schedule_command("remove " + name); c.changed.emit(); });
        toggle(line, schedule.enabled, "Run " + name + " on schedule", [&c, name](bool on) { c.schedule_command(std::string(on ? "on " : "off ") + name); c.changed.emit(); });
    }
    entry_row(schedule_group, "Add a schedule", "NAME  daily 09:00  [auto]  --  what to do    (also hourly :MM, weekdays HH:MM, weekly DAY HH:MM)", "Add", [&c](const std::string& text) {
        c.schedule_command("add " + text);
        c.changed.emit();
    });

    // ---- what follows shares its code with the command line: each control runs the command it names ----
    auto run = [&c](const std::string& name, const std::string& rest) { c.host_command(name, rest); };

    // ---- project policy ----
    if (session) {
        AdwPreferencesGroup* policy_group = group(content_, "Project policy", "Limits for this project, kept outside it so nothing in the project can change them.");
        AdwActionRow* hooks = row(policy_group, "Run the project's hooks", "In Auto, workflows marked afterEdit run after each edit, and ones marked beforeVerify must pass before a request may finish.");
        toggle(hooks, policy && policy->hooks, "Run the project's hooks", [run](bool on) { run("/policy", on ? "hooks on" : "hooks off"); });
        row(policy_group, "Workflows that may run", policy && policy->workflows ? (policy->workflows->empty() ? std::string("None") : join(*policy->workflows, ", ")) : "Any workflow in mason.workflows.json.");
        entry_row(policy_group, "Limit workflows", "test lint    (or: any)", "Set", [run](const std::string& text) { run("/policy", "workflows " + text); });
        if (policy) {
            AdwActionRow* clear_row = row(policy_group, "Policy " + policy->name, policy->expires != 0 ? "It lapses by itself; see /policy for the date." : "It stays until you remove it.");
            button(clear_row, "Remove Policy", [run] { run("/policy", "clear"); });
        }
    }

    // ---- model roles ----
    AdwPreferencesGroup* roles_group = group(content_, "Model roles", "Other local models beside the one doing the work. A reviewer reads what a request changed before it may finish, and for the Review button. A visual model looks at pictures of a window.");
    AdwActionRow* reviewer_row = row(roles_group, "Reviewer", chosen_roles.reviewer ? chosen_roles.reviewer->model + " at " + chosen_roles.reviewer->url : "The model doing the work");
    if (chosen_roles.reviewer) button(reviewer_row, "Turn Off", [run] { run("/roles", "reviewer off"); });
    entry_row(roles_group, "Choose a reviewer", "http://127.0.0.1:8081/  MODEL", "Use", [run](const std::string& text) { run("/roles", "reviewer " + text); });
    AdwActionRow* visual_row = row(roles_group, "Visual model", chosen_roles.visual ? chosen_roles.visual->model + " at " + chosen_roles.visual->url : "The model doing the work");
    if (chosen_roles.visual) button(visual_row, "Turn Off", [run] { run("/roles", "visual off"); });
    entry_row(roles_group, "Choose a visual model", "http://127.0.0.1:8082/  MODEL", "Use", [run](const std::string& text) { run("/roles", "visual " + text); });
    AdwActionRow* trace_row = row(roles_group, "Record every model call", chosen_roles.trace.empty() ? "Off" : "Written to " + chosen_roles.trace);
    if (!chosen_roles.trace.empty()) button(trace_row, "Turn Off", [run] { run("/roles", "trace off"); });
    entry_row(roles_group, "Record to a folder", "~/mason-traces    (outside your projects)", "Record", [run](const std::string& text) { run("/roles", "trace " + text); });
    entry_row(roles_group, "Reviewer calls in one review: " + std::to_string(chosen_roles.limit), "64", "Set", [run](const std::string& text) { run("/roles", "limit " + text); });

    // ---- generation settings ----
    AdwPreferencesGroup* generation_group = group(content_, "Generation settings", "Kept for the model in use, and sent whenever it works as the coding agent. A setting you leave alone is the server's. "
                                                                                     "Top K, Min P and repeat penalty are llama.cpp's. Chats have their own options.");
    if (!c.endpoint) {
        row(generation_group, "No model in use", "Start or choose a model first.");
    } else {
        const std::map<std::string, double> kept = generation::values(*c.endpoint, home);
        for (const GenerationField& field : generation::fields()) {
            const auto found = kept.find(field.key);
            const std::string key = field.key;
            const std::string range = key == "seed" ? "A whole number, or off" : generation::shown(field.low, field) + " to " + generation::shown(field.high, field) + ", or off";
            entry_row(generation_group, field.label + ": " + (found == kept.end() ? "the server's default" : generation::shown(found->second, field)), range, "Set",
                      [run, key](const std::string& text) { run("/generation", key + " " + text); });
        }
        if (!kept.empty()) {
            AdwActionRow* clear_generation = row(generation_group, "All of them", c.endpoint->model + " at " + c.endpoint->url);
            button(clear_generation, "Use the Server's Defaults", [run] { run("/generation", "clear"); });
        }
    }

    // ---- bundles ----
    AdwPreferencesGroup* bundle_group = group(content_, "Bundles", "Folders of skills and documentation, installed once for every project. A bundle may also name MCP servers; they are added switched off.");
    if (installed.empty()) row(bundle_group, "No bundles", "A bundle is a folder with bundle.json, skills/NAME/SKILL.md and docs/*.md.");
    for (const InstalledBundle& bundle : installed) {
        const std::string name = bundle.name;
        AdwActionRow* line = row(bundle_group, bundle.name + " " + bundle.version, bundle.description);
        button(line, "Add Its Servers", [run, name] { run("/bundle", "servers " + name + " add"); });
        button(line, "Remove", [run, name] { run("/bundle", "remove " + name); });
    }
    AdwActionRow* install_row = row(bundle_group, "Install a bundle", "Choose the bundle's folder.");
    button(install_row, "Choose Folder…", [this, run] {
        auto dialog = Gtk::FileDialog::create();
        dialog->set_title("Install a Bundle");
        auto* window = dynamic_cast<Gtk::Window*>(get_root());
        if (!window) return;
        dialog->select_folder(*window, [dialog, run](const Glib::RefPtr<Gio::AsyncResult>& result) {
            try {
                if (const auto folder = dialog->select_folder_finish(result)) run("/bundle", "install " + folder->get_path());
            } catch (const Glib::Error&) {
                // Cancelled.
            }
        });
    });

    // ---- language servers ----
    AdwPreferencesGroup* language_group = group(content_, "Language servers", "After each edit Mason hands the file to its language's server, and the errors the server reports go back to the model. "
                                                                               "A server runs in the sandbox in Auto, and asks first otherwise. Mason installs one only when you choose Install, using that language's own tool.");
    std::vector<const Language*> languages;
    if (!workspace.empty()) {
        try {
            languages = language_servers::present(Workspace(workspace));
        } catch (const std::exception&) {
        }
    }
    const bool every_language = languages.empty();
    if (every_language) {
        for (const Language& entry : language_servers::known()) languages.push_back(&entry);
    }
    for (const Language* language : languages) {
        const LanguageState found = language_servers::state(*language, home);
        const auto [said, can_install] = language_servers::described(*language, home);
        // With no project open every language is listed, so only the ones that can be acted on are shown.
        if (every_language && !found.installed && !can_install) continue;
        AdwActionRow* line = row(language_group, language->name, said);
        const std::string key = language->key;
        if (can_install) button(line, "Install", [run, key] { run("/languages", "install " + key); });
    }

    if (!session) return;

    // ---- experiments ----
    AdwPreferencesGroup* experiment_group = group(content_, "Experiments", "Copies of this project in their own Git worktrees, each started from the project exactly as it stood. Try something there, then bring back the hunks you want.");
    if (trials.empty()) row(experiment_group, "No experiments", "Name one below to copy the project, uncommitted and untracked files included, into a worktree beside it.");
    for (const Experiment& trial : trials) {
        const std::string name = trial.name, path = trial.path;
        AdwActionRow* line = row(experiment_group, trial.name, trial.path + "\nBranch " + trial.branch);
        button(line, "What Changed", [run, name] { run("/experiment", "diff " + name); });
        button(line, "Open Here", [&c, path] { c.new_session(path); });
        button(line, "Forget", [run, name] { run("/experiment", "forget " + name); });
    }
    entry_row(experiment_group, "Start an experiment", "name", "Start", [run](const std::string& text) { run("/experiment", "new " + text); });
    entry_row(experiment_group, "See one file's hunks", "NAME  PATH", "Show", [run](const std::string& text) { run("/experiment", "diff " + text); });
    entry_row(experiment_group, "Apply a hunk to this project", "NAME  PATH  ID    (or: all)", "Apply", [run](const std::string& text) { run("/experiment", "apply " + text); });

    // ---- pull request ----
    AdwPreferencesGroup* delivery_group = group(content_, "Pull request", "One commit made from the hunks you choose, without touching your files, index or branches. Publishing pushes it to a new branch and opens a draft pull request through gh.");
    delivery_group_ = GTK_WIDGET(delivery_group);
    if (draft) {
        AdwActionRow* line = row(delivery_group, draft->title, "Onto " + draft->base + "  ·  commit " + draft->commit.substr(0, 12) + "  ·  " + std::to_string(draft->paths.size()) + (draft->paths.size() == 1 ? " file" : " files") +
                                                                 (draft->url.empty() ? "\nNot published" : "\n" + draft->url));
        if (draft->url.empty()) button(line, "Publish", [run] { run("/deliver", "publish"); });
        else {
            button(line, "Feedback", [run] { run("/deliver", "feedback"); });
            button(line, "Send to Mason", [run] { run("/deliver", "feedback send"); });
        }
    } else {
        row(delivery_group, "No draft", "Review against a branch, then draft from the hunks listed.");
    }
    entry_row(delivery_group, "Review against a branch", "main", "Review", [run](const std::string& text) { run("/deliver", "review " + text); });
    entry_row(delivery_group, "Draft a pull request", "main  all  Fix the cart total    (or hunk ids separated by commas)", "Draft", [run](const std::string& text) { run("/deliver", "draft " + text); });

    // ---- background tasks ----
    AdwPreferencesGroup* task_group = group(content_, "Background tasks", "Requests that keep running after you close Mason. Outside Auto, a task waits here before each edit and command.");
    task_group_ = GTK_WIDGET(task_group);
    if (tasks.empty()) row(task_group, "No background tasks", "Start one below. It uses the model that is running now and this session's permission.");
    for (const auto& [task, status] : tasks) {
        const std::string id = task.id;
        std::string detail = status.state + (status.detail.empty() ? "" : " (" + status.detail + ")");
        if (status.approval) detail += "\nWaiting on: " + status.approval->title;
        AdwActionRow* line = row(task_group, utf8_length(task.prompt) > 70 ? utf8_prefix(task.prompt, 70) + "…" : task.prompt, detail);
        if (status.approval) {
            button(line, "Allow", [run, id] { run("/task", "allow " + id); });
            button(line, "Deny", [run, id] { run("/task", "deny " + id); });
        }
        button(line, "Show", [run, id] { run("/task", "show " + id); });
        if (status.active()) button(line, "Stop", [run, id] { run("/task", "cancel " + id); });
    }
    entry_row(task_group, "Start a background task", "What to do", "Start", [run](const std::string& text) { run("/task", "start " + text); });
    AdwActionRow* refresh_row = row(task_group, "Tasks run on their own", "This list is read when the page is opened.");
    button(refresh_row, "Refresh", [this] { shown_.clear(); refresh(); });
}

void SettingsPage::open_file(const std::string& path, bool folder) {
    GFile* file = g_file_new_for_path(path.c_str());
    GtkFileLauncher* launcher = gtk_file_launcher_new(file);
    g_object_unref(file);
    GtkWindow* window = GTK_WINDOW(gtk_widget_get_root(GTK_WIDGET(gobj())));
    auto finished = +[](GObject* source, GAsyncResult* result, gpointer) {
        GError* error = nullptr;
        gtk_file_launcher_launch_finish(GTK_FILE_LAUNCHER(source), result, &error);
        if (error) g_error_free(error);
        g_object_unref(source);
    };
    auto finished_folder = +[](GObject* source, GAsyncResult* result, gpointer) {
        GError* error = nullptr;
        gtk_file_launcher_open_containing_folder_finish(GTK_FILE_LAUNCHER(source), result, &error);
        if (error) g_error_free(error);
        g_object_unref(source);
    };
    if (folder) gtk_file_launcher_open_containing_folder(launcher, window, nullptr, finished_folder, nullptr);
    else gtk_file_launcher_launch(launcher, window, nullptr, finished, nullptr);
}

void SettingsPage::show_text(const std::string& title, const std::string& path, const std::string& text) {
    auto* view = Gtk::make_managed<Gtk::TextView>();
    view->set_editable(false);
    view->set_cursor_visible(false);
    view->set_monospace(true);
    view->set_wrap_mode(Gtk::WrapMode::WORD_CHAR);
    view->set_left_margin(12);
    view->set_right_margin(12);
    view->set_top_margin(12);
    view->set_bottom_margin(12);
    view->get_buffer()->set_text(text);
    gtk_accessible_update_property(GTK_ACCESSIBLE(view->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, title.c_str(), -1);
    auto* scroll = Gtk::make_managed<Gtk::ScrolledWindow>();
    scroll->set_child(*view);
    scroll->set_vexpand(true);
    auto* where = dim_label(path);
    where->add_css_class("caption");
    where->set_margin(8);
    where->set_ellipsize(Pango::EllipsizeMode::MIDDLE);
    auto* body = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 0);
    body->append(*scroll);
    body->append(*where);
    GtkWidget* toolbar = adw_toolbar_view_new();
    GtkWidget* header = adw_header_bar_new();
    adw_header_bar_set_title_widget(ADW_HEADER_BAR(header), adw_window_title_new(title.c_str(), ""));
    adw_toolbar_view_add_top_bar(ADW_TOOLBAR_VIEW(toolbar), header);
    adw_toolbar_view_set_content(ADW_TOOLBAR_VIEW(toolbar), GTK_WIDGET(body->gobj()));
#if ADW_CHECK_VERSION(1, 5, 0)
    AdwDialog* dialog = adw_dialog_new();
    adw_dialog_set_title(dialog, title.c_str());
    adw_dialog_set_content_width(dialog, 680);
    adw_dialog_set_content_height(dialog, 560);
    adw_dialog_set_child(dialog, toolbar);
    adw_dialog_present(dialog, GTK_WIDGET(gobj()));
#else
    GtkWidget* window = adw_window_new();
    gtk_window_set_title(GTK_WINDOW(window), title.c_str());
    gtk_window_set_default_size(GTK_WINDOW(window), 680, 560);
    gtk_window_set_modal(GTK_WINDOW(window), true);
    gtk_window_set_transient_for(GTK_WINDOW(window), GTK_WINDOW(gtk_widget_get_root(GTK_WIDGET(gobj()))));
    adw_window_set_content(ADW_WINDOW(window), toolbar);
    gtk_window_present(GTK_WINDOW(window));
#endif
}

void SettingsPage::new_skill(const std::string& workspace) {
    struct Request {
        SettingsPage* page;
        std::string workspace;
        GtkWidget *name, *description;
    };
    auto* fields = Gtk::make_managed<Gtk::Box>(Gtk::Orientation::VERTICAL, 8);
    auto* name = Gtk::make_managed<Gtk::Entry>();
    name->set_placeholder_text("Name, such as release-notes");
    gtk_accessible_update_property(GTK_ACCESSIBLE(name->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Skill name", -1);
    auto* description = Gtk::make_managed<Gtk::Entry>();
    description->set_placeholder_text("When to use it, in one line");
    gtk_accessible_update_property(GTK_ACCESSIBLE(description->gobj()), GTK_ACCESSIBLE_PROPERTY_LABEL, "Skill description", -1);
    fields->append(*name);
    fields->append(*description);
    auto* request = new Request{this, workspace, GTK_WIDGET(name->gobj()), GTK_WIDGET(description->gobj())};
    auto respond = +[](GObject*, const char* response, gpointer data) {
        auto* request = static_cast<Request*>(data);
        if (std::string(response) != "create") return;
        Controller& c = request->page->controller_;
        try {
            const std::string typed = gtk_editable_get_text(GTK_EDITABLE(request->name));
            const std::string about = gtk_editable_get_text(GTK_EDITABLE(request->description));
            const fs::path made = skills::create(Workspace(request->workspace), trim(typed), about);
            c.notice.emit("Made " + made.string());
            request->page->open_file(made.string(), false);
        } catch (const std::exception& error) {
            c.notice.emit(error.what());
        }
        c.changed.emit();
    };
    auto release = +[](gpointer data, GClosure*) { delete static_cast<Request*>(data); };
    const std::string body = "Mason makes .mason/skills/NAME/SKILL.md in this project from a short template and opens it.";
#if ADW_CHECK_VERSION(1, 5, 0)
    AdwDialog* dialog = adw_alert_dialog_new("New Skill", body.c_str());
    adw_alert_dialog_set_extra_child(ADW_ALERT_DIALOG(dialog), GTK_WIDGET(fields->gobj()));
    adw_alert_dialog_add_responses(ADW_ALERT_DIALOG(dialog), "cancel", "Cancel", "create", "Create", nullptr);
    adw_alert_dialog_set_response_appearance(ADW_ALERT_DIALOG(dialog), "create", ADW_RESPONSE_SUGGESTED);
    adw_alert_dialog_set_default_response(ADW_ALERT_DIALOG(dialog), "create");
    adw_alert_dialog_set_close_response(ADW_ALERT_DIALOG(dialog), "cancel");
    g_signal_connect_data(dialog, "response", G_CALLBACK(respond), request, release, GConnectFlags(0));
    adw_dialog_present(dialog, GTK_WIDGET(gobj()));
#else
    GtkWidget* dialog = adw_message_dialog_new(GTK_WINDOW(gtk_widget_get_root(GTK_WIDGET(gobj()))), "New Skill", body.c_str());
    adw_message_dialog_set_extra_child(ADW_MESSAGE_DIALOG(dialog), GTK_WIDGET(fields->gobj()));
    adw_message_dialog_add_responses(ADW_MESSAGE_DIALOG(dialog), "cancel", "Cancel", "create", "Create", nullptr);
    adw_message_dialog_set_response_appearance(ADW_MESSAGE_DIALOG(dialog), "create", ADW_RESPONSE_SUGGESTED);
    adw_message_dialog_set_default_response(ADW_MESSAGE_DIALOG(dialog), "create");
    adw_message_dialog_set_close_response(ADW_MESSAGE_DIALOG(dialog), "cancel");
    g_signal_connect_data(dialog, "response", G_CALLBACK(respond), request, release, GConnectFlags(0));
    gtk_window_present(GTK_WINDOW(dialog));
#endif
    name->grab_focus();
}

void SettingsPage::show_section(const std::string& section) {
    // Tasks run on their own, so the page is read again before it is shown.
    shown_.clear();
    refresh();
    // Once laid out, the section's top is scrolled to. The first frames may come before the page has its size.
    // The section is looked up each frame, as a rebuild of the page replaces it.
    auto frames = std::make_shared<int>(0);
    GtkWidget* page = GTK_WIDGET(page_.gobj());
    const Glib::RefPtr<Gtk::Adjustment> adjustment = get_vadjustment();
    add_tick_callback([this, section, page, adjustment, frames](const Glib::RefPtr<Gdk::FrameClock>&) {
        GtkWidget* target = section == "background" ? task_group_ : delivery_group_;
        graphene_rect_t bounds;
        if (target && adjustment->get_upper() > 0 && gtk_widget_compute_bounds(target, page, &bounds)) {
            adjustment->set_value(std::max(0.0, static_cast<double>(bounds.origin.y) + page_.get_margin_top() - 12));
        }
        return ++*frames < 6;
    });
}

}  // namespace mason::app
