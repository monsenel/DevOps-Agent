let graph;
let dotNet;
let themeListener;
// "servers", "service" or "network": the Servers view builds its own cards and layout, the other two use cytoscape-elk.
let viewMode = "service";
// Lookup tables of the Servers view (real id -> card/row/aggregated edge); undefined in the other views.
let serversModel;
// Every setData call takes a number; an older call that finishes late (it awaits fonts and ELK) must not draw over a newer one.
let dataGeneration = 0;

function tokens() {
    const style = getComputedStyle(document.documentElement);
    const read = name => style.getPropertyValue(`--dva-${name}`).trim();
    return {
        bg: read("bg"), card: read("card"), inset: read("inset"), ink: read("ink"), muted: read("muted"),
        line: read("line"), edge: read("edge"), accent: read("accent"), warn: read("warn"),
        warnSoft: read("warn-soft"), onWarn: read("on-warn"), danger: read("danger"),
        roleProxy: read("role-proxy"), roleApp: read("role-app"), roleData: read("role-data"),
        roleCi: read("role-ci"), roleClients: read("role-clients"), graphPort: read("graph-port"), ssh: read("ssh"),
        font: '"Plus Jakarta Sans", system-ui, sans-serif', mono: '"JetBrains Mono", monospace'
    };
}

// ---- Servers view: roles, icons, sizes ----

// Layout constants of a server card, in graph pixels (the card is a compound node; its size comes from its children).
const CARD = { pad: 14, gap: 8, rowH: 34, rowMinW: 180, rowPadX: 32, headerH: 30, icon: 20, iconGap: 8, captionH: 26, unknownPadX: 28,
    // The SSH warning badge at the right end of the header row of a host card (T8.18).
    badge: 20, badgeGap: 10 };
const CLIENTS_ID = "servers-view:clients";
const UNKNOWN_HOST_PREFIX = "servers-view:unknown:";
const headerId = boxId => `servers-view:header:${boxId}`;
const badgeId = boxId => `servers-view:ssh-badge:${boxId}`;

// Names are matched at their start, after a leading path, and end at a separator or the end: "redis-server", "postgresql@15-main.service"
// and "nginx" count, "worker-redis" and "docker-proxy" do not.
const ROLE_PATTERNS = {
    proxy: /^(?:nginx|haproxy|traefik|caddy|envoy)(?:$|[.@:_-])/,
    data: /^(?:postgres(?:ql)?|mysqld?|mariadbd?|redis|mongo(?:db|d)?|elasticsearch|opensearch|clickhouse|rabbitmq|kafka|minio|etcd)(?:$|[.@:_-])/,
    ci: /^(?:gitea|gitlab|jenkins|drone|woodpecker|runner|registry|nexus|harbor|argocd)(?:$|[.@:_-])/
};
// Ports of databases and brokers: a server that receives connections on one of them is a data server.
const DATA_PORTS = new Set(["5432", "3306", "6379", "27017", "9200", "9300", "5672", "9092", "8123", "2379"]);
// nginx detail nodes (T2.x): never drawn as rows.
const DETAIL_LABEL = /^(?:server block|upstream) ·/;

const ROLES = {
    proxy: { title: "Reverse proxy", icon: "layers", token: "roleProxy" },
    app: { title: "Application server", icon: "server", token: "roleApp" },
    data: { title: "Data server", icon: "database", token: "roleData" },
    ci: { title: "Git / CI server", icon: "code", token: "roleCi" },
    clients: { title: "Internet / Clients", icon: "users", token: "roleClients" },
    // Not a detected role: the card of an address that is not in the inventory.
    unknownHost: { title: "Unknown host", icon: "server", token: "warn" }
};

// Stroke icons on a 24 x 24 grid, drawn in the role colour.
const ICONS = {
    layers: '<path d="M12 2 3 7l9 5 9-5-9-5Z"/><path d="m3 12 9 5 9-5"/><path d="m3 17 9 5 9-5"/>',
    server: '<rect x="3" y="3" width="18" height="7" rx="2"/><rect x="3" y="14" width="18" height="7" rx="2"/><path d="M7 6.5h.01M7 17.5h.01"/>',
    database: '<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>',
    code: '<path d="m8 7-5 5 5 5"/><path d="m16 7 5 5-5 5"/><path d="m14 4-4 16"/>',
    users: '<path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
    // Warning triangle: the SSH badge of a host card.
    alert: '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z"/><path d="M12 9v4"/><path d="M12 17h.01"/>'
};

// Cytoscape cannot use currentColor or CSS variables inside an image, so the icon is built with the colour value of the token.
function iconUri(name, color) {
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="${color}" `
        + `stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">${ICONS[name]}</svg>`;
    return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
}

// The role of a server. `rowNames` has, per service/container row, the lowercase names of the row and of everything nested in it
// (label, unit, process, compose service; never the image, an app may ship nginx inside its image). `evidence` comes from the
// edges: `fromClients` (the Internet / Clients card connects to the server: it is a proxy whatever its rows are called) and
// `dataPorts` (it receives connections on a database port). Proxy evidence, then data, then CI, otherwise an application server.
// With both proxy and data evidence the clients edge wins, otherwise the side with more rows (a tie goes to the proxy).
export function detectRole(rowNames, evidence = {}) {
    const count = pattern => rowNames.filter(names => names.some(name => pattern.test(name))).length;
    const proxyRows = count(ROLE_PATTERNS.proxy);
    const dataScore = Math.max(count(ROLE_PATTERNS.data), evidence.dataPorts ? 1 : 0);
    if (evidence.fromClients) return "proxy";
    if (proxyRows > 0 && dataScore > 0) return dataScore > proxyRows ? "data" : "proxy";
    if (proxyRows > 0) return "proxy";
    if (dataScore > 0) return "data";
    return count(ROLE_PATTERNS.ci) > 0 ? "ci" : "app";
}

const measureContext = document.createElement("canvas").getContext("2d");

function textWidth(text, font) {
    measureContext.font = font;
    return Math.ceil(measureContext.measureText(text).width);
}

// Canvas measures with a font only after it is loaded; without this the first view would be sized with the fallback font.
async function loadFonts(c) {
    try {
        await Promise.all([`500 13px ${c.font}`, `700 15px ${c.font}`, `400 12px ${c.mono}`, `500 12px ${c.mono}`]
            .map(font => document.fonts.load(font)));
    } catch {
        // Measuring with the fallback font only makes the cards a little too wide or narrow.
    }
}

