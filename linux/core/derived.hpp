// Checks Mason works out from a request before the work starts, and runs itself when the model says it is done; and a
// built-in component for one common request, a fresh single-page offline store.
#pragma once
#include <set>

#include "acceptance.hpp"

namespace mason {

struct DerivedPlan {
    std::vector<std::string> units;
    std::vector<DerivedCheck> checks;
    /// Requirements Mason had no way to check.
    std::vector<std::string> unverified() const;
    /// What the model is told before it starts, so it builds towards what will be checked.
    std::string brief() const;
};

namespace derived {
/// The check in a sentence, for the model and for the receipt.
std::string description(const DerivedCheck& check);
bool passed(const DerivedReceipt& receipt);
std::string summary(const DerivedReceipt& receipt);
std::string report(const DerivedReceipt& receipt);
/// The separate requirements of a request: its clauses, with quoted text kept whole.
std::vector<std::string> units(const std::string& request);
/// Works out what can be checked. `model` (may be null) is asked only when some requirement has no exact check and
/// the request is about pages.
DerivedPlan prepare(const std::string& request, const Workspace& workspace, ChatModel* model, const CancelToken& cancel = nullptr);
/// Runs every check. Browser checks are marked not run where there is no browser.
std::vector<DerivedResult> verify(const DerivedPlan& plan, const Workspace& workspace, bool require_sandbox = false, const CancelToken& cancel = nullptr);
}  // namespace derived

struct CatalogItem {
    std::string name;
    int cents = 0;       ///< In cents, so no total is ever a rounded fraction.
    int sale_cents = 0;
    int threshold = 0;   ///< Quantity from which the sale price applies to this item; 0 for none.
    bool operator==(const CatalogItem& other) const { return name == other.name && cents == other.cents && sale_cents == other.sale_cents && threshold == other.threshold; }
};

struct CatalogSpec {
    std::vector<CatalogItem> items;
    std::string receipt_prefix;
    bool persist_receipt = false, clear_cart = false;
    std::vector<std::string> other_requirements;
};

struct CatalogDesign {
    std::string title, introduction, accent, background;
    std::vector<std::string> descriptions;
};

/// The model reads the offers out of the request and writes the store's name, copy and colours. Mason writes the page:
/// totals are computed in whole cents and the receipt is generated in the browser. The offers are checked against the
/// request, and the page against scenarios derived from them, before anything is claimed.
namespace catalog {
std::string money(int cents);
/// Every amount of money the request states, in cents.
std::set<int> amounts(const std::string& request);
bool requires_fresh_receipt(const std::string& request);
/// A receipt prefix the request spells out, such as ORD-, when it names exactly one.
std::optional<std::string> explicit_receipt_prefix(const std::string& request);
/// The component is for an empty project and a request that is plainly an offline shop page with prices and a cart.
bool applies(const std::string& request, const Workspace& workspace);
/// Binds each item's unit price and quantity discount to what the request says about that item by name.
std::vector<CatalogItem> bind(const std::string& request, const std::vector<CatalogItem>& proposed);
/// Asks the model for the offers and checks every one against the request. Nothing when the request is not one the
/// component can honestly build.
std::optional<CatalogSpec> extract(const std::string& request, ChatModel& model, const CancelToken& cancel = nullptr);
/// True when the component builds everything the request asks for.
bool complete(const CatalogSpec& spec, const std::string& request);
/// The scenarios that show the page does what the request asked.
std::vector<AcceptanceScenario> scenarios(const CatalogSpec& spec, const std::string& request);
CatalogDesign design(const std::string& request, const CatalogSpec& spec, ChatModel& model, const CancelToken& cancel = nullptr);
std::string render(const CatalogSpec& spec, const CatalogDesign& design);
}  // namespace catalog

}  // namespace mason
