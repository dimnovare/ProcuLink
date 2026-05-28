# Integration Platform Submission Guide

## Zapier

**Status:** Ready for Zapier Developer Platform review when API is live.

### Pre-submission checklist
- [ ] Zapier developer account at https://developer.zapier.com
- [ ] App definition: `zapier-app.json` (this directory)
- [ ] Test API key created in ProcuLink → Settings → API Keys
- [ ] Test org slug noted from same screen
- [ ] Zapier CLI: `npm install -g zapier-platform-cli`

### Submission steps
1. `zapier register "ProcuLink"` in the Zapier SDK project directory
2. `zapier push` to upload
3. Test each trigger + action in the Zapier editor with a real org slug + key
4. Submit for Zapier review via Developer Platform dashboard

### Notes
- Webhook triggers use Zapier REST hook pattern (subscribe/unsubscribe via `/api/integrations`)
- Auth: custom `X-ProcuLink-Key` header, not OAuth2
- Org slug is stable — found in ProcuLink → Settings → API Keys

---

## Make.com (formerly Integromat)

**Status:** Ready for Make.com partner review when API is live.

### Pre-submission checklist
- [ ] Make.com partner account at https://partners.make.com
- [ ] Connector JSON: `make-connector.json` (this directory)
- [ ] Test connection verified via `GET /api/ingress/{slug}/ping`

### Submission steps
1. Log in to Make.com Partner Portal
2. Create new connector, paste `make-connector.json`
3. Verify all triggers fire correctly with a test scenario
4. Submit for Make.com review

---

## Webhook Security

All outbound events from ProcuLink carry:
- `X-ProcuLink-Signature: sha256=<hex>` — HMAC-SHA256(secret, payload_bytes)
- `X-ProcuLink-Event: <event-type>` — e.g. `order.created`

To verify in Python:
```python
import hmac, hashlib
expected = hmac.new(secret.encode(), payload_bytes, hashlib.sha256).hexdigest()
assert f"sha256={expected}" == request.headers["X-ProcuLink-Signature"]
```

To verify in Node.js:
```js
const crypto = require('crypto');
const sig = crypto.createHmac('sha256', secret).update(rawBody).digest('hex');
assert.strictEqual(`sha256=${sig}`, req.headers['x-proculink-signature']);
```

---

## Supported Events

| Event | When it fires |
|---|---|
| `order.created` | A new PO is uploaded or received via inbound API |
| `order.delivered` | PO successfully delivered to the supplier |
| `order.failed` | PO delivery failed after all retry attempts |

---

## Inbound API (push orders into ProcuLink)

`POST /api/ingress/{slug}/orders`  
Auth: `X-ProcuLink-Key: plk_...`

```json
{
  "supplierId": "uuid-or-external-id",
  "orderNumber": "PO-12345",
  "currency": "EUR",
  "lines": [
    { "buyerItemCode": "PART-001", "description": "Widget", "quantity": 100, "unit": "EA", "unitPrice": 5.50 }
  ]
}
```

Test the connection first:
`GET /api/ingress/{slug}/ping`  
Returns `200 OK` with `{ "message": "ProcuLink inbound API OK", "slug": "...", "timestamp": "..." }`
