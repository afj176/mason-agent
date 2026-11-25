namespace Mason;

/// <summary>X3: favourite and recently used model connections, shared by the Code and Chat pickers.</summary>
public sealed record ModelPicks(List<Guid> Favourites, List<Guid> Recent)
{
    const string File = "model-picks.json";
    public static ModelPicks Read() { try { return Store.Read<ModelPicks>(File) ?? new([], []); } catch (Exception) { return new([], []); } }
    public static void Use(Guid id)
    {
        var picks = Read(); picks.Recent.Remove(id); picks.Recent.Insert(0, id);
        if (picks.Recent.Count > 5) picks.Recent.RemoveRange(5, picks.Recent.Count - 5);
        try { Store.Write(File, picks); } catch (Exception) { /* Ordering is a convenience; a failed write keeps the last order. */ }
    }
    public static void ToggleFavourite(Guid id)
    {
        var picks = Read(); if (!picks.Favourites.Remove(id)) picks.Favourites.Add(id);
        try { Store.Write(File, picks); } catch (Exception) { }
    }
}
