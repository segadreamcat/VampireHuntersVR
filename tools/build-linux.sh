#!/bin/bash
G="/mnt/user-data/uploads/Vampire Hunters"
R=()
for f in "$G"/dotnet/*.dll "$G"/BepInEx/core/*.dll "$G"/BepInEx/interop/*.dll; do R+=("-r:$f"); done
dotnet /usr/lib/dotnet/sdk/8.0.131/Roslyn/bincore/csc.dll -nologo -noconfig -nostdlib -target:library -optimize+ -unsafe -langversion:latest \
  -nowarn:CS0618,CS0649,CS0414,CS8632 -out:build/VampireHuntersVR.dll "${R[@]}" src/*.cs
