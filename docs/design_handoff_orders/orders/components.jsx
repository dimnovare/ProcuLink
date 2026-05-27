// orders/components.jsx — quiet primitives & app shell

const T = {
  // Brand
  blue: "#1E66C9", blueDeep: "#0F4FA8", blueSoft: "#EAF0F8",
  green: "#2E8E3A", greenDeep: "#1E6D29", greenSoft: "#E9F1EA",
  // Chrome (navy)
  navy: "#0B1A2F", navySurface: "#14253D", navyBorder: "#1F3252",
  navyText: "#C8D1E0", navyMuted: "#7C8DA6",
  // Light surfaces
  bg: "#F7F8FA", surface: "#FFFFFF", surface2: "#F1F3F7",
  border: "#E5E8EE", borderStrong: "#CBD0DA", borderFaint: "#EEF0F4",
  ink: "#0B1A2F", inkMuted: "#5E6779", inkFaint: "#98A0AE",
  // Semantic
  amber: "#B36D14", amberSoft: "#FAF1DD",
  danger: "#B43838", dangerSoft: "#FAE6E6",
  ai: "#6F4FCE", aiSoft: "#F0EAFB",
  // Type
  ui: '"Inter", -apple-system, system-ui, sans-serif',
  display: '"Bricolage Grotesque", "Inter", system-ui, sans-serif',
  mono: '"JetBrains Mono", ui-monospace, monospace',
};
T.linkGradient = `linear-gradient(90deg, ${T.blue} 0%, ${T.blue} 35%, ${T.green} 65%, ${T.green} 100%)`;

/* ============================================================
   MarkSystem — brand mark
   ============================================================ */
function MarkSystem({ size = 24 }) {
  const id = `mg-${size}`;
  return (
    <svg width={size} height={size} viewBox="0 0 40 40" fill="none" style={{ flexShrink: 0 }}>
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor={T.blue}/><stop offset="100%" stopColor={T.green}/>
        </linearGradient>
      </defs>
      <path d="M8 10h14a10 10 0 0 1 10 10v0a10 10 0 0 1-10 10H8" stroke={`url(#${id})`} strokeWidth="3.6" strokeLinecap="round" fill="none"/>
      <circle cx="8" cy="10" r="2.5" fill={T.blue}/>
      <circle cx="8" cy="30" r="2.5" fill={T.green}/>
    </svg>
  );
}

/* ============================================================
   LinkSpine — quiet 1.5px gradient under topbar
   ============================================================ */
function LinkSpine({ height = 2, soft = true }) {
  return <div style={{ height, background: soft
    ? `linear-gradient(90deg, transparent, ${T.blue}88 30%, ${T.green}88 70%, transparent)`
    : T.linkGradient }} />;
}

/* ============================================================
   StatusPill — single dot + word. Monochrome by default;
   amber/red only for action states.
   ============================================================ */
function StatusPill({ status, size = "md" }) {
  const map = {
    new:        { dot: T.inkFaint,   label: "New" },
    extracting: { dot: T.blue,       label: "Extracting" },
    review:     { dot: T.amber,      label: "Needs review",  tint: true },
    ready:      { dot: T.green,      label: "Ready" },
    sent:       { dot: T.greenDeep,  label: "Delivered" },
    failed:     { dot: T.danger,     label: "Failed", tint: true, danger: true },
  };
  const s = map[status] || map.new;
  const fs = size === "sm" ? 11 : 12;
  const py = size === "sm" ? 2 : 3;
  return (
    <span style={{
      display: "inline-flex", alignItems: "center", gap: 7,
      padding: `${py}px 10px ${py}px 9px`,
      borderRadius: 999,
      background: s.tint ? (s.danger ? T.dangerSoft : T.amberSoft) : "transparent",
      color: s.tint ? (s.danger ? T.danger : T.amber) : T.ink,
      fontSize: fs, fontWeight: 500, lineHeight: 1.3,
      border: s.tint ? "none" : `1px solid ${T.border}`,
      whiteSpace: "nowrap",
    }}>
      <span style={{ width: 7, height: 7, borderRadius: 999, background: s.dot, flexShrink: 0 }}/>
      {s.label}
    </span>
  );
}

