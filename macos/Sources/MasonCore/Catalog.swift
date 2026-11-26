import Foundation

// A built-in component for one common request: a fresh, single-page offline store with unit prices, quantity
// discounts, a cart that clears on checkout and a saved receipt. The model reads the offers out of the request and
// writes the store's name, copy and colours. Mason writes the page: totals are computed in whole cents and the
// receipt is generated in the browser. The offers are checked against the request, and the page against scenarios
// derived from them, before anything is claimed.

public struct CatalogItem: Equatable {
    public var name: String
    /// In cents, so no total is ever a rounded fraction.
    public var cents: Int
    public var saleCents: Int
    /// Quantity from which the sale price applies to this item; 0 for none.
    public var threshold: Int
    public init(name: String, cents: Int, saleCents: Int? = nil, threshold: Int = 0) { self.name = name; self.cents = cents; self.saleCents = saleCents ?? cents; self.threshold = threshold }
}

public struct CatalogSpec: Equatable {
    public var items: [CatalogItem]
    public var receiptPrefix = ""
    public var persistReceipt = false
    public var clearCart = false
    public var otherRequirements: [String] = []
}

public struct CatalogDesign: Equatable {
    public var title = "", introduction = "", accent = "", background = ""
    public var descriptions: [String] = []
}

public enum Catalog {
    static let amount = #"\$\s*(\d+(?:\.\d{1,2})?)(?![\d.])"#
    static let numbers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"]

    static func cents(_ text: String) -> Int? { Double(text).map { Int(($0 * 100).rounded()) } }
    static func money(_ cents: Int) -> String { String(format: "%d.%02d", cents / 100, cents % 100) }

