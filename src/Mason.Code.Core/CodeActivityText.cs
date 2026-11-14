using System.Text.Json;

namespace Mason.Code;

public static class CodeActivityText
{
    public static string Command(CodeActivity activity)
    {
        try
        {
            using var json=JsonDocument.Parse(activity.Arguments);var root=json.RootElement;
            if(root.TryGetProperty("command",out var command))return command.GetString()??"";
            if(root.TryGetProperty("executable",out var executable))return CodeCommand.CommandLine(executable.GetString()??"",root.GetProperty("arguments").EnumerateArray().Select(a=>a.GetString()??"").ToArray());
            if(root.TryGetProperty("path",out var path))return path.GetString()??"";
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException){}
        return activity.Tool.Replace('_',' ');
    }
    public static string Result(CodeActivity activity)
    {
        try
        {
            using var json=JsonDocument.Parse(activity.Result);var root=json.RootElement;
            if(root.TryGetProperty("Output",out var output))
            {
                var raw=output.GetString()??"";
                if(root.TryGetProperty("Failure",out var failure)&&failure.ValueKind==JsonValueKind.Object&&
                    failure.TryGetProperty("Summary",out var summary)&&summary.ValueKind==JsonValueKind.String&&
                    failure.TryGetProperty("Guidance",out var guidance)&&guidance.ValueKind==JsonValueKind.String)
                    return summary.GetString()+"\n\n"+guidance.GetString()+"\n\nCommand output:\n"+raw;
                return raw;
            }
            if(root.TryGetProperty("Files",out var files))return files.GetArrayLength()==0?"No files found in this workspace.":string.Join('\n',files.EnumerateArray().Select(f=>f.GetString()));
            if(root.TryGetProperty("state",out var state))return state.GetString()??"";
            if(root.TryGetProperty("Content",out var content))return content.GetString()??"";
            return JsonSerializer.Serialize(root,new JsonSerializerOptions{WriteIndented=true});
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException){return activity.Result;}
    }
}
