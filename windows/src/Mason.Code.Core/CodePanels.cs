namespace Mason.Code;

/// <summary>Artifact presentation intent, independent of native control geometry.</summary>
public static class CodePanels
{
    public static bool Open(CodeSession session, CodeTab artifact, bool user)
    {
        // Older sessions could pin the empty placeholder. A pin needs an artifact.
        if (session.Tabs.Count == 0) session.ViewerPinned = false;
        if (session.Tabs.All(t => t.Key != artifact.Key))
        {
            if (session.Tabs.Count >= 12) return false;
            session.Tabs.Add(artifact);
        }
        if (user || !session.ViewerPinned) session.SelectedTab = artifact.Key;
        if (user || !session.ViewerDismissed && !session.ViewerPinned)
        {
            session.ViewerVisible = true;
            if (user) session.ViewerDismissed = false;
        }
        return true;
    }

    public static void Hide(CodeSession session)
    {
        session.ViewerVisible = false;
        session.ViewerDismissed = true;
    }

    public static void Show(CodeSession session)
    {
        if (session.Tabs.Count == 0) return;
        session.ViewerVisible = true;
        session.ViewerDismissed = false;
    }

    public static void Close(CodeSession session, string key)
    {
        int index = session.Tabs.FindIndex(t => t.Key == key);
        if (index < 0) return;
        session.Tabs.RemoveAt(index);
        if (session.SelectedTab == key)
            session.SelectedTab = session.Tabs.Count == 0 ? null : session.Tabs[Math.Min(index, session.Tabs.Count - 1)].Key;
        if (session.Tabs.Count == 0) { session.ViewerPinned = false; Hide(session); }
    }
}
