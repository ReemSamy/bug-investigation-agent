from pathlib import Path
from fpdf import FPDF

out = Path(r"D:\bug-investigation-agent\docs\Manager-Meeting-Briefing.pdf")
out.parent.mkdir(parents=True, exist_ok=True)


class PDF(FPDF):
    def footer(self):
        self.set_y(-12)
        self.set_font("Helvetica", "I", 8)
        self.set_text_color(120, 120, 120)
        self.cell(0, 8, f"Bug Investigation Agent  |  Page {self.page_no()}/{{nb}}", align="C")


pdf = PDF(orientation="P", unit="mm", format="A4")
pdf.alias_nb_pages()
pdf.set_auto_page_break(auto=True, margin=18)
pdf.add_page()
pdf.set_left_margin(14)
pdf.set_right_margin(14)
# Keep x at left margin after set_margins
pdf.set_x(14)


def h1(t: str) -> None:
    pdf.set_x(14)
    pdf.set_font("Helvetica", "B", 16)
    pdf.set_text_color(20, 40, 80)
    pdf.multi_cell(0, 8, t)
    pdf.ln(1)


def h2(t: str) -> None:
    pdf.ln(3)
    pdf.set_x(14)
    pdf.set_font("Helvetica", "B", 12)
    pdf.set_text_color(25, 70, 120)
    pdf.multi_cell(0, 7, t)
    pdf.ln(1)


def h3(t: str) -> None:
    pdf.ln(1)
    pdf.set_x(14)
    pdf.set_font("Helvetica", "B", 10)
    pdf.set_text_color(40, 40, 40)
    pdf.multi_cell(0, 6, t)


def body(t: str) -> None:
    pdf.set_x(14)
    pdf.set_font("Helvetica", "", 10)
    pdf.set_text_color(30, 30, 30)
    pdf.multi_cell(0, 5.5, t)
    pdf.ln(0.5)


def bullet(t: str) -> None:
    pdf.set_x(14)
    pdf.set_font("Helvetica", "", 10)
    pdf.set_text_color(30, 30, 30)
    pdf.multi_cell(0, 5.5, f"-  {t}")


def quote(t: str) -> None:
    pdf.set_x(14)
    pdf.set_font("Helvetica", "I", 10)
    pdf.set_text_color(50, 50, 50)
    pdf.multi_cell(0, 5.5, f'Say: "{t}"')
    pdf.ln(1)


def kv(k: str, v: str) -> None:
    pdf.set_x(14)
    pdf.set_font("Helvetica", "B", 10)
    pdf.set_text_color(30, 30, 30)
    pdf.multi_cell(0, 5.5, f"{k}")
    pdf.set_x(14)
    pdf.set_font("Helvetica", "", 10)
    pdf.multi_cell(0, 5.5, v)
    pdf.ln(0.8)


h1("Bug Investigation Agent")
h1("Manager Meeting Briefing")
body("Quick reference for interview / deep-dive. Read on mobile while discussing.")
body("Stack: .NET 8 Blazor Server | Custom orchestrator | Playwright | Optional LLM")

h2("OPENING (30 seconds)")
quote(
    "This is a custom .NET investigation agent - not LangChain or Semantic Kernel. "
    "It runs a fixed pipeline of skills: Jira -> environment -> browser login -> reproduce -> "
    "(optional LLM investigate) -> human approval -> apply -> validate -> retest. "
    "V1 does NOT auto-commit or open PRs. Human stays in control."
)

h2("1. AGENT FRAMEWORK")
kv("Using a framework?", "No. No LangChain / Semantic Kernel / AutoGen.")
kv(
    "Why not?",
    "We needed a controlled engineering workflow with permissions, credentials, and a human gate - "
    "not free-form tool loops. Custom keeps V1 predictable and reviewable.",
)
kv("Orchestration custom?", "Yes. AgentOrchestrator runs skills in a hardcoded order.")
kv(
    "Framework later?",
    "Maybe - for adaptive planning / multi-tool reasoning. Not required for the current pipeline.",
)
quote("Orchestration is intentional and sequential. The LLM is a specialist, not the boss.")

h2("2. SKILLS")
h3("What is a skill?")
body(
    "A permission-gated capability (ISkill) that does one job: Jira fetch, browser login, reproduce, "
    "investigate code, apply diff, validate, report evidence."
)
h3("How does it know which skill?")
body("The orchestrator picks them by id in order - NOT an LLM router.")
h3("Active skills (pipeline order)")
for s in [
    "Jira",
    "Environment discovery",
    "Browser login",
    "Reproduction",
    "Code investigation (LLM)",
    "Code modification (after approval)",
    "Validation",
    "Evidence reporting",
]:
    bullet(s)
