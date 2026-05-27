// orders/screen-order.jsx — Order detail (restrained)

const { T: TT, StatusPill: SP, Button: BN, SrcChip: SC, Icon: IC } = window.PROCU;
const { ORDER_DETAIL } = window.PROCU_DATA;

/* ============================================================
   Section card — no edge strips, just clean grouped section
   ============================================================ */
function Section({ title, action, children, style = {}, dense }) {
  return (
    <div style={{
      background: TT.surface, border: `1px solid ${TT.border}`, borderRadius: 10,
      marginBottom: 14, overflow: "hidden",
      ...style,
    }}>
      {title && (
        <div style={{
          padding: dense ? "11px 16px" : "14px 20px",
          display: "flex", alignItems: "center", gap: 10,
          borderBottom: `1px solid ${TT.border}`,
        }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, color: TT.ink }}>{title}</div>
          {action && <div style={{ marginLeft: "auto" }}>{action}</div>}
        </div>
      )}
      {children}
    </div>
  );
}

/* ============================================================
   KV row for sidebar
   ============================================================ */
function KV({ label, value, mono, color }) {
  return (
    <div style={{ padding: "9px 0", display: "grid", gridTemplateColumns: "100px 1fr", alignItems: "baseline", gap: 12, borderTop: `1px solid ${TT.borderFaint}` }}>
      <div style={{ fontSize: 11.5, color: TT.inkFaint, fontWeight: 400 }}>{label}</div>
      <div style={{
        fontFamily: mono ? TT.mono : TT.ui,
        fontSize: mono ? 12 : 12.5,
        fontWeight: 500, color: color || TT.ink,
        wordBreak: "break-all",
      }}>{value}</div>
    </div>
  );
}

/* ============================================================
   Resolve form — quiet, clean, AI as subtle prefill
   ============================================================ */
