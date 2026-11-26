import Foundation

/// A request Mason starts by itself at set times. Schedules live in `schedules.json` in Mason's data folder, never
/// in a project. The app checks them while it is open, and `mason --run-due` runs whatever is due from a terminal
/// or a system timer. A run missed while nothing was checking starts once at the next check, never repeatedly.
public struct Schedule: Codable, Identifiable, Equatable {
    public enum Cadence: String, Codable, CaseIterable { case hourly, daily, weekdays, weekly }

    public var id = UUID()
    public var name: String
    public var workspace: String
    public var prompt: String
    /// The model server to use. When it is not answering at run time, the run is recorded as skipped.
    public var endpoint: ModelEndpoint
    /// Auto: edits apply and commands run sandboxed. Otherwise the run is read-only. Nothing is ever approved for it.
    public var auto: Bool
    public var cadence: Cadence
    public var hour: Int
    public var minute: Int
    /// 0 is Sunday. Only used by a weekly schedule.
    public var weekday: Int
    public var enabled = true
    /// When the schedule was made or last switched on. Run times are counted from here or from the last run, whichever is later.
    public var created = Date()
    public var lastRun: Date?
    public var lastSession: UUID?
    public var lastOutcome = ""

    public init(name: String, workspace: String, prompt: String, endpoint: ModelEndpoint, auto: Bool, cadence: Cadence, hour: Int, minute: Int, weekday: Int = 1) {
        self.name = name; self.workspace = workspace; self.prompt = prompt; self.endpoint = endpoint; self.auto = auto
        self.cadence = cadence; self.hour = hour; self.minute = minute; self.weekday = weekday
    }

    static let days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]

    /// The first run time strictly after `after`, in local time.
    public func next(after: Date, calendar: Calendar = .current) -> Date {
        if cadence == .hourly {
            var parts = calendar.dateComponents([.year, .month, .day, .hour], from: after)
            parts.minute = minute; parts.second = 0
            let thisHour = calendar.date(from: parts) ?? after
            return thisHour > after ? thisHour : (calendar.date(byAdding: .hour, value: 1, to: thisHour) ?? thisHour)
        }
        var candidate = calendar.date(bySettingHour: hour, minute: minute, second: 0, of: calendar.startOfDay(for: after)) ?? after
        for _ in 0..<8 {
            let day = calendar.component(.weekday, from: candidate) - 1
            let fits = cadence == .daily || (cadence == .weekdays && day != 0 && day != 6) || (cadence == .weekly && day == weekday)
            if candidate > after, fits { return candidate }
            candidate = calendar.date(byAdding: .day, value: 1, to: candidate) ?? candidate
        }
        return candidate
    }

    public var summary: String {
        let time = String(format: "%02d:%02d", hour, minute)
        switch cadence {
        case .hourly: return String(format: "Every hour at :%02d", minute)
        case .daily: return "Every day at \(time)"
        case .weekdays: return "Weekdays at \(time)"
        case .weekly: return "Every \(Schedule.days[min(max(weekday, 0), 6)]) at \(time)"
        }
    }
}

public enum Schedules {
    static let limit = 32
    static func file(_ home: URL) -> URL { home.appendingPathComponent("schedules.json") }
    static var encoder: JSONEncoder { let e = JSONEncoder(); e.outputFormatting = [.prettyPrinted, .sortedKeys]; e.dateEncodingStrategy = .iso8601; return e }
    static var decoder: JSONDecoder { let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601; return d }

    public static func load(home: URL) -> [Schedule] {
        guard let data = try? Data(contentsOf: file(home)) else { return [] }
        return (try? decoder.decode([Schedule].self, from: data)) ?? []
    }

    public static func save(_ schedules: [Schedule], home: URL) throws {
        guard schedules.count <= limit else { throw MasonError("Keep up to \(limit) schedules.") }
        guard Set(schedules.map { $0.name.lowercased() }).count == schedules.count else { throw MasonError("Each schedule needs its own name.") }
        for schedule in schedules {
            var isFolder: ObjCBool = false
            let name = schedule.name.trimmingCharacters(in: .whitespaces), prompt = schedule.prompt.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !name.isEmpty, name.count <= 80, !name.contains(" "), !prompt.isEmpty, prompt.count <= 16_000,
                  FileManager.default.fileExists(atPath: schedule.workspace, isDirectory: &isFolder), isFolder.boolValue,
                  (0..<24).contains(schedule.hour), (0..<60).contains(schedule.minute), (0..<7).contains(schedule.weekday) else {
                throw MasonError("A schedule needs a one-word name, a request of up to 16,000 characters, a time, and an existing project folder.")
            }
        }
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try encoder.encode(schedules).write(to: file(home), options: .atomic)
    }

    /// Enabled schedules whose next run after their last run (or creation) has come.
    public static func due(_ schedules: [Schedule], now: Date = Date(), calendar: Calendar = .current) -> [Schedule] {
        schedules.filter { $0.enabled && $0.next(after: max($0.lastRun ?? $0.created, $0.created), calendar: calendar) <= now }
    }

