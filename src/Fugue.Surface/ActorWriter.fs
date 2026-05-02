namespace Fugue.Surface

open System
open System.IO
open System.Text

/// TextWriter that funnels every Write call into the Surface actor as a
/// `RawAnsi` DrawOp. Installed as `Console.Out` (and `Console.Error`) at
/// startup so EVERY writer in the process — Spectre.Console, Markdig,
/// AnsiConsole.MarkupLine, our internal `writeRaw`, raw Console.WriteLine
/// from third-party libraries — automatically serialises through the
/// actor's mailbox queue.
///
/// This is the missing piece for Phase 1.3c: with ReadLine routed through
/// the actor (Phase 1.3b) but streaming tokens / markdown / slash-command
/// output still going direct to Console.Out, races between heartbeat paints
/// and stream tokens caused visible flicker / overlap. With this writer
/// installed, the actor is the SOLE consumer of the real terminal stdout.
///
/// CRITICAL: the actor's own writes (RealExecutor.write) MUST go through the
/// captured original stdout, NOT through this writer. Otherwise every actor
/// write posts a message to itself → mailbox grows forever, no progress.
/// `RealExecutor.originalStdout` captures Console.Out at module load
/// (which happens at `RealExecutor.start ()` from Program.fs) BEFORE this
/// writer is installed.
type ActorWriter(agent: MailboxProcessor<SurfaceMessage>) =
    inherit TextWriter()

    /// Surrogate object that turns a string write into one Execute message.
    /// Centralised so Write/WriteLine/Write(char) all funnel here.
    let post (s: string) =
        if not (String.IsNullOrEmpty s) then
            agent.Post(SurfaceMessage.Execute [ DrawOp.RawAnsi s ])

    override _.Encoding = Encoding.UTF8

    override _.Write(value: string | null) = post (match value with null -> "" | s -> s)
    override this.Write(value: char) = post (string value)
    override this.Write(buffer: char[] | null) =
        match buffer with
        | null -> ()
        | b    -> post (String b)
    override this.Write(buffer: char[] | null, index: int, count: int) =
        match buffer with
        | null -> ()
        | b    -> post (String(b, index, count))

    override this.WriteLine() = post "\n"
    override this.WriteLine(value: string | null) =
        match value with
        | null -> post "\n"
        | s    -> post (s + "\n")
    override this.WriteLine(value: char) = post (string value + "\n")

    /// Flush is a no-op: the actor processes its queue independently.
    /// Callers that need a hard barrier should post `ExecuteAndAck` directly.
    override _.Flush() = ()

    /// Don't dispose the underlying writer — that's owned elsewhere
    /// (Console's original stdout) and we never created it.
    override _.Dispose(disposing: bool) =
        base.Dispose(disposing)
