$ErrorActionPreference='Stop'
# The ping sound is replaceable. Whatever PCM16 WAV sits in media/ is decoded with the
# production method, so replacing bell_b.wav validates your file instead of the original.
$bell=Join-Path $PSScriptRoot 'media/bell_b.wav'
if(!(Test-Path -LiteralPath $bell -PathType Leaf)){
    'Skipped: media/bell_b.wav is missing. Drop a PCM16 WAV there (mono or stereo) to run this decode check.'
    exit 0
}
$s=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CoopPings.cs'))
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
 public static string Run(string path){
  var c=ReadBell(path);
  if(c.rate<8000||c.rate>96000)throw new Exception("Unexpected sample rate");
  if(c.channels<1||c.channels>2)throw new Exception("Unexpected channel count");
  if(c.frames<1||c.data.Length!=c.frames*c.channels)throw new Exception("Sample count mismatch");
  bool sound=false;
  foreach(float s in c.data){if(!float.IsFinite(s)||Math.Abs(s)>1)throw new Exception("Invalid sample");if(Math.Abs(s)>.01)sound=true;}
  if(!sound)throw new Exception("Silent bell");
  return string.Format("{0} Hz, {1} channel(s), {2} frames, {3} samples",c.rate,c.channels,c.frames,c.data.Length);
 }
}
'@
Add-Type -TypeDefinition $fixture
$info=[PingAudioTest]::Run($bell)
"Ping sound decoded by the production method: $info. Replace media/bell_b.wav with any PCM16 WAV; playback still needs in-game testing."
