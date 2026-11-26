#include "derived.hpp"
#include "project_tests.hpp"

#include <algorithm>
#include <cmath>
#include <regex>
#include <set>

namespace mason {

namespace {

bool word_byte(unsigned char c) { return std::isalnum(c) || c == '_' || c >= 0x80; }
bool letter_or_digit(unsigned char c) { return std::isalnum(c) || c >= 0x80; }

/// Positions where `needle` appears in `text` without regard to case and not inside a longer word.
std::vector<size_t> find_name(const std::string& text, const std::string& needle) {
    std::vector<size_t> found;
    const std::string lower = to_lower(text), wanted = to_lower(needle);
    if (wanted.empty()) return found;
    for (size_t at = lower.find(wanted); at != std::string::npos; at = lower.find(wanted, at + 1)) {
        const bool before = at == 0 || !letter_or_digit(static_cast<unsigned char>(lower[at - 1]));
        const bool after = at + wanted.size() >= lower.size() || !letter_or_digit(static_cast<unsigned char>(lower[at + wanted.size()]));
        if (before && after) found.push_back(at);
    }
    return found;
}

bool found(const std::string& pattern, const std::string& text) { return std::regex_search(text, std::regex(pattern, std::regex::icase)); }

std::optional<int> to_cents(const std::string& text) {
    char* end = nullptr;
    const double value = std::strtod(text.c_str(), &end);
    if (text.empty() || !end || *end != '\0') return std::nullopt;
    return static_cast<int>(std::lround(value * 100));
}

std::vector<std::string> clauses(const std::string& request) {
    std::vector<std::string> parts;
    std::string current;
    for (size_t i = 0; i < request.size(); i++) {
        const char c = request[i];
        if (c == ';' || c == '\r' || c == '\n') {
            parts.push_back(current);
            current.clear();
            continue;
        }
        current += c;
        if ((c == '.' || c == '!' || c == '?') && i + 1 < request.size() && std::isspace(static_cast<unsigned char>(request[i + 1]))) {
            parts.push_back(current);
            current.clear();
        }
    }
    parts.push_back(current);
    std::vector<std::string> kept;
    for (const std::string& part : parts) {
        if (!trim(part).empty()) kept.push_back(trim(part));
    }
    return kept;
}

std::optional<json> object_in(const std::string& text) {
    const size_t start = text.find('{'), end = text.rfind('}');
    if (start == std::string::npos || end == std::string::npos || start > end) return std::nullopt;
    json parsed = json::parse(text.substr(start, end - start + 1), nullptr, false);
    if (!parsed.is_object()) return std::nullopt;
    return parsed;
}

/// A field of a model's JSON reply, whatever case the model wrote the name in.
const json* field(const json& object, const std::string& name) {
    for (auto it = object.begin(); it != object.end(); ++it) {
        if (to_lower(it.key()) == name) return &it.value();
    }
    return nullptr;
}
std::string text_of(const json& object, const std::string& name) {
    const json* value = field(object, name);
    return value && value->is_string() ? value->get<std::string>() : "";
}

std::string prose(const std::string& request) { return request.substr(0, request.find("```mason.acceptance.json")); }

/// The pages a request names, such as shop.html.
std::vector<std::string> pages_in(const std::string& request) {
    static const std::regex pattern(R"((?:[\w-]+/)*[\w-]+\.html?\b)", std::regex::icase);
    std::vector<std::string> pages;
    for (std::sregex_iterator it(request.begin(), request.end(), pattern), end; it != end; ++it) {
        const size_t at = static_cast<size_t>(it->position(0));
        if (at > 0 && (word_byte(static_cast<unsigned char>(request[at - 1])) || std::string("/\\:.-").find(request[at - 1]) != std::string::npos)) continue;
        const std::string page = it->str(0);
        if (std::none_of(pages.begin(), pages.end(), [&](const std::string& known) { return to_lower(known) == to_lower(page); })) pages.push_back(page);
    }
    return pages;
}

std::optional<DerivedCheck> explicit_check(const std::string& clause, int unit, const std::vector<std::string>& pages) {
    static const std::regex document(R"rx(^(?:create|update|write)\s+([\w./-]+\.(?:txt|md|csv))\s+(?:containing|to contain|with the text)\s+"([^"]+)"$)rx", std::regex::icase);
    static const std::regex heading(R"rx(^(?:in\s+[\w./-]+\.html?\s+)?(?:show|display|add|create)\s+(?:a|the|an)\s+(?:page\s+)?heading\s+(?:(?:named|reading|labeled|saying|with (?:the )?text)\s+)?"([^"]+)"$)rx", std::regex::icase);
    static const std::regex click(R"rx(^(?:clicking|pressing|tapping)\s+(?:(?:the|a)\s+)?(?:(?:button|link)\s+)?"([^"]+)"\s+(?:must\s+|should\s+)?(?:show|display|reveal)s?\s+"([^"]+)"(?:\s+on (?:the )?page)?$)rx", std::regex::icase);
    std::smatch match;
    if (std::regex_match(clause, match, document)) return DerivedCheck{unit, clause, "document_text", match[1].str(), "", match[2].str(), {}, ""};
    if (pages.size() > 1) return std::nullopt;
    const std::string page = pages.empty() ? "index.html" : pages[0];
    if (std::regex_match(clause, match, heading)) return DerivedCheck{unit, clause, "browser_heading", page, "", match[1].str(), {}, ""};
    if (std::regex_match(clause, match, click)) return DerivedCheck{unit, clause, "browser_click", page, match[1].str(), match[2].str(), {}, ""};
    return std::nullopt;
}

bool same_check(const DerivedCheck& a, const DerivedCheck& b) { return a.unit == b.unit && a.kind == b.kind && a.path == b.path && a.target == b.target && a.expected == b.expected; }

/// True when a proposed check is one Mason can run and that really covers its requirement.
bool valid(const DerivedCheck& check, const std::string& unit, const std::string& request, const Workspace& workspace) {
    if (trim(check.feature).empty() || utf8_length(check.feature) > 400 || utf8_length(check.target) > 200 || check.expected.empty() || utf8_length(check.expected) > 500 || check.path.empty() || check.path.size() > 260) return false;
    try {
        workspace.resolve(check.path);
    } catch (const std::exception&) {
        return false;
    }
    const std::string kind = to_lower(fs::path(check.path).extension().string());
    const bool browser = check.kind == "browser_heading" || check.kind == "browser_text" || check.kind == "browser_click";
    if (browser && kind != ".html" && kind != ".htm") return false;
    if (check.kind == "document_text") {
        if (!(kind == ".txt" || kind == ".md" || kind == ".csv") || !found(R"(\.(?:txt|md|csv)\b|\b(?:documentation|document|readme|notes|file|text)\b)", request)) return false;
    } else if (!browser) {
        return false;
    }
    // "visible" or "true" is not something a page shows unless the request quotes it.
    if (browser && std::regex_match(check.expected, std::regex("visible|true|passed|successfully displayed", std::regex::icase)) && to_lower(unit).find("\"" + to_lower(check.expected) + "\"") == std::string::npos) return false;
    const bool interaction = found(R"(\b(click(?:ing|ed)?|press(?:ing|ed)?|tapp?(?:ing|ed)?|toggl(?:e|ing))\b)", unit);
    if (interaction && check.kind != "browser_click") return false;
    if (check.kind == "browser_click" && trim(check.target).empty()) return false;
    // The expected text must come from the request, not from the model's imagination.
    return request.find(check.expected) != std::string::npos && (check.target.empty() || request.find(check.target) != std::string::npos);
}

const char* const kPlannerPrompt =
    "Design black-box acceptance checks BEFORE the work is done. Reply with only JSON: {\"checks\": [{\"Unit\": 1, \"Feature\": \"...\", \"Kind\": \"...\", \"Path\": \"...\", \"Target\": \"\", \"Expected\": \"...\"}]}.\n"
    "Unit is the number of the requirement the check covers. Kind is one of:\n"
    "browser_heading: the page at Path shows a heading whose text is exactly Expected.\n"
    "browser_text: the page at Path shows the exact text Expected.\n"
    "browser_click: clicking the button or link labelled exactly Target on the page at Path makes the text Expected appear. Use this for any requirement about clicking, pressing or toggling.\n"
    "document_text: the text file at Path (.txt, .md or .csv) contains Expected.\n"
    "unverified: none of these can check the requirement; say why in Expected.\n"
    "Use only text the request itself gives in quotes or states exactly. Do not invent labels. Path is relative to the project; use index.html when the request names no page. At most four checks.";

}  // namespace

std::vector<std::string> DerivedPlan::unverified() const {
    std::vector<std::string> open;
    for (size_t i = 0; i < units.size(); i++) {
        if (std::none_of(checks.begin(), checks.end(), [&](const DerivedCheck& check) { return check.unit == static_cast<int>(i) + 1; })) open.push_back(units[i]);
    }
    return open;
}

std::string DerivedPlan::brief() const {
    if (checks.empty()) return "";
    std::string text = "Mason will check these itself when you say you are done:";
    for (const DerivedCheck& check : checks) text += "\n- " + derived::description(check);
    return text;
}

namespace derived {

std::string passed_receipt(const Session& session) {
    for (auto turn = session.turns.rbegin(); turn != session.turns.rend(); ++turn) {
        if (turn->undone || !turn->checks) continue;
        return passed(*turn->checks) && !turn->checks->results.empty() ? report(*turn->checks) : "";
    }
    return "";
}

std::string description(const DerivedCheck& check) {
    if (check.kind == "document_text") return check.path + " contains “" + check.expected + "”";
    if (check.kind == "browser_heading") return check.path + " shows a heading that reads “" + check.expected + "”";
    if (check.kind == "browser_text") return check.path + " shows “" + check.expected + "”";
    if (check.kind == "browser_click") return "in " + check.path + ", clicking “" + check.target + "” makes “" + check.expected + "” appear";
    if (check.kind == "project_test") return "the project's test “" + check.target + "” passes when the workflow " + check.path + " runs (its test files are pinned: they must stay as they are)";
    return check.path + ": " + check.feature;
}

bool passed(const DerivedReceipt& receipt) {
    return std::none_of(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state == "failed"; });
}

std::string summary(const DerivedReceipt& receipt) {
    const auto failed = std::count_if(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state == "failed"; });
    const auto ran = std::count_if(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state != "notRun"; });
    const std::string count = std::to_string(ran) + (ran == 1 ? " requirement" : " requirements");
    std::string text = ran == 0 ? "Mason's own checks could not run here" : failed == 0 ? "Mason checked " + count + " itself: all hold" : "Mason checked " + count + " itself: " + std::to_string(failed) + " failed";
    if (!receipt.unverified.empty()) text += " · " + std::to_string(receipt.unverified.size()) + " had no check it could run";
    if (receipt.repairs > 0) text += " · handed back " + std::to_string(receipt.repairs) + (receipt.repairs == 1 ? " time" : " times");
    return text;
}

std::string report(const DerivedReceipt& receipt) {
    std::string text = summary(receipt) + ".";
    for (const DerivedResult& result : receipt.results) {
        text += std::string("\n") + (result.state == "passed" ? "PASSED " : result.state == "failed" ? "FAILED " : "NOT RUN ") + description(result.check) + (result.detail.empty() ? "" : "\n  " + result.detail);
    }
    for (const std::string& unit : receipt.unverified) text += "\nNO CHECK " + unit;
    return text;
}

std::vector<std::string> units(const std::string& request) {
    const std::string input = prose(request);
    std::vector<std::string> found;
    std::string current;
    const auto flush = [&] {
        if (!trim(current).empty()) found.push_back(trim(current));
        current.clear();
    };
    const auto starts = [&](size_t at, const char* text) { return input.compare(at, std::strlen(text), text) == 0; };
    size_t i = 0;
    while (i < input.size()) {
        const char c = input[i];
        // Quoted text is kept whole, whatever it contains.
        const char* close = c == '"' ? "\"" : starts(i, "“") ? "”" : starts(i, "‘") ? "’" : nullptr;
        if (close) {
            const size_t open = c == '"' ? 1 : 3;
            const size_t end = input.find(close, i + open);
            if (end != std::string::npos) {
                current += input.substr(i, end + std::strlen(close) - i);
                i = end + std::strlen(close);
                continue;
            }
        }
        if (c == ';' || c == '\r' || c == '\n') { flush(); i++; continue; }
        if ((c == '.' || c == '!' || c == '?') && (i + 1 == input.size() || std::isspace(static_cast<unsigned char>(input[i + 1])))) { flush(); i++; continue; }
        if (c == ',' && i + 1 < input.size() && std::isspace(static_cast<unsigned char>(input[i + 1]))) { flush(); i++; continue; }
        if (std::isspace(static_cast<unsigned char>(c))) {
            // "and", "and also", "and then", "as well as" and "plus" between two requirements.
            size_t next = i;
            while (next < input.size() && std::isspace(static_cast<unsigned char>(input[next]))) next++;
            bool joined = false;
            for (const char* word : {"and also", "and then", "as well as", "and", "plus"}) {
                const size_t length = std::strlen(word);
                if (to_lower(input.substr(next, length)) == word && next + length < input.size() && std::isspace(static_cast<unsigned char>(input[next + length]))) {
                    flush();
                    i = next + length;
                    joined = true;
                    break;
                }
            }
            if (joined) continue;
        }
        current += c;
        i++;
    }
    flush();
    return found;
}

DerivedPlan prepare(const std::string& request, const Workspace& workspace, ChatModel* model, const CancelToken& cancel, bool require_sandbox) {
    DerivedPlan plan;
    // A contract given with the request belongs to this request: its scenarios are the checks.
    const size_t fence = request.find("```mason.acceptance.json");
    if (fence != std::string::npos) {
        const std::string block = trim(request.substr(fence + std::strlen("```mason.acceptance.json")));
        if (!ends_with(block, "```")) return plan;
        try {
            const AcceptanceContract contract = acceptance::parse(workspace, block.substr(0, block.size() - 3));
            for (size_t i = 0; i < contract.scenarios.size(); i++) {
                const AcceptanceScenario& scenario = contract.scenarios[i];
                plan.units.push_back(scenario.requirement);
                DerivedCheck check{static_cast<int>(i) + 1, scenario.requirement, "browser_flow", scenario.page, scenario.id, "All scenario assertions pass", {}, contract.application};
                for (const AcceptanceStep& step : scenario.steps) check.steps.push_back({step.op, step.target, step.value});
                plan.checks.push_back(std::move(check));
            }
        } catch (const std::exception&) {
            plan = DerivedPlan();
        }
        return plan;
    }
    plan.units = units(request);
    if (plan.units.size() > 64) plan.units.resize(64);
    // A requirement the project names a test for is checked by that test. A request that is one such requirement as a
    // whole stays whole.
    std::vector<ProjectTest> named;
    try {
        named = project_tests::load(workspace);
    } catch (const std::exception&) {
    }
    bool some_unit = false;
    for (const std::string& unit : plan.units) some_unit = some_unit || project_tests::bound(unit, named).has_value();
    if (!some_unit && project_tests::bound(request, named)) plan.units = {trim(request)};
    if (plan.units.empty() || plan.units.size() > 32) return plan;
    std::set<int> tested;
    for (size_t i = 0; i < plan.units.size(); i++) {
        if (const auto test = project_tests::bound(plan.units[i], named)) {
            plan.checks.push_back(project_tests::check(*test, static_cast<int>(i) + 1, workspace, require_sandbox, cancel));
            tested.insert(static_cast<int>(i) + 1);
        }
    }
    const std::vector<std::string> pages = pages_in(request);
    for (size_t i = 0; i < plan.units.size(); i++) {
        if (tested.count(static_cast<int>(i) + 1)) continue;
        if (auto check = explicit_check(plan.units[i], static_cast<int>(i) + 1, pages)) plan.checks.push_back(*check);
    }
    const std::vector<std::string> open = plan.unverified();
    const bool about_pages = !pages.empty() || workspace.exists("index.html") || found(R"(\.(?:txt|md|csv)\b)", request);
    if (open.empty() || !about_pages || !model) return plan;
    std::string listed;
    for (size_t i = 0; i < plan.units.size(); i++) listed += (i ? "\n" : "") + std::to_string(i + 1) + ". " + plan.units[i];
    ModelReply reply;
    try {
        reply = model->complete({{"system", kPlannerPrompt, {}, ""}, {"user", "Request:\n" + prose(request) + "\n\nRequirements:\n" + listed, {}, ""}}, {}, [](const std::string&) {}, cancel);
    } catch (const Cancelled&) {
        throw;
    } catch (const std::exception&) {
        return plan;
    }
    const std::optional<json> answer = object_in(reply.text);
    const json* proposed = answer ? field(*answer, "checks") : nullptr;
    if (!proposed || !proposed->is_array() || proposed->size() > 64) return plan;
    size_t taken = 0;
    for (const json& item : *proposed) {
        if (taken++ >= 8 || !item.is_object()) continue;
        const json* number = field(item, "unit");
        const int unit = number && number->is_number_integer() ? number->get<int>() : 0;
        if (unit < 1 || static_cast<size_t>(unit) > plan.units.size()) continue;
        if (std::any_of(plan.checks.begin(), plan.checks.end(), [&](const DerivedCheck& known) { return known.unit == unit && explicit_check(plan.units[static_cast<size_t>(unit) - 1], unit, pages).has_value(); })) continue;
        DerivedCheck check{unit, text_of(item, "feature").empty() ? plan.units[static_cast<size_t>(unit) - 1] : text_of(item, "feature"), text_of(item, "kind"), text_of(item, "path"), text_of(item, "target"), text_of(item, "expected"), {}, ""};
        if (valid(check, plan.units[static_cast<size_t>(unit) - 1], request, workspace) && std::none_of(plan.checks.begin(), plan.checks.end(), [&](const DerivedCheck& known) { return same_check(known, check); })) {
            plan.checks.push_back(std::move(check));
        }
    }
    std::stable_sort(plan.checks.begin(), plan.checks.end(), [](const DerivedCheck& a, const DerivedCheck& b) { return a.unit < b.unit; });
    return plan;
}

std::vector<DerivedResult> verify(const DerivedPlan& plan, const Workspace& workspace, bool require_sandbox, const CancelToken& cancel) {
    std::vector<DerivedResult> results;
    for (const DerivedCheck& check : plan.checks) {
        check_cancel(cancel);
        if (check.kind == "project_test") {
            results.push_back(project_tests::verify(check, workspace, require_sandbox, cancel));
            continue;
        }
        if (check.kind == "document_text") {
            std::optional<std::string> content;
            try {
                if (workspace.exists(check.path)) content = workspace.read(check.path);
            } catch (const std::exception&) {
            }
            const bool holds = content && content->find(check.expected) != std::string::npos;
            results.push_back({check, holds ? "passed" : "failed", holds ? "" : content ? "The file does not contain it." : check.path + " does not exist."});
            continue;
        }
        if (browser::find().empty()) {
            results.push_back({check, "notRun", "No Chrome, Chromium, Edge or Brave on this computer."});
            continue;
        }
        try {
            if (check.kind == "browser_flow") {
                AcceptanceScenario scenario{check.target, check.feature, check.path, {}};
                for (const std::vector<std::string>& step : check.steps) scenario.steps.push_back({step.empty() ? "" : step[0], step.size() > 1 ? step[1] : "", step.size() > 2 ? step[2] : ""});
                const AcceptanceResult outcome = acceptance::run(workspace, AcceptanceContract{check.application, {scenario}}, nullptr, cancel, require_sandbox).at(0);
                results.push_back({check, outcome.passed ? "passed" : "failed", join(outcome.detail, "; ")});
                continue;
            }
            if (!workspace.exists(check.path)) {
                results.push_back({check, "failed", check.path + " does not exist."});
                continue;
            }
            json steps = json::array();
            if (check.kind == "browser_heading") steps.push_back({{"action", "expect_heading"}, {"text", check.expected}});
            else if (check.kind == "browser_text") steps.push_back({{"action", "expect_text"}, {"text", check.expected}});
            else {
                // The text has to appear because of the click, so it must not be there before it.
                steps.push_back({{"action", "expect_no_text"}, {"text", check.expected}});
                steps.push_back({{"action", "click"}, {"text", check.target}});
                steps.push_back({{"action", "expect_text"}, {"text", check.expected}});
            }
            const browser::Result result = browser::run(workspace, check.path, steps, cancel);
            std::string detail;
            const auto failed = std::find_if(result.steps.begin(), result.steps.end(), [](const browser::Step& step) { return !step.ok; });
            if (failed != result.steps.end()) {
                detail = check.kind == "browser_click" && failed == result.steps.begin() ? "“" + check.expected + "” is already shown before the click." : failed->action + ": " + failed->detail;
            } else if (!result.errors.empty()) {
                detail = "The page raised: " + result.errors.front();
            }
            results.push_back({check, detail.empty() ? "passed" : "failed", detail});
        } catch (const Cancelled&) {
            throw;
        } catch (const std::exception& error) {
            results.push_back({check, "failed", error.what()});
        }
    }
    return results;
}

}  // namespace derived

// ---- the catalog component --------------------------------------------------------------------------------------

namespace catalog {

namespace {

const char* const kAmount = R"(\$\s*(\d+(?:\.\d{1,2})?)(?![\d.]))";
const char* const kNumbers[] = {"zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"};

const char* const kExtractPrompt =
    "Extract the explicitly requested catalog rules, using original product names and unit prices. Reply with only JSON:\n"
    "{\"Items\": [{\"Name\": \"...\", \"Price\": 0, \"SalePrice\": 0, \"Threshold\": 0}], \"ReceiptPrefix\": \"\", \"PersistReceipt\": false, \"ClearCart\": false, \"OtherRequirements\": []}\n"
    "For regular items use Threshold 0 and SalePrice equal to Price. A quantity discount applies only to its named product. Use an empty ReceiptPrefix unless a prefix is specified.\n"
    "PersistReceipt and ClearCart describe explicit checkout rules. In OtherRequirements copy each original clause that asks for a functional feature outside listings, unit prices,\n"
    "add-to-cart, quantity discounts, totals, checkout, cart clearing and receipt persistence. Use [] when those cover everything asked. Do not include design wishes.";

const char* const kDesignPrompt =
    "Design the copy and palette of the requested offline storefront. Reply with only JSON:\n"
    "{\"Title\": \"...\", \"Introduction\": \"...\", \"Accent\": \"#000000\", \"Background\": \"#ffffff\", \"Descriptions\": [\"one per product, in the given order\"]}\n"
    "Write a distinctive store name, a short introduction in the requested voice and one short description per product. Use a dark accent and a light background.\n"
    "Product names, prices, discount notices and controls are rendered separately. Do not put monetary amounts or additional offers in your copy.";

const char* const kPage = R"page(<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>@TITLE@</title><style>
  :root { --accent: @ACCENT@; --paper: @PAPER@; }
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
<header><h1>@TITLE@</h1><p>@INTRO@</p></header>
<section class="shelf" aria-label="Products">@OFFERS@</section>
<section class="basket" aria-label="Shopping cart"><h2>Your cart</h2><ul id="cart-items"></ul>
<p id="cart-total" aria-live="polite"></p><button type="button" id="checkout">Checkout</button>
<p id="receipt" role="status"></p></section></main>
<script>
const offers = @DATA@;
const prefix = @PREFIX@;
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
)page";

std::string escape_regex(const std::string& text) {
    std::string out;
    for (const char c : text) {
        if (std::string("\\^$.|?*+()[]{}").find(c) != std::string::npos) out += '\\';
        out += c;
    }
    return out;
}

/// True when the name appears in the text as a whole word.
bool names(const std::string& text, const std::string& name) { return !find_name(text, name).empty(); }

std::string html(const std::string& value) {
    std::string out;
    for (const char c : value) {
        switch (c) {
            case '&': out += "&amp;"; break;
            case '<': out += "&lt;"; break;
            case '>': out += "&gt;"; break;
            case '"': out += "&quot;"; break;
            case '\'': out += "&#39;"; break;
            default: out += c;
        }
    }
    return out;
}

/// A string as a JavaScript literal that is safe inside a script element.
std::string script(const std::string& value) {
    return replace_all(replace_all(replace_all(json(value).dump(), "<", "\\u003c"), ">", "\\u003e"), "&", "\\u0026");
}

/// Small models copy clauses the component already covers into OtherRequirements. A clause made only of the
/// vocabulary of listings, prices, the cart, discounts, checkout and the receipt is one of those.
bool covered(const std::string& clause, const CatalogSpec& spec) {
    std::string text = to_lower(clause);
    const bool discounted = std::any_of(spec.items.begin(), spec.items.end(), [](const CatalogItem& item) { return item.threshold > 0; });
    if (found(R"(\b(?:persist\w*|reload\w*)\b)", text) && !spec.persist_receipt) return false;
    if (found(R"(\bclear\w*\b)", text) && !spec.clear_cart) return false;
    if (found(R"(\b(?:discount\w*|three or more|threshold)\b)", text) && !discounted) return false;
    for (const CatalogItem& item : spec.items) text = replace_all(text, to_lower(item.name), " ");
    if (!spec.receipt_prefix.empty()) text = replace_all(text, to_lower(spec.receipt_prefix), " ");
    text = std::regex_replace(text, std::regex(R"(\bindex\.html\b|\$?\d+(?:\.\d+)?)"), " ");
    static const std::set<std::string> known = [] {
        std::set<std::string> words;
        for (const std::string& word : split("a an the product products item items food foods store shop market single offline html in build create make website at each cost costs shelf prices price for of and to their cart basket shoppers users can add per unit units jar jars when there are one two three four five six seven eight or more charge show display total new id order issue must checkout check out receipt clear clears that persists persist after reload see every other keep own sum is line totals issues same page reloaded on its counts quantity quantities discounted discount only applies threshold with it this directory current functionality calculation process generates persistence", ' ', false)) {
            words.insert(word);
        }
        return words;
    }();
    static const std::regex word("[a-z]+");
    for (std::sregex_iterator it(text.begin(), text.end(), word), end; it != end; ++it) {
        if (!known.count(it->str(0))) return false;
    }
    return true;
}

}  // namespace

std::string money(int cents) {
    char text[32];
    std::snprintf(text, sizeof text, "%d.%02d", cents / 100, cents % 100);
    return text;
}

std::set<int> amounts(const std::string& request) {
    static const std::regex pattern(R"((?:\$\s*(\d+(?:\.\d{1,2})?)|\b(\d+(?:\.\d{1,2})?)\s+(?:US\s+)?dollars?\b))", std::regex::icase);
    std::set<int> stated;
    for (std::sregex_iterator it(request.begin(), request.end(), pattern), end; it != end; ++it) {
        if (const auto value = to_cents((*it)[1].matched ? (*it)[1].str() : (*it)[2].str())) stated.insert(*value);
    }
    return stated;
}

bool requires_fresh_receipt(const std::string& request) { return found(R"(\b(?:new|distinct|fresh|unique)\b[^.!?\n]{0,35}\b(?:receipt|order|identifier)\b)", request); }

std::optional<std::string> explicit_receipt_prefix(const std::string& request) {
    static const std::regex before(R"(([A-Za-z][A-Za-z0-9]{0,22}[-_])['"`]?\s+(?:[Rr]eceipt|[Oo]rder\b|[Cc]onfirmation))");
    static const std::regex after(R"(\b(?:prefix(?:ed)?(?:\s+with)?|(?:start|begin)(?:s|ning|ing)?\s+with)\s+['"`]?([A-Za-z][A-Za-z0-9]{0,22}[-_]))", std::regex::icase);
    std::set<std::string> prefixes;
    for (std::sregex_iterator it(request.begin(), request.end(), before), end; it != end; ++it) {
        const size_t at = static_cast<size_t>(it->position(1));
        if (at > 0 && (word_byte(static_cast<unsigned char>(request[at - 1])) || request[at - 1] == '-')) continue;
        prefixes.insert((*it)[1].str());
    }
    for (std::sregex_iterator it(request.begin(), request.end(), after), end; it != end; ++it) prefixes.insert((*it)[1].str());
    if (prefixes.size() != 1) return std::nullopt;
    return *prefixes.begin();
}

bool applies(const std::string& request, const Workspace& workspace) {
    try {
        if (!workspace.files(".", 2).paths.empty()) return false;
    } catch (const std::exception&) {
        return false;
    }
    return to_lower(request).find("index.html") != std::string::npos && found(R"(\b(store|shop|market)\b)", request) && found(R"(\b(cart|basket)\b)", request) && amounts(request).size() >= 2;
}

std::vector<CatalogItem> bind(const std::string& request, const std::vector<CatalogItem>& proposed) {
    std::vector<CatalogItem> offers = proposed;
    for (CatalogItem& offer : offers) {
        const std::regex price(escape_regex(offer.name) + R"(\s+(?:(?:is|are|costs?|at)\s+)?)" + kAmount + R"(\s*(?:each\b|per\b))", std::regex::icase);
        for (std::sregex_iterator it(request.begin(), request.end(), price), end; it != end; ++it) {
            const size_t at = static_cast<size_t>(it->position(0));
            if (at > 0 && letter_or_digit(static_cast<unsigned char>(request[at - 1]))) continue;
            if (const auto value = to_cents((*it)[1].str())) offer.cents = *value;
            break;
        }
    }
    static const std::regex quantity(R"(\b(?:at least\s+(\d+|one|two|three|four|five|six|seven|eight|nine|ten)|(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+or more)\b)", std::regex::icase);
    static const std::regex each(std::string(kAmount) + R"(\s*(?:each\b|per\b))", std::regex::icase);
    std::map<size_t, std::pair<int, int>> tiers;  // item -> threshold, sale price in cents
    for (const std::string& clause : clauses(request)) {
        std::vector<std::string> quantities;
        for (std::sregex_iterator it(clause.begin(), clause.end(), quantity), end; it != end; ++it) quantities.push_back(to_lower((*it)[1].matched ? (*it)[1].str() : (*it)[2].str()));
        if (quantities.empty()) continue;
        std::vector<size_t> owners;
        for (size_t i = 0; i < offers.size(); i++) {
            if (names(clause, offers[i].name)) owners.push_back(i);
        }
        if (owners.size() != 1 || quantities.size() != 1) return offers;
        const size_t owner = owners[0];
        std::set<int> prices;
        for (std::sregex_iterator it(clause.begin(), clause.end(), each), end; it != end; ++it) {
            const auto value = to_cents((*it)[1].str());
            if (value && *value != offers[owner].cents) prices.insert(*value);
        }
        int count = 0;
        if (std::isdigit(static_cast<unsigned char>(quantities[0][0]))) count = std::atoi(quantities[0].c_str());
        else count = static_cast<int>(std::find(std::begin(kNumbers), std::end(kNumbers), quantities[0]) - std::begin(kNumbers));
        if (count < 1 || count > 10 || prices.size() != 1 || tiers.count(owner)) return offers;
        tiers[owner] = {count, *prices.begin()};
    }
    if (tiers.empty()) return offers;
    for (size_t i = 0; i < offers.size(); i++) {
        if (const auto tier = tiers.find(i); tier != tiers.end()) {
            offers[i].threshold = tier->second.first;
            offers[i].sale_cents = tier->second.second;
            continue;
        }
        const bool copied = proposed[i].threshold > 0 && std::any_of(tiers.begin(), tiers.end(), [&](const auto& tier) { return tier.second.first == proposed[i].threshold && tier.second.second == proposed[i].sale_cents; });
        if (!copied) continue;
        // A discount copied onto another item is dropped unless the request words a promotion for that item too.
        bool promoted = false;
        for (const std::string& clause : clauses(request)) {
            if (names(clause, offers[i].name) && found(R"(\b(?:discount\w*|bulk|sale|cheaper|when|if|over|above|below|threshold|or more|at least)\b|%)", clause)) promoted = true;
        }
        if (!promoted) {
            offers[i].threshold = 0;
            offers[i].sale_cents = offers[i].cents;
        }
    }
    return offers;
}

std::optional<CatalogSpec> extract(const std::string& request, ChatModel& model, const CancelToken& cancel) {
    ModelReply reply;
    try {
        reply = model.complete({{"system", kExtractPrompt, {}, ""}, {"user", request, {}, ""}}, {}, [](const std::string&) {}, cancel);
    } catch (const Cancelled&) {
        throw;
    } catch (const std::exception&) {
        return std::nullopt;
    }
    const std::optional<json> answer = object_in(reply.text);
    const json* listed = answer ? field(*answer, "items") : nullptr;
    if (!listed || !listed->is_array() || listed->size() < 2 || listed->size() > 8) return std::nullopt;
    const auto cents_of = [](const json* value) -> std::optional<int> {
        if (!value || !value->is_number()) return std::nullopt;
        return static_cast<int>(std::lround(value->get<double>() * 100));
    };
    std::vector<CatalogItem> items;
    for (const json& item : *listed) {
        if (!item.is_object()) return std::nullopt;
        const std::string name = trim(text_of(item, "name"));
        const std::optional<int> price = cents_of(field(item, "price"));
        if (name.empty() || utf8_length(name) > 80 || !price) return std::nullopt;
        const json* threshold = field(item, "threshold");
        items.push_back({name, *price, cents_of(field(item, "saleprice")).value_or(*price), threshold && threshold->is_number_integer() ? threshold->get<int>() : 0});
    }
    CatalogSpec spec;
    spec.items = catalog::bind(request, items);
    spec.receipt_prefix = text_of(*answer, "receiptprefix");
    const auto flag = [&](const char* name) { const json* value = field(*answer, name); return value && value->is_boolean() && value->get<bool>(); };
    spec.persist_receipt = flag("persistreceipt");
    spec.clear_cart = flag("clearcart");
    if (const auto prefix = explicit_receipt_prefix(request)) spec.receipt_prefix = *prefix;
    else if (!spec.receipt_prefix.empty() && request.find(spec.receipt_prefix) == std::string::npos) {
        if (found(R"(\bprefix\w*|\b(?:start|begin)\w* with)", request)) return std::nullopt;
        spec.receipt_prefix.clear();
    }
    const std::set<int> stated = amounts(request);
    std::set<std::string> seen;
    for (const CatalogItem& item : spec.items) {
        if (to_lower(request).find(to_lower(item.name)) == std::string::npos || !stated.count(item.cents) || item.threshold < 0 || item.threshold > 8 || (item.threshold > 0 && !stated.count(item.sale_cents)) ||
            item.cents < 0 || item.cents > 100000000 || item.sale_cents < 0 || item.sale_cents > 100000000 || !seen.insert(to_lower(item.name)).second) {
            return std::nullopt;
        }
    }
    if (const json* other = field(*answer, "otherrequirements"); other && other->is_array()) {
        for (const json& clause : *other) {
            if (clause.is_string() && !trim(clause.get<std::string>()).empty() && !covered(clause.get<std::string>(), spec)) spec.other_requirements.push_back(clause.get<std::string>());
        }
    }
    return spec;
}

bool complete(const CatalogSpec& spec, const std::string& request) {
    return spec.other_requirements.empty() && found(R"(\boffline\b)", request) && found(R"(\bcheckout|check out\b)", request) && spec.clear_cart && spec.persist_receipt;
}

std::vector<AcceptanceScenario> scenarios(const CatalogSpec& spec, const std::string& request) {
    std::vector<AcceptanceScenario> all;
    for (size_t i = 0; i < spec.items.size(); i++) {
        const CatalogItem& item = spec.items[i];
        const std::string card = "#product-" + std::to_string(i + 1);
        all.push_back({"offer-" + std::to_string(i), "Show " + item.name + " at its requested unit price and add it to the cart", "index.html",
                       {{"text", card + " h2", item.name}, {"shelf_price", card, money(item.cents)}, {"click", card + " button", ""}, {"money", "#cart-total", money(item.threshold == 1 ? item.sale_cents : item.cents)}}});
        if (item.threshold > 1) {
            std::vector<AcceptanceStep> steps;
            for (int count = 1; count <= item.threshold; count++) {
                steps.push_back({"click", card + " button", ""});
                if (count >= item.threshold - 1) steps.push_back({"money", "#cart-total", money(count * (count >= item.threshold ? item.sale_cents : item.cents))});
            }
            all.push_back({"discount-" + std::to_string(i), "Apply the specified quantity discount for " + item.name, "index.html", steps});
        }
    }
    std::vector<AcceptanceStep> steps = {{"click", "#product-1 button", ""}, {"click", "#checkout", ""}, {"capture", "#receipt", spec.receipt_prefix}};
    if (spec.clear_cart) {
        steps.push_back({"money", "#cart-total", "0.00"});
        steps.push_back({"empty", "#cart-items", ""});
    }
    const auto reloaded = [&] {
        steps.push_back({"reload", "", ""});
        steps.push_back({"same", "#receipt", ""});
    };
    if (spec.persist_receipt) reloaded();
    if (requires_fresh_receipt(request)) {
        steps.push_back({"click", "#product-1 button", ""});
        steps.push_back({"click", "#checkout", ""});
        steps.push_back({"new_id", "#receipt", spec.receipt_prefix});
        if (spec.persist_receipt) reloaded();
    }
    if (steps.back().op == "capture") steps.push_back({"same", "#receipt", ""});
    all.push_back({"order", std::string("Checkout issues the requested receipt") + (spec.persist_receipt ? " and retains it on reload" : ""), "index.html", steps});
    return all;
}

CatalogDesign design(const std::string& request, const CatalogSpec& spec, ChatModel& model, const CancelToken& cancel) {
    std::vector<std::string> names_listed;
    for (const CatalogItem& item : spec.items) names_listed.push_back(item.name);
    ModelReply reply;
    try {
        reply = model.complete({{"system", kDesignPrompt, {}, ""}, {"user", "Brief:\n" + request + "\n\nProducts, in order: " + join(names_listed, ", "), {}, ""}}, {}, [](const std::string&) {}, cancel);
    } catch (const Cancelled&) {
        throw;
    } catch (const std::exception&) {
        return {};
    }
    const std::optional<json> answer = object_in(reply.text);
    if (!answer) return {};
    // The copy may not state prices: those come only from the checked offers.
    const auto clean = [](const std::string& value) { return found(R"(\$\s*\d|\b\d+(?:\.\d+)?\s*(?:dollars?|%|percent))", value) ? std::string() : value; };
    CatalogDesign made;
    made.title = clean(utf8_prefix(text_of(*answer, "title"), 80));
    made.introduction = clean(utf8_prefix(text_of(*answer, "introduction"), 200));
    made.accent = text_of(*answer, "accent");
    made.background = text_of(*answer, "background");
    if (const json* listed = field(*answer, "descriptions"); listed && listed->is_array()) {
        for (const json& item : *listed) made.descriptions.push_back(item.is_string() ? clean(utf8_prefix(item.get<std::string>(), 150)) : "");
    }
    return made;
}

std::string render(const CatalogSpec& spec, const CatalogDesign& design) {
    static const std::regex hex("#[0-9A-Fa-f]{6}");
    const auto color = [](const std::string& value, const char* fallback) { return std::regex_match(value, hex) ? value : std::string(fallback); };
    std::string offers, data = "[";
    for (size_t i = 0; i < spec.items.size(); i++) {
        const CatalogItem& item = spec.items[i];
        const std::string note = item.threshold > 0 ? "<p>Buy " + std::to_string(item.threshold) + " or more for $" + money(item.sale_cents) + " each.</p>" : "";
        offers += std::string(i ? "\n" : "") + "<article id=\"product-" + std::to_string(i + 1) + "\">\n  <h2>" + html(item.name) + "</h2><p class=\"price\">$" + money(item.cents) + " each</p>\n  <p>" +
                  html(i < design.descriptions.size() ? design.descriptions[i] : "") + "</p>" + note + "\n  <button type=\"button\" data-item=\"" + std::to_string(i) + "\" aria-label=\"Add " + html(item.name) +
                  " to cart\">Add to cart</button>\n</article>";
        data += std::string(i ? ", " : "") + "{name: " + script(item.name) + ", cents: " + std::to_string(item.cents) + ", saleCents: " + std::to_string(item.sale_cents) + ", threshold: " + std::to_string(item.threshold) + "}";
    }
    data += "]";
    std::string page = kPage;
    const std::string title = html(trim(design.title).empty() ? "Food market" : design.title);
    for (const auto& [mark, value] : std::vector<std::pair<std::string, std::string>>{{"@TITLE@", title}, {"@ACCENT@", color(design.accent, "#435238")}, {"@PAPER@", color(design.background, "#f6f1e2")},
                                                                                         {"@INTRO@", html(design.introduction)}, {"@OFFERS@", offers}, {"@DATA@", data}, {"@PREFIX@", script(spec.receipt_prefix)}}) {
        page = replace_all(page, mark, value);
    }
    return page;
}

}  // namespace catalog
}  // namespace mason
