namespace NdcHostEmulator.Web.Services;

/// <summary>
/// Represents a single log entry produced by a terminal.
/// </summary>
/// <param name="Timestamp">Time at which the log entry was produced.</param>
/// <param name="Type">Category of the log entry (e.g. CONNECT, DISCONNECT, INCOMING, OUTGOING, DATA, ERROR, SYSTEM).</param>
/// <param name="Message">Human-readable message describing the event.</param>
public record LogEntry(DateTime Timestamp, string Type, string Message);
