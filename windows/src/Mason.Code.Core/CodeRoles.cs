namespace Mason.Code;

/// <summary>The CLI's /roles: which saved connection reviews and which looks at pages and windows for a session,
/// how often one review may ask them, and whether model calls are kept for comparison. These are the session's
/// fields the app's Model roles page edits, so the app and the CLI honour the same choices.</summary>
public static class CodeRoles
{
    public const string Usage =
        "/roles                          The models in each role\n" +
        "/roles reviewer NUMBER|NAME     Use another saved connection for /review and checks (or: /roles reviewer off)\n" +
        "/roles visual NUMBER|NAME       Use another saved connection to look at pages and windows (or: /roles visual off)\n" +
        "/roles limit N                  How many times one review or verification may ask a role (1 to 128)\n" +
        "/roles trace on|off             Keep the last 12 model calls in the data folder to compare later\n" +
        "NUMBER is a connection's number in /model.";

    /// <param name="connections">The saved chat connections in /model's order.</param>
    public static string Command(string rest, CodeSession session, IReadOnlyList<(Guid Id, string Name)> connections)
    {
        string Named(Guid? id) => id is not { } value ? "the model doing the work"
            : connections.Where(c => c.Id == value).Select(c => c.Name).FirstOrDefault() ?? "a removed connection (choose again)";
        string Shown() => "reviewer  " + Named(session.ReviewerModel) + "\nvisual    " + Named(session.VisualModel) +
            "\nlimit     " + session.RoleCallLimit + " calls per review or verification role\ntrace     " + (session.RecordModelDecisions ? "on" : "off");
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Shown();
        var verb = words[0].ToLowerInvariant();
        var argument = string.Join(' ', words.Skip(1));
        if (verb is "reviewer" or "visual")
        {
            Guid? picked;
            if (argument.Equals("off", StringComparison.OrdinalIgnoreCase) || argument.Equals("main", StringComparison.OrdinalIgnoreCase)) picked = null;
            else if (argument.Length == 0) throw new ArgumentException("Use /roles " + verb + " NUMBER|NAME, or /roles " + verb + " off.");
            else if (int.TryParse(argument, out var number))
                picked = number >= 1 && number <= connections.Count ? connections[number - 1].Id : throw new ArgumentException("Use a number from /model.");
            else
            {
                var matches = connections.Where(c => c.Name.Contains(argument, StringComparison.OrdinalIgnoreCase)).ToArray();
                picked = matches.Length == 1 ? matches[0].Id : throw new ArgumentException(matches.Length == 0 ? "No saved connection matches \"" + argument + "\"." : "More than one connection matches \"" + argument + "\". Use its number from /model.");
            }
            if (verb == "reviewer") session.ReviewerModel = picked; else session.VisualModel = picked;
        }
        else if (verb == "limit")
        {
            if (words.Length != 2 || !int.TryParse(words[1], out var limit) || limit is < 1 or > 128) throw new ArgumentException("Use /roles limit N, from 1 to 128.");
            session.RoleCallLimit = limit;
        }
        else if (verb == "trace")
        {
            session.RecordModelDecisions = argument.ToLowerInvariant() switch { "on" => true, "off" => false, _ => throw new ArgumentException("Use /roles trace on, or /roles trace off.") };
        }
        else return Usage;
        return Shown();
    }
}