function applyStyles() {
    if (!graph) return;
    const c = tokens();
    graph.style([
        { selector: "node", style: {
            "background-color": c.card, "border-color": c.line, "border-width": 1,
            "shape": "round-rectangle", "label": "data(displayLabel)", "color": c.ink,
            "font-family": c.font, "font-size": 13, "font-weight": 500,
            "text-valign": "center", "text-halign": "center", "padding": 10,
            "text-wrap": "wrap", "text-max-width": 170, "width": "label", "height": "label"
        } },
        { selector: 'node[kind = "Host"]', style: {
            "background-color": c.inset, "border-width": 0, "shape": "round-rectangle",
            "text-valign": "top", "text-halign": "left", "text-margin-x": 10, "text-margin-y": 8,
            "font-family": c.mono, "font-size": 11, "color": c.muted, "padding": 22,
            "compound-sizing-wrt-labels": "include"
        } },
        { selector: 'node[kind = "Subnet"]', style: {
            "background-color": c.inset, "border-color": c.line, "border-style": "dashed",
            "shape": "round-rectangle", "font-family": c.mono, "font-size": 11, "color": c.muted
        } },
        { selector: 'node[kind = "Domain"]', style: {
            "shape": "round-rectangle", "background-color": c.inset, "border-width": 0,
            "font-family": c.mono, "font-size": 11
        } },
        { selector: 'node[kind = "Unknown"]', style: {
            "background-color": c.warnSoft, "border-color": c.warn, "border-width": 1.5,
            "border-style": "dashed", "color": c.onWarn
        } },
        { selector: 'node[kind = "Container"], node[kind = "Service"]', style: {
            "shape": "round-rectangle", "border-width": 1, "border-color": c.line,
            "padding": 12, "text-halign": "center", "text-valign": "center"
        } },
        { selector: 'node[stale = "stale"]', style: { "opacity": 0.55, "border-style": "dashed" } },
        { selector: 'node[alertSeverity = "Critical"]', style: { "border-width": 3, "border-color": c.danger } },
        { selector: 'node[alertSeverity = "High"]', style: { "border-width": 2, "border-color": c.danger } },
        { selector: 'node[alertSeverity = "Medium"]', style: { "border-width": 2, "border-color": c.warn } },
        { selector: "edge", style: {
            "width": 1.5, "line-color": c.edge, "target-arrow-color": c.edge,
            "target-arrow-shape": "triangle", "curve-style": "bezier", "label": "data(edgeLabel)",
            "font-family": c.mono, "font-size": 10, "color": c.muted, "text-background-opacity": 0,
            "text-rotation": "autorotate"
        } },
        { selector: "edge[kind = 'Declared']", style: { "line-style": "dashed", "line-dash-pattern": [2, 4], "opacity": 0.5 } },
        { selector: "edge[kind = 'Observed']", style: { "line-style": "dashed", "line-dash-pattern": [5, 4], "line-color": c.warn, "target-arrow-color": c.warn } },
        { selector: "edge[kind = 'Both']", style: { "line-style": "solid", "opacity": 1 } },
        // The Servers rules come after the shared ones so they win on every property they set; selection, trace and dimming
        // come last so they win over the Servers rules.
        ...(viewMode === "servers" ? serversStyles(c) : []),
        { selector: "edge:selected", style: { "width": 2.5, "line-color": c.accent, "target-arrow-color": c.accent, "source-arrow-color": c.accent, "color": c.ink } },
        { selector: "node:selected", style: { "border-width": 2, "border-color": c.accent } },
        { selector: "node.traced", style: { "border-width": 2, "border-color": c.accent } },
        { selector: "edge.traced", style: { "width": 2.5, "line-color": c.accent, "target-arrow-color": c.accent, "source-arrow-color": c.accent, "color": c.ink } },
        { selector: ".trace-dim", style: { "opacity": 0.16 } },
        { selector: ".search-dim", style: { "opacity": 0.18 } }
    ]);
    applySegmentStyles();
}

// Edges that ELK routed around a card get their bend points as segments. They are set per edge (each has its own values) and set
// again after every applyStyles, in case a new stylesheet drops them.
function applySegmentStyles() {
    for (const [id, route] of serversModel?.segments ?? []) {
        graph.getElementById(id).style({
            "curve-style": "segments", "edge-distances": "node-position",
            "segment-weights": route.weights.join(" "), "segment-distances": route.distances.join(" ")
        });
    }
}

