#!/bin/bash
# Restores, builds and tests the whole solution; writes build.log and test.log next to this script.
cd "$(dirname "$0")"
export PATH="$PATH:/usr/local/share/dotnet:$HOME/.dotnet:/opt/homebrew/bin"
{
  echo "== dotnet SDKs =="; dotnet --list-sdks
  echo "== build =="; dotnet build SmartX.slnx -nologo 2>&1
  echo "BUILD_EXIT=$?"
} > build.log 2>&1
dotnet test SmartX.Tests -nologo > test.log 2>&1
echo "TEST_EXIT=$?" >> test.log
echo "Done — see build.log and test.log"
