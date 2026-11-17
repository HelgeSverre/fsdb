using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using System.Text.Json;

var etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
using var source = log.Events.GetSource();
var types = new Dictionary<string, long>();
var frames = new Dictionary<string, long>();
var leaves = new Dictionary<string, long>();
var asyncFrames = new Dictionary<string, long>();
long count = 0, total = 0;

source.Clr.GCAllocationTick += data =>
{
    long amount = data.AllocationAmount64;
    count++;
    total += amount;
    var type = data.TypeName ?? "unknown";
    types[type] = types.GetValueOrDefault(type) + amount;
    var seen = new HashSet<string>();
    bool first = true;
    for (var stack = data.CallStack(); stack != null; stack = stack.Caller)
    {
        var name = stack.CodeAddress.FullMethodName;
        if (name != null && (name.Contains("Fsdb.Executor") || name.Contains("Fsdb.Storage")) && seen.Add(name))
        {
            frames[name] = frames.GetValueOrDefault(name) + amount;
            if (first)
            {
                leaves[name] = leaves.GetValueOrDefault(name) + amount;
                first = false;
            }
            if (type.Contains("IAsyncLocal"))
                asyncFrames[name] = asyncFrames.GetValueOrDefault(name) + amount;
        }
    }
};
source.Process();

var summary = new
{
    events = count,
    sampledBytes = total,
    types = types.OrderByDescending(x => x.Value).Take(40),
    frames = frames.OrderByDescending(x => x.Value).Take(80),
    leaves = leaves.OrderByDescending(x => x.Value).Take(40),
    asyncFrames = asyncFrames.OrderByDescending(x => x.Value).Take(40)
};
File.WriteAllText(args[1], JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"events={count} sampledBytes={total}");