// Styles of the Servers view. Everything that depends on a token is built here, so a theme change (which runs applyStyles
// again) also regenerates the role colours and the icon images.
function serversStyles(c) {
    const cards = 'node[kind = "Host"], node[kind = "Clients"], node[?unknownHost]';
    const rules = [
        // The card: a compound node, with a soft glow made of its outline. Its size comes from the header and rows inside it plus the padding; its own label is the
        // caption above it and must not widen it.
        { selector: cards, style: {
            "background-color": c.card, "border-width": 1.75, "border-style": "solid", "shape": "round-rectangle",
            "corner-radius": 18, "padding": CARD.pad, "compound-sizing-wrt-labels": "exclude",
            "label": "data(displayLabel)", "text-valign": "top", "text-halign": "center",
            "text-margin-x": "data(captionDx)", "text-margin-y": -7, "text-wrap": "none",
            "font-family": c.mono, "font-size": 12, "font-weight": 400, "color": c.muted,
            "outline-width": 5, "outline-opacity": 0.18, "outline-offset": 0, "outline-style": "solid"
        } },
        { selector: 'node[kind = "Clients"], node[?unknownHost]', style: { "border-style": "dashed", "outline-opacity": 0.07 } },
        { selector: 'node[kind = "HostHeader"]', style: {
            "shape": "rectangle", "background-opacity": 0, "border-width": 0, "padding": 0,
            "width": "data(w)", "height": "data(h)", "label": "data(displayLabel)",
            "text-wrap": "none", "text-halign": "center", "text-valign": "center",
            // The label is centred in the node; half of icon + gap moves it right of the icon.
            "text-margin-x": (CARD.icon + CARD.iconGap) / 2,
            "font-family": c.font, "font-size": 15, "font-weight": 700, "color": c.ink,
            "background-position-x": "0px", "background-position-y": "50%",
            "background-width": `${CARD.icon}px`, "background-height": `${CARD.icon}px`,
            "background-fit": "none", "background-clip": "none"
        } },
        // The SSH warning badge: a triangle icon in the header row, at the right inner edge of the card. Its image comes from the
        // level rules below (the colour is a token value).
        { selector: 'node[kind = "HostBadge"]', style: {
            "shape": "rectangle", "background-opacity": 0, "border-width": 0, "padding": 0,
            "width": "data(w)", "height": "data(h)", "label": "",
            "background-position-x": "50%", "background-position-y": "50%",
            "background-width": `${CARD.badge}px`, "background-height": `${CARD.badge}px`,
            "background-fit": "none", "background-clip": "none"
        } },
        // An unknown endpoint without an address stands alone; an unknown row (below) is styled by the row rules.
        { selector: 'node[kind = "Unknown"][!unknownHost]', style: { "padding": 0, "width": "data(w)", "height": "data(h)", "text-wrap": "wrap", "text-max-width": 400 } },
        // A service or container row (and a domain or unknown-port row in the clients and unknown cards), one pill under the header.
        { selector: "node[?row]", style: {
            "shape": "round-rectangle", "corner-radius": 10, "background-color": c.bg, "border-width": 1, "border-style": "solid", "border-color": c.line,
            "padding": 0, "width": "data(w)", "height": "data(h)", "label": "data(displayLabel)", "text-wrap": "none",
            "text-halign": "center", "text-valign": "center", "font-family": c.font, "font-size": 13, "font-weight": 500, "color": c.ink
        } },
        { selector: 'node[?row][kind = "Domain"], node[?row][kind = "Unknown"]', style: { "font-family": c.mono, "font-size": 12 } },
        // An unknown port on an address of this server: nothing it runs answers there, so the row is dashed like an unknown host.
        { selector: "node[?row][?unmatched]", style: { "border-color": c.warn, "border-style": "dashed" } }
    ];
    for (const [role, definition] of Object.entries(ROLES)) {
        const color = c[definition.token];
        rules.push({ selector: `node[kind = "Host"][role = "${role}"], node[kind = "Clients"][role = "${role}"], node[?unknownHost][role = "${role}"]`,
            style: { "border-color": color, "outline-color": color } });
        rules.push({ selector: `node[kind = "HostHeader"][role = "${role}"]`, style: { "background-image": iconUri(definition.icon, color) } });
    }
    // SSH sessions (T8.18): "notice" has its own colour, "warning" (a source that is not allowed) is danger. They come after the
    // Medium alert, so they win over it, and before High / Critical, whose border colour wins over them. The caption always says it
    // in text and the badge shows on every card with a level, whatever the border colour is.
    const sshColors = { notice: c.ssh, warning: c.danger };
    rules.push({ selector: 'node[kind = "Host"][alertSeverity = "Medium"]', style: { "border-width": 2.5, "border-color": c.warn, "outline-color": c.warn } });
    for (const [level, color] of Object.entries(sshColors)) {
        rules.push({ selector: `node[kind = "Host"][sshLevel = "${level}"]`,
            style: { "border-width": level === "warning" ? 3 : 2.5, "border-color": color, "outline-color": color } });
        rules.push({ selector: `node[kind = "HostBadge"][sshLevel = "${level}"]`, style: { "background-image": iconUri("alert", color) } });
    }
    rules.push(
        { selector: 'node[kind = "Host"][alertSeverity = "Critical"]', style: { "border-width": 3, "border-color": c.danger, "outline-color": c.danger } },
        { selector: 'node[kind = "Host"][alertSeverity = "High"]', style: { "border-width": 2.5, "border-color": c.danger, "outline-color": c.danger } },
        { selector: 'node[stale = "stale"]', style: { "opacity": 0.55, "border-style": "dashed" } },
        // One dashed arrow per pair of servers; the colour keeps the legend: observed only is warn, declared only is faded.
        { selector: "edge", style: {
            "width": 1.75, "line-style": "dashed", "line-dash-pattern": [6, 5], "curve-style": "bezier", "opacity": 1,
            "line-color": c.edge, "target-arrow-color": c.edge, "source-arrow-color": c.edge, "target-arrow-shape": "triangle",
            "label": "data(edgeLabel)", "font-family": c.mono, "font-size": 12, "font-weight": 600, "color": c.graphPort,
            "text-rotation": "none",
            // The map background is the inset colour, so the label background matches it and the line looks interrupted.
            "text-background-color": c.inset, "text-background-opacity": 0.9, "text-background-padding": 3,
            "text-background-shape": "round-rectangle"
        } },
        { selector: "edge[?bothWays]", style: { "source-arrow-shape": "triangle" } },
        { selector: "edge[kind = 'Observed']", style: { "line-color": c.warn, "target-arrow-color": c.warn, "source-arrow-color": c.warn } },
        { selector: "edge[kind = 'Declared']", style: { "opacity": 0.55 } }
    );
    return rules;
}

function runLayout(view = "service", fit = true) {
    if (!graph || graph.elements().length === 0) return;
    graph.layout({
        name: "elk", fit, padding: 28, animate: false, nodeDimensionsIncludeLabels: true,
        elk: {
            algorithm: "layered",
            "elk.direction": view === "network" ? "DOWN" : "RIGHT",
            "elk.layered.spacing.nodeNodeBetweenLayers": "64",
            "elk.spacing.nodeNode": "28",
            "elk.hierarchyHandling": "INCLUDE_CHILDREN"
        }
    }).run();
}

export function init(container, dotNetReference) {
    dotNet = dotNetReference;
    if (!window.cytoscape) throw new Error("Cytoscape.js was not loaded.");
    graph = window.cytoscape({
        container,
        elements: [],
        style: [],
        // A large estate fits at about 0.1: the old floor of 0.08 left almost no room to zoom out from the fitted view.
        minZoom: 0.02,
        maxZoom: 3,
        userZoomingEnabled: true,
        userPanningEnabled: true,
        boxSelectionEnabled: false
    });
    // The current zoom factor, readable from the page (and from a browser check).
    graph.on("zoom", () => { container.dataset.zoom = graph.zoom().toFixed(3); });
    // The wheel is handled here: with Cytoscape's own handling (wheelSensitivity 0.2) one notch of a mouse wheel changed the
    // zoom by about 1 %, so the map looked as if it did not zoom at all. One notch is about 16 %; a trackpad pinch (a wheel
    // event with ctrlKey) is finer. The capture listener runs before Cytoscape's, which then never sees the event.
    container.addEventListener("wheel", event => {
        if (!graph) return;
        event.preventDefault();
        event.stopPropagation();
        const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 400 : 1;
        const factor = Math.exp(-event.deltaY * unit * (event.ctrlKey ? 0.01 : 0.0015));
        const bounds = container.getBoundingClientRect();
        zoomAt(factor, { x: event.clientX - bounds.left, y: event.clientY - bounds.top });
    }, { capture: true, passive: false });
    graph.on("select", "node, edge", event => {
        dotNet?.invokeMethodAsync("OnGraphSelection", JSON.stringify(event.target.data()));
    });
    graph.on("unselect", "node, edge", () => dotNet?.invokeMethodAsync("OnGraphSelection", null));
    // The header and the SSH badge of a server card are not selectable; a tap on one selects the server card instead.
    graph.on("tap", 'node[kind = "HostHeader"], node[kind = "HostBadge"]', event => {
        const card = graph.getElementById(event.target.data("hostId"));
        graph.elements(":selected").unselect();
        card.select();
    });
    themeListener = () => applyStyles();
    document.addEventListener("dva-theme-changed", themeListener);
    applyStyles();
}