h3("Disabled stubs in V1")
bullet("Git, GitHub, Pull Request, CI/CD, Deployment")
h3("How much KMS knowledge is in skills?")
body(
    "Skills are mostly GENERIC. KMS-shaped knowledge lives mainly in navigation/repro heuristics "
    "(TicketStepPlanner / ReproductionSkill): Modules vs Administration, Auditor Proposal, "
    "Investigator Checklists. Skills are NOT meant to hold all of KMS."
)
h3("How new behavior is added")
bullet("Better ticket steps / rules in the planner")
bullet("Human-validated memory lessons (SQLite)")
bullet("Workspace rules (.cursor/rules, AGENTS.md) injected into LLM")
bullet("New skill classes registered in DI")
quote("Skills = tools. Product knowledge = layered heuristics + memory + repo rules - not one giant skill.")

h2("3. JIRA")
kv("Where ticket enters", "User pastes ticket key/URL in the UI, then Analyze.")
kv("Authentication", "Atlassian email + API token -> HTTP Basic (email:token).")
kv("How ticket is retrieved", "GET /rest/api/3/issue/{key} (+ myself check, comments).")
kv("Summary / description", "Used as context for discovery, defect description, and LLM prompt.")
kv("Reproduction steps", "Parsed from description; drive the browser plan when present.")
kv("Environment / URLs", "Extracted for portal login target.")
kv("Attachments / screenshots", "Metadata only today - not downloaded into the browser run.")
body("Settings live in Account / local settings: BaseUrl, email, API token.")

h2("4. NAVIGATION")
kv("How it knows where to go", "From ticket text first (Steps to Reproduce / Admin -> X -> Y).")
kv("Entry point", "Rules choose Administration vs Modules vs direct label - NOT always Modules.")
kv("Ticket -> plan", "TicketStepPlanner expands steps / arrow paths into clickable steps.")
kv("Where implemented", "TicketStepPlanner.cs + ReproductionSkill.TryFollowStepAsync")
kv("LLM or rules?", "Deterministic RULES for navigation. LLM is NOT used for clicking.")
kv(
    "New / unfamiliar area?",
    "Relies on Jira steps + generic clicks; may end Ambiguous. Known V1 limit - "
    "adaptive/LLM planning is future work.",
)
quote("Navigation is rules-first on purpose so we can explain every click. LLM comes later for unfamiliar UIs.")

h2("5. BROWSER AUTOMATION")
bullet("Uses Microsoft Playwright (Chromium), headless.")
bullet("Navigation plan -> actions: click by role/label/text, open grids, Save/Promote, tabs, etc.")
bullet("Login: opens env URL, fills portal username/password (#UserName / #Password).")
bullet("Session: PlaywrightSessionHub keeps one browser/page per investigation so login survives across skills.")
bullet("Did click work?: page fingerprint (URL/title/heading); mark failed if UI did not change.")
bullet("Screenshots: after meaningful steps under App_Data/evidence/...")
bullet(
    "Reproduced?: ticket-type signals (JS toast, confirmation text, empty field-wrapper for Text Response) "
    "- NOT LLM vision on images."
)
quote("Browser is Playwright with a shared session. Reproduction is evidence + heuristics, not AI guessed it.")

h2("6. AI / LLM")
kv("Where used?", "Almost only CodeInvestigationSkill (root cause + proposed fix).")
kv(
    "Providers?",
    "OpenAI-compatible and Anthropic implemented; Azure/Cursor stubs; default can be null (offline).",
)
kv("Why provider-agnostic?", "IAgentRuntime interface - swap provider without rewriting skills/orchestrator.")
kv(
    "What is sent?",
    "Ticket summary/description/steps/notes + keyword-matched code snippets + safety/workspace rules + "
    "memory lessons. Secrets are redacted.",
)
kv("Root cause / files / fix?", "LLM returns structured JSON: rootCause, proposedFiles, proposedFixSummary, proposedDiff.")
kv("No API key?", "Can still reproduce + collect evidence; completes without a real code fix.")
quote("LLM investigates code AFTER we reproduce. It does not drive Jira or browser navigation in V1.")