/* ============================================================
   Button — quieter
   ============================================================ */
function Button({ children, variant = "secondary", size = "md", onClick, icon, disabled, style = {} }) {
  const v =
    variant === "primary"   ? { bg: T.navy, fg: "#fff", border: T.navy } :
    variant === "send"      ? { bg: T.green, fg: "#fff", border: T.greenDeep } :
    variant === "danger"    ? { bg: T.danger, fg: "#fff", border: T.danger } :
    variant === "ghost"     ? { bg: "transparent", fg: T.inkMuted, border: "transparent" } :
                              { bg: T.surface, fg: T.ink, border: T.borderStrong };
  const h = size === "sm" ? 28 : size === "lg" ? 38 : 32;
  const fs = size === "sm" ? 12 : size === "lg" ? 13.5 : 12.5;
  const px = size === "sm" ? 10 : size === "lg" ? 16 : 14;
  return (
    <button onClick={disabled ? null : onClick} style={{
      height: h, padding: `0 ${px}px`,
      background: v.bg, color: v.fg, border: `1px solid ${v.border}`,
      borderRadius: 6, fontSize: fs, fontWeight: 500, fontFamily: T.ui,
      display: "inline-flex", alignItems: "center", gap: 7,
      cursor: disabled ? "not-allowed" : "pointer",
      opacity: disabled ? 0.5 : 1, whiteSpace: "nowrap",
      transition: "background .15s",
      ...style,
    }}>
      {icon && <span style={{ display: "inline-flex" }}>{icon}</span>}
      {children}
    </button>
  );
}

/* ============================================================
   SrcChip — small file-type tag. Restrained palette.
   ============================================================ */
function SrcChip({ type }) {
  // Single neutral background; type-letter color carries the meaning quietly
  const colors = {
    PDF: "#B43838",
    XLSX: T.greenDeep,
    CSV: "#345470",
    XML: "#5E3DB0",
    cXML: "#5E3DB0",
    EDI: T.amber,
    EMAIL: "#4A5568",
    API: T.greenDeep,
    JSON: "#846100",
  };
  const fg = colors[type] || T.inkMuted;
  return (
    <span style={{
      fontFamily: T.mono, fontSize: 10, fontWeight: 700,
      padding: "2px 6px", borderRadius: 3,
      background: T.surface2, color: fg, letterSpacing: "0.04em",
      border: `1px solid ${T.border}`,
    }}>{type}</span>
  );
}

/* ============================================================
   Icons
   ============================================================ */
