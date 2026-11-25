using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mason.Code;

namespace Mason;

public sealed partial class CodeLocalModel
{
    internal sealed record CatalogDesign(string Title,string Introduction,string Accent,string Background,string[] Descriptions);

    async Task<CodeAction> DraftOfflineCatalog(CodePageDraft draft,CatalogSpec catalog,CancellationToken ct)
    {
        var color=new{type="string",pattern="^#[0-9A-Fa-f]{6}$"};
        var design=await IsolatedDecision<CatalogDesign>("""
            Design the copy and palette of the requested offline storefront. Write a distinctive store name,
            a short introduction in the requested voice and one short description per product, in the given order.
            Use a dark accent and a light background. Product names, prices, discount notices and controls
            are rendered separately. Do not put monetary amounts or additional offers in your copy.
            """,new{brief=draft.Request,items=catalog.Items.Select(i=>i.Name)},
            Shape(("Title",Str(80)),("Introduction",Str(200)),("Accent",color),("Background",color),
                ("Descriptions",new{type="array",minItems=catalog.Items.Length,maxItems=catalog.Items.Length,items=Str(150)})),ct);
        var content=RenderCatalog(catalog,design);
        return new("Creating the offline store using the catalog pricing and checkout component.","create_file",
            JsonSerializer.SerializeToElement(new{path=draft.Path,content}));
    }

    internal static string RenderCatalog(CatalogSpec catalog,CatalogDesign design)
    {
        static string H(string? value)=>WebUtility.HtmlEncode(value??"");
        static string Money(decimal price)=>price.ToString("0.00",CultureInfo.InvariantCulture);
        static string Color(string? value,string fallback)=>value!=null && Regex.IsMatch(value,@"\A#[a-fA-F0-9]{6}\z")?value:fallback;
        var offers=catalog.Items.Select((item,index)=>{
            var note=item.Threshold>0?$"<p>Buy {item.Threshold} or more for ${Money(item.SalePrice)} each.</p>":"";
            return $"""
                <article id="product-{index+1}">
                  <h2>{H(item.Name)}</h2><p class="price">${Money(item.Price)} each</p>
                  <p>{H(design.Descriptions?.ElementAtOrDefault(index))}</p>{note}
                  <button type="button" data-item="{index}" aria-label="Add {H(item.Name)} to cart">Add to cart</button>
                </article>
                """;
        });
        // Encode script literals with the default HTML-safe serializer. Keep arithmetic in integer cents.
        var data=JsonSerializer.Serialize(catalog.Items.Select(i=>new{name=i.Name,cents=decimal.ToInt64(i.Price*100),
            saleCents=decimal.ToInt64(i.SalePrice*100),threshold=i.Threshold}));
        var title=H(string.IsNullOrWhiteSpace(design.Title)?"Food market":design.Title);
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title><style>
              :root { --accent: {{Color(design.Accent,"#435238")}}; --paper: {{Color(design.Background,"#f6f1e2")}}; }
              * { box-sizing: border-box; } body { margin:0; color:#251f17; background:var(--paper); font:1.05rem/1.55 system-ui,sans-serif; }
              main { max-width:68rem; margin:auto; padding:clamp(1rem,5vw,4rem); }
              header { padding:1.5rem 0; border-bottom:3px solid var(--accent); }
              h1,h2 { font-family:Georgia,serif; line-height:1.1; margin:0 0 .5rem; } h1 { font-size:clamp(2.5rem,7vw,5.5rem); }
              .shelf { display:grid; grid-template-columns:repeat(auto-fit,minmax(min(100%,14rem),1fr)); gap:1rem; margin:2rem 0; }
              article,.basket { background:#fffdf8; border:1px solid #cec6b9; border-radius:1rem; padding:1.4rem; }
              article { display:flex; flex-direction:column; align-items:start; } article p { margin:.25rem 0 .75rem; }
              button { font:inherit; font-weight:650; color:white; background:var(--accent); border:0; border-radius:.5rem; padding:.65rem 1rem; cursor:pointer; }
              article button { margin-top:auto; } button:focus-visible { outline:3px solid currentColor; outline-offset:-5px; }
              button:disabled { opacity:.6; cursor:default; } .price,#cart-total { font-weight:bold; } #receipt { overflow-wrap:anywhere; }
            </style></head><body><main>
            <header><h1>{{title}}</h1><p>{{H(design.Introduction)}}</p></header>
            <section class="shelf" aria-label="Products">{{string.Join("\n",offers)}}</section>
            <section class="basket" aria-label="Shopping cart"><h2>Your cart</h2><ul id="cart-items"></ul>
            <p id="cart-total" aria-live="polite"></p><button type="button" id="checkout">Checkout</button>
            <p id="receipt" role="status"></p></section></main>
            <script>
            const offers = {{data}};
            const prefix = {{JsonSerializer.Serialize(catalog.ReceiptPrefix??"")}};
            const receiptKey = 'mason-store:receipt:' + location.pathname;
            const quantities = offers.map(() => 0);
            const rows = document.getElementById('cart-items');
            const checkout = document.getElementById('checkout');
            const receipt = document.getElementById('receipt');
            const currency = cents => '$' + (cents / 100).toFixed(2);
            function render() {
              let total = 0;
              rows.replaceChildren();
              offers.forEach((item, index) => {
                const quantity = quantities[index];
                if (!quantity) return;
                const unit = item.threshold > 0 && quantity >= item.threshold ? item.saleCents : item.cents;
                const subtotal = quantity * unit;
                total += subtotal;
                const row = document.createElement('li');
                row.textContent = quantity + ' × ' + item.name + ' — ' + currency(subtotal);
                rows.append(row);
              });
              document.getElementById('cart-total').textContent = 'Total: ' + currency(total);
              checkout.disabled = !quantities.some(quantity => quantity > 0);
            }
            document.querySelectorAll('button[data-item]').forEach(button => {
              button.addEventListener('click', () => { quantities[Number(button.dataset.item)]++; render(); });
            });
            checkout.addEventListener('click', () => {
              if (checkout.disabled) return;
              const bytes = crypto.getRandomValues(new Uint8Array(16));
              const id = prefix + Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
              localStorage.setItem(receiptKey, id);
              receipt.textContent = id;
              quantities.fill(0);
              render();
            });
            receipt.textContent = localStorage.getItem(receiptKey) || '';
            render();
            </script></body></html>
            """;
    }
}
