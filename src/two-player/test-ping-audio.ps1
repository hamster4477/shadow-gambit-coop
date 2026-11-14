$ErrorActionPreference='Stop'
# The bell WAV is a user-provided test sound and is NOT distributed under the MIT
# grant, so this check only runs when someone drops their own copy in place.
$bell=Join-Path $PSScriptRoot 'media/bell_b.wav'
if(!(Test-Path -LiteralPath $bell -PathType Leaf)){
    'Skipped: media/bell_b.wav is not distributed with this repository. Add your own PCM16 bell WAV there to run this decode check.'
    exit 0
}
$s=Get-Content (Join-Path $PSScriptRoot 'CoopPings.cs') -Raw
$start=$s.IndexOf('    private static AudioClip ReadBell(')
$brace=$s.IndexOf('{',$start);$depth=1;$end=$brace+1
while($depth -gt 0){if($s[$end] -eq '{'){$depth++};if($s[$end] -eq '}'){$depth--};$end++}
$method=$s.Substring($start,$end-$start)
$fixture=@'
using System;using System.IO;
class AudioClip {public int rate,channels,frames;public float[] data;public static AudioClip Create(string n,int f,int c,int r,bool stream){if(stream)throw new Exception();return new AudioClip{rate=r,channels=c,frames=f};}public bool SetData(float[] d,int off){data=d;return off==0;}}
namespace UnityEngine {static class Object {public static void Destroy(AudioClip c){}}}
public class PingAudioTest {
'@
$fixture+=$method
$fixture+=@'
 public static void Run(string path){var c=ReadBell(path);if(c.rate!=48000||c.channels!=1||c.frames!=21813||c.data.Length!=21813)throw new Exception("Bell metadata mismatch");bool sound=false;foreach(float s in c.data){if(!float.IsFinite(s)||Math.Abs(s)>1)throw new Exception("Invalid sample");if(Math.Abs(s)>.01)sound=true;}if(!sound)throw new Exception("Silent bell");}
}
'@
Add-Type -TypeDefinition $fixture
[PingAudioTest]::Run($bell)
'Provided bell WAV decoded by production method: mono PCM16, 48kHz, 21813 valid non-silent samples. Playback needs game testing.'