// `serverInfo` maps a server id to { os, address } (Servers view captions). `listenerPorts` maps a node id to the tcp ports it
// listens on beyond loopback, and `inventory` an address (normalised like the Unknown grouping) to the { serverId, name } of the
// inventory server that owns it. Returns a promise: the Servers view measures its cards and runs ELK before drawing, and the
// caller awaits it.
export async function setData(elements, view, alertStatuses = {}, serverInfo = {}, listenerPorts = {}, inventory = {}) {
    if (!graph) return;
    const generation = ++dataGeneration;
    if (view === "servers") {
        const c = tokens();
        await loadFonts(c);
        const built = buildServers(elements, alertStatuses || {}, serverInfo || {}, c, listenerPorts || {}, inventory || {});
        const layout = built.boxes.size === 0 ? { origins: new Map(), bends: new Map() } : await layoutServers(built);
        if (!graph || generation !== dataGeneration) return;
        graph.elements().remove();
        viewMode = "servers";
        serversModel = built.model;
        applyStyles();
        graph.add(serversElements(built, layout));
        applySegmentStyles();
        fit();
        return;
    }
    graph.elements().remove();
    viewMode = view;
    serversModel = undefined;
    graph.add(elements.map(data => {
        const attributes = data.attributes || {};
        let displayLabel = data.label || "";
        if (data.kind === "Host" && Array.isArray(attributes.Addresses) && attributes.Addresses.length > 0) {
            displayLabel = `${displayLabel} · ${attributes.Addresses[0]}`;
        } else if (data.kind === "Container") {
            displayLabel = `${displayLabel}\ncontainer · ${attributes.ComposeService || attributes.State || "container"}`;
        } else if (data.kind === "Service") {
            displayLabel = `${displayLabel}\nservice · ${attributes.Unit || "systemd"}`;
        } else if (data.kind === "Unknown") {
            displayLabel = `Unknown\n${attributes.Address || "unknown endpoint"}`;
        }
        // Status is never color-only: the border colour is backed by a text marker in the label.
        const alertSeverity = alertStatuses[data.serverId] || "";
        if (alertSeverity) displayLabel = `${displayLabel}\n· ${alertSeverity} alert`;
        // NFR-3: a stale node (server unreachable at the last discovery) is dimmed and says so in text.
        const stale = attributes.Stale === true ? "stale" : "";
        if (stale) displayLabel = `${displayLabel}\n${staleText(attributes.LastSeenUtc)}`;
        return { data: { ...data, displayLabel, edgeLabel: data.port ? `:${data.port}` : "", alertSeverity, stale } };
    }));
    applyStyles();
    runLayout(view, true);
}

function staleText(lastSeenUtc) {
    const seen = lastSeenUtc ? new Date(lastSeenUtc) : null;
    if (!seen || Number.isNaN(seen.getTime())) return "stale · never discovered";
    const pad = value => String(value).padStart(2, "0");
    return `stale · last seen ${seen.getUTCFullYear()}-${pad(seen.getUTCMonth() + 1)}-${pad(seen.getUTCDate())} ${pad(seen.getUTCHours())}:${pad(seen.getUTCMinutes())} UTC`;
}

// ---- Servers view: model, layout, elements ----

const isDetail = node => typeof node?.label === "string" && DETAIL_LABEL.test(node.label);

// The lowercase names a role is detected from: label, unit, process and compose service, without a leading path. Never the image.
function namesOf(node) {
    const attributes = node.attributes || {};
    return [node.label, attributes.Unit, attributes.ProcessName, attributes.ComposeService]
        .filter(value => typeof value === "string" && value).map(value => value.split("/").pop().toLocaleLowerCase());
}

// Where a node lives in the Servers view: `box` is the top-level box (server card, clients card, unknown node) and `row` the
// row of that card it is drawn as (null for the box itself). A row is the ancestor that is a direct child of the host; deeper
// nodes (compose service in a container, systemd unit) are aliased to it and not drawn. nginx "server block" and "upstream"
// nodes are never rows: they go to the host's proxy row, or to the card when it has none.
function place(node, byId, hostByServer, proxyRowByHost) {
    const placed = placeInTree(node, byId, hostByServer);
    if (placed.row !== null && isDetail(byId.get(placed.row))) return { box: placed.box, row: proxyRowByHost.get(placed.box) ?? null };
    return placed;
}

function placeInTree(node, byId, hostByServer) {
    if (node.kind === "Host") return { box: node.id, row: null };
    if (node.kind === "Domain") return { box: CLIENTS_ID, row: node.id };
    let top = node;
    const seen = new Set([node.id]);
    for (let parent = byId.get(node.parent); parent && !seen.has(parent.id); parent = byId.get(parent.parent)) {
        if (parent.kind === "Host") return { box: parent.id, row: top.id };
        seen.add(parent.id);
        top = parent;
    }
    if (node.kind === "Unknown") return { box: node.id, row: null };
    // No host above it: use the host of the same server, or stand alone as a single box.
    const host = hostByServer.get(node.serverId);
    return host ? { box: host, row: top.id } : { box: top.id, row: null };
}

// The address an unknown endpoint is grouped by: trimmed, lowercase, without IPv6 brackets. Empty when it has none.
const unknownAddress = node => {
    const address = node.attributes?.Address;
    return typeof address === "string" ? address.trim().toLocaleLowerCase().replace(/^\[|\]$/g, "") : "";
};

// The port of an unknown endpoint as text, "" when it has none.
const unknownPort = node => {
    const port = node.attributes?.Port;
    return Number.isFinite(Number(port)) && port !== null && port !== "" ? String(Number(port)) : "";
};

// A real port number, or 0 for anything else (edges and attributes may carry text, null or nothing).
const portNumber = value => {
    const port = Number(value);
    return value !== null && value !== "" && Number.isInteger(port) && port > 0 && port <= 65535 ? port : 0;
};

// At most three ports, ascending, then how many more: ":80, :443, :8080 +2".
function portsText(ports) {
    const sorted = [...ports].sort((a, b) => a - b);
    const shown = sorted.slice(0, 3).map(port => `:${port}`).join(", ");
    return sorted.length > 3 ? `${shown} +${sorted.length - 3}` : shown;
}

