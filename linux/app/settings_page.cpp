// The Settings page: saved approvals, agents and commands, MCP servers, and schedules, in one place.
#include "window.hpp"

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
    content_.set_margin(24);
    GtkWidget* clamp = adw_clamp_new();
    adw_clamp_set_maximum_size(ADW_CLAMP(clamp), 860);
    adw_clamp_set_child(ADW_CLAMP(clamp), GTK_WIDGET(content_.gobj()));
    gtk_scrolled_window_set_child(gobj(), clamp);
    set_policy(Gtk::PolicyType::NEVER, Gtk::PolicyType::AUTOMATIC);
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

    // The page is rebuilt only when what it lists changes, so typing in an entry is not interrupted.
    const std::optional<EmbeddingSettings> embeddings = semantic::load(home);
    std::string print = workspace + "|" + (session ? session->agent : "") + "|" + problem + "|" + c.mcp_busy;
    print += embeddings ? embeddings->url + embeddings->model + embeddings->rerank_url + embeddings->rerank_model + "|" : "none|";
    for (const auto& rule : rules.rules()) print += rule.tool + rule.prefix + "|";
    for (const Definition& item : agents) print += item.name + "|";
    for (const Definition& item : commands) print += item.name + "|";
    for (const McpServer& server : servers) print += server.name + server.transport() + (server.enabled ? "1" : "0") + join(server.tools, ",") + "|";
    for (const auto& [name, offered] : c.mcp_offered) print += name + std::to_string(offered.size()) + "|";
    for (const Schedule& schedule : all_schedules) print += schedule.id + (schedule.enabled ? "1" : "0") + schedule.last_outcome + std::to_string(schedule.last_run) + "|";
    if (print == shown_) return;
    shown_ = print;
    clear(content_);

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
}

}  // namespace mason::app
