#!/usr/bin/env bash
# Ad-hoc Cosmos data-plane reader over Entra (no keys — local auth is disabled).
# Requires: az login as an identity holding a Cosmos SQL data role, and the account's
# public network access enabled for your IP (or run from inside the VNet).
#   Usage: cosmos-read.sh <account> [database] [container] [max]
#     account    e.g. cosmos-smx-dev-lmxnb
#     database   default: smx
#     container  omit to list databases+containers; provide to dump docs
#     max        max docs to print (default 5)
set -euo pipefail
ACCT="${1:?account name, e.g. cosmos-smx-dev-lmxnb}"
DB="${2:-smx}"
COLL="${3:-}"
MAX="${4:-5}"
HOST="https://${ACCT}.documents.azure.com"

TOKEN="$(az account get-access-token --resource https://cosmos.azure.com --query accessToken -o tsv)"
# Cosmos AAD REST: Authorization = url-encoded "type=aad&ver=1.0&sig=<oauth-token>"
urlenc() { python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.stdin.read().strip(),safe=""))'; }
AUTH="type%3Daad%26ver%3D1.0%26sig%3D$(printf '%s' "$TOKEN" | urlenc)"
DATE="$(date -u +'%a, %d %b %Y %H:%M:%S GMT' | tr '[:upper:]' '[:lower:]')"
hdr=(-H "authorization: ${AUTH}" -H "x-ms-version: 2018-12-31" -H "x-ms-date: ${DATE}")

if [ -z "$COLL" ]; then
  echo "=== databases ==="
  curl -s "${hdr[@]}" "${HOST}/dbs" | python3 -m json.tool
  echo "=== containers in '${DB}' ==="
  curl -s "${hdr[@]}" "${HOST}/dbs/${DB}/colls" | python3 -c 'import sys,json;[print(" -",c["id"]) for c in json.load(sys.stdin).get("DocumentCollections",[])]'
else
  echo "=== up to ${MAX} docs from ${DB}/${COLL} (ReadFeed) ==="
  # ReadFeed (plain GET) — the gateway serves this cross-partition; a raw SQL query would need
  # the partition-range planning the .NET SDK does for you.
  curl -s "${hdr[@]}" -H "x-ms-max-item-count: ${MAX}" \
    "${HOST}/dbs/${DB}/colls/${COLL}/docs" | python3 -m json.tool
fi