// Unknown endpoints with the same address are one machine: one card, one row per port. Nodes with the same address and port
// (seen by different servers) are one row; the first by id is the row, the others are aliased to it.
// A client-side endpoint is the source of at least one edge and the target of none: it was seen connecting TO inventory servers
// (the graph keeps its ephemeral source port as the node's port, which says nothing about the machine). Those members have no
// port rows; they are `clients`, drawn as one row, with the first by id as the row and the others aliased to it. A node that is
// the target of any edge (an nginx upstream, an observed outbound connection) is a backend and keeps its port row.
function groupUnknownHosts(nodes, edges) {
    const sources = new Set(edges.map(edge => edge.source));
    const targets = new Set(edges.map(edge => edge.target));
    const groups = new Map();
    for (const node of nodes.filter(node => node.kind === "Unknown" && unknownAddress(node)).sort((a, b) => a.id.localeCompare(b.id))) {
        const address = unknownAddress(node);
        let group = groups.get(address);
        if (!group) {
            group = { id: `${UNKNOWN_HOST_PREFIX}${address}`, address, members: [], ports: new Map(), clients: [] };
            groups.set(address, group);
        }
        group.members.push(node);
        if (sources.has(node.id) && !targets.has(node.id)) {
            group.clients.push(node);
            continue;
        }
        const port = unknownPort(node);
        if (!group.ports.has(port)) group.ports.set(port, { row: node, nodes: [] });
        group.ports.get(port).nodes.push(node);
    }
    // Only client-side members: an external client, not a machine that serves anything.
    for (const group of groups.values()) group.external = group.clients.length > 0 && group.ports.size === 0;
    return groups;
}

// The label of the one row that stands for the client-side endpoints of an address: how many of them (distinct unknown nodes,
// each one remote address and source port, so one connection each) were seen connecting.
const clientRowLabel = count => `client · ${count} ${count === 1 ? "connection" : "connections"}`;

