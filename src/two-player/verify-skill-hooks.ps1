param([string]$GameDirectory=$env:SHADOW_GAMBIT_GAME_DIR, [string]$Plugin=(Join-Path $PSScriptRoot 'staging/ShadowGambitCoop.MoveProbe.all-skills-dev.dll'))
$ErrorActionPreference='Stop'
$game=$GameDirectory
if(!$game){throw 'GameDirectory is required for the local native interface audit.'}
Add-Type -Path (Join-Path $game 'BepInEx/core/Mono.Cecil.dll')
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new()
foreach($folder in 'BepInEx/interop','BepInEx/core','dotnet'){$resolver.AddSearchDirectory((Join-Path $game $folder))}
$parameters=[Mono.Cecil.ReaderParameters]::new();$parameters.AssemblyResolver=$resolver
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin,$parameters)
$families=@('ControlReleaseInput','PlanningSync','TrainingSession','TrainingDeckTrace','StoryObservation','GameSpeedSync','GuestWorldMap','GuestMissionPreparation','GuestLoadMenuGuard','NativeSkillHooks','SkillLifecycleTrace','BaitObjectSync','MimicChoice','GuestSkillPreview','SkillResourceSync','TimeFreezeSync','EffectProcessClock','BlindSync','FrozenSync','FrozenProcessClock','FrozenDuelSync','DigStateSync','CannonStateSync','CannonLaunchSync','CannonFlightSync','FishingPullSync','FishingItemSync')
$failures=[Collections.Generic.List[string]]::new();$count=0
try {
 $registry=$assembly.MainModule.Types | Where-Object Name -eq 'NativeSkillHooks'
 if(!$registry){throw 'Native skill hook registry is missing from the built DLL'}
 $init=$registry.Methods | Where-Object Name -eq '.cctor'
 $registered=@($init.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldtoken' -and $_.Operand -is [Mono.Cecil.TypeReference]} | ForEach-Object {$_.Operand.FullName})
 $declared=@($assembly.MainModule.Types | Where-Object Name -in $families | ForEach-Object NestedTypes | Where-Object {@($_.CustomAttributes | Where-Object {$_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch'}).Count})
 foreach($patch in $declared) {
  if(@($registered | Where-Object {$_ -eq $patch.FullName}).Count -ne 1){$failures.Add('Missing or duplicate registration: '+$patch.FullName)}
  $attribute=$patch.CustomAttributes | Where-Object {$_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch'}
  $arguments=$attribute.ConstructorArguments
  if($arguments.Count -lt 2){$failures.Add('Unresolved dynamic patch target: '+$patch.FullName);continue}
  $targetType=$arguments[0].Value.Resolve();$targetName=[string]$arguments[1].Value
  if($arguments[1].Type.FullName -eq 'HarmonyLib.MethodType') {
   $failures.Add('Non-method IL2CPP hook is unsupported by the installed native patch backend: '+$patch.FullName);continue
  }
  $explicitParameters=$arguments.Count -ge 3
  $wanted=@();if($explicitParameters){$wanted=@($arguments[2].Value | ForEach-Object {$_.Value.FullName})}
  $targetMethods=@($targetType.Methods | Where-Object {$_.Name -eq $targetName -and (!$explicitParameters -or (($_.Parameters | ForEach-Object {$_.ParameterType.FullName}) -join '|') -ceq ($wanted -join '|'))})
  if($targetMethods.Count -ne 1){$failures.Add("Ambiguous or missing native method: $($patch.FullName) -> $($targetType.FullName).$targetName ($($targetMethods.Count) candidates)");continue}
  $original=$targetMethods[0]
  # This installed bridge emits ldind.i for native by-ref inputs. Managed
  # Prefix ref __result is fine; native ref float/other value inputs are not.
  foreach($argument in $original.Parameters) {
   if($argument.ParameterType.IsByReference -and $argument.ParameterType.GetElementType().IsValueType){$failures.Add('Unsupported native by-ref value argument: '+$original.FullName)}
  }
  if(@($original.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldstr' -and $_.Operand -like '*unstripping*'}).Count){$failures.Add('Native method is a throw stub: '+$original.FullName)}
  foreach($hook in $patch.Methods | Where-Object {$_.Name -in @('Prefix','Postfix','Finalizer')}) {
   foreach($parameter in $hook.Parameters) {
    $expected=$null
    if($parameter.Name -eq '__instance'){$expected=$targetType.FullName;if($original.IsStatic){$failures.Add('Static native method cannot provide __instance: '+$hook.FullName)}}
    elseif($parameter.Name -eq '__result'){$expected=$original.ReturnType.FullName}
    elseif($parameter.Name -match '^__(\d+)$') {
     $index=[int]$Matches[1]
     if($index -ge $original.Parameters.Count){$failures.Add('Missing native argument for '+$hook.FullName);continue}
     $expected=$original.Parameters[$index].ParameterType.FullName.TrimEnd('&')
    }
    if($expected -and $parameter.ParameterType.FullName.TrimEnd('&') -cne $expected){$failures.Add('Hook argument type mismatch: '+$hook.FullName+' '+$parameter.Name+' expected '+$expected)}
   }
  }
  $count++
 }
 foreach($type in $registered){if($type -notin @($declared.FullName)){$failures.Add('Unreviewed hook in skill registry: '+$type)}}
 $install=$registry.Methods | Where-Object Name -eq 'Install'
 $calls=@($install.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]} | ForEach-Object {$_.Operand.FullName})
 if(!($calls -match 'HarmonyLib.Harmony::PatchAll\(System.Type\)')){$failures.Add('Registry never installs its patch types')}
 if(!($calls -match 'HarmonyLib.Harmony::UnpatchSelf\(\)')){$failures.Add('Registry has no failure rollback')}
 $pluginType=$assembly.MainModule.Types | Where-Object Name -eq 'MoveProbePlugin'
 $load=$pluginType.Methods | Where-Object Name -eq 'Load'
 $entry=@($load.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -eq 'System.Boolean NativeSkillHooks::Install()'})
 if($entry.Count -ne 2){$failures.Add('Plugin Load is missing its normal or isolated-smoke native skill registration path')}
 if($failures.Count){throw ($failures -join [Environment]::NewLine)}
 "Built DLL skill hook audit passed: $count registered hooks; native target overloads and callback argument types checked. Does not execute native detours."
}finally{$assembly.Dispose();$resolver.Dispose()}
