#!/bin/sh
set -u
# This checks the verified listener, not whether an operator has unsealed it.
bao status -format=json >/dev/null
status=$?
case "$status" in
  0|2) exit 0 ;;
  *) exit "$status" ;;
esac
