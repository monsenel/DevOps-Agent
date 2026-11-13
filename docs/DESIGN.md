# Design system — DevOps Agent

The UI is a **bento dashboard**: rounded cards of different sizes on a soft page background, one green accent, generous spacing, light and dark themes. Every page uses the same shell (sidebar + content) and the same tokens.

Reference mockups (approved):
- `docs/design/mockups/overview.dc.html` — Overview page
- `docs/design/mockups/settings-llm.dc.html` — Settings › LLM connection

They are templated HTML: `{{bg}}`, `{{card}}`, `{{accent}}` … are the token names in §1, with values from the light or dark column. Use them for layout, sizes and copy. **Do not copy their inline styles into components**; implement them with the tokens and classes below.

---

## 1. Color tokens

Defined once in `wwwroot/css/tokens.css` as CSS custom properties on `:root[data-theme="light"]` and `:root[data-theme="dark"]`. Components use `var(--dva-*)` only. **No hex values anywhere else.**

| Token | Mockup name | Light | Dark | Use |
|---|---|---|---|---|
| `--dva-bg` | bg | `#eceef1` | `#0c0f11` | Page background |
| `--dva-card` | card | `#ffffff` | `#161a1e` | Card surface |
| `--dva-inset` | inset | `#f3f5f7` | `#1e2429` | Wells inside cards: inputs, code, list rows, icon chips, host groups in graph |
| `--dva-ink` | ink | `#14181b` | `#eef1f3` | Primary text |
| `--dva-muted` | muted | `#56616b` | `#9da8b1` | Secondary text, inactive nav |
| `--dva-line` | line | `#d5dae0` | `#2a3239` | Input borders, neutral node borders |
| `--dva-edge` | edge | `#7a858f` | `#7c8892` | Graph edges, arrowheads, off-switch track |
| `--dva-accent` | accent | `#15803d` | `#4ade80` | Primary buttons, active nav, emphasised tile, traced path, on-switches, focus ring |
| `--dva-on-accent` | onAccent | `#ffffff` | `#052e14` | Text/icons on accent |
| `--dva-soft` | soft | `#dcfce7` | `#12331f` | Success / selected background |
| `--dva-on-soft` | onSoft | `#14532d` | `#86efac` | Text on soft; "Added" in drift |
| `--dva-warn` | warn | `#b45309` | `#fbbf24` | Warning strokes, high-load bars, observed-only edges |
| `--dva-warn-soft` | warnSoft | `#fef3c7` | `#3b2d0b` | High-severity alert rows, Unknown node fill, badge bg |
| `--dva-on-warn` | onWarn | `#7c2d12` | `#fcd34d` | Text on warn-soft; "Changed" in drift |
| `--dva-danger` | — | `#b91c1c` | `#f87171` | Critical severity, destructive text (Remove) |
| `--dva-danger-soft` | — | `#fee2e2` | `#3b1212` | Critical alert background |
| `--dva-on-danger` | — | `#7f1d1d` | `#fca5a5` | Text on danger-soft |
| `--dva-inv` | inv | `#14181b` | `#1e2429` | Inverted promo card in sidebar (Model card) |
| `--dva-on-inv` | onInv | `#f3f5f7` | `#eef1f3` | Text on inverted card |
| `--dva-on-inv-muted` | — | `#aab4bd` | `#aab4bd` | Secondary text on inverted card |

Rules:
- One emphasised (accent-filled) tile per row at most (e.g. "Open alerts" tile on Overview, "Last test passed" on Settings).
- Status is never color-only: always a word ("High", "ok", "Added") next to it.
- Severity mapping: high → warn-soft/on-warn; critical → danger-soft/on-danger; medium → inset + warn text; low → inset + muted label.

## 2. Theme