function buildServers(elements, alertStatuses, serverInfo, c, listenerPorts, inventory) {
    const inventoryByAddress = new Map(Object.entries(inventory));
    const nodes = [];
    const edges = [];
    for (const data of elements) (data.source && data.target ? edges : nodes).push(data);
    const byId = new Map(nodes.map(node => [node.id, node]));
    const hostByServer = new Map(nodes.filter(node => node.kind === "Host" && node.serverId).map(node => [node.serverId, node.id]));
    const proxyRowByHost = new Map();
    for (const node of nodes) {
        const parent = byId.get(node.parent);
        if (parent?.kind === "Host" && !isDetail(node) && !proxyRowByHost.has(parent.id)
            && namesOf(node).some(name => ROLE_PATTERNS.proxy.test(name))) proxyRowByHost.set(parent.id, node.id);
    }
    const alias = new Map(nodes.map(node => [node.id, place(node, byId, hostByServer, proxyRowByHost)]));
    const unknownHosts = groupUnknownHosts(nodes, edges);
    // The unknown rows that are drawn inside the card of the inventory server that owns their address (row id -> row data): ports
    // that nothing on that server answers on.
    const mergedRows = new Map();
    for (const group of unknownHosts.values()) {
        group.inventory = inventoryByAddress.get(group.address);
        // Only a server with a host card in this view takes the rows; otherwise the unknown card stays, named after the server.
        const hostId = group.inventory ? hostByServer.get(group.inventory.serverId) : undefined;
        for (const [port, { row, nodes: sameRow }] of group.ports) {
            for (const node of sameRow) alias.set(node.id, { box: hostId ?? group.id, row: row.id });
            if (hostId) mergedRows.set(row.id, { port, row: { ...row, memberNodeIds: sameRow.map(node => node.id), unmatched: true } });
        }
        // Client-side endpoints are one row of the unknown card. On the card of an inventory server they get no row: their ports
        // are the other side's ephemeral ports, not ports of that server, so they are the server itself (their edges are its).
        for (const node of group.clients) alias.set(node.id, hostId ? { box: hostId, row: null } : { box: group.id, row: group.clients[0].id });
        if (hostId) group.mergedInto = hostId;
    }

    // Boxes: servers, unknown endpoints, nodes without a host, and one clients card for all domains.
    const boxes = new Map();
    for (const node of nodes) {
        const placed = alias.get(node.id);
        if (placed.box !== node.id || placed.row !== null) continue;
        const kind = node.kind === "Host" ? "host" : node.kind === "Unknown" ? "unknown" : "standalone";
        boxes.set(node.id, { id: node.id, kind, node, rows: [] });
    }
    if (nodes.some(node => node.kind === "Domain")) {
        boxes.set(CLIENTS_ID, { id: CLIENTS_ID, kind: "clients", node: { id: CLIENTS_ID, kind: "Clients", label: "Internet / Clients" }, rows: [] });
    }
    for (const group of unknownHosts.values()) {
        if (group.mergedInto) continue;
        // The card answers the details panel like an unknown node: its address and the observer for "Add this server".
        const first = group.members.find(node => node.serverId) || group.members[0];
        const rowLabels = new Map();
        const rows = [...group.ports.entries()].sort(([a], [b]) => (a === "") - (b === "") || Number(a) - Number(b)).map(([port, entry]) => {
            rowLabels.set(entry.row.id, port ? `:${port}` : "no port");
            // A row stands for all unknown nodes with this address and port, so its connections are theirs together.
            return { ...entry.row, memberNodeIds: entry.nodes.map(node => node.id) };
        });
        if (group.clients.length > 0) {
            // The client row goes last; selecting it shows the connections of all client-side endpoints together.
            const [clientRow] = group.clients;
            rowLabels.set(clientRow.id, clientRowLabel(group.clients.length));
            rows.push({ ...clientRow, memberNodeIds: group.clients.map(node => node.id) });
        }
        boxes.set(group.id, {
            id: group.id, kind: "unknownHost", rows, rowLabels, address: first.attributes.Address, inventory: group.inventory,
            external: group.external,
            // An address of an inventory server that is not on the map yet: the card is named after that server.
            node: { id: group.id, kind: "Unknown", label: group.inventory?.name || (group.external ? "External client" : "Unknown host"),
                serverId: first.serverId, attributes: { Address: first.attributes.Address }, memberNodeIds: group.members.map(node => node.id),
                inventoryServerId: group.inventory?.serverId, external: group.external || undefined },
            allStale: group.members.every(node => node.attributes?.Stale === true)
        });
    }
    const rowNames = new Map();
    const rowSearch = new Map();
    const cardSearch = new Map();
    for (const node of nodes) {
        const placed = alias.get(node.id);
        const json = JSON.stringify(node).toLocaleLowerCase();
        if (placed.row === null) {
            // Not drawn and without a row (an nginx detail node on a server without an nginx row): searchable through the card.
            if (placed.box !== node.id) cardSearch.set(placed.box, `${cardSearch.get(placed.box) || ""}\n${json}`);
            continue;
        }
        if (placed.row === node.id && boxes.get(placed.box)?.kind !== "unknownHost") boxes.get(placed.box)?.rows.push(mergedRows.get(node.id)?.row ?? node);
        rowNames.set(placed.row, [...(rowNames.get(placed.row) || []), ...namesOf(node)]);
        rowSearch.set(placed.row, `${rowSearch.get(placed.row) || ""}\n${json}`);
    }
    // Unmatched unknown ports go below the real rows, by port ("no port" last).
    for (const box of boxes.values()) {
        const portOf = row => mergedRows.get(row.id)?.port;
        box.rows = [...box.rows.filter(row => portOf(row) === undefined),
            ...box.rows.filter(row => portOf(row) !== undefined).sort((a, b) => (portOf(a) === "") - (portOf(b) === "") || Number(portOf(a)) - Number(portOf(b)))];
    }

    // The ports a service or container answers on: what it listens on (not only on loopback), what its container publishes on
    // the host, and the ports of the connections it receives, over the row and everything aliased to it.
    const rowPorts = new Map();
    const addRowPort = (rowId, value) => {
        const port = portNumber(value);
        if (!port || mergedRows.has(rowId)) return;
        if (!rowPorts.has(rowId)) rowPorts.set(rowId, new Set());
        rowPorts.get(rowId).add(port);
    };
    for (const node of nodes) {
        const rowId = alias.get(node.id)?.row;
        if (!rowId) continue;
        for (const port of listenerPorts[node.id] ?? []) addRowPort(rowId, port);
        for (const published of Array.isArray(node.attributes?.PublishedPorts) ? node.attributes.PublishedPorts : []) {
            if (String(published?.Protocol ?? "tcp").toLocaleLowerCase() === "tcp") addRowPort(rowId, published?.HostPort);
        }
    }
    for (const edge of edges) {
        const rowId = alias.get(edge.target)?.row;
        if (rowId) addRowPort(rowId, edge.port);
    }
    // Searching a port finds the rows that answer on it, whichever of the three sources it came from.
    for (const [rowId, ports] of rowPorts) rowSearch.set(rowId, `${rowSearch.get(rowId) || ""}\n${[...ports].map(port => `:${port}`).join(" ")}`);

    // Edges between the same two boxes become one edge; edges inside a box are not drawn.
    const groups = new Map();
    const edgeAlias = new Map();
    const incomingPorts = new Map();
    for (const edge of edges) {
        const source = alias.get(edge.source)?.box;
        const target = alias.get(edge.target)?.box;
        if (!source || !target || source === target) continue;
        const [first, second] = [source, target].sort();
        const id = `servers-view:edge:${first}|${second}`;
        let group = groups.get(id);
        if (!group) {
            group = { id, first, second, firstSource: source, forward: 0, backward: 0, members: [], ports: new Set(), kinds: new Set() };
            groups.set(id, group);
        }
        if (source === first) group.forward++; else group.backward++;
        group.members.push(edge.id);
        group.kinds.add(edge.kind);
        if (edge.port !== undefined && edge.port !== null && edge.port !== "") {
            group.ports.add(String(edge.port));
            incomingPorts.set(target, [...(incomingPorts.get(target) || []), String(edge.port)]);
        }
        edgeAlias.set(edge.id, id);
    }
    // Declared and observed connections together are as good as "declared + observed", so they show as Both.
    const aggregateKind = kinds => kinds.has("Both") || (kinds.has("Declared") && kinds.has("Observed")) ? "Both"
        : kinds.has("Observed") ? "Observed" : "Declared";
    const aggregated = [...groups.values()].map(group => {
        const source = group.forward > group.backward ? group.first : group.backward > group.forward ? group.second : group.firstSource;
        const ports = [...group.ports].sort((a, b) => Number(a) - Number(b) || a.localeCompare(b)).join(", ");
        return {
            id: group.id, source, target: source === group.first ? group.second : group.first, kind: aggregateKind(group.kinds),
            ports, memberEdgeIds: group.members, bothWays: group.forward > 0 && group.backward > 0
        };
    });
    // Roles use the edges, so they are known only now.
    const fromClients = new Set(aggregated.flatMap(edge => edge.source === CLIENTS_ID ? [edge.target] : edge.target === CLIENTS_ID ? [edge.source] : []));

    const font = { row: `500 13px ${c.font}`, domain: `500 12px ${c.mono}`, header: `700 15px ${c.font}`, caption: `400 12px ${c.mono}` };
    for (const box of boxes.values()) {
        const attributes = box.node.attributes || {};
        const alert = box.kind === "host" ? alertStatuses[box.node.serverId] || "" : "";
        const stale = box.kind === "unknownHost" ? box.allStale : box.kind !== "clients" && attributes.Stale === true;
        box.stale = stale;
        box.alert = alert;
        box.ownSearch = JSON.stringify(box.node).toLocaleLowerCase() + (cardSearch.get(box.id) || "");
        if (box.kind === "host" || box.kind === "clients" || box.kind === "unknownHost") {
            // The observer's server info does not describe an unknown host.
            const info = box.kind === "unknownHost" ? {} : serverInfo[box.node.serverId] || {};
            box.role = box.kind === "clients" ? "clients" : box.kind === "unknownHost" ? "unknownHost"
                : detectRole(box.rows.filter(row => !mergedRows.has(row.id)).map(row => rowNames.get(row.id) || []), {
                    fromClients: fromClients.has(box.id), dataPorts: (incomingPorts.get(box.id) || []).some(port => DATA_PORTS.has(port))
                });
            box.name = box.node.label || "";
            // SSH sessions to look at (T8.18): the page judged them already; "none" (or anything else) draws nothing.
            const ssh = box.kind === "host" ? info.ssh : undefined;
            box.sshLevel = ssh?.level === "notice" || ssh?.level === "warning" ? ssh.level : "";
            box.sshSummary = box.sshLevel ? (typeof ssh.summary === "string" && ssh.summary.trim()) || "SSH sessions to review" : "";
            const address = info.address || (Array.isArray(attributes.Addresses) ? attributes.Addresses[0] : "");
            // The machine's own name is worth a word only when it is not the name the server was added with.
            const hostname = typeof attributes.Hostname === "string" ? attributes.Hostname.trim() : "";
            const knownAs = hostname && hostname.toLocaleLowerCase() !== box.name.toLocaleLowerCase() ? hostname : "";
            const parts = box.kind === "host" ? [ROLES[box.role].title, knownAs, address, info.os]
                : box.kind === "unknownHost" ? [box.inventory ? "In inventory, not discovered yet" : "Not in inventory", box.address] : [];
            if (alert) parts.push(`${alert} alert`);
            if (stale) parts.push(staleText(attributes.LastSeenUtc));
            // The colour and the badge are backed by text: "SSH from 2 sources", "SSH from a source that is not allowed".
            if (box.sshSummary) parts.push(box.sshSummary);
            box.caption = parts.filter(Boolean).join(" • ");
            box.ownSearch += ` ${JSON.stringify({ os: info.os, address: info.address })} ${box.sshSummary}`.toLocaleLowerCase();
            // All rows of a card share one width: the longest label plus padding, at least the minimum, at least the header.
            if (box.kind === "host") {
                box.rowLabels = new Map(box.rows.map(row => {
                    const merged = mergedRows.get(row.id);
                    if (merged) return [row.id, `${merged.port ? `:${merged.port}` : "no port"} · no listener matched`];
                    const ports = rowPorts.get(row.id);
                    return [row.id, ports ? `${row.label ?? ""} · ${portsText(ports)}` : row.label ?? ""];
                }));
            }
            // Unmatched rows are Unknown rows: drawn in the mono font like the rows of an unknown host card.
            const rowFont = row => box.kind === "host" && !mergedRows.has(row.id) ? font.row : font.domain;
            const labelWidth = Math.max(0, ...box.rows.map(row => textWidth(rowLabel(box, row), rowFont(row))));
            box.headerW = CARD.icon + CARD.iconGap + textWidth(box.name, font.header);
            // The badge sits at the right end of the header row, so the card must hold the header, a gap and the badge.
            const headerRowW = box.headerW + (box.sshLevel ? CARD.badgeGap + CARD.badge : 0);
            box.inner = box.rows.length === 0 ? headerRowW : Math.max(headerRowW, CARD.rowMinW, labelWidth + CARD.rowPadX);
            box.w = box.inner + 2 * CARD.pad;
            box.h = 2 * CARD.pad + CARD.headerH + box.rows.length * (CARD.gap + CARD.rowH);
            box.captionW = box.caption ? textWidth(box.caption, font.caption) : 0;
            // The caption is left-aligned with the card; the card is at most as wide as its layout slot.
            box.slotW = Math.max(box.w, box.captionW + 4);
            box.slotH = box.h + (box.caption ? CARD.captionH : 0);
        } else if (box.kind === "unknown") {
            box.lines = ["Unknown", attributes.Address || "unknown endpoint"];
            if (stale) box.lines.push(staleText(attributes.LastSeenUtc));
            box.w = Math.max(...box.lines.map(line => textWidth(line, font.row))) + CARD.unknownPadX;
            box.h = box.lines.length * 18 + 16;
            box.slotW = box.w;
            box.slotH = box.h;
        } else {
            box.w = Math.max(CARD.rowMinW, textWidth(box.node.label || "", font.row) + CARD.rowPadX);
            box.h = CARD.rowH;
            box.slotW = box.w;
            box.slotH = box.h;
        }
    }

    return { boxes, aggregated, rowSearch, model: { alias, edgeAlias, boxes, rowSearch } };
}