function ResolveForm({ unresolvedLines, onResolve, saveMappings, setSaveMappings }) {
  const [codes, setCodes] = React.useState(
    Object.fromEntries(unresolvedLines.map((l) => [l.line, l.ai || ""])),
  );
  const [aiAccepted, setAiAccepted] = React.useState(
    Object.fromEntries(unresolvedLines.map((l) => [l.line, !!l.ai])),
  );
  const allFilled = unresolvedLines.every((l) => (codes[l.line] || "").trim().length > 0);

  return (
    <Section
      title={`Resolve ${unresolvedLines.length} missing supplier code${unresolvedLines.length === 1 ? "" : "s"}`}
      action={<span style={{ fontSize: 11.5, color: TT.inkFaint }}>AI suggestions are prefilled — review each one</span>}
    >
      <div style={{
        display: "grid", gridTemplateColumns: "40px 1.1fr 1.7fr 70px 90px 1.4fr 32px",
        padding: "11px 20px", background: TT.surface2,
        fontSize: 10.5, fontWeight: 500, textTransform: "uppercase", letterSpacing: "0.08em", color: TT.inkFaint,
        gap: 14, borderBottom: `1px solid ${TT.border}`,
      }}>
        <div>Line</div>
        <div>Buyer SKU</div>
        <div>Description</div>
        <div style={{ textAlign: "right" }}>Qty</div>
        <div style={{ textAlign: "right" }}>Price</div>
        <div>Supplier code</div>
        <div></div>
      </div>

      {unresolvedLines.map((l, idx) => {
        const value = codes[l.line] || "";
        const isAiPrefill = aiAccepted[l.line] && value === l.ai;
        return (
          <div key={l.line} style={{
            display: "grid", gridTemplateColumns: "40px 1.1fr 1.7fr 70px 90px 1.4fr 32px",
            gap: 14, padding: "16px 20px", alignItems: "center",
            borderTop: idx > 0 ? `1px solid ${TT.borderFaint}` : 0,
          }}>
            <div style={{ fontFamily: TT.mono, fontSize: 12, color: TT.inkMuted, fontWeight: 500 }}>{String(l.line).padStart(2, "0")}</div>

            <div style={{ fontFamily: TT.mono, fontSize: 12, fontWeight: 500, color: TT.ink }}>{l.buyerCode}</div>

            <div style={{ fontSize: 12.5, color: TT.ink, lineHeight: 1.45 }}>{l.desc}</div>

            <div style={{ textAlign: "right", fontFamily: TT.mono, fontSize: 12, color: TT.ink }}>{l.qty}</div>

            <div style={{ textAlign: "right", fontFamily: TT.mono, fontSize: 12, color: TT.inkMuted }}>{l.price}</div>

            <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <div style={{
                flex: 1, height: 32, position: "relative",
                border: `1px solid ${value ? TT.borderStrong : TT.amber + "55"}`,
                borderRadius: 6, background: value ? TT.surface : TT.amberSoft + "55",
                display: "flex", alignItems: "center", padding: "0 10px",
              }}>
                <input
                  value={value}
                  onChange={(e) => { setCodes({ ...codes, [l.line]: e.target.value }); setAiAccepted({ ...aiAccepted, [l.line]: false }); }}
                  placeholder="Enter code…"
                  style={{
                    flex: 1, border: 0, outline: "none", background: "transparent",
                    fontFamily: TT.mono, fontSize: 12.5, color: isAiPrefill ? TT.inkMuted : TT.ink, fontWeight: 500,
                  }}
                />
                {isAiPrefill && l.ai && (
                  <span title={`AI suggestion · ${l.aiConf}% confidence`} style={{
                    display: "inline-flex", alignItems: "center", gap: 4,
                    fontSize: 10.5, color: TT.inkFaint, fontWeight: 500,
                    letterSpacing: "0.04em",
                  }}>
                    <IC.Sparkle size={10} c={TT.inkFaint}/> {l.aiConf}%
                  </span>
                )}
              </div>
            </div>

            <div>
              <button
                onClick={() => { setCodes({ ...codes, [l.line]: "" }); setAiAccepted({ ...aiAccepted, [l.line]: false }); }}
                title="Clear"
                style={{
                  width: 26, height: 26, borderRadius: 5, background: "transparent",
                  border: 0, cursor: "pointer", color: TT.inkFaint,
                  display: "inline-flex", alignItems: "center", justifyContent: "center",
                }}><IC.Close c={TT.inkFaint}/></button>
            </div>
          </div>
        );
      })}

      {/* Resolve actions */}
      <div style={{
        padding: "14px 20px",
        background: TT.surface2, borderTop: `1px solid ${TT.border}`,
        display: "flex", alignItems: "center", gap: 14,
      }}>
        <label style={{ display: "inline-flex", alignItems: "center", gap: 9, fontSize: 12.5, color: TT.inkMuted, cursor: "pointer" }}>
          <input type="checkbox" checked={saveMappings} onChange={(e) => setSaveMappings(e.target.checked)} style={{ accentColor: TT.blue, width: 14, height: 14 }}/>
          Save as mappings for future orders
        </label>
        <div style={{ marginLeft: "auto", display: "flex", alignItems: "center", gap: 8 }}>
          <BN size="md" variant="ghost">Skip</BN>
          <BN size="md" variant="primary" onClick={onResolve} disabled={!allFilled}>Resolve and continue</BN>
        </div>
      </div>
    </Section>
  );
}

/* ============================================================
   Line items table — quiet, missing shown as italic muted text
   ============================================================ */