    /// Every amount of money the request states, in cents.
    public static func amounts(_ request: String) -> Set<Int> {
        Set(Rx.matches(#"(?:\$\s*(\d+(?:\.\d{1,2})?)|\b(\d+(?:\.\d{1,2})?)\s+(?:US\s+)?dollars?\b)"#, in: request, options: [.caseInsensitive]).compactMap { cents($0[1].isEmpty ? $0[2] : $0[1]) })
    }

    public static func requiresFreshReceipt(_ request: String) -> Bool { Rx.found(#"\b(?:new|distinct|fresh|unique)\b[^.!?\n]{0,35}\b(?:receipt|order|identifier)\b"#, in: request) }

    /// A receipt prefix the request spells out, such as ORD-, when it names exactly one.
    public static func explicitReceiptPrefix(_ request: String) -> String? {
        let token = "([A-Za-z][A-Za-z0-9]{0,22}[-_])"
        let found = Rx.matches(#"(?<![\w-])"# + token + #"(?=['"`]?\s+(?i:receipt|order\b|confirmation))|(?i:\b(?:prefix(?:ed)?(?:\s+with)?|(?:start|begin)(?:s|ning|ing)?\s+with))\s+['"`]?"# + token, in: request).map { $0[1].isEmpty ? $0[2] : $0[1] }
        let distinct = Array(Set(found))
        return distinct.count == 1 ? distinct[0] : nil
    }

    /// The component is for an empty project and a request that is plainly an offline shop page with prices and a cart.
    public static func applies(_ request: String, workspace: Workspace) -> Bool {
        guard let listing = try? workspace.files(limit: 2), listing.paths.isEmpty else { return false }
        return request.lowercased().contains("index.html") && Rx.found(#"\b(store|shop|market)\b"#, in: request) && Rx.found(#"\b(cart|basket)\b"#, in: request) && amounts(request).count >= 2
    }

    static func clauses(_ request: String) -> [String] { Rx.split(#"(?<=[.!?])\s+|[;\r\n]+"#, request).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty } }

    /// Binds each item's unit price and quantity discount to what the request says about that item by name, so a
    /// price the model attached to the wrong product is corrected from the text.
    public static func bind(_ request: String, _ proposed: [CatalogItem]) -> [CatalogItem] {
        var offers = proposed
        let names = proposed.map { #"(?<![\p{L}\p{N}])"# + NSRegularExpression.escapedPattern(for: $0.name) + #"(?![\p{L}\p{N}])"# }
        for index in offers.indices {
            if let price = Rx.first(names[index] + #"\s+(?:(?:is|are|costs?|at)\s+)?"# + amount + #"\s*(?:each\b|per\b)"#, in: request, options: [.caseInsensitive]), let value = cents(price[1]) { offers[index].cents = value }
        }
        var tiers: [Int: (threshold: Int, cents: Int)] = [:]
        for clause in clauses(request) {
            let quantities = Rx.matches(#"\b(?:at least\s+(\d+|one|two|three|four|five|six|seven|eight|nine|ten)|(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+or more)\b"#, in: clause, options: [.caseInsensitive])
            if quantities.isEmpty { continue }
            let owners = names.indices.filter { Rx.found(names[$0], in: clause) }
            guard owners.count == 1, quantities.count == 1 else { return offers }
            let owner = owners[0]
            let prices = Set(Rx.matches(amount + #"\s*(?:each\b|per\b)"#, in: clause, options: [.caseInsensitive]).compactMap { cents($0[1]) }.filter { $0 != offers[owner].cents })
            let literal = (quantities[0][1].isEmpty ? quantities[0][2] : quantities[0][1]).lowercased()
            let count = Int(literal) ?? numbers.firstIndex(of: literal) ?? 0
            guard (1...10).contains(count), prices.count == 1, tiers[owner] == nil else { return offers }
            tiers[owner] = (count, prices.first!)
        }
        guard !tiers.isEmpty else { return offers }
        for index in offers.indices {
            if let tier = tiers[index] { offers[index].threshold = tier.threshold; offers[index].saleCents = tier.cents }
            else if proposed[index].threshold > 0, tiers.values.contains(where: { $0.threshold == proposed[index].threshold && $0.cents == proposed[index].saleCents }) {
                // A discount copied onto another item is dropped unless the request words a promotion for that item too.
                let promoted = clauses(request).contains { Rx.found(names[index], in: $0) && Rx.found(#"\b(?:discount\w*|bulk|sale|cheaper|when|if|over|above|below|threshold|or more|at least)\b|%"#, in: $0) }
                if !promoted { offers[index].threshold = 0; offers[index].saleCents = offers[index].cents }
            }
        }
        return offers
    }

    static let extractPrompt = """
    Extract the explicitly requested catalog rules, using original product names and unit prices. Reply with only JSON:
    {"Items": [{"Name": "...", "Price": 0, "SalePrice": 0, "Threshold": 0}], "ReceiptPrefix": "", "PersistReceipt": false, "ClearCart": false, "OtherRequirements": []}
    For regular items use Threshold 0 and SalePrice equal to Price. A quantity discount applies only to its named product. Use an empty ReceiptPrefix unless a prefix is specified.
    PersistReceipt and ClearCart describe explicit checkout rules. In OtherRequirements copy each original clause that asks for a functional feature outside listings, unit prices,
    add-to-cart, quantity discounts, totals, checkout, cart clearing and receipt persistence. Use [] when those cover everything asked. Do not include design wishes.
    """

    static let designPrompt = """
    Design the copy and palette of the requested offline storefront. Reply with only JSON:
    {"Title": "...", "Introduction": "...", "Accent": "#000000", "Background": "#ffffff", "Descriptions": ["one per product, in the given order"]}
    Write a distinctive store name, a short introduction in the requested voice and one short description per product. Use a dark accent and a light background.
    Product names, prices, discount notices and controls are rendered separately. Do not put monetary amounts or additional offers in your copy.
    """

    static func object(_ text: String) -> [String: Any]? {
        guard let start = text.firstIndex(of: "{"), let end = text.lastIndex(of: "}"), start < end else { return nil }
        return try? JSONSerialization.jsonObject(with: Data(text[start...end].utf8)) as? [String: Any]
    }

    /// Asks the model for the offers and checks every one against the request: each name and price must be in it.
    /// Returns nil when the request is not one the component can honestly build.
    public static func extract(_ request: String, model: ChatModel) async -> CatalogSpec? {
        guard let reply = try? await model.complete(messages: [ChatMessage(role: "system", content: extractPrompt), ChatMessage(role: "user", content: request)], tools: [], onText: { _ in }),
              let answer = object(reply.text), let listed = answer["Items"] as? [[String: Any]], (2...8).contains(listed.count) else { return nil }
        func number(_ value: Any?) -> Int? { (value as? NSNumber).map { Int(($0.doubleValue * 100).rounded()) } }
        var items: [CatalogItem] = []
        for item in listed {
            guard let name = (item["Name"] as? String)?.trimmingCharacters(in: .whitespaces), !name.isEmpty, name.count <= 80, let price = number(item["Price"]) else { return nil }
            items.append(CatalogItem(name: name, cents: price, saleCents: number(item["SalePrice"]) ?? price, threshold: item["Threshold"] as? Int ?? 0))
        }
        var spec = CatalogSpec(items: bind(request, items), receiptPrefix: answer["ReceiptPrefix"] as? String ?? "", persistReceipt: answer["PersistReceipt"] as? Bool ?? false,
                               clearCart: answer["ClearCart"] as? Bool ?? false, otherRequirements: answer["OtherRequirements"] as? [String] ?? [])
        if let prefix = explicitReceiptPrefix(request) { spec.receiptPrefix = prefix }
        else if !spec.receiptPrefix.isEmpty, !request.contains(spec.receiptPrefix) {
            if Rx.found(#"\bprefix\w*|\b(?:start|begin)\w* with"#, in: request) { return nil }
            spec.receiptPrefix = ""
        }
        let stated = amounts(request)
        for item in spec.items {
            guard request.lowercased().contains(item.name.lowercased()), stated.contains(item.cents), (0...8).contains(item.threshold), item.threshold == 0 || stated.contains(item.saleCents),
                  (0...100_000_000).contains(item.cents), (0...100_000_000).contains(item.saleCents) else { return nil }
        }
        guard Set(spec.items.map { $0.name.lowercased() }).count == spec.items.count else { return nil }
        spec.otherRequirements = spec.otherRequirements.filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty && !covered($0, spec) }
        return spec
    }

    /// Small models copy clauses the component already covers into OtherRequirements. A clause made only of the
    /// vocabulary of listings, prices, the cart, discounts, checkout and the receipt is one of those.
    static func covered(_ clause: String, _ spec: CatalogSpec) -> Bool {
        var text = clause.lowercased()
        if Rx.found(#"\b(?:persist\w*|reload\w*)\b"#, in: text), !spec.persistReceipt { return false }
        if Rx.found(#"\bclear\w*\b"#, in: text), !spec.clearCart { return false }
        if Rx.found(#"\b(?:discount\w*|three or more|threshold)\b"#, in: text), !spec.items.contains(where: { $0.threshold > 0 }) { return false }
        for item in spec.items { text = text.replacingOccurrences(of: item.name.lowercased(), with: " ") }
        if !spec.receiptPrefix.isEmpty { text = text.replacingOccurrences(of: spec.receiptPrefix.lowercased(), with: " ") }
        text = text.replacingOccurrences(of: #"\bindex\.html\b|\$?\d+(?:\.\d+)?"#, with: " ", options: .regularExpression)
        let known = Set("a an the product products item items food foods store shop market single offline html in build create make website at each cost costs shelf prices price for of and to their cart basket shoppers users can add per unit units jar jars when there are one two three four five six seven eight or more charge show display total new id order issue must checkout check out receipt clear clears that persists persist after reload see every other keep own sum is line totals issues same page reloaded on its counts quantity quantities discounted discount only applies threshold with it this directory current functionality calculation process generates persistence".split(separator: " ").map(String.init))
        return Rx.matches("[a-z]+", in: text).allSatisfy { known.contains($0[0]) }
    }

    /// True when the component builds everything the request asks for: nothing else to implement, an offline page,
    /// and a checkout that clears the cart and keeps its receipt.
    public static func complete(_ spec: CatalogSpec, request: String) -> Bool {
        spec.otherRequirements.isEmpty && Rx.found(#"\boffline\b"#, in: request) && Rx.found(#"\bcheckout|check out\b"#, in: request) && spec.clearCart && spec.persistReceipt
    }

    /// The scenarios that show the page does what the request asked, in the acceptance contract's own steps.
    public static func scenarios(_ spec: CatalogSpec, request: String) -> [AcceptanceScenario] {
        var all: [AcceptanceScenario] = []
        func step(_ op: String, _ target: String = "", _ value: String = "") -> AcceptanceStep { AcceptanceStep(op: op, target: target, value: value) }
        for (index, item) in spec.items.enumerated() {
            let card = "#product-\(index + 1)"
            all.append(AcceptanceScenario(id: "offer-\(index)", requirement: "Show \(item.name) at its requested unit price and add it to the cart", page: "index.html",
                                          steps: [step("text", card + " h2", item.name), step("shelf_price", card, money(item.cents)), step("click", card + " button"), step("money", "#cart-total", money(item.threshold == 1 ? item.saleCents : item.cents))]))
            if item.threshold > 1 {
                var steps: [AcceptanceStep] = []
                for count in 1...item.threshold {
                    steps.append(step("click", card + " button"))
                    if count >= item.threshold - 1 { steps.append(step("money", "#cart-total", money(count * (count >= item.threshold ? item.saleCents : item.cents)))) }
                }
                all.append(AcceptanceScenario(id: "discount-\(index)", requirement: "Apply the specified quantity discount for \(item.name)", page: "index.html", steps: steps))
            }
        }
        var steps = [step("click", "#product-1 button"), step("click", "#checkout"), step("capture", "#receipt", spec.receiptPrefix)]
        if spec.clearCart { steps += [step("money", "#cart-total", "0.00"), step("empty", "#cart-items")] }
        if spec.persistReceipt { steps += [step("reload"), step("same", "#receipt")] }
        if requiresFreshReceipt(request) {
            steps += [step("click", "#product-1 button"), step("click", "#checkout"), step("new_id", "#receipt", spec.receiptPrefix)]
            if spec.persistReceipt { steps += [step("reload"), step("same", "#receipt")] }
        }
        if steps.last?.op == "capture" { steps.append(step("same", "#receipt")) }
        all.append(AcceptanceScenario(id: "order", requirement: "Checkout issues the requested receipt" + (spec.persistReceipt ? " and retains it on reload" : ""), page: "index.html", steps: steps))
        return all
    }

    /// The store's name, copy and colours, from the model. Anything missing or malformed falls back to a plain default.
    public static func design(_ request: String, spec: CatalogSpec, model: ChatModel) async -> CatalogDesign {
        let names = spec.items.map(\.name).joined(separator: ", ")
        guard let reply = try? await model.complete(messages: [ChatMessage(role: "system", content: designPrompt), ChatMessage(role: "user", content: "Brief:\n\(request)\n\nProducts, in order: \(names)")], tools: [], onText: { _ in }),
              let answer = object(reply.text) else { return CatalogDesign() }
        func text(_ key: String, _ limit: Int) -> String { String((answer[key] as? String ?? "").prefix(limit)) }
        // The copy may not state prices: those come only from the checked offers.
        func clean(_ value: String) -> String { Rx.found(#"\$\s*\d|\b\d+(?:\.\d+)?\s*(?:dollars?|%|percent)"#, in: value) ? "" : value }
        return CatalogDesign(title: clean(text("Title", 80)), introduction: clean(text("Introduction", 200)), accent: text("Accent", 7), background: text("Background", 7),
                             descriptions: (answer["Descriptions"] as? [String] ?? []).map { clean(String($0.prefix(150))) })
    }

    static func html(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;").replacingOccurrences(of: "'", with: "&#39;")
    }

    /// A string as a JavaScript literal that is safe inside a script element.
    static func script(_ value: String) -> String {
        let encoded = (try? JSONSerialization.data(withJSONObject: [value])).map { String(decoding: $0, as: UTF8.self) } ?? "[\"\"]"
        return String(encoded.dropFirst().dropLast()).replacingOccurrences(of: "<", with: "\\u003c").replacingOccurrences(of: ">", with: "\\u003e").replacingOccurrences(of: "&", with: "\\u0026")
    }

    /// The page. Arithmetic is in whole cents; the receipt identifier is made in the browser and kept in its storage.
    public static func render(_ spec: CatalogSpec, _ design: CatalogDesign) -> String {
        func color(_ value: String, _ fallback: String) -> String { value.range(of: "^#[0-9A-Fa-f]{6}$", options: .regularExpression) != nil ? value : fallback }
        let offers = spec.items.enumerated().map { index, item -> String in
            let note = item.threshold > 0 ? "<p>Buy \(item.threshold) or more for $\(money(item.saleCents)) each.</p>" : ""
            let description = index < design.descriptions.count ? design.descriptions[index] : ""
            return """
            <article id="product-\(index + 1)">
              <h2>\(html(item.name))</h2><p class="price">$\(money(item.cents)) each</p>
              <p>\(html(description))</p>\(note)
              <button type="button" data-item="\(index)" aria-label="Add \(html(item.name)) to cart">Add to cart</button>
            </article>
            """
        }.joined(separator: "\n")
        let data = "[" + spec.items.map { "{name: \(script($0.name)), cents: \($0.cents), saleCents: \($0.saleCents), threshold: \($0.threshold)}" }.joined(separator: ", ") + "]"
        let title = html(design.title.trimmingCharacters(in: .whitespaces).isEmpty ? "Food market" : design.title)
        return """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>\(title)</title><style>
          :root { --accent: \(color(design.accent, "#435238")); --paper: \(color(design.background, "#f6f1e2")); }
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
        <header><h1>\(title)</h1><p>\(html(design.introduction))</p></header>
        <section class="shelf" aria-label="Products">\(offers)</section>
        <section class="basket" aria-label="Shopping cart"><h2>Your cart</h2><ul id="cart-items"></ul>
        <p id="cart-total" aria-live="polite"></p><button type="button" id="checkout">Checkout</button>
        <p id="receipt" role="status"></p></section></main>
        <script>
        const offers = \(data);
        const prefix = \(script(spec.receiptPrefix));
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

        """
    }
}