h2("7. MEMORY")
kv("What is it?", "Human-validated engineering lessons (IEngineeringMemoryStore).")
kv("Where stored?", "SQLite via Entity Framework.")
kv("How lessons added?", "After successful human-validated outcomes (Memory UI).")
kv("How retrieved?", "Filter by workspace + simple tag/text match, then inject into LLM prompt.")
kv("Is this RAG?", "No - no embeddings / vector search yet.")
kv(
    "Smarter over time?",
    "Add embeddings/RAG, link lessons to module/ticket patterns, feed failed navigation into lessons.",
)

h2("8. CODE CHANGES")
kv("Can it fix a bug?", "It can PROPOSE a fix; apply only after human approval.")
kv("Modify source automatically?", "Only if human Approves -> CodeModificationSkill applies a local diff.")
kv("Commit / push / PR?", "No in V1 (skills stubbed; permissions off).")
kv(
    "How prevent uncontrolled changes?",
    "Permissions gate, approval waiter, allowlisted validation commands, secret redaction, no git push.",
)
body("Final success intent: local changes validated -> ready for human review.")

h2("9. COMPLEX BUGS (be honest)")
h3("Today")
bullet("Strong at: structured tickets, clear steps, UI defects with clear signals.")
bullet("Weak at: deep multi-hop bugs, wrong menu guesses, incomplete steps.")
h3("Capability checklist")
kv("Correlate browser <-> code", "Partially (repro notes + LLM on workspace).")
kv("Inspect logs", "No (not in V1).")
kv("Trace UI -> API -> backend -> DB", "No (not in V1).")
kv("Identify root cause", "Via LLM + local code search after successful reproduce.")
kv("Propose + validate fix", "Yes path: propose -> approve -> apply -> dotnet build/test -> browser retest.")
quote("V1 is reproduce + evidence + LLM code proposal. Full request tracing is roadmap, not current.")

h2("10. FUTURE DIRECTION")
bullet("Adaptive navigation: LLM/planner chooses clicks when rules fail; verify with DOM fingerprints.")
bullet("Learn from investigations: failed steps + successful paths -> memory (later RAG).")
bullet("Multi-project: already workspace-scoped; keep product knowledge in memory/rules, not hardcoded forever.")
bullet(
    "More than ask LLM about Jira: we already open the env, click the app, capture evidence, "
    "gate code changes, and retest."
)
bullet("Valuable engineering agent = reliable repro + evidence + safe patch + (later) PR + CI + log/API correlation.")

h2("IF HE DIGS THE CODE")
kv("Pipeline", "AgentOrchestrator.cs")
kv("Skill contract", "ISkill / SkillIds")
kv("Nav rules", "TicketStepPlanner.cs")
kv("Browser actions", "ReproductionSkill.cs, BrowserAutomationSkill.cs")
kv("Session hub", "PlaywrightSessionHub")
kv("LLM", "CodeInvestigationSkill.cs, IAgentRuntime")
kv("Memory", "IEngineeringMemoryStore / EF store")
kv("Disabled git/PR", "DisabledSkills.cs")

h2("TOUGH QUESTIONS - SHORT ANSWERS")
h3("Is it smart?")
quote(
    "Smart enough to run an investigation pipeline; not a replacement for an engineer. "
    "Value is speed, consistency, and a safe human gate."
)
h3("Why not just ChatGPT + Jira?")
quote(
    "Because we actually open the env, click the app, capture evidence, gate code changes, "
    "and retest - not only summarize a ticket."
)
h3("Why so much custom code?")
quote("Control: permissions, credentials, no surprise commits, explainable steps for demos and audits.")
h3("Biggest gap?")
quote(
    "Navigation is still mostly rule-based; complex/unfamiliar UI paths and deep backend tracing "
    "need the next iteration."
)

h2("CLOSING LINE")
quote(
    "V1 proves the loop: ticket -> login -> reproduce -> evidence -> investigate -> approve -> validate -> retest, "
    "without giving the agent write access to git. Next we make navigation and learning adaptive so it handles "
    "more tickets with less hand-holding."
)

pdf.ln(3)
pdf.set_x(14)
pdf.set_font("Helvetica", "I", 9)
pdf.set_text_color(100, 100, 100)
pdf.multi_cell(
    0,
    5,
    "Tip: On mobile, open in landscape or zoom. Answer from architecture, stay honest on limits, "
    "point to the pipeline as the differentiator.",
)

pdf.output(str(out))
print(out)
print("size_bytes", out.stat().st_size)