function LineItemsTable({ items }) {
  const totalQty = items.reduce((s, i) => s + i.qty, 0);
  return (
    <Section
      title="Line items"
      action={<span style={{ fontSize: 11.5, color: TT.inkFaint }}>
        {items.length} items{items.filter((i) => i.unresolved).length > 0 && (
          <span style={{ color: TT.amber, marginLeft: 6 }}>· {items.filter((i) => i.unresolved).length} unresolved</span>
        )}
      </span>}
    >
      <div style={{ overflowX: "auto" }}>
        <div style={{
          display: "grid",
          gridTemplateColumns: "40px 1.2fr 1.2fr 2.2fr 60px 90px 100px",
          padding: "11px 20px", background: TT.surface2,
          fontSize: 10.5, fontWeight: 500, textTransform: "uppercase", letterSpacing: "0.08em",
          color: TT.inkFaint, gap: 14,
          borderBottom: `1px solid ${TT.border}`,
        }}>
          <div>#</div>
          <div>Buyer SKU</div>
          <div>Supplier code</div>
          <div>Description</div>
          <div style={{ textAlign: "right" }}>Qty</div>
          <div style={{ textAlign: "right" }}>Unit price</div>
          <div style={{ textAlign: "right" }}>Line total</div>
        </div>

        {items.map((it, idx) => (
          <div key={it.line} style={{
            display: "grid",
            gridTemplateColumns: "40px 1.2fr 1.2fr 2.2fr 60px 90px 100px",
            padding: "13px 20px", alignItems: "center", gap: 14,
            background: TT.surface,
            borderTop: idx > 0 ? `1px solid ${TT.borderFaint}` : 0,
          }}>
            <div style={{ fontFamily: TT.mono, fontSize: 11, color: TT.inkFaint, fontWeight: 400 }}>{String(it.line).padStart(2, "0")}</div>

            <div style={{ fontFamily: TT.mono, fontSize: 12, color: TT.ink, fontWeight: 500 }}>{it.buyerCode}</div>

            <div>
              {it.supplierCode ? (
                <span style={{ fontFamily: TT.mono, fontSize: 12, color: TT.ink, fontWeight: 500 }}>{it.supplierCode}</span>
              ) : (
                <span style={{ fontSize: 12, fontStyle: "italic", color: TT.amber, display: "inline-flex", alignItems: "center", gap: 6 }}>
                  <span style={{ width: 6, height: 6, borderRadius: 999, background: TT.amber }}/>
                  Needs code
                </span>
              )}
            </div>

            <div style={{ fontSize: 12.5, color: TT.inkMuted }}>{it.desc}</div>

            <div style={{ textAlign: "right", fontFamily: TT.mono, fontSize: 12, color: TT.ink }}>{it.qty}</div>
            <div style={{ textAlign: "right", fontFamily: TT.mono, fontSize: 12, color: TT.inkMuted }}>{it.price}</div>
            <div style={{ textAlign: "right", fontFamily: TT.mono, fontSize: 12, color: TT.ink, fontWeight: 500 }}>{it.lineTotal}</div>
          </div>
        ))}

        {/* Totals row */}
        <div style={{
          display: "grid",
          gridTemplateColumns: "40px 1.2fr 1.2fr 2.2fr 60px 90px 100px",
          padding: "14px 20px", alignItems: "center", gap: 14,
          background: TT.surface, borderTop: `1px solid ${TT.border}`,
        }}>
          <div></div><div></div><div></div>
          <div style={{ fontSize: 11.5, color: TT.inkFaint, fontWeight: 500 }}>Total</div>
          <div style={{ textAlign: "right", fontFamily: TT.mono, fontSize: 12, color: TT.inkMuted }}>{totalQty}</div>
          <div></div>
          <div style={{ textAlign: "right", fontFamily: TT.mono, fontSize: 14, color: TT.ink, fontWeight: 600 }}>€ 4,436.73</div>
        </div>
      </div>
    </Section>
  );
}

/* ============================================================
   Sidebar — three quiet cards
   ============================================================ */
