let graph;
let dotNet;
let themeListener;

function tokens() {
    const style = getComputedStyle(document.documentElement);
    const read = name => style.getPropertyValue(`--dva-${name}`).trim();
    return {
        card: read("card"), inset: read("inset"), ink: read("ink"), muted: read("muted"),
        line: read("line"), edge: read("edge"), accent: read("accent"), warn: read("warn"),
        warnSoft: read("warn-soft"), onWarn: read("on-warn"), danger: read("danger"),
        font: '"Plus Jakarta Sans", system-ui, sans-serif', mono: '"JetBrains Mono", monospace'
    };
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
        { selector: "edge:selected", style: { "width": 2.5, "line-color": c.accent, "target-arrow-color": c.accent, "color": c.ink } },
        { selector: "node:selected", style: { "border-width": 2, "border-color": c.accent } },
        { selector: "node.traced", style: { "border-width": 2, "border-color": c.accent } },
        { selector: "edge.traced", style: { "width": 2.5, "line-color": c.accent, "target-arrow-color": c.accent, "color": c.ink } },
        { selector: ".trace-dim", style: { "opacity": 0.16 } },
        { selector: ".search-dim", style: { "opacity": 0.18 } }
    ]);
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
        minZoom: 0.08,
        maxZoom: 3,
        wheelSensitivity: 0.2,
        userZoomingEnabled: true,
        userPanningEnabled: true,
        boxSelectionEnabled: false
    });
    graph.on("select", "node, edge", event => {
        dotNet?.invokeMethodAsync("OnGraphSelection", JSON.stringify(event.target.data()));
    });
    graph.on("unselect", "node, edge", () => dotNet?.invokeMethodAsync("OnGraphSelection", null));
    themeListener = () => applyStyles();
    document.addEventListener("dva-theme-changed", themeListener);
    applyStyles();
}

export function setData(elements, view, alertStatuses = {}) {
    if (!graph) return;
    graph.elements().remove();
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

export function setTheme() { applyStyles(); }
export function fit() { graph?.fit(undefined, 28); }

export function select(id) {
    if (!graph) return;
    graph.elements().unselect();
    graph.getElementById(id).select();
}

// Selects a node and brings it into view (global search, T8.2).
export function focus(id) {
    if (!graph) return;
    const element = graph.getElementById(id);
    if (element.empty()) return;
    graph.elements().unselect();
    element.select();
    graph.animate({ center: { eles: element }, zoom: Math.max(graph.zoom(), 1.1) }, { duration: 250 });
}

export function highlightTrace(nodeIds, edgeIds) {
    if (!graph) return;
    clearTrace();
    graph.elements().addClass("trace-dim");
    for (const id of nodeIds || []) {
        const element = graph.getElementById(id);
        element.removeClass("trace-dim").addClass("traced");
    }
    for (const id of edgeIds || []) {
        const element = graph.getElementById(id);
        element.removeClass("trace-dim").addClass("traced");
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
    const matches = graph.nodes().filter(node => JSON.stringify(node.data()).toLocaleLowerCase().includes(value));
    graph.nodes().not(matches).addClass("search-dim");
    graph.edges().addClass("search-dim");
    matches.connectedEdges().removeClass("search-dim");
}

export function dispose() {
    document.removeEventListener("dva-theme-changed", themeListener);
    graph?.destroy();
    graph = undefined;
    dotNet = undefined;
}
