#!/usr/bin/env bash
# Creates a resource group, a Document Intelligence (S0) resource and a storage account, then prints
# the environment variables docintel needs. Requires: az login. Untested helper - review before running.
# Cleanup: az group delete -n "$RG" --yes
set -euo pipefail

LOCATION="${LOCATION:-eastus}"          # choose a region that supports custom neural models
RG="${RG:-rg-docintel-lab}"
SUFFIX="${SUFFIX:-$RANDOM}"
DI_NAME="docintel-lab-$SUFFIX"
SA_NAME="docintellab$SUFFIX"          # 3-24 lowercase letters/digits

az group create -n "$RG" -l "$LOCATION" -o none
az cognitiveservices account create -n "$DI_NAME" -g "$RG" -l "$LOCATION" \
  --kind FormRecognizer --sku S0 --custom-domain "$DI_NAME" --yes -o none
az storage account create -n "$SA_NAME" -g "$RG" -l "$LOCATION" \
  --sku Standard_LRS --allow-blob-public-access false -o none

ENDPOINT=$(az cognitiveservices account show -n "$DI_NAME" -g "$RG" --query properties.endpoint -o tsv)
KEY=$(az cognitiveservices account keys list -n "$DI_NAME" -g "$RG" --query key1 -o tsv)
CONN=$(az storage account show-connection-string -n "$SA_NAME" -g "$RG" --query connectionString -o tsv)

cat <<OUT

export DOCINTEL_ENDPOINT="$ENDPOINT"
export DOCINTEL_KEY="$KEY"
export STORAGE_CONNECTION_STRING="$CONN"
OUT
