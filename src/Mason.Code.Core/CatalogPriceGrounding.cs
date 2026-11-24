using System.Globalization;
using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>Bind explicit unit-price and quantity clauses to their named items in the original brief.</summary>
public static class CatalogPriceGrounding
{
    const string Amount=@"\$\s*(?<price>\d+(?:\.\d{1,2})?)(?![\d.])";
    const string Count=@"(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)";
    static readonly Regex Threshold=new(@"\b(?:at least\s+"+Count+@"|"+Count+@"\s+or more)\b",RegexOptions.IgnoreCase);
    static readonly string[] Numbers=["zero","one","two","three","four","five","six","seven","eight","nine","ten"];

    public static CatalogItem[] Bind(string request,CatalogItem[] proposed)
    {
        var offers=proposed.ToArray();
        var names=offers.Select(i=>new Regex(@"(?<![\p{L}\p{N}])"+Regex.Escape(i.Name)+@"(?![\p{L}\p{N}])",RegexOptions.IgnoreCase)).ToArray();
        for(int i=0;i<offers.Length;i++){
            var price=Regex.Match(request,names[i]+@"\s+(?:(?:is|are|costs?|at)\s+)?"+Amount+@"\s*(?:each\b|per\b)",RegexOptions.IgnoreCase);
            if(price.Success && decimal.TryParse(price.Groups["price"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))
                offers[i]=offers[i] with{Price=value};
        }
        var tiers=new Dictionary<int,(int Threshold,decimal Price)>();
        foreach(var clause in Regex.Split(request,@"(?<=[.!?])\s+|[;\r\n]+")){
            var quantities=Threshold.Matches(clause);
            if(quantities.Count==0)continue;
            var owners=Enumerable.Range(0,names.Length).Where(i=>names[i].IsMatch(clause)).ToArray();
            if(owners.Length!=1 || quantities.Count!=1)return offers;
            int owner=owners[0];
            var amounts=Regex.Matches(clause,Amount+@"\s*(?:each\b|per\b)",RegexOptions.IgnoreCase)
                .Select(m=>decimal.TryParse(m.Groups["price"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var p)?p:-1)
                .Where(p=>p>=0 && p!=offers[owner].Price).Distinct().ToArray();
            var literal=quantities[0].Groups["count"].Value.ToLowerInvariant();
            int count=int.TryParse(literal,out var n)?n:Array.IndexOf(Numbers,literal);
            if(count is <1 or >10 || amounts.Length!=1 || !tiers.TryAdd(owner,(count,amounts[0])))return offers;
        }
        if(tiers.Count==0)return offers;
        for(int i=0;i<offers.Length;i++){
            if(tiers.TryGetValue(i,out var tier))offers[i]=offers[i] with{Threshold=tier.Threshold,SalePrice=tier.Price};
            else if(proposed[i].Threshold>0 && tiers.Values.Any(t=>t.Threshold==proposed[i].Threshold && t.Price==proposed[i].SalePrice)){
                // Discard a copied tier only when all text naming this other item describes its
                // regular offer. A separately worded promotion must go through ordinary planning.
                bool mentionsPromotion=Regex.Split(request,@"(?<=[.!?])\s+|[;\r\n]+").Any(s=>names[i].IsMatch(s) &&
                    Regex.IsMatch(s,@"\b(?:discount\w*|bulk|sale|cheaper|when|if|over|above|below|threshold|or more|at least)\b|%",RegexOptions.IgnoreCase));
                if(!mentionsPromotion)offers[i]=offers[i] with{Threshold=0,SalePrice=offers[i].Price};
            }
        }
        return offers;
    }
}
