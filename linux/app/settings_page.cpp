// The Settings page: saved approvals, agents and commands, MCP servers, and schedules, in one place.
#include "window.hpp"

#include "background.hpp"
#include "bundles.hpp"
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

    const std::optional<ProjectPolicy> policy = workspace.empty() ? std::nullopt : policies::load(home, workspace, true);
    const ModelRoles chosen_roles = roles::load(home);
    const bool asks_network = network_settings::asks(home);
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
    print += (chosen_roles.reviewer ? chosen_roles.reviewer->url + chosen_roles.reviewer->model : "") + "|" + (chosen_roles.visual ? chosen_roles.visual->url + chosen_roles.visual->model : "") + "|" + chosen_roles.trace + std::to_string(chosen_roles.limit) + (asks_network ? "|ask|" : "|off|");
    for (const InstalledBundle& bundle : installed) print += bundle.name + bundle.version + "|";
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

    // ---- what follows shares its code with the command line: each control runs the command it names ----
    auto run = [&c](const std::string& name, const std::string& rest) { c.host_command(name, rest); };

    // ---- project policy ----
    if (session) {
        AdwPreferencesGroup* policy_group = group(content_, "Project policy", "Limits for this project, kept outside it so nothing in the project can change them.");
        AdwActionRow* hooks = row(policy_group, "Run the project's hooks", "In Auto, workflows marked afterEdit run after each edit, and ones marked beforeVerify must pass before a request may finish.");
        toggle(hooks, policy && policy->hooks, "Run the project's hooks", [run](bool on) { run("/policy", on ? "hooks on" : "hooks off"); });
        row(policy_group, "Tools switched off", policy && !policy->disabled_tools.empty() ? join(policy->disabled_tools, ", ") : "None. Every tool is available.");
        entry_row(policy_group, "Switch tools off", "run_shell start_job", "Switch Off", [run](const std::string& text) { run("/policy", "disable " + text); });
        if (policy && !policy->disabled_tools.empty()) entry_row(policy_group, "Switch tools back on", join(policy->disabled_tools, " "), "Switch On", [run](const std::string& text) { run("/policy", "enable " + text); });
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

    // ---- network ----
    AdwPreferencesGroup* network_group = group(content_, "Network in Auto", "The setting for this computer; a session can choose its own with /network. Commands in the sandbox have no network unless you allow it.");
    AdwActionRow* network_row = row(network_group, "Ask when a command needs a host", "A sandboxed command can then ask for one host over HTTPS. Your answer lasts for that request. This computer, your local network and plain HTTP stay out of reach.");
    toggle(network_row, asks_network, "Ask when a command needs a host", [run](bool on) { run("/network", on ? "computer ask" : "computer off"); });

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

}  // namespace mason::app
