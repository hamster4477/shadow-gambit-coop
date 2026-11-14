param([switch]$Benchmark)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot
$baseline=Join-Path $project 'research/udp-parser-perf-20261114'
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'UdpParsingTests.cs'))
$prelude=@'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Socket=UdpParserSocket;
public class UdpBenchResult {public int SnapshotBytes,Iterations;public double AllocatedBytesPerSnapshot,MicrosecondsPerSnapshot;}
public sealed class UdpParserSocket:IDisposable {
 public static UdpParserSocket Last;public bool Blocking;public byte[] LastSend;public int Received;
 public readonly Queue<byte[]> Input=new Queue<byte[]>();
 public UdpParserSocket(AddressFamily a,SocketType b,ProtocolType c){Last=this;}
 public void Connect(IPEndPoint point){}public void Dispose(){}
 public void Queue(string text)=>Input.Enqueue(Encoding.ASCII.GetBytes(text));
 public int Send(byte[] data){LastSend=data;return data.Length;}
 public bool Poll(int timeout,SelectMode mode)=>Input.Count!=0;
 public int Receive(byte[] buffer){var value=Input.Dequeue();Received++;int size=Math.Min(buffer.Length,value.Length);Buffer.BlockCopy(value,0,buffer,0,size);return size;}
}
public static class SyncProtocol {internal static bool Session(string text)=>text!=null&&Regex.IsMatch(text,@"\A[0-9a-f]{32}\z");}
'@
$production=foreach($name in 'SnapshotTransport.cs','LanStateFeed.cs'){
    [regex]::Replace([IO.File]::ReadAllText((Join-Path $project $name)),'(?m)^using [^;]+;\r?\n','')
}
$code=$prelude+"`nnamespace Current {`n"+($production -join "`n")+"`n"+$fixture+"`n}"
if($Benchmark){
    $old=foreach($name in 'SnapshotTransport','LanStateFeed'){
        [regex]::Replace([IO.File]::ReadAllText((Join-Path $baseline ($name+'.before.cs'))),'(?m)^using [^;]+;\r?\n','')
    }
    $code+="`nnamespace Before {`n"+($old -join "`n")+"`n"+$fixture+"`n}"
}
Add-Type -TypeDefinition $code
Write-Output ('PASS production UDP Send/Drain parsing: '+[Current.UdpParsingTests]::Run()+' checks; socket only simulated.')
if($Benchmark){
    Write-Output ('PASS baseline parity: '+[Before.UdpParsingTests]::Run()+' checks.')
    $rows=@(foreach($size in 1024,8192,32768){
        $oldRuns=@();$newRuns=@()
        for($trial=0;$trial -lt 3;$trial++){
            if($trial % 2 -eq 0){
                $old=[Before.UdpParsingTests]::Benchmark($size,1500)
                $new=[Current.UdpParsingTests]::Benchmark($size,1500)
            }else{
                $new=[Current.UdpParsingTests]::Benchmark($size,1500)
                $old=[Before.UdpParsingTests]::Benchmark($size,1500)
            }
            if($new.AllocatedBytesPerSnapshot -ge $old.AllocatedBytesPerSnapshot){throw 'UDP parser allocation regression'}
            $oldRuns+=$old;$newRuns+=$new
        }
        [ordered]@{snapshotBytes=$size;iterationsPerTrial=1500;trials=3;
            beforeAllocatedBytes=$old.AllocatedBytesPerSnapshot;afterAllocatedBytes=$new.AllocatedBytesPerSnapshot;
            reductionPercent=[Math]::Round((1-$new.AllocatedBytesPerSnapshot/$old.AllocatedBytesPerSnapshot)*100,2);
            beforeMedianMicroseconds=($oldRuns.MicrosecondsPerSnapshot|Sort-Object)[1];
            afterMedianMicroseconds=($newRuns.MicrosecondsPerSnapshot|Sort-Object)[1];
            beforeTimeSamples=$oldRuns.MicrosecondsPerSnapshot;afterTimeSamples=$newRuns.MicrosecondsPerSnapshot}
    })
    $report=[ordered]@{date='2026-11-14';scope='Production UDP receive/parser with simulated socket; no network/game FPS claim';
        timing='Indicative only; allocation reduction is the asserted metric. Alternating order, 200 warmup frames per trial.';checks=12;results=$rows}
    $report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $baseline 'benchmark.json') -Encoding UTF8
    $report|ConvertTo-Json -Depth 6
}