function OrderSidebar({ order }) {
  return (
    <div style={{ width: 304, flexShrink: 0 }}>
      <Section title="Details">
        <div style={{ padding: "4px 18px 14px" }}>
          <KV label="Order date" value={order.orderDate}/>
          <KV label="Reference" value={order.reference}/>
          <KV label="Currency" value={order.currency} mono/>
          <KV label="Incoterm" value={order.incoterm} mono/>
          <KV label="Payment" value={order.paymentTerms}/>
          <KV label="Ship to" value={order.delivery.split("·")[1].trim()}/>
          <KV label="By" value={order.delivery.split("·")[0].trim()}/>
        </div>
      </Section>

      <Section title="Counterparties">
        <div style={{ padding: "14px 18px" }}>
          <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
            <span style={{ width: 8, height: 8, borderRadius: 999, background: TT.blue, flexShrink: 0 }}/>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 10.5, color: TT.inkFaint, letterSpacing: "0.06em", textTransform: "uppercase", fontWeight: 500 }}>Buyer</div>
              <div style={{ fontSize: 13, fontWeight: 500, color: TT.ink, marginTop: 2 }}>{order.buyer}</div>
              <div style={{ fontSize: 11, color: TT.inkFaint, fontFamily: TT.mono, marginTop: 2 }}>{order.buyerCode}</div>
            </div>
          </div>
          <div style={{ height: 1, background: TT.borderFaint, margin: "14px 0" }}/>
          <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
            <span style={{ width: 8, height: 8, borderRadius: 999, background: TT.green, flexShrink: 0 }}/>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 10.5, color: TT.inkFaint, letterSpacing: "0.06em", textTransform: "uppercase", fontWeight: 500 }}>Supplier</div>
              <div style={{ fontSize: 13, fontWeight: 500, color: TT.ink, marginTop: 2 }}>{order.supplier}</div>
              <div style={{ fontSize: 11, color: TT.inkFaint, fontFamily: TT.mono, marginTop: 2 }}>{order.supplierCode}</div>
            </div>
          </div>
          <div style={{ height: 1, background: TT.borderFaint, margin: "14px 0" }}/>
          <div style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 12, color: TT.inkMuted }}>
            <span style={{ fontSize: 10.5, color: TT.inkFaint, letterSpacing: "0.06em", textTransform: "uppercase", fontWeight: 500 }}>Output</span>
            <SC type="cXML"/>
            <span style={{ fontFamily: TT.mono, fontSize: 11.5, color: TT.ink }}>{order.output.template}</span>
          </div>
        </div>
      </Section>

      <Section title="Activity" action={
        <button style={{ background: "transparent", border: 0, color: TT.inkMuted, fontSize: 11.5, cursor: "pointer" }}>View all</button>
      }>
        <div style={{ padding: "8px 18px 14px" }}>
          {order.activity.map((a, i) => {
            const dotColor = { ok: TT.green, warn: TT.amber, err: TT.danger, ai: TT.inkFaint }[a.icon] || TT.inkFaint;
            return (
              <div key={i} style={{
                display: "grid", gridTemplateColumns: "14px 1fr auto", gap: 10,
                padding: "10px 0", alignItems: "flex-start",
                borderTop: i > 0 ? `1px solid ${TT.borderFaint}` : 0,
              }}>
                <span style={{ width: 7, height: 7, borderRadius: 999, background: dotColor, marginTop: 5 }}/>
                <div>
                  <div style={{ fontSize: 12.5, color: TT.ink, lineHeight: 1.4 }}>{a.t}</div>
                  <div style={{ fontSize: 11, color: TT.inkFaint, marginTop: 2 }}>{a.who}</div>
                </div>
                <div style={{ fontSize: 11, color: TT.inkFaint, whiteSpace: "nowrap", marginTop: 1 }}>{a.when}</div>
              </div>
            );
          })}
        </div>
      </Section>
    </div>
  );
}

/* ============================================================
   Order header — generous whitespace, one primary action
   ============================================================ */
function OrderHeader({ order, onBack }) {
  return (
    <div style={{ padding: "28px 0 22px" }}>
      <button onClick={onBack} style={{
        display: "inline-flex", alignItems: "center", gap: 8,
        background: "transparent", border: 0, padding: "0 0 16px",
        color: TT.inkMuted, fontSize: 12, fontWeight: 500, cursor: "pointer",
      }}>
        <IC.ArrowLeft c={TT.inkMuted}/> Back to orders
      </button>

      <div style={{ display: "flex", alignItems: "flex-end", gap: 18, flexWrap: "wrap" }}>
        <div style={{ flex: "1 1 360px", minWidth: 0 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 12, marginBottom: 8, flexWrap: "wrap" }}>
            <h1 style={{ margin: 0, fontFamily: TT.display, fontSize: 30, fontWeight: 600, letterSpacing: "-0.022em", lineHeight: 1, color: TT.ink, wordBreak: "break-all" }}>
              {order.po}
            </h1>
            <SP status={order.status}/>
          </div>
          <div style={{ fontSize: 12.5, color: TT.inkMuted }}>
            <span style={{ color: TT.ink, fontWeight: 500 }}>{order.buyer}</span>
            <span style={{ margin: "0 8px", color: TT.borderStrong }}>→</span>
            <span style={{ color: TT.ink, fontWeight: 500 }}>{order.supplier}</span>
            <span style={{ margin: "0 10px", color: TT.borderStrong }}>·</span>
            Created {order.created}
          </div>
        </div>

        <div style={{ display: "flex", alignItems: "center", gap: 8, flexShrink: 0 }}>
          <BN size="md" variant="ghost" icon={<IC.Doc c={TT.inkMuted}/>}>View source</BN>
          <BN size="md" variant="secondary">Save draft</BN>
          <BN size="md" variant="primary" disabled>Cross the bridge</BN>
        </div>
      </div>
    </div>
  );
}

