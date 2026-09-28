# Adaptive browser via Playwright MCP (spike)

## Goal

Long-term loop:

```
LLM → observe page (accessibility snapshot) → reason → choose action → browser tool → observe → repeat
```

## Recommended tool

Official **Playwright MCP** (`@playwright/mcp`):

- Accessibility-tree snapshots (no vision model required)
- Tools: `browser_navigate`, `browser_snapshot`, `browser_click`, `browser_type`, `browser_take_screenshot`, …

Run locally:

```bash
npx @playwright/mcp@latest --headless --port 8931
```

## Strands / .NET

- Official **AWS Strands** SDKs are Python / TypeScript.
- There is **no official AWS Strands .NET SDK**.
- Community ports (e.g. Jacquard.NET) exist but should **not** replace this agent's Skills orchestrator.
- Prefer a thin MCP client calling Playwright MCP while keeping `AgentOrchestrator` + Skills.

## This codebase

| Type | Role |
|---|---|
| `DeterministicPlaywrightBrowserSurface` | Current Playwright session hub (default, production path) |
| `PlaywrightMcpBrowserSurface` | Spike stub — throws until MCP client is wired |
| `Agent:Browser:Mode` | `Deterministic` (default) \| `Adaptive` \| `Hybrid` |

**Do not remove** the deterministic `TicketStepPlanner` / `ReproductionSkill` path.

## Next wiring steps

1. Add MCP C# client package and connect to `http://localhost:8931/mcp`.
2. Implement snapshot/click on `PlaywrightMcpBrowserSurface`.
3. Optional `AdaptiveReproductionSkill` when Intent is rich and deterministic nav fails (`Hybrid`).
4. Keep screenshots flowing into `RuntimeContext.Evidence`.