    /// Runs one schedule now as its own session, with nobody to ask: every approval is declined. Auto runs edits and
    /// sandboxed commands; otherwise the run is read-only. The schedule's last run and outcome are saved.
    @discardableResult
    public static func run(_ schedule: Schedule, home: URL, store: SessionStore, model: ChatModel? = nil, emit: @escaping (AgentEvent) -> Void = { _ in }) async -> Session? {
        var outcome = ""
        var result: Session?
        do {
            let chosen: ChatModel
            if let model { chosen = model } else {
                guard let listed = await OpenAIClient.models(at: schedule.endpoint.url), listed.contains(schedule.endpoint.model) else {
                    throw MasonError("Skipped: \(schedule.endpoint.model) was not answering at \(schedule.endpoint.url).")
                }
                chosen = try OpenAIClient(schedule.endpoint)
            }
            let workspace = try Workspace(path: schedule.workspace)
            var session = try store.create(workspace: workspace.root.path, permission: schedule.auto ? .auto : .readOnly)
            session.title = "Scheduled: \(schedule.name)"
            session.askOnly = !schedule.auto
            await Agent(store: store, model: chosen, approve: { _ in false }, emit: emit).run(&session, request: schedule.prompt)
            outcome = session.state + (session.detail.isEmpty ? "" : ": " + session.detail)
            result = session
        } catch {
            outcome = (error as? MasonError)?.description ?? error.localizedDescription
        }
        // Another run or an edit may have changed the file meanwhile, so only this schedule's record is updated.
        var all = load(home: home)
        if let index = all.firstIndex(where: { $0.id == schedule.id }) {
            all[index].lastRun = Date(); all[index].lastSession = result?.id; all[index].lastOutcome = String(outcome.prefix(300))
            try? save(all, home: home)
        }
        return result
    }

    // MARK: the /schedule command, shared by the command line and the app

    public static let usage = """
        /schedule                                       list schedules
        /schedule add NAME hourly :MM [auto] -- REQUEST
        /schedule add NAME daily|weekdays HH:MM [auto] -- REQUEST
        /schedule add NAME weekly DAY HH:MM [auto] -- REQUEST
        /schedule on|off|remove NAME
        """

    /// Carries out everything except "run", and returns what to tell the user. `workspace` and `endpoint` are where
    /// a new schedule will work and which model it will use.
    public static func command(_ arguments: String, home: URL, workspace: String, endpoint: ModelEndpoint?, now: Date = Date()) throws -> String {
        var schedules = load(home: home)
        let parts = arguments.components(separatedBy: " -- ")
        let words = parts[0].split(separator: " ").map(String.init)
        func index(_ name: String?) throws -> Int {
            guard let name, let found = schedules.firstIndex(where: { $0.name.lowercased() == name.lowercased() }) else { throw MasonError("No schedule named \(name ?? ""). /schedule lists them.") }
            return found
        }
        switch words.first?.lowercased() {
        case nil, "list":
            if schedules.isEmpty { return "No schedules. Add one with:\n" + usage }
            let formatter = DateFormatter(); formatter.dateFormat = "EEE d MMM HH:mm"
            return schedules.map { schedule in
                let next = schedule.enabled ? "next " + formatter.string(from: schedule.next(after: max(schedule.lastRun ?? schedule.created, now))) : "off"
                let last = schedule.lastRun.map { "last " + formatter.string(from: $0) + (schedule.lastOutcome.isEmpty ? "" : " · " + schedule.lastOutcome) } ?? "not run yet"
                return "\(schedule.name)  ·  \(schedule.summary)  ·  \(schedule.auto ? "Auto" : "read-only")  ·  \(next)\n    \(URL(fileURLWithPath: schedule.workspace).lastPathComponent): \(schedule.prompt.prefix(80))\n    \(last)"
            }.joined(separator: "\n")
        case "add":
            guard parts.count >= 2, words.count >= 4, let cadence = Schedule.Cadence(rawValue: words[2].lowercased()) else { throw MasonError("Use:\n" + usage) }
            guard let endpoint else { throw MasonError("A schedule uses the model that is running now. Start one first.") }
            var rest = Array(words.dropFirst(3))
            var weekday = 1
            if cadence == .weekly {
                guard let day = Schedule.days.firstIndex(where: { $0.lowercased().hasPrefix(rest.first?.lowercased() ?? "?") && (rest.first?.count ?? 0) >= 3 }) else { throw MasonError("Name the day for a weekly schedule, such as Monday.") }
                weekday = day; rest.removeFirst()
            }
            guard let time = rest.first else { throw MasonError("Give the time as HH:MM, or :MM for an hourly schedule.") }
            let pieces = time.split(separator: ":", omittingEmptySubsequences: false).map(String.init)
            let hour = cadence == .hourly ? 0 : Int(pieces.first ?? "") ?? -1
            guard pieces.count == 2, let minute = Int(pieces[1]), (0..<60).contains(minute), (0..<24).contains(hour) else { throw MasonError("Give the time as HH:MM, or :MM for an hourly schedule.") }
            let auto = rest.dropFirst().contains { $0.lowercased() == "auto" }
            let prompt = parts.dropFirst().joined(separator: " -- ").trimmingCharacters(in: .whitespacesAndNewlines)
            var schedule = Schedule(name: words[1], workspace: workspace, prompt: prompt, endpoint: endpoint, auto: auto, cadence: cadence, hour: hour, minute: minute, weekday: weekday)
            schedule.created = now
            schedules.append(schedule)
            try save(schedules, home: home)
            return "Scheduled \(schedule.name): \(schedule.summary.lowercased()), \(auto ? "in Auto (edits and sandboxed commands)" : "read-only"), with \(endpoint.model). It runs while the Mason app is open, or when mason --run-due is run."
        case "on", "off":
            let at = try index(words.dropFirst().first)
            schedules[at].enabled = words[0].lowercased() == "on"
            // Switching on counts from now, so a long-paused schedule does not fire at once for a time long past.
            if schedules[at].enabled { schedules[at].created = now }
            try save(schedules, home: home)
            return "\(schedules[at].name) is \(schedules[at].enabled ? "on" : "off")."
        case "remove":
            let removed = schedules.remove(at: try index(words.dropFirst().first))
            try save(schedules, home: home)
            return "Removed \(removed.name)."
        default:
            throw MasonError("Use:\n" + usage)
        }
    }
}
