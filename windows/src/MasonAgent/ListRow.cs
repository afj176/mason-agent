using Microsoft.UI.Xaml.Data;

namespace Mason;

// Immutable row projection with the domain snapshot retained for exact selection/review.
[Bindable]
public sealed class ListRow(object source,params string[] values)
{
    public override string ToString()=>string.Join(" · ",Values.Select(v=>v.Length>140?v[..140]+"…":v));
    public object Source {get;}=source;
    public string[] Values {get;}=values;
}