// The text of a row: its name, or ":8080" / "no port" in an unknown host card.
const rowLabel = (box, row) => box.rowLabels?.get(row.id) ?? row.label ?? "";

// Positions the top-level boxes with ELK (layered, left to right) and routes the edges; resolves to the top left corner of every
// box's slot and the bend points of every edge (an edge that skips a layer gets some, so it goes around the cards in between).
async function layoutServers(built) {
    const result = await new ELK().layout({
        id: "servers-view",
        layoutOptions: {
            "elk.algorithm": "layered",
            "elk.direction": "RIGHT",
            "elk.spacing.nodeNode": "50",
            "elk.spacing.componentComponent": "50",
            "elk.layered.spacing.nodeNodeBetweenLayers": "100",
            "elk.edgeRouting": "POLYLINE",
            // A bend point is where the port label ends up: keep it clear of the cards beside it.
            "elk.spacing.edgeNode": "24",
            "elk.layered.spacing.edgeNodeBetweenLayers": "24"
        },
        // Clients (the Internet / Clients card and external clients) only connect to servers: they are the first layer, on the
        // left, so they never sit between two servers and look like a hop.
        children: [...built.boxes.values()].map(box => ({
            id: box.id, width: box.slotW, height: box.slotH,
            ...(box.kind === "clients" || box.external ? { layoutOptions: { "elk.layered.layering.layerConstraint": "FIRST" } } : {})
        })),
        edges: built.aggregated.map(edge => ({ id: edge.id, sources: [edge.source], targets: [edge.target] }))
    });
    return {
        origins: new Map((result.children || []).map(child => [child.id, { x: child.x, y: child.y }])),
        bends: new Map((result.edges || []).map(edge => [edge.id, edge.sections?.[0]?.bendPoints || []]))
    };
}

// A bend point as Cytoscape wants it: `weight` is the position along the line from the source centre to the target centre
// (0 at the source, 1 at the target) and `distance` the signed distance from that line (the side of Cytoscape's normal
// (-dy, dx)). The centres must be the ones Cytoscape will compute (edge-distances: node-position).
function segmentRoute(source, target, bends) {
    if (!source || !target || bends.length === 0) return undefined;
    const dx = target.x - source.x;
    const dy = target.y - source.y;
    const squared = dx * dx + dy * dy;
    if (squared < 1) return undefined;
    const length = Math.sqrt(squared);
    return {
        weights: bends.map(point => ((point.x - source.x) * dx + (point.y - source.y) * dy) / squared),
        distances: bends.map(point => (dx * (point.y - source.y) - dy * (point.x - source.x)) / length)
    };
}

// Cytoscape elements with preset positions. Only the children get positions: a card (compound node) takes its size and place
// from the header and rows inside it.
function serversElements(built, layout) {
    const { origins, bends } = layout;
    const result = [];
    // ELK knows the slots (card plus caption); the card is below the caption, so its centre is lower than the slot's.
    const centres = new Map();
    for (const box of built.boxes.values()) {
        const origin = origins.get(box.id) || { x: 0, y: 0 };
        centres.set(box.id, { x: origin.x + box.w / 2, y: origin.y + (box.caption ? CARD.captionH : 0) + box.h / 2 });
    }
    built.model.segments = new Map();
    for (const box of built.boxes.values()) {
        const origin = origins.get(box.id) || { x: 0, y: 0 };
        const stale = box.stale ? "stale" : "";
        if (box.kind === "unknown" || box.kind === "standalone") {
            const isUnknown = box.kind === "unknown";
            result.push({ data: {
                ...box.node, parent: null, displayLabel: isUnknown ? box.lines.join("\n") : box.node.label || "",
                w: box.w, h: box.h, stale, row: isUnknown ? undefined : true
            }, position: { x: origin.x + box.w / 2, y: origin.y + box.h / 2 } });
            continue;
        }
        const top = origin.y + (box.caption ? CARD.captionH : 0);
        result.push({ data: {
            ...box.node, parent: null, role: box.role, displayLabel: box.caption, alertSeverity: box.alert, stale,
            captionDx: (box.captionW - box.w) / 2 + 4, unknownHost: box.kind === "unknownHost" ? true : undefined,
            sshLevel: box.sshLevel || ""
        } });
        result.push({ data: {
            id: headerId(box.id), kind: "HostHeader", hostId: box.id, role: box.role, parent: box.id, label: box.name,
            displayLabel: box.name, w: box.headerW, h: CARD.headerH, stale
        }, position: { x: origin.x + CARD.pad + box.headerW / 2, y: top + CARD.pad + CARD.headerH / 2 },
        selectable: false, grabbable: false });
        if (box.sshLevel) {
            // At the right inner edge of the card, vertically centred in the header row. A tap selects the host (see init).
            result.push({ data: {
                id: badgeId(box.id), kind: "HostBadge", hostId: box.id, sshLevel: box.sshLevel, parent: box.id,
                w: CARD.badge, h: CARD.badge, stale
            }, position: { x: origin.x + CARD.pad + box.inner - CARD.badge / 2, y: top + CARD.pad + CARD.headerH / 2 },
            selectable: false, grabbable: false });
        }
        box.rows.forEach((row, index) => {
            result.push({ data: {
                ...row, parent: box.id, row: true, displayLabel: rowLabel(box, row), w: box.inner, h: CARD.rowH,
                stale: row.attributes?.Stale === true ? "stale" : ""
            }, position: {
                x: origin.x + CARD.pad + box.inner / 2,
                y: top + CARD.pad + CARD.headerH + CARD.gap + index * (CARD.rowH + CARD.gap) + CARD.rowH / 2
            }, grabbable: false });
        });
    }
    for (const edge of built.aggregated) {
        const route = segmentRoute(centres.get(edge.source), centres.get(edge.target), bends.get(edge.id) || []);
        if (route) built.model.segments.set(edge.id, route);
        result.push({ data: { ...edge, edgeLabel: edge.ports } });
    }
    return result;
}