const Icon = {
  Search: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <circle cx="7" cy="7" r="4.5" stroke={c} strokeWidth="1.4"/><path d="M11 11l3 3" stroke={c} strokeWidth="1.4" strokeLinecap="round"/>
    </svg>
  ),
  Bell: ({ size = 16, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <path d="M8 2.5a3.5 3.5 0 0 0-3.5 3.5v2.2c0 .5-.2 1-.6 1.4L3 11h10l-.9-1.4c-.4-.4-.6-.9-.6-1.4V6A3.5 3.5 0 0 0 8 2.5z" stroke={c} strokeWidth="1.3"/><path d="M6.5 13a1.5 1.5 0 0 0 3 0" stroke={c} strokeWidth="1.3"/>
    </svg>
  ),
  Upload: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <path d="M8 11V3M5 6l3-3 3 3M3 12v1.5h10V12" stroke={c} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
    </svg>
  ),
  Refresh: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <path d="M13 3v3.5h-3.5M3 13V9.5H6.5" stroke={c} strokeWidth="1.5" strokeLinecap="round"/>
      <path d="M11.8 6.5A4.5 4.5 0 0 0 3.6 6 M4.2 9.5a4.5 4.5 0 0 0 8.2.5" stroke={c} strokeWidth="1.5" strokeLinecap="round"/>
    </svg>
  ),
  ArrowLeft: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <path d="M10 3l-5 5 5 5M5 8h10" stroke={c} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
    </svg>
  ),
  Chevron: ({ size = 12, dir = "right", c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 12 12" fill="none" style={{ transform: dir === "down" ? "rotate(90deg)" : dir === "left" ? "rotate(180deg)" : dir === "up" ? "rotate(-90deg)" : "" }}>
      <path d="M4.5 2.5L8 6l-3.5 3.5" stroke={c} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
    </svg>
  ),
  Warn: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="8" r="6.5" stroke={c} strokeWidth="1.3"/><path d="M8 5v3.5M8 11v.5" stroke={c} strokeWidth="1.4" strokeLinecap="round"/>
    </svg>
  ),
  Check: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <path d="M3 8.5L6.5 12 13 4.5" stroke={c} strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
    </svg>
  ),
  Sparkle: ({ size = 12, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 12 12" fill="none">
      <path d="M6 1.5l1 3 3 1-3 1-1 3-1-3-3-1 3-1z" fill={c}/>
    </svg>
  ),
  Doc: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <path d="M3.5 1.5h6L13 5v9.5H3.5z" stroke={c} strokeWidth="1.4" strokeLinejoin="round"/><path d="M9.5 1.5V5H13" stroke={c} strokeWidth="1.4" strokeLinejoin="round"/>
    </svg>
  ),
  Plus: ({ size = 12, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 12 12" fill="none">
      <path d="M6 2v8M2 6h8" stroke={c} strokeWidth="1.5" strokeLinecap="round"/>
    </svg>
  ),
  More: ({ size = 14, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none">
      <circle cx="4" cy="8" r="1.2" fill={c}/><circle cx="8" cy="8" r="1.2" fill={c}/><circle cx="12" cy="8" r="1.2" fill={c}/>
    </svg>
  ),
  Close: ({ size = 12, c = "currentColor" }) => (
    <svg width={size} height={size} viewBox="0 0 12 12" fill="none">
      <path d="M3 3l6 6M9 3l-6 6" stroke={c} strokeWidth="1.5" strokeLinecap="round"/>
    </svg>
  ),
};

/* ============================================================
   Sidebar — sparser, badges only when >0, less colour
   ============================================================ */
function Sidebar({ active, onNav, counts }) {
  const items = [
    { type: "item",  k: "bridge",  label: "Bridge" },
    { type: "group", label: "Inbox" },
    { type: "item",  k: "crossings", label: "All crossings" },
    { type: "item",  k: "new",       label: "New" },
    { type: "item",  k: "review",    label: "Needs review", badge: counts?.review || null },
    { type: "item",  k: "failed",    label: "Failed",       badge: counts?.failed || null, danger: true },
    { type: "item",  k: "sent",      label: "Sent" },
    { type: "group", label: "Workbench" },
    { type: "item",  k: "upload",  label: "Upload" },
    { type: "item",  k: "orders",  label: "Orders" },
    { type: "item",  k: "drafts",  label: "Drafts" },
    { type: "group", label: "Library" },
    { type: "item",  k: "suppliers", label: "Suppliers" },
    { type: "item",  k: "buyers",    label: "Buyers" },
    { type: "item",  k: "mappings",  label: "Mappings" },
    { type: "item",  k: "rules",     label: "Rules" },
    { type: "item",  k: "templates", label: "Output templates" },
    { type: "group", label: "Operations" },
    { type: "item",  k: "log",   label: "Crossings log" },
    { type: "item",  k: "conn",  label: "Connectors" },
    { type: "item",  k: "hooks", label: "Webhooks" },
  ];
  return (
    <aside style={{
      width: 224, flexShrink: 0, background: T.navy, color: T.navyText,
      display: "flex", flexDirection: "column",
    }}>
      <div style={{ padding: "18px 18px 16px", display: "flex", alignItems: "center", gap: 10, cursor: "pointer" }} onClick={() => onNav?.("bridge")}>
        <MarkSystem size={24}/>
        <span style={{ fontWeight: 600, fontSize: 14.5, color: "white", letterSpacing: "-0.005em" }}>ProcuLink</span>
      </div>

      <div style={{ padding: "0 12px 12px" }}>
        <div style={{
          padding: "9px 11px", borderRadius: 7,
          background: T.navySurface,
          display: "flex", alignItems: "center", gap: 10, cursor: "pointer",
        }}>
          <div style={{ width: 24, height: 24, borderRadius: 5, background: T.linkGradient, color: "white", fontWeight: 700, fontSize: 10, letterSpacing: "0.02em", display: "flex", alignItems: "center", justifyContent: "center" }}>ND</div>
          <div style={{ flex: 1, overflow: "hidden" }}>
            <div style={{ fontWeight: 500, color: "white", fontSize: 12.5, whiteSpace: "nowrap", textOverflow: "ellipsis", overflow: "hidden" }}>Nordic Distribution</div>
            <div style={{ fontSize: 10.5, color: T.navyMuted, marginTop: 1 }}>Free plan</div>
          </div>
          <Icon.Chevron dir="down" c={T.navyMuted}/>
        </div>
      </div>

      <div style={{ flex: 1, overflow: "auto", padding: "4px 8px 12px" }}>
        {items.map((it, i) => {
          if (it.type === "group") {
            return <div key={"g" + i} style={{ padding: "16px 12px 6px", fontSize: 10, color: T.navyMuted, fontWeight: 600, letterSpacing: "0.1em", textTransform: "uppercase" }}>{it.label}</div>;
          }
          const on = active === it.k;
          return (
            <div key={it.k} onClick={() => onNav?.(it.k)} style={{
              padding: "7px 12px", borderRadius: 6, margin: "1px 0",
              display: "flex", alignItems: "center", gap: 8, fontSize: 13,
              background: on ? T.navySurface : "transparent",
              color: on ? "white" : T.navyText,
              fontWeight: on ? 500 : 400,
              cursor: "pointer",
            }}>
              <span>{it.label}</span>
              {it.badge != null && (
                <span style={{
                  marginLeft: "auto",
                  fontSize: 10.5, fontWeight: 500,
                  fontVariantNumeric: "tabular-nums",
                  color: it.danger ? T.danger : on ? "white" : T.navyMuted,
                }}>{it.badge}</span>
              )}
            </div>
          );
        })}
      </div>

      <div style={{ padding: "12px 18px", fontSize: 11.5, color: T.navyMuted, display: "flex", alignItems: "center", gap: 8, borderTop: `1px solid ${T.navyBorder}` }}>
        <span style={{ width: 6, height: 6, borderRadius: "50%", background: T.green }}/>
        Bridge healthy
      </div>
    </aside>
  );
}

/* ============================================================
   Topbar
   ============================================================ */
function Topbar({ crumb }) {
  return (
    <div style={{ position: "relative", flexShrink: 0 }}>
      <div style={{
        height: 54, background: T.navy, color: T.navyText,
        display: "flex", alignItems: "center", padding: "0 24px", gap: 14,
      }}>
        <div style={{ fontSize: 13, color: T.navyText, display: "flex", alignItems: "center", gap: 10 }}>{crumb}</div>
        <div style={{ marginLeft: "auto", display: "flex", alignItems: "center", gap: 10 }}>
          <div style={{
            height: 32, padding: "0 11px", borderRadius: 6,
            background: T.navySurface,
            display: "flex", alignItems: "center", gap: 9,
            fontSize: 12, color: T.navyMuted, width: 320,
          }}>
            <Icon.Search c={T.navyMuted}/>
            <span style={{ flex: 1 }}>Search orders, suppliers, SKUs…</span>
            <span style={{ fontFamily: T.mono, fontSize: 10.5, color: T.navyMuted, padding: "1px 5px", border: `1px solid ${T.navyBorder}`, borderRadius: 3 }}>⌘K</span>
          </div>
          <button style={{ width: 32, height: 32, borderRadius: 6, background: "transparent", border: 0, color: T.navyText, display: "inline-flex", alignItems: "center", justifyContent: "center", cursor: "pointer" }}>
            <Icon.Bell c={T.navyText}/>
          </button>
          <div style={{ width: 30, height: 30, borderRadius: "50%", background: T.linkGradient, color: "white", fontSize: 11, fontWeight: 600, display: "inline-flex", alignItems: "center", justifyContent: "center" }}>MK</div>
        </div>
      </div>
      <div style={{ position: "absolute", left: 0, right: 0, bottom: 0 }}><LinkSpine height={1.5} soft/></div>
    </div>
  );
}

window.PROCU = {
  T, MarkSystem, LinkSpine, StatusPill, Button, SrcChip, Icon, Sidebar, Topbar,
};
