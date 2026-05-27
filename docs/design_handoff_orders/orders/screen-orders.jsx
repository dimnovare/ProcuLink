// orders/screen-orders.jsx — Orders list (restrained)

const { T, StatusPill, Button, SrcChip, Icon } = window.PROCU;
const { ORDERS } = window.PROCU_DATA;

function StatusDropdown({ value, onChange }) {
  const opts = [
    { k: "all",        label: "All statuses" },
    { k: "new",        label: "New" },
    { k: "extracting", label: "Extracting" },
    { k: "review",     label: "Needs review" },
    { k: "ready",      label: "Ready" },
    { k: "sent",       label: "Delivered" },
    { k: "failed",     label: "Failed" },
  ];
  const [open, setOpen] = React.useState(false);
  const current = opts.find((o) => o.k === value) || opts[0];
  return (
    <div style={{ position: "relative" }}>
      <button onClick={() => setOpen((v) => !v)} style={{
        height: 32, padding: "0 12px", borderRadius: 6,
        background: T.surface, border: `1px solid ${T.borderStrong}`,
        display: "inline-flex", alignItems: "center", gap: 10,
        fontSize: 12.5, color: T.ink, fontWeight: 500,
        cursor: "pointer", minWidth: 160,
      }}>
        <span>{current.label}</span>
        <Icon.Chevron dir="down" c={T.inkFaint}/>
      </button>
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: "fixed", inset: 0, zIndex: 50 }}/>
          <div style={{
            position: "absolute", top: "calc(100% + 4px)", left: 0,
            width: 200, background: T.surface,
            border: `1px solid ${T.border}`, borderRadius: 8,
            boxShadow: "0 8px 24px rgba(11,26,47,0.10)", padding: 4, zIndex: 51,
          }}>
            {opts.map((o) => (
              <div key={o.k} onClick={() => { onChange(o.k); setOpen(false); }} style={{
                padding: "7px 10px", borderRadius: 5, fontSize: 12.5, cursor: "pointer",
                color: T.ink, background: value === o.k ? T.surface2 : "transparent",
                display: "flex", alignItems: "center", gap: 8, fontWeight: value === o.k ? 500 : 400,
              }}
                onMouseEnter={(e) => (e.currentTarget.style.background = T.surface2)}
                onMouseLeave={(e) => (e.currentTarget.style.background = value === o.k ? T.surface2 : "transparent")}
              >
                {o.label}
                {value === o.k && <Icon.Check size={12} c={T.greenDeep} style={{ marginLeft: "auto" }}/>}
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

function OrdersListScreen({ onOpen, density = "cozy" }) {
  const [query, setQuery] = React.useState("");
  const [statusFilter, setStatusFilter] = React.useState("all");
  const [sort, setSort] = React.useState({ key: "created", dir: "desc" });

  const rows = React.useMemo(() => {
    let r = [...ORDERS];
    if (statusFilter !== "all") r = r.filter((x) => x.status === statusFilter);
    if (query) {
      const q = query.toLowerCase();
      r = r.filter((x) =>
        x.po.toLowerCase().includes(q) ||
        x.supplier.toLowerCase().includes(q) ||
        x.buyer.toLowerCase().includes(q),
      );
    }
    return r;
  }, [query, statusFilter, sort]);

  const total = ORDERS.length;
  const reviewCount = ORDERS.filter((o) => o.status === "review").length;

  const rowPadY = density === "compact" ? 11 : 16;
  const rowGap  = density === "compact" ? 12 : 14;

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", minWidth: 0, minHeight: 0, background: T.bg }}>
      {/* Page header */}
      <div style={{ background: T.surface, borderBottom: `1px solid ${T.border}`, padding: "28px 32px 22px", display: "flex", alignItems: "flex-end", gap: 24 }}>
        <div>
          <h1 style={{ margin: 0, fontFamily: T.display, fontSize: 28, fontWeight: 600, letterSpacing: "-0.022em", color: T.ink, lineHeight: 1 }}>
            Orders
          </h1>
          <div style={{ marginTop: 8, fontSize: 12.5, color: T.inkMuted }}>
            <span style={{ fontVariantNumeric: "tabular-nums", color: T.ink }}>{total}</span> total
            {reviewCount > 0 && (
              <>
                <span style={{ margin: "0 8px", color: T.borderStrong }}>·</span>
                <span style={{ color: T.amber, fontWeight: 500, fontVariantNumeric: "tabular-nums" }}>{reviewCount}</span>
                <span style={{ color: T.inkMuted, marginLeft: 5 }}>need review</span>
              </>
            )}
          </div>
        </div>
        <div style={{ marginLeft: "auto", display: "flex", alignItems: "center", gap: 10 }}>
          <Button size="md" variant="ghost" icon={<Icon.Refresh c={T.inkMuted}/>}/>
          <Button size="md" variant="primary" icon={<Icon.Upload c="#fff"/>}>Upload</Button>
        </div>
      </div>

      {/* Filter bar */}
      <div style={{ padding: "14px 32px", borderBottom: `1px solid ${T.border}`, background: T.surface, display: "flex", alignItems: "center", gap: 10 }}>
        <div style={{
          height: 32, flex: 1, maxWidth: 420,
          display: "flex", alignItems: "center", gap: 9,
          background: T.surface, border: `1px solid ${T.borderStrong}`, borderRadius: 6, padding: "0 12px",
        }}>
          <Icon.Search c={T.inkFaint}/>
          <input value={query} onChange={(e) => setQuery(e.target.value)}
            placeholder="Search by PO number, supplier, buyer or SKU…"
            style={{ flex: 1, border: 0, outline: "none", fontSize: 12.5, background: "transparent", color: T.ink }}/>
          {query && <button onClick={() => setQuery("")} style={{ background: "transparent", border: 0, color: T.inkFaint, cursor: "pointer", padding: 0, display: "inline-flex" }}><Icon.Close c={T.inkFaint}/></button>}
        </div>
        <StatusDropdown value={statusFilter} onChange={setStatusFilter}/>
      </div>

      {/* Table */}
      <div style={{ flex: 1, overflow: "auto", padding: "20px 32px 32px" }}>
        <div style={{
          background: T.surface, border: `1px solid ${T.border}`, borderRadius: 10,
          overflow: "hidden",
        }}>
          <div style={{ overflowX: "auto" }}>
          {/* Header */}
          <div style={{
            display: "grid",
            gridTemplateColumns: "minmax(220px, 1.4fr) minmax(220px, 1.6fr) 110px 70px 110px 140px 90px",
            alignItems: "center",
            padding: `10px 24px`,
            background: T.surface, borderBottom: `1px solid ${T.border}`,
            fontSize: 10.5, fontWeight: 500, textTransform: "uppercase", letterSpacing: "0.08em", color: T.inkFaint,
            gap: rowGap,
            minWidth: 1040,
          }}>
            <ColHead label="PO Number" k="po" sort={sort} setSort={setSort}/>
            <ColHead label="Supplier" k="supplier" sort={sort} setSort={setSort}/>
            <ColHead label="Date" k="orderDate" sort={sort} setSort={setSort}/>
            <ColHead label="Lines" k="lines" sort={sort} setSort={setSort} align="right"/>
            <ColHead label="Total" k="total" sort={sort} setSort={setSort} align="right"/>
            <ColHead label="Status" k="status" sort={sort} setSort={setSort}/>
            <ColHead label="Updated" k="created" sort={sort} setSort={setSort} align="right"/>
          </div>

          {/* Rows */}
          {rows.map((o, i) => (
            <div
              key={o.id}
              onClick={() => onOpen?.(o)}
              style={{
                display: "grid",
                gridTemplateColumns: "minmax(220px, 1.4fr) minmax(220px, 1.6fr) 110px 70px 110px 140px 90px",
                alignItems: "center",
                padding: `${rowPadY}px 24px`,
                borderTop: `1px solid ${T.borderFaint}`,
                background: T.surface, cursor: "pointer",
                gap: rowGap,
                minWidth: 1040,
                transition: "background .12s",
              }}
              onMouseEnter={(e) => (e.currentTarget.style.background = T.surface2)}
              onMouseLeave={(e) => (e.currentTarget.style.background = T.surface)}
            >
              <div style={{ minWidth: 0, display: "flex", alignItems: "center", gap: 10 }}>
                <SrcChip type={o.src}/>
                <span style={{ fontFamily: T.mono, fontSize: 12.5, fontWeight: 500, color: T.ink, letterSpacing: "-0.005em" }}>{o.po}</span>
              </div>

              <div style={{ minWidth: 0 }}>
                <div style={{ fontSize: 13, color: T.ink, fontWeight: 500, whiteSpace: "nowrap", textOverflow: "ellipsis", overflow: "hidden" }}>{o.supplier}</div>
                <div style={{ fontSize: 11.5, color: T.inkFaint, marginTop: 2 }}>from {o.buyer}</div>
              </div>

              <div style={{ fontSize: 12.5, color: T.inkMuted }}>{o.orderDate}</div>

              <div style={{ textAlign: "right", fontSize: 13, color: T.ink, fontVariantNumeric: "tabular-nums" }}>
                {o.unresolved > 0 ? (
                  <span><span style={{ color: T.ink }}>{o.lines}</span><span style={{ color: T.amber, fontWeight: 500, marginLeft: 4, fontSize: 11.5 }}>· {o.unresolved}</span></span>
                ) : o.lines}
              </div>

              <div style={{ textAlign: "right", fontFamily: T.mono, fontSize: 12.5, color: T.ink, fontWeight: 500 }}>{o.total}</div>

              <div><StatusPill status={o.status} size="sm"/></div>

              <div style={{ fontSize: 11.5, color: T.inkFaint, textAlign: "right" }}>
                {timeAgo(o.created)}
              </div>
            </div>
          ))}
          </div>

          {/* Footer */}
          <div style={{
            padding: "12px 24px",
            background: T.surface,
            borderTop: `1px solid ${T.border}`,
            display: "flex", alignItems: "center",
            fontSize: 11.5, color: T.inkMuted,
          }}>
            <span>Showing <span style={{ color: T.ink, fontWeight: 500 }}>{rows.length}</span> of <span style={{ color: T.ink, fontWeight: 500 }}>{ORDERS.length}</span></span>
            <span style={{ marginLeft: "auto", display: "inline-flex", alignItems: "center", gap: 12 }}>
              <button style={{ background: "transparent", border: 0, cursor: "pointer", color: T.inkFaint, padding: 4, opacity: 0.4 }}><Icon.Chevron dir="left"/></button>
              <span style={{ fontFamily: T.mono, fontSize: 11 }}>1 / 1</span>
              <button style={{ background: "transparent", border: 0, cursor: "pointer", color: T.inkFaint, padding: 4, opacity: 0.4 }}><Icon.Chevron dir="right"/></button>
            </span>
          </div>
        </div>
      </div>
    </div>
  );
}

function timeAgo(s) {
  // Inputs look like "May 27, 2026 · 09:05". Show a short relative-ish hint.
  if (!s) return "";
  const part = s.split("·")[1];
  return part ? part.trim() : s;
}

function ColHead({ label, k, sort, setSort, align = "left" }) {
  const on = sort.key === k;
  return (
    <button onClick={() => setSort({ key: k, dir: on && sort.dir === "asc" ? "desc" : "asc" })} style={{
      background: "transparent", border: 0, padding: 0,
      display: "inline-flex", alignItems: "center", gap: 4,
      color: on ? T.inkMuted : T.inkFaint,
      fontSize: 10.5, fontWeight: 500, textTransform: "uppercase", letterSpacing: "0.08em",
      cursor: "pointer", fontFamily: T.ui,
      justifyContent: align === "right" ? "flex-end" : "flex-start",
      width: "100%",
    }}>
      {label}
      {on && <span style={{ fontSize: 9 }}>{sort.dir === "asc" ? "↑" : "↓"}</span>}
    </button>
  );
}

window.OrdersListScreen = OrdersListScreen;
