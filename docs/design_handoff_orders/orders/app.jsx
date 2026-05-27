// orders/app.jsx — top-level shell with router state

const { Sidebar, Topbar, T } = window.PROCU;

function App() {
  const [t, setTweak] = window.useTweaks(window.TWEAK_DEFAULTS);

  const [view, setView] = React.useState(t.view || "orders");
  const [openOrder, setOpenOrder] = React.useState(null);

  React.useEffect(() => { setView(t.view); }, [t.view]);

  const counts = (() => {
    const o = window.PROCU_DATA.ORDERS;
    return {
      review: o.filter((x) => x.status === "review").length,
      failed: o.filter((x) => x.status === "failed").length,
    };
  })();

  return (
    <div style={{ display: "flex", height: "100vh", width: "100vw", overflow: "hidden", background: T.bg }}>
      <Sidebar
        active={view === "detail" ? "orders" : "orders"}
        onNav={(k) => { setView(k === "orders" ? "orders" : k); setOpenOrder(null); }}
        counts={counts}
      />
      <div style={{ flex: 1, display: "flex", flexDirection: "column", minWidth: 0 }}>
        <Topbar crumb={
          view === "detail" ? (
            <>
              <span style={{ color: T.navyMuted, cursor: "pointer" }} onClick={() => { setView("orders"); setOpenOrder(null); }}>Orders</span>
              <span style={{ color: T.navyMuted }}>›</span>
              <span style={{ color: "white", fontWeight: 500, fontFamily: T.mono, fontSize: 12.5 }}>{openOrder?.po || window.PROCU_DATA.ORDER_DETAIL.po}</span>
            </>
          ) : (
            <span style={{ color: "white", fontWeight: 500 }}>Orders</span>
          )
        }/>
        {view === "detail" ? (
          <window.OrderDetailScreen onBack={() => { setView("orders"); setOpenOrder(null); }}/>
        ) : (
          <window.OrdersListScreen
            density={t.density}
            onOpen={(o) => { setOpenOrder(o); setView("detail"); }}
          />
        )}
      </div>

      {/* Tweaks panel */}
      {window.TweaksPanel && (
        <window.TweaksPanel>
          <window.TweakSection label="View"/>
          <window.TweakRadio
            label="Screen"
            value={t.view}
            onChange={(v) => setTweak("view", v)}
            options={[
              { value: "orders", label: "List" },
              { value: "detail", label: "Detail" },
            ]}
          />
          <window.TweakSection label="List density"/>
          <window.TweakRadio
            label="Rows"
            value={t.density}
            onChange={(v) => setTweak("density", v)}
            options={[
              { value: "compact", label: "Compact" },
              { value: "cozy", label: "Cozy" },
            ]}
          />
        </window.TweaksPanel>
      )}
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App/>);
