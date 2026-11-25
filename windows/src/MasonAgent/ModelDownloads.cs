using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason;

public sealed record DownloadSettings(string Folder, string ProtectedToken = "");
public sealed record ModelDownload
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repository { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Variant { get; init; } = "";
    public string Format { get; init; } = "";
    public string License { get; init; } = "";
    public string StorageRoot { get; init; } = "";
    public HubFile[] Files { get; init; } = [];
    public string State { get; init; } = "Queued";
    public string Detail { get; init; } = "Waiting for a transfer slot";
    public long Received { get; init; }
    public double BytesPerSecond { get; init; }
    public string CurrentFile { get; init; } = "";
    public int VerifiedFiles { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Completed { get; init; }
    public DateTimeOffset? LastByteAt { get; init; }
    public long Total => Files.Sum(f=>f.Size);
    public double Percent => Total == 0 ? 0 : Math.Clamp(Received * 100d / Total,0,100);
    public string Name => Repository.Split('/').Last();
    // Earlier releases saved downloads under "db-cowboy-" names. A download that already has such a folder keeps it,
    // so nothing on disk is orphaned; every new download uses the "mason-" names.
    static string? Existing(string path) => Directory.Exists(path) ? path : null;
    public static string FolderFor(string storageRoot, Guid id) => Existing(Path.Combine(storageRoot,"db-cowboy-"+id.ToString("N"))) ?? Path.Combine(storageRoot,"mason-"+id.ToString("N"));
    public string Folder => FolderFor(StorageRoot,Id);
    public string Staging => Existing(Path.Combine(StorageRoot,ModelDiscovery.LegacyStagingFolder,Id.ToString("N"))) ?? Path.Combine(StorageRoot,ModelDiscovery.StagingFolder,Id.ToString("N"));
    public bool Active => State is "Downloading" or "Verifying files" or "Pausing";
    public bool Terminal => State is "Ready in library" or "Cancelled";
    public string ProgressText => State == "Verifying files" ? $"{VerifiedFiles} / {Files.Length} files verified" : HubFormat.Size(Received)+" / "+HubFormat.Size(Total);
    public string RateText => State == "Downloading" && BytesPerSecond > 0 && LastByteAt is {} at && DateTimeOffset.UtcNow-at<TimeSpan.FromSeconds(8) ? HubFormat.Size((long)BytesPerSecond)+"/s · about "+TimeSpan.FromSeconds(Math.Clamp((Total-Received)/BytesPerSecond,0,864000)).ToString(@"hh\:mm\:ss")+" remaining" : State=="Downloading"&&LastByteAt!=null?"Waiting for data…":"";
}

/// <summary>One durable queue per data directory, independent of page lifetime. Only verified bundles are published.</summary>
public sealed class ModelDownloads : IAsyncDisposable
{
    readonly object gate = new();
    readonly string statePath;
    readonly HubClient hub;
    readonly FileStream ownership;
    readonly List<ModelDownload> jobs;
    readonly string[] caches;
    CancellationTokenSource? current;
    Guid? currentId;
    string stopState = "Paused";
    Task worker = Task.CompletedTask;
    bool stopping;
    public ModelDownloads(string dataRoot, HubClient hub, IEnumerable<string>? cacheRoots = null)
    {
        this.hub = hub; Directory.CreateDirectory(dataRoot);
        ownership = new FileStream(Path.Combine(dataRoot,"downloads.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        statePath = Path.Combine(dataRoot,"model-downloads.json");
        caches=(cacheRoots??[]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            jobs=File.Exists(statePath)?JsonSerializer.Deserialize<List<ModelDownload>>(File.ReadAllText(statePath))??throw new InvalidDataException("Empty download state."):[];
            if(jobs.Count>500)throw new InvalidDataException("Download history is too large.");
            foreach(var j in jobs)Validate(j);
            if(jobs.Select(j=>j.Id).Distinct().Count()!=jobs.Count)throw new InvalidDataException("Duplicate download IDs.");
            for(int i=0;i<jobs.Count;i++)
            {
                var job=jobs[i];
                if(job.Active||job.State=="Queued")jobs[i]=job with{State="Paused",Detail="Interrupted by app closure · resume when ready",BytesPerSecond=0};
                else if(job.State=="Ready in library"&&job.Files.Any(f=>!File.Exists(HubFormat.Under(job.Folder,f.Path))||new FileInfo(HubFormat.Under(job.Folder,f.Path)).Length!=f.Size))jobs[i]=job with{State="Needs attention",Detail="Published files are missing or changed. Check the destination drive; retry verifies retained files without overwriting published models.",BytesPerSecond=0};
            }
            Persist();
        }
        catch { ownership.Dispose(); throw; }
    }
    static void Validate(ModelDownload j)
    {
        if(j==null||j.Files==null||j.Repository==null||j.Revision==null||j.StorageRoot==null||j.Variant==null||j.Files.Any(f=>f==null||f.Path==null||f.Hash==null))throw new InvalidDataException("Invalid saved download manifest. Existing state is preserved.");
        if(j.State is not("Queued" or "Downloading" or "Verifying files" or "Pausing" or "Paused" or "Needs attention" or "Ready in library" or "Cancelled"))throw new InvalidDataException("Unknown saved download state. Existing state is preserved.");
        HubFormat.Repo(j.Repository);
        if(j.Id==Guid.Empty||!Regex.IsMatch(j.Revision,"^[a-fA-F0-9]{40}$")||!Path.IsPathFullyQualified(j.StorageRoot)||j.Files.Length is 0 or > 10000||j.Variant.Length>250)throw new InvalidDataException("Invalid saved download manifest. Existing state is preserved.");
        if(j.Files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=j.Files.Length)throw new InvalidDataException("Conflicting file names.");
        foreach(var f in j.Files){HubFormat.Relative(f.Path);if(f.Size<0||!Regex.IsMatch(f.Hash,f.Lfs?"^[a-fA-F0-9]{64}$":"^[a-fA-F0-9]{40}$"))throw new InvalidDataException("Invalid saved integrity metadata.");}
        checked { _=j.Total; }
    }
    public ModelDownload[] Snapshot(){lock(gate)return jobs.Select(j=>j with{Files=j.Files.ToArray()}).ToArray();}
    public bool Busy{get{lock(gate)return !worker.IsCompleted;}}
    public ModelDownload Enqueue(HubRepository repo, HubVariant variant, string destination,Guid? reviewedId=null)
    {
        var job=new ModelDownload{Id=reviewedId??Guid.NewGuid(),Repository=repo.Id,Revision=repo.Revision,Variant=variant.Name,Format=variant.Format,License=repo.License,StorageRoot=Path.GetFullPath(destination),Files=variant.Files.ToArray()};Validate(job);HubFormat.NoLinks(job.StorageRoot);
        lock(gate)
        {
            if(stopping)throw new InvalidOperationException("Downloads are shutting down.");
            var duplicate=jobs.FirstOrDefault(j=>j.Repository==job.Repository&&j.Revision==job.Revision&&j.Variant==job.Variant&&j.State!="Cancelled");
            if(duplicate!=null)return duplicate;
            if(jobs.Any(j=>j.Id==job.Id))throw new InvalidOperationException("This download review has already been used.");
            if(jobs.Count>=500)throw new InvalidOperationException("Download history limit reached.");
            jobs.Add(job);try{Persist();}catch{jobs.Remove(job);throw;}Pump();return job;
        }
    }
    public void Resume(Guid id)
    {
        lock(gate){if(stopping)throw new InvalidOperationException("Downloads are shutting down.");var j=Find(id);if(j.State is not("Paused" or "Needs attention"))return;Set(j with{State="Queued",Detail="Waiting for a transfer slot",BytesPerSecond=0},true);Pump();}
    }
    public void Pause(Guid id,bool cancel=false)
    {
        lock(gate)
        {
            var j=Find(id);if(j.Terminal)return;
            if(currentId==id){stopState=cancel?"Cancelled":"Paused";Set(j with{State="Pausing",Detail="Stopping safely · partial files retained",BytesPerSecond=0},true);current?.Cancel();}
            else Set(j with{State=cancel?"Cancelled":"Paused",Detail="Partial files retained",BytesPerSecond=0},true);
        }
    }
    ModelDownload Find(Guid id)=>jobs.First(j=>j.Id==id);
    void Set(ModelDownload j,bool persist=false){jobs[jobs.FindIndex(x=>x.Id==j.Id)]=j;if(persist)Persist();}
    void Persist()
    {
        var temporary=statePath+".tmp";
        using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,jobs,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
        File.Move(temporary,statePath,true);
    }
    void Pump(){if(!stopping&&worker.IsCompleted)worker=Task.Run(Run);}
    async Task Run()
    {
        while(true)
        {
            ModelDownload job;CancellationToken ct;
            lock(gate)
            {
                if(stopping||jobs.FirstOrDefault(j=>j.State=="Queued") is not {} next){worker=Task.CompletedTask;return;}
                job=next;current=new();ct=current.Token;currentId=job.Id;stopState="Paused";
                Set(job with{State="Downloading",Detail="Preparing pinned files",BytesPerSecond=0,LastByteAt=null,VerifiedFiles=0});
            }
            try { await Transfer(job,ct).ConfigureAwait(false); }
            catch(Exception e)
            {
                lock(gate)
                {
                    var state=ct.IsCancellationRequested?stopState:"Needs attention";
                    // Never persist raw HTTP exception strings, which can contain signed CDN URLs.
                    string detail=ct.IsCancellationRequested?"Partial files retained · "+(state=="Cancelled"?"download cancelled":"resume when ready"):e is InvalidDataException?e.Message:e is IOException?SafeIo(e.Message):"Connection interrupted or timed out · partial files retained. Retry when available.";
                    Set(Find(job.Id) with{State=state,Detail=detail,BytesPerSecond=0});
                    try{Persist();}catch(IOException){Set(Find(job.Id) with{State="Needs attention",Detail="Cannot save download state. Check free space and access to the app data folder. Partial files retained."});stopping=true;}
                    catch(UnauthorizedAccessException){Set(Find(job.Id) with{State="Needs attention",Detail="Cannot save download state. Restore access to the app data folder. Partial files retained."});stopping=true;}
                }
            }
            finally{lock(gate){current?.Dispose();current=null;currentId=null;}}
        }
    }
    static string SafeIo(string text)=>text.Contains("http",StringComparison.OrdinalIgnoreCase)?"Transfer failed · partial files retained. Check connection, access and storage, then retry.":text;
    async Task Transfer(ModelDownload job,CancellationToken ct)
    {
        HubFormat.NoLinks(job.StorageRoot);Directory.CreateDirectory(job.StorageRoot);
        bool published=Directory.Exists(job.Folder);string work=published?job.Folder:job.Staging;
        HubFormat.NoLinks(work);Directory.CreateDirectory(work);
        long received=0,networkBytes=0;var clock=Stopwatch.StartNew();var saveClock=Stopwatch.StartNew();
        void Progress(string file,long bytes)
        {
            lock(gate){var j=Find(job.Id);Set(j with{Received=bytes,CurrentFile=file,BytesPerSecond=networkBytes/Math.Max(.1,clock.Elapsed.TotalSeconds),LastByteAt=DateTimeOffset.UtcNow,Detail="Downloading pinned files"},saveClock.Elapsed.TotalSeconds>=3);if(saveClock.Elapsed.TotalSeconds>=3)saveClock.Restart();}
        }
        foreach(var file in job.Files)
        {
            ct.ThrowIfCancellationRequested();var path=HubFormat.Under(work,file.Path);HubFormat.NoLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if(File.Exists(path)&&new FileInfo(path).Length==file.Size)
            {
                if(await Verify(path,file,ct).ConfigureAwait(false)){received+=file.Size;Progress(file.Path,received);continue;}
                if(published)throw new InvalidDataException("A published file has changed. Existing library files were preserved; choose a fresh download after inspecting storage.");
                // Only this job's staging file is invalidated. Other verified files remain intact.
                using(var reset=new FileStream(path,FileMode.Truncate,FileAccess.Write,FileShare.None)){}
            }
            if(published)throw new InvalidDataException("A published file is missing or incomplete. Existing library files were preserved.");
            if(!File.Exists(path)&&await ReuseCache(job,file,path,ct).ConfigureAwait(false)){received+=file.Size;Progress(file.Path,received);continue;}
            long offset=File.Exists(path)?new FileInfo(path).Length:0;
            if(offset>file.Size)throw new InvalidDataException("Partial file is larger than its manifest. Inspect the staging folder before retrying.");
            var drive=new DriveInfo(Path.GetPathRoot(work)!);if(drive.IsReady&&drive.AvailableFreeSpace<file.Size-offset+16*1024*1024)throw new IOException("Not enough disk space. Free space on the destination drive, then retry. Partial files were retained.");
            using var response=await hub.Send(HubClient.FileUrl(job.Repository,job.Revision,file.Path),offset,ct).ConfigureAwait(false);
            if(response.StatusCode==HttpStatusCode.PartialContent)
            {
                var range=response.Content.Headers.ContentRange;
                if(range?.From!=offset||range.Length!=file.Size||range.To!=file.Size-1)throw new InvalidDataException("Server returned a mismatched byte range. Partial data was preserved.");
            }
            else offset=0; // Servers may ignore Range. Restart safely instead of appending a second full file.
            await using var output=new FileStream(path,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read,131072,true);
            output.SetLength(offset);output.Position=offset;
            await using var input=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer=new byte[131072];long transferred=offset;
            while(true)
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(45));
                int count=await input.ReadAsync(buffer,timeout.Token).ConfigureAwait(false);if(count==0)break;
                if(transferred+count>file.Size)throw new InvalidDataException("File exceeds the pinned size. Download stopped.");
                await output.WriteAsync(buffer.AsMemory(0,count),ct).ConfigureAwait(false);transferred+=count;networkBytes+=count;Progress(file.Path,received+transferred);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            if(transferred!=file.Size)throw new IOException("Connection interrupted before the file was complete · partial files retained.");
            received+=transferred;
        }
        lock(gate)Set(Find(job.Id) with{State="Verifying files",Received=job.Total,BytesPerSecond=0,VerifiedFiles=0,Detail="Transfer complete · checking file integrity"},true);
        int verified=0;
        foreach(var file in job.Files)
        {
            var path=HubFormat.Under(work,file.Path);HubFormat.NoLinks(path);
            if(!await Verify(path,file,ct).ConfigureAwait(false))
            {
                if(!published){using var reset=new FileStream(path,FileMode.Truncate,FileAccess.Write,FileShare.None);}
                throw new InvalidDataException("Integrity check failed: "+file.Path+". Retry downloads this file again; other verified files are retained.");
            }
            lock(gate)Set(Find(job.Id) with{CurrentFile=file.Path,VerifiedFiles=++verified,Detail="Verified "+verified+" of "+job.Files.Length+" files"},true);
        }
        ct.ThrowIfCancellationRequested();
        if(!published){HubFormat.NoLinks(job.Folder);Directory.Move(work,job.Folder);}
        lock(gate){ct.ThrowIfCancellationRequested();Set(Find(job.Id) with{State="Ready in library",Detail="All selected files verified. Model has not been started.",Completed=DateTimeOffset.UtcNow,BytesPerSecond=0},true);}
    }
    async Task<bool> ReuseCache(ModelDownload job,HubFile file,string target,CancellationToken ct)
    {
        foreach(var cache in caches)
        {
            var repo=Path.Combine(cache,"models--"+job.Repository.Replace("/","--"));
            var candidates=new[]{HubFormat.Under(Path.Combine(repo,"snapshots",job.Revision),file.Path),Path.Combine(repo,"blobs",file.Hash)};
            foreach(var candidate in candidates)
            {
                if(!File.Exists(candidate))continue;
                try
                {
                    if(!await Verify(candidate,file,ct).ConfigureAwait(false))continue;
                    await using var input=File.OpenRead(candidate);await using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true);
                    await input.CopyToAsync(output,ct).ConfigureAwait(false);return true;
                }
                catch(UnauthorizedAccessException){continue;}
            }
        }
        return false;
    }
    public static async Task<bool> Verify(string path,HubFile file,CancellationToken ct)
    {
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,true);
        if(stream.Length!=file.Size)return false;
        using var hash=IncrementalHash.CreateHash(file.Lfs?HashAlgorithmName.SHA256:HashAlgorithmName.SHA1);
        if(!file.Lfs)hash.AppendData(Encoding.UTF8.GetBytes("blob "+file.Size+"\0"));
        var buffer=new byte[131072];int count;
        while((count=await stream.ReadAsync(buffer,ct).ConfigureAwait(false))>0)hash.AppendData(buffer,0,count);
        return Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Hash,StringComparison.OrdinalIgnoreCase);
    }
    public async ValueTask DisposeAsync()
    {
        Task pending;lock(gate){stopping=true;stopState="Paused";current?.Cancel();pending=worker;}
        try{await pending.ConfigureAwait(false);}finally{ownership.Dispose();}
    }
}
