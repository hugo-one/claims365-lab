// Small pieces the desk needs: a file-backed checkpoint store, somewhere to catch the workflow's
// output, and the pending-request cache that lets `status` read from disk in a fresh process.
//
// DEMONSTRATION CODE - part of the Claims 365 training lab.
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace FraudDesk;

/// <summary>
/// Checkpoints on disk, so a claim survives the process exiting.
///
/// The Python track gets this from `FileCheckpointStorage`. .NET ships `JsonCheckpointStore` as an
/// interface plus an in-memory implementation, so the file-backed one is written here - about ten
/// lines, and a genuine difference between the two tracks.
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore<JsonElement>
{
    readonly string _dir;

    public FileCheckpointStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// One file per checkpoint, named `session.checkpoint.json`. The session id is in the FILENAME
    /// rather than a subdirectory, so listing the folder shows a run's whole history at a glance.
    /// </summary>
    string PathFor(string sessionId, string id) =>
        Path.Combine(_dir, $"{sessionId}.{id}.json");

    /// <summary>
    /// Save a checkpoint and append its id to the session index - a plain text file, one id per
    /// line, so "the latest" is the last line rather than an unreliable filesystem timestamp.
    /// </summary>
    public ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString());
        File.WriteAllText(PathFor(sessionId, info.CheckpointId), value.GetRawText());
        var index = Path.Combine(_dir, $"{sessionId}.index");
        File.AppendAllLines(index, new[] { info.CheckpointId });
        return new ValueTask<CheckpointInfo>(info);
    }

    /// <summary>Read one checkpoint back. Throws if it is missing, which is correct: a resume that
    /// silently found nothing would restart the investigation and bill for it again.</summary>
    public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        var raw = File.ReadAllText(PathFor(sessionId, key.CheckpointId));
        return new ValueTask<JsonElement>(JsonDocument.Parse(raw).RootElement.Clone());
    }

    /// <summary>Every checkpoint in a session, oldest first. An empty list for an unknown session,
    /// because "you have not started one" is an ordinary state and not an error.</summary>
    public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId, CheckpointInfo? withParent = null)
    {
        var index = Path.Combine(_dir, $"{sessionId}.index");
        IEnumerable<CheckpointInfo> all = File.Exists(index)
            ? File.ReadAllLines(index).Where(l => l.Length > 0)
                  .Select(id => new CheckpointInfo(sessionId, id)).ToList()
            : new List<CheckpointInfo>();
        return new ValueTask<IEnumerable<CheckpointInfo>>(all);
    }
}

/// <summary>
/// Where the workflow's final output lands, so the CLI can print it. A static is acceptable only
/// because each command is a whole process running one investigation.
/// </summary>
public static class Outcomes
{
    public static FraudOutcome? Last { get; set; }
}

/// <summary>
/// What is waiting for a human, written beside the checkpoints so `status` in a NEW process can
/// report it without replaying the workflow. A convenience CACHE, never a source of truth: the
/// checkpoint holds the real request, and deleting this loses nothing an approve cannot recover.
/// </summary>
public static class Pending
{
    public static string PathIn(string dir) => Path.Combine(dir, "pending.json");

    public static void Write(string dir, ReferralApprovalRequest req) =>
        File.WriteAllText(PathIn(dir), JsonSerializer.Serialize(req));

    /// <summary>The pending request, or null when the desk is not waiting on anybody.</summary>
    public static ReferralApprovalRequest? Read(string dir)
    {
        var f = PathIn(dir);
        return File.Exists(f)
            ? JsonSerializer.Deserialize<ReferralApprovalRequest>(File.ReadAllText(f))
            : null;
    }

    /// <summary>Forget the pending request. Called when the workflow produces its outcome, so that
    /// `status` says "finished" rather than describing a gate that has already been answered.</summary>
    public static void Clear(string dir)
    {
        var f = PathIn(dir);
        if (File.Exists(f)) File.Delete(f);
    }
}