/* ============================================================
   Order metadata strip — quiet single-line key facts
   ============================================================ */
function MetaStrip({ order }) {
  const cells = [
    { label: "Source",   value: <span style={{ display: "inline-flex", alignItems: "center", gap: 8 }}><SC type={order.src}/><span style={{ fontSize: 12, color: TT.inkMuted, fontFamily: TT.mono }}>{order.source.filename}</span></span> },
    { label: "Lines",    value: <span style={{ fontFamily: TT.mono, fontSize: 13, color: TT.ink }}>{order.lineItems.length}</span> },
    { label: "Total",    value: <span style={{ fontFamily: TT.display, fontSize: 17, color: TT.ink, fontWeight: 600, letterSpacing: "-0.015em" }}>{order.total}</span> },
    { label: "Currency", value: <span style={{ fontFamily: TT.mono, fontSize: 12, color: TT.ink }}>{order.currency}</span> },
    { label: "Status",   value: <span style={{ fontSize: 12.5, color: TT.ink }}>{["Parsing","Normalizing","Validating","Transforming","Delivering"][order.stage]} <span style={{ color: TT.inkFaint, marginLeft: 4 }}>· {order.stage + 1} of 5</span></span> },
  ];
  return (
    <div style={{
      background: TT.surface, border: `1px solid ${TT.border}`, borderRadius: 10,
      padding: "16px 22px", marginBottom: 18,
      display: "grid", gridTemplateColumns: "1.5fr 0.6fr 0.9fr 0.6fr 1.2fr", gap: 24,
    }}>
      {cells.map((c, i) => (
        <div key={i} style={{ borderLeft: i > 0 ? `1px solid ${TT.borderFaint}` : "none", paddingLeft: i > 0 ? 24 : 0 }}>
          <div style={{ fontSize: 10.5, color: TT.inkFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 500, marginBottom: 8 }}>{c.label}</div>
          <div>{c.value}</div>
        </div>
      ))}
    </div>
  );
}

/* ============================================================
   OrderDetailScreen
   ============================================================ */
function OrderDetailScreen({ onBack }) {
  const order = ORDER_DETAIL;
  const [saveMappings, setSaveMappings] = React.useState(true);
  const [resolved, setResolved] = React.useState(false);
  const unresolved = order.lineItems.filter((l) => l.unresolved);

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", minWidth: 0, minHeight: 0, background: TT.bg }}>
      <div style={{ flex: 1, overflow: "auto" }}>
        <div style={{ maxWidth: 1280, margin: "0 auto", padding: "0 36px 40px" }}>
          <OrderHeader order={order} onBack={onBack}/>

          <MetaStrip order={order}/>

          <div style={{ display: "grid", gridTemplateColumns: "1fr 304px", gap: 22, alignItems: "flex-start" }}>
            <div style={{ minWidth: 0 }}>
              {!resolved && unresolved.length > 0 && (
                <ResolveForm
                  unresolvedLines={unresolved}
                  onResolve={() => setResolved(true)}
                  saveMappings={saveMappings}
                  setSaveMappings={setSaveMappings}
                />
              )}

              {resolved && (
                <div style={{
                  marginBottom: 14, padding: "14px 18px",
                  background: TT.surface, border: `1px solid ${TT.border}`, borderRadius: 10,
                  display: "flex", alignItems: "center", gap: 12,
                }}>
                  <span style={{ width: 8, height: 8, borderRadius: 999, background: TT.green }}/>
                  <div style={{ flex: 1 }}>
                    <div style={{ fontWeight: 500, fontSize: 13, color: TT.ink }}>All lines resolved · ready to cross</div>
                    <div style={{ fontSize: 11.5, color: TT.inkMuted, marginTop: 2 }}>Mappings saved to library · Heinrich → Acme</div>
                  </div>
                  <BN size="sm" variant="ghost" onClick={() => setResolved(false)}>Undo</BN>
                  <BN size="sm" variant="primary">Cross the bridge</BN>
                </div>
              )}

              <LineItemsTable items={order.lineItems}/>
            </div>

            <OrderSidebar order={order}/>
          </div>
        </div>
      </div>
    </div>
  );
}

window.OrderDetailScreen = OrderDetailScreen;
