using System.Globalization;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record CatalogItem(string Name,decimal Price,decimal SalePrice,int Threshold);
public sealed record CatalogSpec(CatalogItem[] Items,string ReceiptPrefix,bool PersistReceipt,bool ClearCart,string[] OtherRequirements)
{
    public static bool RequiresFreshReceipt(string request)=>Regex.IsMatch(request,
        @"\b(?:new|distinct|fresh|unique)\b[^.!?\n]{0,35}\b(?:receipt|order|identifier)\b",RegexOptions.IgnoreCase);
    public static string? ExplicitReceiptPrefix(string request)
    {
        const string token=@"(?<prefix>[A-Za-z][A-Za-z0-9]{0,22}[-_])";
        var prefixes=Regex.Matches(request,@"(?<![\w-])"+token+@"(?=['""`]?\s+(?i:receipt|order\b|confirmation))|(?i:\b(?:prefix(?:ed)?(?:\s+with)?|(?:start|begin)(?:s|ning|ing)?\s+with))\s+['""`]?"+token)
            .Select(m=>m.Groups["prefix"].Value).Distinct(StringComparer.Ordinal).ToArray();
        return prefixes.Length==1?prefixes[0]:null;
    }
    public static HashSet<decimal> Amounts(string request)
    {
        var amounts=new HashSet<decimal>();
        foreach(Match match in Regex.Matches(request,@"(?:\$\s*(?<price>\d+(?:\.\d{1,2})?)|\b(?<price>\d+(?:\.\d{1,2})?)\s+(?:US\s+)?dollars?\b)",RegexOptions.IgnoreCase))
            if(decimal.TryParse(match.Groups["price"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))amounts.Add(value);
        return amounts;
    }
}
public interface ICodeCatalogPlanner
{
    Task<CatalogSpec> ExtractCatalog(string request,CancellationToken ct);
}

internal static class CodeStorefrontPlan
{
    internal static bool Applies(string request,CodeFiles inventory,ICodeModel model)=>
        model is ICodeCatalogPlanner && inventory.RootIsOnlyDirectory && inventory.Files.Length==0 &&
        request.Contains("index.html",StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(request,@"\b(store|shop|market)\b",RegexOptions.IgnoreCase)&&
        Regex.IsMatch(request,@"\b(cart|basket)\b",RegexOptions.IgnoreCase)&&CatalogSpec.Amounts(request).Count>=2;

    internal static async Task<CodeAcceptancePlan?> Prepare(string request,ICodeCatalogPlanner planner,CancellationToken ct)
    {
        var spec=await planner.ExtractCatalog(request,ct);
        if(spec.Items is not {Length:>=2 and <=8}||spec.OtherRequirements==null)return null;
        if(spec.Items.Any(i=>i==null||string.IsNullOrWhiteSpace(i.Name)))return null;
        spec=spec with{Items=CatalogPriceGrounding.Bind(request,spec.Items)};
        if(CatalogSpec.ExplicitReceiptPrefix(request) is {} prefix)spec=spec with{ReceiptPrefix=prefix};
        else if(!string.IsNullOrEmpty(spec.ReceiptPrefix) && !request.Contains(spec.ReceiptPrefix,StringComparison.Ordinal)){
            if(Regex.IsMatch(request,@"\bprefix\w*|\b(?:start|begin)\w* with",RegexOptions.IgnoreCase))return null;
            spec=spec with{ReceiptPrefix=""};
        }
        var amounts=CatalogSpec.Amounts(request);
        if(spec.Items.Any(i=>i==null||string.IsNullOrWhiteSpace(i.Name)||!request.Contains(i.Name,StringComparison.OrdinalIgnoreCase)||
            !amounts.Contains(i.Price)||i.Threshold is <0 or >8||i.Threshold>0&&!amounts.Contains(i.SalePrice))||
            spec.Items.Select(i=>i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=spec.Items.Length)return null;
        var bindings=spec.Items.Select((item,index)=>{
            string number(decimal value)=>value.ToString(CultureInfo.InvariantCulture);
            var formula=item.Threshold>0
                ? $"q * (q >= {item.Threshold} ? {number(item.SalePrice)} : {number(item.Price)})"
                : $"q * {number(item.Price)}";
            return $"{item.Name}: add control '#product-{index+1} button', original shelf price {number(item.Price)}, line subtotal {formula}.";
        });
        var plan=new CodeAcceptancePlan{Request=request,Storefront=true,BuildFacts=
            "Implementation bindings derived from the requested offers. Here q is the current quantity of that food only. Bind the rendered controls directly with these selectors.\n"+
            string.Join("\n",bindings)+"\nRender the sum of the line subtotals in #cart-total. Keep each offer's original price and current quantity separate."};
        void Add(string feature,string id,params BrowserStep[] steps){
            BrowserContract.Validate(steps);
            plan.Units=[..plan.Units,feature];
            plan.Checks.Add(new(plan.Units.Length,feature,"browser_flow","index.html",id,"All scenario assertions pass"){Steps=steps});
        }
        string Money(decimal n)=>n.ToString("0.00",CultureInfo.InvariantCulture);
        for(int ix=0;ix<spec.Items.Length;ix++){
            var item=spec.Items[ix];string card="#product-"+(ix+1);
            Add("Show "+item.Name+" at its requested unit price and add it to the cart", "offer-"+ix,
                new("text",card+" h2",item.Name),new("shelf_price",card,Money(item.Price)),new("click",card+" button"),new("money","#cart-total",Money(item.Threshold==1?item.SalePrice:item.Price)));
            if(item.Threshold>1){
                var steps=new List<BrowserStep>();
                for(int n=1;n<=item.Threshold;n++){steps.Add(new("click",card+" button"));if(n>=item.Threshold-1)steps.Add(new("money","#cart-total",Money(n*(n>=item.Threshold?item.SalePrice:item.Price))));}
                Add("Apply the specified quantity discount for "+item.Name,"discount-"+ix,[..steps]);
            }
        }
        if(Regex.IsMatch(request,@"\bcheckout|check out\b",RegexOptions.IgnoreCase)){
            plan.BuildFacts+="\nThe checkout control itself is the button #checkout. Bind its click handler after the document's controls exist. "+
                "Keep the receipt output #receipt in the DOM and show the actual generated order identifier inside it."+
                (spec.PersistReceipt?" On checkout, save the same identifier you display; on page load read and render it in that output.":"")+
                (spec.ClearCart?" On checkout reset all quantities, remove the cart rows inside #cart-items and render the new total.":"");
            var steps=new List<BrowserStep>{new("click","#product-1 button"),new("click","#checkout"),new("capture","#receipt",spec.ReceiptPrefix??"")};
            if(spec.ClearCart){steps.Add(new("money","#cart-total","0.00"));steps.Add(new("empty","#cart-items"));}
            if(spec.PersistReceipt){steps.Add(new("reload"));steps.Add(new("same","#receipt"));}
            if(CatalogSpec.RequiresFreshReceipt(request)){
                steps.Add(new("click","#product-1 button"));steps.Add(new("click","#checkout"));
                steps.Add(new("new_id","#receipt",spec.ReceiptPrefix??""));
                if(spec.PersistReceipt){steps.Add(new("reload"));steps.Add(new("same","#receipt"));}
            }
            if(steps[^1].Op=="capture")steps.Add(new("same","#receipt"));
            Add("Checkout issues the requested receipt"+(spec.PersistReceipt?" and retains it on reload":""),"order",[..steps]);
        }
        // Some small models copy already-covered clauses into OtherRequirements. Remove only
        // clauses made entirely of vocabulary for the specific checks compiled above.
        bool Covered(string clause){
            var text=Regex.Replace(clause.ToLowerInvariant(),@"^(?:(?:the )?ability to |allow (?:users|shoppers) to )","");
            text=Regex.Replace(text,@"\b(?:display|view)\b","show");
            if(Regex.IsMatch(text,@"^build a single offline index\.html in (?:this|the current) directory[.!]?$"))return true;
            if(Regex.IsMatch(text,@"^(?:build|make(?: it)?|create) (?:a|an) (?:(?:whimsical|playful|colorful|colourful|rustic|modern|cozy|cosy|minimal|food|bear) ){0,6}(?:store|shop|market)(?: for [\p{L}-]+(?: to go shopping)?| in a single offline index\.html)?[.!]?$"))return true;
            if(Regex.IsMatch(text,@"^(?:add[- ]to[- ]cart functionality for each (?:item|product)|cart total calculation)[.!]?$"))return true;
            if(spec.ClearCart && !string.IsNullOrWhiteSpace(spec.ReceiptPrefix) && Regex.IsMatch(text,
                @"^checkout process that clears the cart and generates an? "+Regex.Escape(spec.ReceiptPrefix.TrimEnd('-').ToLowerInvariant())+@"-? receipt[.!]?$"))return true;
            if(spec.PersistReceipt && Regex.IsMatch(text,@"^receipt persistence after (?:page )?reload[.!]?$"))return true;
            if(Regex.IsMatch(text,@"^(?:(?:build|create|make) )?(?:a|an|the) (?:(?:whimsical|playful|colorful|colourful|rustic|modern|cozy|cosy|minimal|food|bear) ){0,5}(?:store|shop|market)(?: design)?(?: for [\p{L}-]+)?[.!]?$"))return true;
            if(Regex.IsMatch(text,@"\b(?:persist\w*|reload\w*)\b")&&!spec.PersistReceipt)return false;
            if(Regex.IsMatch(text,@"\bclear\w*\b")&&!spec.ClearCart)return false;
            if(Regex.IsMatch(text,@"\b(?:discount\w*|three or more|threshold)\b")&&!spec.Items.Any(i=>i.Threshold>0))return false;
            foreach(var i in spec.Items)
                text=Regex.Replace(text,@"(?<![\p{L}\p{N}_])"+Regex.Escape(i.Name.ToLowerInvariant())+@"(?![\p{L}\p{N}_])"," ");
            text=Regex.Replace(text,@"\bindex\.html\b|\$?\d+(?:\.\d+)?"," ");
            var known=new HashSet<string>("a an the product products item items food foods store shop single offline html in build create website at each cost costs shelf prices price for of and to their cart basket shoppers can add per unit units jar jars when there are one two three four five six seven eight or more charge show total new id order issue must checkout check out receipt clear clears that persists persist after reload see every other keep own sum is line totals issues same page reloaded on its counts quantity quantities discounted discount only applies threshold".Split(' '));
            if(!string.IsNullOrWhiteSpace(spec.ReceiptPrefix))text=text.Replace(spec.ReceiptPrefix.ToLowerInvariant()," ");
            return Regex.Matches(text,@"[a-z]+").All(t=>known.Contains(t.Value));
        }
        foreach(var other in spec.OtherRequirements.Distinct(StringComparer.OrdinalIgnoreCase).Where(s=>!string.IsNullOrWhiteSpace(s)&&!Covered(s))){
            plan.Units=[..plan.Units,other];plan.Checks.Add(new(plan.Units.Length,other,"unverified","","","Requires an additional acceptance check: "+other));
        }
        // The component implements only these interactions in a fresh, explicitly offline checkout.
        if(plan.Checks.All(c=>c.Kind=="browser_flow") && plan.Checks.Any(c=>c.Target=="order") &&
            Regex.IsMatch(request,@"\boffline\b",RegexOptions.IgnoreCase) && spec.ClearCart && spec.PersistReceipt &&
            (string.IsNullOrEmpty(spec.ReceiptPrefix)||request.Contains(spec.ReceiptPrefix,StringComparison.Ordinal)) &&
            spec.Items.All(i=>i.Price is >=0 and <=1000000 && i.SalePrice is >=0 and <=1000000))
            plan.Catalog=spec;
        return plan;
    }
}
