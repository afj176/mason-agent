import Foundation

public struct DiffLine: Equatable {
    public enum Kind: Character { case same = " ", added = "+", removed = "-" }
    public let kind: Kind
    public let text: String
    /// Line number in the new text for same/added lines, in the old text for removed lines.
    public let number: Int
}

public enum Diff {
    static func lines(_ text: String?) -> [Substring] {
        guard let text, !text.isEmpty else { return [] }
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n")
        let trimmed = normalized.hasSuffix("\n") ? normalized.dropLast() : Substring(normalized)
        return trimmed.split(separator: "\n", omittingEmptySubsequences: false)
    }

    /// Line diff of two texts. Shared leading and trailing lines are matched first; the middle uses a
    /// longest-common-subsequence table, or one coarse replace hunk when that table would be too large.
    public static func lines(before: String?, after: String) -> [DiffLine] {
        let old = lines(before), new = lines(after)
        var prefix = 0
        while prefix < min(old.count, new.count), old[prefix] == new[prefix] { prefix += 1 }
        var suffix = 0
        while suffix < min(old.count, new.count) - prefix, old[old.count - 1 - suffix] == new[new.count - 1 - suffix] { suffix += 1 }
        var rows: [DiffLine] = []
        for i in 0..<prefix { rows.append(DiffLine(kind: .same, text: String(new[i]), number: i + 1)) }
        let n = old.count - prefix - suffix, m = new.count - prefix - suffix
        if n > 0 && m > 0 && (n + 1) * (m + 1) <= 4_000_000 {
            // table[x][y] = length of the longest common subsequence of old[x...] and new[y...] within the middle region.
            var table = [Int](repeating: 0, count: (n + 1) * (m + 1))
            let width = m + 1
            for x in stride(from: n - 1, through: 0, by: -1) {
                for y in stride(from: m - 1, through: 0, by: -1) {
                    table[x * width + y] = old[prefix + x] == new[prefix + y]
                        ? table[(x + 1) * width + y + 1] + 1
                        : max(table[(x + 1) * width + y], table[x * width + y + 1])
                }
            }
            var x = 0, y = 0
            while x < n || y < m {
                if x < n, y < m, old[prefix + x] == new[prefix + y] {
                    rows.append(DiffLine(kind: .same, text: String(new[prefix + y]), number: prefix + y + 1)); x += 1; y += 1
                } else if x < n, y == m || table[(x + 1) * width + y] >= table[x * width + y + 1] {
                    rows.append(DiffLine(kind: .removed, text: String(old[prefix + x]), number: prefix + x + 1)); x += 1
                } else {
                    rows.append(DiffLine(kind: .added, text: String(new[prefix + y]), number: prefix + y + 1)); y += 1
                }
            }
        } else {
            for x in 0..<n { rows.append(DiffLine(kind: .removed, text: String(old[prefix + x]), number: prefix + x + 1)) }
            for y in 0..<m { rows.append(DiffLine(kind: .added, text: String(new[prefix + y]), number: prefix + y + 1)) }
        }
        for i in 0..<suffix {
            let index = new.count - suffix + i
            rows.append(DiffLine(kind: .same, text: String(new[index]), number: index + 1))
        }
        return rows
    }

    public static func counts(before: String?, after: String) -> (added: Int, removed: Int) {
        let rows = lines(before: before, after: after)
        return (rows.filter { $0.kind == .added }.count, rows.filter { $0.kind == .removed }.count)
    }
}