- `data-theme="light|dark"` on `<html>`.
- Default: `prefers-color-scheme`. User choice persisted in `localStorage` key `dva-theme` (wrap access in try/catch).
- Set the attribute in an inline script in `<head>` before CSS loads, to avoid a flash.
- Toggle button in the top bar: "Theme: Light" / "Theme: Dark".
- Cytoscape cannot read CSS variables: `graph.js` reads computed token values with `getComputedStyle(document.documentElement)` and re-applies the stylesheet when the theme changes (listen for a `dva-theme-changed` event).

## 3. Typography

Self-hosted `woff2` in `wwwroot/fonts`: **Plus Jakarta Sans** 400/500/600/700, **JetBrains Mono** 400/500. No Google Fonts CDN at runtime.

| Role | Font | Size / line-height | Weight |
|---|---|---|---|
| Page title (h1) | Jakarta | 24px | 700 |
| Hero card title (h2) | Jakarta | 20px | 700 |
| Card title (h2) | Jakarta | 16px | 700 |
| Stat value | Jakarta | 28px | 700 |
| Body | Jakarta | 13px / 1.45 | 400; 500 for nav, 600 for buttons/labels |
| Small | Jakarta | 12px | 400–600 |
| Micro label | Jakarta | 11px, uppercase, letter-spacing .06em | 600 |
| Technical values (IP, port, path, model id, command) | JetBrains Mono | 12px (11px in graph labels) | 400–500 |

Any IP address, port, hostname-in-graph, file path, model id, command or config snippet is set in mono.

## 4. Spacing, radius, sizes

| Token | Value | Use |
|---|---|---|
| `--dva-page-pad` | 20px | Page padding |
| `--dva-gap` | 20px | Between cards, between rows |
| `--dva-card-pad` | 22px (hero: 24px) | Card padding |
| `--dva-radius-card` | 24px | Cards |
| `--dva-radius-tile` | 18px | Tiles inside cards (provider items, inverted card, graph host groups) |
| `--dva-radius-row` | 16px | Alert rows, list rows |
| `--dva-radius-control` | 14px | Buttons, search, code blocks, switch rows |
| `--dva-radius-input` | 12px | Inputs, selects, icon chips, segmented items |
| `--dva-radius-pill` | 10px | Badges, pills |
| `--dva-control-h` | 44px | Buttons, inputs, nav items (minimum touch target) |

Inside cards: 12px gap between blocks; 16px between form fields; 10–14px inside tiles.

## 5. Layout

### App shell
- Page: `display:flex; flex-wrap:wrap; gap:20px; padding:20px; background:var(--dva-bg)`.
- **Sidebar card** (`flex:1 1 220px; max-width:248px`): logo row (32px accent square with graph icon + "DevOps Agent" 16/700), nav list, "Groups" section with counts, spacer, **Model card** (inverted) at the bottom: micro label "Model", provider name, status line, button "LLM settings" → Settings › LLM.
- **Main** (`flex:999 1 560px; min-width:0`): column with 20px gap.
- **Top bar** (no card, inline in main): h1 page title, spacer, search field (inset-free: card background, 280px), theme toggle, primary action of the page (Overview: "Run discovery").
- Under ~800px the sidebar wraps above the content; card rows wrap. Never `position:sticky` or `100vh` on the sidebar.

Nav items (order): Overview, Topology, Servers, Alerts (badge = open count, warn-soft), Updates, Agent chat, Action log, Settings. Active item: accent background, on-accent text, 600. Inactive: transparent, muted, 500.

### Bento rows
Rows are flex-wrap containers with 20px gap; cards size by `flex: <grow> 1 <basis>` so they reflow:
- Hero + stat tiles: hero `5 1 320px`, each stat `2 1 150px`.
- Main + side: main `8 1 520px`, side column `4 1 300px`.
- Three equal: each `1 1 280px`.

## 6. Components

