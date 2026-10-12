# Product list `isActive` filter

## 1. Summary

`GET /api/products` accepts an optional `isActive` query parameter so a client can request only active (or only inactive) products. The change is additive and not breaking.

## 2. Changed endpoints or behaviour

- `GET /api/products` only. No other route, DTO or status code changes.

## 3. Request/query contract

| Parameter | Type | Behaviour |
| --- | --- | --- |
| `isActive` | boolean, optional | Omitted: active and inactive products (unchanged). `true`: active only. `false`: inactive only. |
| `search` | string, optional, <=200 | Unchanged (name or CN code). Combined with `isActive` using AND. |
| `page`, `pageSize` | int | Unchanged (default 1 / 25, `pageSize` 1-100). |

`true`/`false` are accepted case-insensitively. The filter is applied before `totalCount`, ordering and paging; ordering stays `name`, then `id`. Results stay limited to the caller's organization.

Example, product lookup for the shipment form:

```
GET /api/products?isActive=true&search=cof&page=1&pageSize=20
```

## 4. Response contract

Unchanged `PageResult<Product>`; `totalCount` is the number of products matching all filters.

```json
{
  "items": [
    { "id": "6f1c0f0e-0000-0000-0000-000000000001", "name": "Arabica Coffee", "cnCode": "0901", "commodityType": "Coffee", "isActive": true }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

(Item fields abbreviated; the product shape is unchanged.)

## 5. Error/status behaviour

A non-boolean value (for example `isActive=abc`) returns 400 ProblemDetails (title "Validation failed", no per-field `errors`), the same as an invalid `page`.

## 6. Breaking-change notes

None. Requests without `isActive` behave exactly as before.

## 7. Required frontend adaptations

Optional: the shipment form's product lookup should send `isActive=true`. No other client change is needed.

## 8. Compatibility risks

- A client that already sends a malformed `isActive` value (previously ignored) now receives 400.
- The filter only narrows the list. Shipment create/update and CSV import still accept an inactive product ID, so the lookup filter is not a server-side guarantee.

## 9. Out of scope

- Rejecting inactive products on shipment create/update or import (a separate domain decision).
- Filtering suppliers or shipments by active state.

## 10. Backend verification performed

See the final report of this change for the commands and results (focused `HttpContractTests` and `scripts/verify.ps1`).
