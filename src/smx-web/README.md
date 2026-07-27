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
| Intake interview | `/new`, `/new/:sessionId` | `POST/GET /intake-sessions`, `…/messages` (SSE), `…/attachments`, `GET /intake-questions` |
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
composer are **disabled**. Gates are operator-signed records, and **no screen is wired to the
backend's determination/approval endpoints**; making the buttons clickable would fake a signature.
The agent panel has no chat or streaming endpoint to talk to.

The matrix's evidence panel does **read** the Regulatory agent's *proposed* determination
(`proposedDetermination` / `proposedReason`) beside the operator's own — never as it. The proposal is
real agent output, so it carries no `MockBadge`; it is a pre-fill so the operator **confirms** rather
than authors, and it is deliberately rendered *below* the evidence, in the agent's colour, with the
operator's signature line separate and empty until they sign. A UI that collapses those two fields
into one is the agent signing the gate. See `src/domain/proposal.ts`.

## No fixtures, no interceptor

There is no `src/mocks/`, no MSW, and no demo project. Every screen reads a real endpoint, and a
screen with no data says so rather than showing invented data — a fabricated verdict must never be
able to pass for an agent-produced one, and a badge asking the operator to remember which is which is
a weaker guarantee than not shipping the fabrication at all.

If a screen needs data the backend does not serve yet, add the endpoint. Do not add a fixture.

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