| Component | Spec |
|---|---|
| **Card** | `--dva-card` bg, 24px radius, 22px padding, no border, no shadow. Title row: h2 16/700 + optional right-aligned controls. |
| **Hero card** | 24px padding, title 20/700, one muted sentence, spacer, primary button at bottom-left. |
| **Stat tile** | Card with 20px padding: 36px icon chip (inset bg, 12px radius, 16px stroke icon in ink), muted label, value row (28/700 + muted suffix or a badge). Emphasised variant: accent bg, on-accent text, icon chip on-accent bg with accent icon. |
| **Button** | 44px high, 14px radius, 0 18px padding, 600. Primary: accent / on-accent. Secondary: inset / ink. Ghost-destructive: transparent / danger. Disabled: 50% opacity, `cursor:not-allowed`. |
| **Badge / pill** | 2–4px × 9–12px padding, 10px radius, 11–12px 700. Variants: soft, warn, danger, neutral (inset). |
| **Field** | Label 600 above, input 44px, 12px radius, 1px `--dva-line` border, inset bg, 0 12px padding, optional helper text 12px muted below. Mono font for URLs, numbers, model ids. |
| **Select** | Same as field. |
| **Switch row** | Inset tile, 14px radius, 12×14 padding: title 600 + muted helper on the left, switch on the right. Switch: 44×26 track, 13px radius; on = accent track with on-accent knob right; off = inset track, 1px edge border, edge-colored knob left. `role="switch"` + `aria-checked`. |
| **Segmented / tabs** | Container card-bg, 16px radius, 4px padding; items 36px high, 12px radius; active = accent/on-accent 600, inactive = muted 500. Used for Settings sections and graph view switch (graph switch uses inset for active instead of accent). |
| **Alert row** | 16px radius, 12×14 padding, severity word bold + title. High = warn-soft/on-warn; low = inset with muted severity word. |
| **Provider item** | Button-tile, 18px radius, 14×16 padding: name 700, base URL mono 12px muted, status line. Selected = soft bg + 2px accent border + "Default" label right. Unreachable status text in on-warn. |
| **Code block** | `<pre>` inset bg, 14px radius, 12×14 padding, mono 12px, horizontal scroll. |
| **Proposed action card** | Text paragraph (agent's reason), code block of exact tool calls/commands, header "Proposed action · <servers>" + "needs approval", two equal buttons: "Approve and run" (primary) and "Reject" (secondary). After decision: replaced by status line + output collapsible. |
| **Progress bar (load)** | Grid `84px 1fr 40px`: server name mono, 8px track (inset, 4px radius) with fill (accent; warn when ≥ 85%), percent mono right-aligned (on-warn when ≥ 85%). |
| **Drift list** | Rows: 64px kind label (Added = on-soft, Changed = on-warn, Removed = muted) + description. |
| **Empty state** | Centered in card: 16/700 title, one muted sentence, one primary button. |
| **Icons** | Inline stroke SVG, 1.5–1.75 stroke, `currentColor`, 14–18px. No icon fonts, no emoji. Decorative icons `aria-hidden="true"`; icon-only buttons have `aria-label`. |

Focus: `outline: 2px solid var(--dva-accent); outline-offset: 2px` on every interactive element (`:focus-visible`).

## 7. Graph styling (Cytoscape)

| Element | Style |
|---|---|
| Host (compound parent) | Fill inset, no border, radius 18, label top-left "name · ip" mono 11 muted, padding 16 |
| Service / container node | Fill card, 1px line border, radius 12, label name ink 13/500 + second line muted 11 ("container · :5000") |
| Domain node | Pill (radius 22), fill inset, mono 11 ink |
| Unknown node | Fill warn-soft, 1.5px dashed warn border, label "Unknown" on-warn 13/500 + "ip · :port" |
| Edge (declared + observed) | edge color, 1.5px, triangle arrow, label `:port` mono 11 muted beside the line |
| Edge (observed only) | warn color, 1.5px, dashed 5/4 |
| Edge (declared only) | edge color, 1.5px, dashed 2/4, opacity .5 |
| Traced path / selection | Nodes 2px accent border; edges accent 2.5px, labels ink |
| Status overlay | Down node: 2px danger border + small "down" badge; degraded: warn border |
| Layout | Service view: ELK layered, left → right (domain → proxy → app → data). Network view: subnets as compound zones, hosts placed inside each subnet they have an interface on. |

Legend row above the graph: "declared + observed", "observed only", "traced path" with line samples.

## 8. Page specs

### Overview (`/`) — see `overview.dc.html`
1. Top bar: "Overview", search, theme toggle, **Run discovery** (primary).
2. Row: **Hero** "{n} servers, {m} need attention" + "Last discovery ran {time} and found {k} changes since yesterday." + **Ask the agent** · **Servers online** tile (`11` of 11) · **Pending updates** tile (`14` + badge "5 security") · **Open alerts** tile emphasised (`2` + "1 high").
3. Row: **Topology** card (title, segmented Service/Network, trace pill with mono domain, compact graph) · side column: **Alerts** card (rows) + **Agent** card (last suggestion + proposed action + Approve/Reject).
4. Row (three equal): **Drift since yesterday** · **Server load** · **Selected edge** (edge title with mono port in on-soft, kind + connections, mono file:line, code block).

### Settings › LLM connection (`/settings/llm`) — see `settings-llm.dc.html`
1. Top bar: "Settings" + segmented: LLM connection (active), Servers and SSH, Execution modes, Notifications, Discovery.
2. Row: **Providers** card (`4 1 300px`): provider items, spacer, "Add provider" (secondary, full width) · **Connection** card (`8 1 520px`): title + soft badge with provider name; 2-column form grid: Provider type (select), Display name; Base URL (full width, mono, helper "Any endpoint that speaks the OpenAI chat completions API."); API key (full width, password, placeholder depends on type); Model (select + Refresh), Context window; Temperature, Request timeout. Switch rows: Native tool calling, Streaming, Use as default. Buttons: Save (primary), Test connection (secondary), spacer, Remove provider (ghost-destructive).
3. Row (three equal): **Last test** card — emphasised (accent) when passed, warn-soft when failed; grid of check → result · **Task routing** card (v2: rendered with a "v2" badge and disabled) · **Cloud providers** card: switch rows (Redact secrets/IPs — functional; Keep raw logs local — v2 disabled; Fallback — v2 disabled).

### Other pages (same system, no mockup yet)
| Page | Layout |
|---|---|
| Login | Centered card 400px wide on bg: logo row, "Sign in", username, password, primary button full width. |
| Topology | Toolbar card (segmented view switch, trace field, search, filters, legend) full width; row: graph card `8 1 520px` (min-height 640px) + detail panel card `4 1 300px` (node or edge details, evidence code block, actions). |
| Servers | Card with table (name, host mono, group, tags pills, sudo, status, last seen); "Add server" + "Import ssh config" buttons in title row. Server detail: hero + stat tiles (CPU, memory, disk, uptime) + listeners card + containers card + recent alerts card. |
| Alerts | Segmented filter (Open, Acknowledged, Resolved); list of alert rows; right card with selected alert details + "Ask the agent" + Acknowledge/Resolve. |
| Updates | Card per server group: rows with server, pending count, security badge, reboot-required badge, "Review" button; review shows package list code block + proposed action card. |
| Drift | Snapshot pickers (from/to), drift list card, mini graph card with added (soft) / removed (danger, dashed) / changed (warn) highlights. |
| Agent chat | Conversation list card `1 1 260px` + chat card `999 1 560px`: messages (user bubble inset right-aligned, agent plain text), tool-run lines in muted 12px ("Ran 3 read-only tools on 11 servers"), proposed action cards inline, input 44px with send icon button. |
| Action log | Card with table: time, tool, servers, status badge, duration; row expands to commands + output code blocks. |

## 9. Accessibility

- Text contrast WCAG AA in both themes (the token pairs above are chosen for this; re-check any new pair).
- Real elements: `<button>`, `<a href>`, `<input>` with `<label for>`; no click handlers on `div`.
- Visible focus everywhere; keyboard reachable graph actions (search/trace field + detail panel list as alternative to clicking nodes).
- The graph has an `aria-label` summary and the detail panel is the accessible view of the selection.