// The element a real node or edge id stands for in the current view: itself, or in the Servers view its row, its card or the
// aggregated edge that contains it.
function resolveElement(id) {
    const direct = graph.getElementById(id);
    if (direct.nonempty() || !serversModel) return direct;
    const placed = serversModel.alias.get(id);
    if (placed) return graph.getElementById(placed.row ?? placed.box);
    const edgeId = serversModel.edgeAlias.get(id);
    return edgeId ? graph.getElementById(edgeId) : direct;
}

export function setTheme() { applyStyles(); }
// The Servers view has port labels on the edges that reach beyond the cards, so it keeps a wider margin.
export function fit() { graph?.fit(undefined, viewMode === "servers" ? 40 : 28); }

// Zooms by `factor` around a point of the visible map (pixels from its top left), kept inside the zoom limits.
function zoomAt(factor, renderedPosition) {
    if (!graph) return;
    const level = Math.min(graph.maxZoom(), Math.max(graph.minZoom(), graph.zoom() * factor));
    graph.zoom({ level, renderedPosition });
}

// Zoom buttons: a step around the middle of the visible map.
export function zoomBy(factor) {
    if (graph) zoomAt(factor, { x: graph.width() / 2, y: graph.height() / 2 });
}

export function select(id) {
    if (!graph) return;
    // A connection inside one card is not drawn in the Servers view: keep the selection, the page shows that connection itself.
    const element = resolveElement(id);
    if (element.empty() && serversModel) return;
    graph.elements().unselect();
    element.select();
}

// Selects a node and brings it into view (global search, T8.2).
export function focus(id) {
    if (!graph) return;
    const element = resolveElement(id);
    if (element.empty()) return;
    graph.elements().unselect();
    element.select();
    graph.animate({ center: { eles: element }, zoom: Math.max(graph.zoom(), 1.1) }, { duration: 250 });
}

// A card with its header (but not its rows).
function cardFrame(boxId) {
    const card = graph.getElementById(boxId);
    return card.union(card.children('[kind = "HostHeader"], [kind = "HostBadge"]'));
}

export function highlightTrace(nodeIds, edgeIds) {
    if (!graph) return;
    clearTrace();
    graph.elements().addClass("trace-dim");
    if (serversModel) {
        highlightServersTrace(nodeIds || [], edgeIds || []);
        return;
    }
    for (const id of nodeIds || []) {
        const element = graph.getElementById(id);
        element.removeClass("trace-dim").addClass("traced");
    }
    for (const id of edgeIds || []) {
        const element = graph.getElementById(id);
        element.removeClass("trace-dim").addClass("traced");
    }
}

// Traced rows (and the cards around them) and the aggregated edges that contain a traced connection stay lit.
function highlightServersTrace(nodeIds, edgeIds) {
    for (const id of nodeIds) {
        const placed = serversModel.alias.get(id);
        if (!placed) continue;
        cardFrame(placed.box).removeClass("trace-dim");
        if (placed.row) {
            graph.getElementById(placed.row).removeClass("trace-dim").addClass("traced");
        } else {
            graph.getElementById(placed.box).addClass("traced").children().removeClass("trace-dim");
        }
    }
    for (const id of edgeIds) {
        const edge = graph.getElementById(serversModel.edgeAlias.get(id) ?? "");
        edge.removeClass("trace-dim").addClass("traced");
        edge.connectedNodes().forEach(node => cardFrame(node.id()).removeClass("trace-dim"));
    }
}

export function clearTrace() {
    graph?.elements().removeClass("trace-dim traced");
}

export function search(query) {
    if (!graph) return;
    const value = (query || "").trim().toLocaleLowerCase();
    graph.elements().removeClass("search-dim");
    if (!value) return;
    if (serversModel) {
        searchServers(value);
        return;
    }
    const matches = graph.nodes().filter(node => JSON.stringify(node.data()).toLocaleLowerCase().includes(value));
    graph.nodes().not(matches).addClass("search-dim");
    graph.edges().addClass("search-dim");
    matches.connectedEdges().removeClass("search-dim");
}

// A card stays lit when it matches itself (then all its rows stay lit) or when any row matches, including what is nested in
// the row but not drawn. Edges stay lit when a port matches or when one of the cards they join is lit.
function searchServers(value) {
    graph.elements().addClass("search-dim");
    for (const [boxId, box] of serversModel.boxes) {
        const rows = box.rows.filter(row => serversModel.rowSearch.get(row.id)?.includes(value));
        const own = box.ownSearch.includes(value);
        if (!own && rows.length === 0) continue;
        cardFrame(boxId).removeClass("search-dim");
        const lit = own ? graph.getElementById(boxId).children() : graph.collection(rows.map(row => graph.getElementById(row.id)));
        lit.removeClass("search-dim");
    }
    graph.edges().forEach(edge => {
        const portMatch = (edge.data("ports") || "").includes(value);
        if (portMatch || edge.connectedNodes().some(node => !node.hasClass("search-dim"))) edge.removeClass("search-dim");
    });
}

export function dispose() {
    document.removeEventListener("dva-theme-changed", themeListener);
    graph?.destroy();
    graph = undefined;
    dotNet = undefined;
    serversModel = undefined;
}
