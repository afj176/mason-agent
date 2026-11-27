import SwiftUI
import UniformTypeIdentifiers
import MasonCore

// Forking a session from an earlier request, and saving or adding a session as a file.

extension AppModel {
    /// Opens a new session holding the conversation before this request, with the request back in the request box.
    func forkSession(before turn: Turn, restoreFiles: Bool) {
        guard !running, var current = session, let store, let index = current.turns.firstIndex(where: { $0.id == turn.id }) else { return }
        do {
            let made = try SessionTransfer.fork(&current, store: store, turn: index, restoreFiles: restoreFiles)
            replace(current)
            replace(made.session)
            selection = made.session.id
            draft = made.request
            if restoreFiles { restoredAt = Date() }
            notice = made.session.detail
        } catch {
            notice = describe(error)
        }
    }

    func exportSession(_ session: Session, sanitize: Bool) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = String(session.title.prefix(40)).replacingOccurrences(of: "/", with: "-") + (sanitize ? " (sanitized)" : "") + ".json"
        panel.message = sanitize ? "The copy leaves out the project's path, likely secrets and the contents of changed files." : "The file holds the whole conversation, including the contents of changed files."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try SessionTransfer.export(session, sanitize: sanitize).write(to: url, options: .atomic) } catch { notice = "The session could not be saved: \(describe(error))" }
    }

    /// Adds an exported session to a project.
    func importSession(into project: String) {
        guard let store else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.message = "Choose a session exported from Mason Agent."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let imported = try SessionTransfer.importSession(try Data(contentsOf: url), store: store, workspace: project)
            replace(imported)
            selection = imported.id
            pane = .code
            notice = imported.detail
        } catch {
            notice = describe(error)
        }
    }
}
