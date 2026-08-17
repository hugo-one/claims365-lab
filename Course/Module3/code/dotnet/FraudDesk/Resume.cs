// Running and resuming the fraud desk.
//
// THE REAL DIFFERENCE BETWEEN THE TWO LANGUAGE TRACKS.
//
// Python answers a pending human gate with a dictionary:
//
//     wf.run(responses={request_id: decision}, checkpoint_id=cp)
//
// .NET does not. Restoring from a checkpoint RE-EMITS the pending request as a `RequestInfoEvent`,
// and you answer the event. So the C# resume path is an event loop rather than a lookup:
//
//     await foreach (var evt in run.WatchStreamAsync(blockOnPendingRequest: true))
//         if (evt is RequestInfoEvent req) await run.SendResponseAsync(req.Request.CreateResponse(x));
//
// Same durability, same guarantee, different shape. It is also why the C# graph has five nodes to
// Python's four: the gate is a `RequestPort` in the topology rather than a call inside a method.
using Microsoft.Agents.AI.Workflows;

namespace FraudDesk;

public static class Resume
{
    /// <summary>
    /// Did an executor throw during the last run? The framework does NOT let a handler's exception
    /// escape - it becomes an `ExecutorFailedEvent` and the run completes normally - so without
    /// this the process would print an error and still exit 0.
    /// </summary>
    public static bool Failed { get; private set; }

    /// <summary>
    /// The message worth showing a person: the innermost exception's. The framework wraps handler
    /// failures in a TargetInvocationException, so the outer message names the handler, not the
    /// cause.
    /// </summary>
    static string Describe(object? data)
    {
        if (data is not Exception ex) return data?.ToString() ?? "unknown error";
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    /// <summary>JSON checkpoints in a folder. One manager per command, since each is its own process.</summary>
    static CheckpointManager Manager(string dir) =>
        CheckpointManager.CreateJson(new FileCheckpointStore(dir));

    /// <summary>Start a fresh investigation and run until the desk needs a human.</summary>
    public static async Task StartAsync(string dir, string sessionId, SeedClaim seed)
    {
        var run = await InProcessExecution.RunStreamingAsync(
            Desk.BuildWorkflow(), seed, Manager(dir), sessionId);
        await PumpAsync(run, dir, decision: null);
    }

    /// <summary>Answer the pending gate, in a process that never started the investigation.</summary>
    public static async Task AnswerAsync(string dir, string sessionId, ReferralDecision decision)
    {
        var mgr = Manager(dir);
        var latest = await mgr.GetLatestCheckpointAsync(sessionId)
            ?? throw new Exception("no checkpoint for this session. run 'start' first.");
        var run = await InProcessExecution.ResumeStreamingAsync(
            Desk.BuildWorkflow(), latest, mgr);
        await PumpAsync(run, dir, decision);
    }

    /// <summary>
    /// Drain the event stream. If a decision was supplied, answer the first request that comes back;
    /// otherwise record what is pending so a later process can report it without replaying anything.
    /// </summary>
    static async Task PumpAsync(StreamingRun run, string dir, ReferralDecision? decision)
    {
        await foreach (var evt in run.WatchStreamAsync(blockOnPendingRequest: true))
        {
            // M3_TRACE=1 prints every workflow event. Worth knowing: a silent run is almost always
            // a routing problem, and this is how you see it.
            if (Environment.GetEnvironmentVariable("M3_TRACE") == "1")
                Console.WriteLine($"    [event] {evt.GetType().Name}");
            // A failing executor is reported as an EVENT, not thrown, so record it here.
            // Print the innermost message only, with the full trace behind M3_TRACE: the sentence
            // that tells you what to do is otherwise buried under framework frames.
            if (evt is ExecutorFailedEvent bad)
            {
                Failed = true;
                Console.Error.WriteLine();
                Console.Error.WriteLine($"  !! the '{bad.ExecutorId}' step failed: {Describe(bad.Data)}");
                Console.Error.WriteLine("  (set M3_TRACE=1 for the full stack trace)");
                if (Environment.GetEnvironmentVariable("M3_TRACE") == "1")
                    Console.Error.WriteLine(bad.Data);
            }
            // Usually the same exception again, one layer out. Only useful with the trace on.
            if (evt is WorkflowErrorEvent werr)
            {
                Failed = true;
                if (Environment.GetEnvironmentVariable("M3_TRACE") == "1")
                    Console.Error.WriteLine($"    !! workflow error: {werr.Data}");
            }
            switch (evt)
            {
                case RequestInfoEvent req:
                    if (decision is null)
                    {
                        // Nobody is here to answer. Write down what is waiting and stop.
                        // PortableValue is the framework's type-erased envelope. `Is<T>(out T)`
                        // is how you get your own type back out of it.
                        if (req.Request.Data.Is<ReferralApprovalRequest>(out var ask))
                            Pending.Write(dir, ask);
                        return;
                    }
                    await run.SendResponseAsync(req.Request.CreateResponse(decision));
                    break;

                case WorkflowOutputEvent done:
                    if (done.Data is FraudOutcome outcome)
                    {
                        Outcomes.Last = outcome;
                        Pending.Clear(dir);
                    }
                    break;
            }
        }
    }
}
