using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mason.Code;

public sealed record TurnEdit(string Path,string? Before,bool Bom,string AfterHash,bool ExternalChange=false);
public static class CodeRewind
{
    public static CodeTurn? Latest(CodeSession session)=>session.Turns.LastOrDefault(t=>t.Edits.Count>0 && !t.Rewound);
    internal static void Record(CodeTurn turn,string path,CodeFile? before,string after)
    {
        var i=turn.Edits.FindIndex(e=>e.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
        var hash=CodeProtocol.Hash(CodeWorkspace.Bytes(after,before?.Bom==true));
        if(i<0)turn.Edits.Add(new(path,before?.Content,before?.Bom==true,hash));
        else{
            var previous=turn.Edits[i];
            turn.Edits[i]=previous with{AfterHash=hash,ExternalChange=previous.ExternalChange || previous.AfterHash!=before?.Sha256};
        }
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file,int infoClass,ref byte info,uint size);
    public static void Apply(CodeStore store,CodeSession session,Guid turnId)
    {
        if(session.State is "Running" or "Awaiting review")throw new InvalidOperationException("Stop the active turn before rewinding.");
        var turn=Latest(session);
        if(turn==null || turn.Id!=turnId)throw new InvalidOperationException("The latest editable turn has changed.");
        var scope=new CodeWorkspace(session.Workspace);
        var locked=new List<(TurnEdit Edit,FileStream File,byte[]? Bytes)>();
        try{
            // Lock and compare every target before changing the first file. A pending
            // rewind can resume after a crash, including files already restored.
            foreach(var edit in turn.Edits.OrderBy(e=>e.Path,StringComparer.OrdinalIgnoreCase)){
                var path=scope.Resolve(edit.Path);
                if(edit.ExternalChange)throw new IOException("Another editor changed this file between Mason edits: "+edit.Path);
                var desired=edit.Before==null?null:CodeWorkspace.Bytes(edit.Before,edit.Bom);
                if(!File.Exists(path) && desired==null && turn.RewindPending)continue;
                var handle=CreateFileW(path,0xC0000000u|(desired==null?0x10000u:0),0,IntPtr.Zero,3,0,IntPtr.Zero);
                if(handle.IsInvalid){handle.Dispose();throw new IOException("Cannot lock "+edit.Path,new Win32Exception(Marshal.GetLastWin32Error()));}
                var file=new FileStream(handle,FileAccess.ReadWrite);
                locked.Add((edit,file,desired));
                var current=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
                if(turn.RewindPending && desired!=null && CodeProtocol.Hash(desired)==current)continue;
                if(current!=edit.AfterHash)throw new IOException("Rewind conflict. Later changes were found in "+edit.Path);
            }
            turn.RewindPending=true;store.Save(session);
            foreach(var (_,file,bytes) in locked){
                if(bytes==null){
                    byte delete=1;
                    if(!SetFileInformationByHandle(file.SafeFileHandle,4,ref delete,1))throw new IOException("Could not remove the file created by this turn.",new Win32Exception(Marshal.GetLastWin32Error()));
                }else{file.Position=0;file.Write(bytes);file.SetLength(bytes.Length);file.Flush(true);}
            }
        }finally{foreach(var item in locked)item.File.Dispose();}
        foreach(var edit in turn.Edits){
            var tracked=session.Changes.LastOrDefault(c=>!c.Restored && c.Path.Equals(edit.Path,StringComparison.OrdinalIgnoreCase));
            if(tracked==null)continue;
            if(edit.Before==null || tracked.Before==edit.Before){tracked.Restored=true;continue;}
            tracked.After=edit.Before;tracked.AfterHash=CodeProtocol.Hash(CodeWorkspace.Bytes(edit.Before,edit.Bom));
        }
        foreach(var prior in session.Turns.Where(t=>t.Acceptance!=null)){
            prior.Acceptance!.Results.Clear();prior.Acceptance.VerifiedRevision=null;
        }
        session.Messages.Add(new("user","The user rewound this turn's file edits. Re-read the affected files and verify the current revision before claiming completion: "+string.Join(", ",turn.Edits.Select(e=>e.Path))));
        turn.Rewound=true;turn.RewindPending=false;
        session.State="Needs verification";session.Detail="Rewound "+turn.Edits.Count+" files. Verify the current revision.";
        session.Activities.Add(new(){Tool="rewind_turn",State="Completed",Result=session.Detail});
        store.Save(session);
    }
}
