public static class SnapshotAssemblyTests {
 static int checks;const string Nonce="0123456789abcdef0123456789abcdef";
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static string[] Parts(string body,int read){
  string hash=Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(body))).ToLowerInvariant();
  int n=(body.Length+1023)/1024;var result=new string[n];
  for(int i=0;i<n;i++)result[i]=$"PART {Nonce} {read} {i} {n} {body.Length} {hash} "+body.Substring(i*1024,Math.Min(1024,body.Length-i*1024));
  return result;
 }
 public static int Run(){
  var a=new SnapshotAssembler();Func<int,float?> sent=id=>id>0&&id<=20?(float?)1:null;
  string body="S19 "+new string('x',32764);var p=Parts(body,2);
  int read;float started;string result;
  for(int i=p.Length-1;i>0;i--)Check(!a.Accept(p[i],Nonce,0,1.1f,sent,out read,out started,out result)&&result==null,"partial frame never released");
  Check(!a.Accept(p[1],Nonce,0,1.1f,sent,out read,out started,out result),"duplicate fragment cannot complete frame");
  Check(a.Accept(p[0],Nonce,0,1.2f,sent,out read,out started,out result)&&read==2&&started==1&&result==body,"reversed full frame preserves original read age");
  Check(!a.Accept(p[0],Nonce,2,1.2f,sent,out read,out started,out result),"old completed frame rejected");
  p=Parts("S19 "+new string('y',1100),3);a.Clear();
  Check(!a.Accept(p[0],Nonce,0,1.1f,sent,out read,out started,out result)&&!a.Accept(p[1],Nonce,0,1.9f,sent,out read,out started,out result),"late last piece cannot revive expired read");
  a.Clear();a.Accept(p[0],Nonce,0,1.1f,sent,out read,out started,out result);
  Check(!a.Accept(p[1].Substring(0,p[1].Length-1)+"z",Nonce,0,1.2f,sent,out read,out started,out result),"corrupted assembled payload rejected");
  var newer=Parts("S19 newer",4);a.Clear();a.Accept(p[0],Nonce,0,1.1f,sent,out read,out started,out result);
  Check(a.Accept(newer[0],Nonce,0,1.2f,sent,out read,out started,out result)&&read==4,"newer complete frame bypasses lost older frame");
  Check(!a.Accept(p[1],Nonce,4,1.3f,sent,out read,out started,out result),"older partial cannot turn back newer state");
  Check(!a.Accept(newer[0],new string('a',32),0,1.2f,sent,out read,out started,out result),"wrong connection nonce denied");
  Check(!a.Accept(Parts("S19 missing read",21)[0],Nonce,0,1.2f,sent,out read,out started,out result),"unsolicited read denied");
  foreach(string bad in new[]{p[0].Replace(" 0 2 1104 "," 0 1000000 1104 "),p[0].Replace(" 0 2 1104 "," 2 2 1104 "),p[0]+"x",new string('x',1281),"PART "+Nonce+" 3 0 32 32769 "+new string('a',64)+" x"})
   Check(!a.Accept(bad,Nonce,0,1.2f,sent,out read,out started,out result),"invalid part length and bounds denied");
  a.Clear();Check(a.Accept("FRAME "+Nonce+" 5 S18 old",Nonce,0,1.2f,sent,out read,out started,out result)&&result=="S18 old","legacy small frame remains readable");
  a.Clear();var left=Parts("S19 "+new string('l',2200),6);var right=Parts("S19 "+new string('r',2200),7);
  a.Accept(left[0],Nonce,0,1.1f,sent,out read,out started,out result);a.Accept(right[1],Nonce,0,1.1f,sent,out read,out started,out result);
  a.Accept(left[2],Nonce,0,1.1f,sent,out read,out started,out result);
  Check(a.Accept(left[1],Nonce,0,1.2f,sent,out read,out started,out result)&&read==6&&result.EndsWith("lll"),"interleaved old frame completes without mixed actors");
  a.Accept(right[0],Nonce,6,1.2f,sent,out read,out started,out result);
  Check(a.Accept(right[2],Nonce,6,1.3f,sent,out read,out started,out result)&&read==7&&result.EndsWith("rrr"),"interleaved newer frame still completes");
  return checks;
 }
}
