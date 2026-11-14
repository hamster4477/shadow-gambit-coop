$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$source="using System;`nusing System.Collections.Generic;`nusing System.IO;`nusing System.Security.Cryptography;`nusing System.Text;`nusing System.Text.Json;`nusing System.Linq;`nusing Mimimi.CharacterSelection;`nusing UnityEngine;`n"
 $source+="using HarmonyLib;`n"
foreach($name in 'PeerPace.cs','MultiPeerContext.cs','LobbyProtocol.cs','LobbyDeployment.cs','LobbySelectionHooks.cs','LobbyHost.cs'){$source+=([IO.File]::ReadAllText((Join-Path $root $name)) -replace '(?m)^using [^;]+;\r?\n','')+"`n"}
$source+=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LobbyHostTests.cs'))
$source+="`ninternal static class SceneLookup { internal static T Find<T>()where T:class=>UnityEngine.Object.FindObjectOfType<T>(); }`ninternal static class GameSpeedSync {internal static float? Capture()=>1f;internal static bool? CaptureEffect()=>false;internal static float? CaptureAlarm()=>0;}`n"
Add-Type -TypeDefinition $source
"Native lobby host checks passed: $([LobbyHostTests]::Run((Join-Path $root 'staging'))). Actual host adapter, protocol and files; original UI APIs simulated."
