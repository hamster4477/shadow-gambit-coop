using System;
using System.Collections.Generic;

// Preserve each manifest's complete-inventory semantics. Only omission of an
// entire tail means unknown; cutting rows out could delete live skill objects.
internal sealed class SnapshotTailBudget
{
    private readonly string[] pending;
    private readonly string[][][] pages;
    private readonly string[] pageTags;
    private readonly int[] pageCursors;
    private int cursor;
    internal SnapshotTailBudget(int families){if(families<1)throw new ArgumentOutOfRangeException(nameof(families));pending=new string[families];pages=new string[families][][];pageTags=new string[families];pageCursors=new int[families];}
    internal void Clear(){Array.Clear(pending,0,pending.Length);Array.Clear(pages,0,pages.Length);Array.Clear(pageTags,0,pageTags.Length);Array.Clear(pageCursors,0,pageCursors.Length);cursor=0;}
    // Only protocols with unknown-on-row-omission semantics may use this.
    // Select rows at send time: advancing when captured can permanently skip
    // rows if another family wins the remaining transport space.
    internal void OfferPage(int family,string tag,IReadOnlyList<string[]> rows,Func<string[],bool> valid)
    {
        if(family<0||family>=pending.Length||tag!="K3"&&tag!="Y1")throw new ArgumentException("Invalid paged family");
        if(rows==null)return;
        if(rows.Count>64)throw new ArgumentException("Too many flight rows");
        var copy=new string[rows.Count][];var ids=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<rows.Count;i++)
        {
            if(!valid(rows[i])||!ids.Add(rows[i][0]+":"+rows[i][1]))throw new ArgumentException("Invalid flight row");
            copy[i]=(string[])rows[i].Clone();
        }
        pages[family]=copy;pageTags[family]=tag;pending[family]=null;
    }
    internal void Offer(int family,string core,string attached)
    {
        if(family<0||family>=pending.Length||core==null||attached==null||!attached.StartsWith(core,StringComparison.Ordinal))throw new ArgumentException("Invalid snapshot family");
        string tail=attached.Substring(core.Length);
        // A null capture serializes no tail. Retain an already waiting sample;
        // a newer explicit empty manifest replaces it like any other sample.
        if(tail.Length==0)return;
        if(tail[0]!='\n')throw new ArgumentException("Invalid snapshot tail");
        pending[family]=tail;
        pages[family]=null;pageTags[family]=null;
    }
    internal string Append(string core,int limit)
    {
        if(core==null||core.Length>limit)throw new InvalidOperationException("Required crew state exceeds snapshot bound");
        int available=limit-core.Length,first=-1;
        var selected=new bool[pending.Length];
        var selectedTails=new string[pending.Length];
        var remainder=new string[pending.Length][][];
        for(int n=0;n<pending.Length;n++)
        {
            int index=(cursor+n)%pending.Length;string tail=pending[index];
            if(pages[index]!=null)
            {
                var rows=pages[index];var picked=new List<string>();int firstRow=-1;
                var sentRows=new bool[rows.Length];
                // Reserve the largest count header so packing cannot overflow
                // when the selected count changes from one to two digits.
                int left=available-(pageTags[index].Length+5);
                for(int j=0;j<rows.Length&&left>=0;j++)
                {
                    int rowIndex=(pageCursors[index]+j)%rows.Length;
                    string wire=string.Join(" ",rows[rowIndex]);
                    if(wire.Length+1>left)continue;
                    picked.Add(wire);sentRows[rowIndex]=true;left-=wire.Length+1;if(firstRow<0)firstRow=rowIndex;
                }
                if(rows.Length>0&&picked.Count==0)continue;
                tail="\n"+pageTags[index]+" "+picked.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(picked.Count>0)tail+=" "+string.Join(" ",picked);
                if(tail.Length>available)continue;
                if(firstRow>=0)pageCursors[index]=(firstRow+1)%rows.Length;
                if(picked.Count<rows.Length)
                {
                    var waiting=new List<string[]>();for(int j=0;j<rows.Length;j++)if(!sentRows[j])waiting.Add(rows[j]);
                    remainder[index]=waiting.ToArray();
                }
            }
            if(tail==null||tail.Length>available)continue;
            selected[index]=true;selectedTails[index]=tail;available-=tail.Length;if(first<0)first=index;
        }
        // Selection rotates, serialization stays in the protocol's fixed order.
        var body=new System.Text.StringBuilder(core);
        for(int i=0;i<pending.Length;i++)if(selected[i]){body.Append(selectedTails[i]);pending[i]=null;pages[i]=remainder[i];}
        if(first>=0)cursor=(first+1)%pending.Length;
        return body.ToString();
    }
}

internal sealed class FlightPageBudget
{
    private bool fishingFirst;
    private int cannonCursor,fishingCursor;
    internal void Clear(){fishingFirst=false;cannonCursor=fishingCursor=0;}
    internal void Select(IReadOnlyList<string[]> cannon,IReadOnlyList<string[]> fishing,int available,
        out List<string[]> cannonPage,out List<string[]> fishingPage)
    {
        cannonPage=new();fishingPage=new();
        // Both headers, including two-digit counts. Null means no source page;
        // empty pages have unknown-on-omission semantics in both receivers.
        int left=available-(cannon==null?0:7)-(fishing==null?0:7);
        if(left<0)throw new InvalidOperationException("No space for trajectory headers");
        void Pack(IReadOnlyList<string[]> rows,List<string[]> page,ref int cursor,bool active)
        {
            if(rows==null||rows.Count==0)return;
            int start=cursor%rows.Count,last=-1;
            for(int i=0;i<rows.Count&&page.Count<64;i++)
            {
                int index=(start+i)%rows.Count;var row=rows[index];
                if((row[3]=="A")!=active)continue;
                int size=string.Join(" ",row).Length+1;
                if(size>left)continue;
                page.Add(row);left-=size;last=index;
            }
            if(last>=0)cursor=(last+1)%rows.Count;
        }
        // Alternate first access to scarce space. Live motion precedes retained
        // endings, but a large row cannot prevent smaller rows from fitting.
        for(int pass=0;pass<2;pass++)
        {
            if(fishingFirst){Pack(fishing,fishingPage,ref fishingCursor,pass==0);Pack(cannon,cannonPage,ref cannonCursor,pass==0);}
            else {Pack(cannon,cannonPage,ref cannonCursor,pass==0);Pack(fishing,fishingPage,ref fishingCursor,pass==0);}
        }
        fishingFirst=!fishingFirst;
        if(cannon==null)cannonPage=null;if(fishing==null)fishingPage=null;
    }
}
