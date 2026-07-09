# smx-web

The SMX operator frontend. React + Vite + TypeScript, English/LTR, styled from the design tokens in
[`project_files/mockups_*.html`](../../project_files/).

```
npm install
npm run dev        # http://localhost:5173, proxies /api -> http://localhost:5169
npm run build      # tsc --noEmit && vite build
npm test           # vitest
```

## What is real and what is mocked

The backend ([`src/Smx.Backend`](../Smx.Backend)) serves four routes. Only three screens are backed
by them:

| Screen | Route | Endpoint |
| --- | --- | --- |
| Intake form | `/new` | `POST /projects` |
| Project / stage spine | `/p/:id/intake` | `GET /projects/{id}` (polled while a stage runs) |
| Compatibility matrix | `/p/:id/matrix` | `GET /projects/{id}/matrix` (+ `?format=xlsx`) |

Every other screen — Background, Discovery's candidate tiers, the Regulatory gate, Dosing, Cost, the
Decision matrix, and the three cross-project surfaces — renders **fixture data** and carries a
`MockBadge`.

**The badge is load-bearing.** SMX exists because a wrong marker recommendation causes real-world
harm, and every real verdict traces to a cited source. A fabricated verdict that renders identically
to an agent-produced one is precisely the failure the badge prevents. Do not remove a badge from a
screen until that screen reads from a real endpoint.

For the same reason the gate controls (Regulatory approval, VP R&D approval) and the agent chat
composer are **disabled**. Gates are operator-signed records and there is no endpoint to sign one;
making the buttons clickable would fake a signature. The agent panel has no chat or streaming
endpoint to talk to.

## MSW

`src/mocks/` serves the reserved project id `proj-demo` from fixtures so the matrix can be
demonstrated while the Claude Foundry deployment stays param-gated off (`deployClaude=false`) and no
real project can produce one. **Real project ids pass straight through to the backend** — a mock must
never stand between the operator and a real verdict.

MSW is started only under `import.meta.env.DEV`, and `publicDir` is disabled for production builds so
`mockServiceWorker.js` cannot ship. `handlers.ts` and `fixtures/demoMatrix.ts` are tree-shaken from
`dist/`.

When the backend grows an endpoint for a mocked stage, replace that screen's fixture import with a
real client call and delete the fixture — do not add a handler.

## CORS

There is none, and none is needed. In dev, Vite's proxy makes `/api/*` same-origin; in Azure,
Application Gateway's `apiPathRule` routes `/api/*` to the backend container, also same-origin.

## Types

`src/api/types.ts` mirrors the C# records in [`src/Smx.Domain/Records`](../Smx.Domain/Records) —
camelCase fields, enums as strings, nulls omitted. `GET /projects/{id}` returns a *projection*
(`projectId`, `client`, `product`, `stages`), not the whole `ProjectDoc`.

`src/domain/matrix.ts` reimplements the worst-wins fold from `VerdictDoc.Fold` so the UI can assert a
cell's `overall` agrees with its own dimensions; a mismatch renders a loud inconsistency banner.

## Deploy

```
infra/scripts/build-images.sh <env>            # cloud build; tags with the short git SHA
infra/scripts/deploy.sh <env> -p frontendImage=<acr>.azurecr.io/smx-frontend:<tag>
```

The `frontend` Container App already exists (`infra/modules/compute.bicep`, `targetPort: 80`,
internal ingress) and defaults to a placeholder image.

Pass the image through the `frontendImage` Bicep parameter, not `swap-images.sh`. The swap script
mutates only the live Container App, so the next `deploy.sh` reconciles it back to the placeholder
declared in Bicep. Use `swap-images.sh <env> frontend <image>` only as a stopgap when you cannot run
a full deploy, and follow up with a real one.
